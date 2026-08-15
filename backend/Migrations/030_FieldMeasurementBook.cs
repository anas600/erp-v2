using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 55 — Field Measurement Book (الدفتر الفني).
///
/// Real construction billing uses an intermediate document
/// between "BOQ quantities" and "billing amounts": the
/// Field Measurement Book (FMB). The FMB records the engineer's
/// on-site measurements for each BOQ line item:
///
///   - Each line item can have MULTIPLE sub-measurements
///     (e.g. 4 façades of a wall, each measured separately)
///   - Each sub-measurement: count × length × width × height
///   - Subtotal = Σ sub-measurements
///   - Deductions (e.g. concrete blinding subtracted from
///     excavation volume) further reduce the final quantity
///   - Final amount = (subtotal - deductions) × unit_price
///
/// This migration creates 3 tables:
///   1. field_measurement_books (the book header)
///   2. field_measurement_entries (one per BOQ line, with
///      sub-measurements stored as JSONB)
///   3. contract_line_item_progress (denormalized progress % per
///      BOQ line — auto-updated when an FMB is approved)
/// </summary>
[Migration(20260815000030)]
public class FieldMeasurementBook : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) field_measurement_books — the book header
        // ============================================================
        if (!Schema.Table("field_measurement_books").Exists())
        {
            Create.Table("field_measurement_books")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("project_id").AsGuid().NotNullable()
                    .ForeignKey("projects", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("contract_id").AsGuid().Nullable()
                    .ForeignKey("contracts", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("book_number").AsString(50).NotNullable()
                .WithColumn("measurement_date").AsDateTime().NotNullable()
                .WithColumn("measurement_period_from").AsDateTime().Nullable()
                .WithColumn("measurement_period_to").AsDateTime().Nullable()
                .WithColumn("engineer_user_id").AsGuid().Nullable()
                    .ForeignKey("users", "id")
                .WithColumn("engineer_name").AsString(200).Nullable()
                .WithColumn("consultant_user_id").AsGuid().Nullable()
                    .ForeignKey("users", "id")
                .WithColumn("consultant_name").AsString(200).Nullable()
                .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("DRAFT")
                // DRAFT → SUBMITTED → APPROVED → CANCELLED
                .WithColumn("approved_at").AsDateTime().Nullable()
                .WithColumn("notes").AsString(int.MaxValue).Nullable()
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentDateTime)
                .WithColumn("updated_at").AsDateTime().Nullable();

            Create.Index("ux_fmb_company_book_number")
                .OnTable("field_measurement_books")
                .OnColumn("company_id").Ascending()
                .OnColumn("book_number").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table("field_measurement_books").Index("ix_fmb_project").Exists())
            Create.Index("ix_fmb_project").OnTable("field_measurement_books").OnColumn("project_id");
        if (!Schema.Table("field_measurement_books").Index("ix_fmb_status").Exists())
            Create.Index("ix_fmb_status").OnTable("field_measurement_books").OnColumn("status");

        // ============================================================
        // 2) field_measurement_entries — one per BOQ line item
        //    sub-measurements stored as JSONB
        // ============================================================
        if (!Schema.Table("field_measurement_entries").Exists())
        {
            // Use raw SQL because FluentMigrator escapes the jsonb
            // default value into a syntax the Postgres JSON parser
            // rejects. The DEFAULT '[]'::jsonb is the cleanest form
            // and the only one Postgres accepts.
            Execute.Sql(@"
                CREATE TABLE field_measurement_entries (
                    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
                    fmb_id uuid NOT NULL REFERENCES field_measurement_books(id) ON DELETE CASCADE,
                    line_item_id uuid NOT NULL REFERENCES contract_line_items(id) ON DELETE CASCADE,
                    measurements jsonb NOT NULL DEFAULT '[]'::jsonb,
                    initial_total decimal(18,3) NOT NULL DEFAULT 0,
                    deductions_total decimal(18,3) NOT NULL DEFAULT 0,
                    final_total decimal(18,3) NOT NULL DEFAULT 0,
                    unit_price decimal(18,3) NOT NULL DEFAULT 0,
                    amount decimal(18,3) NOT NULL DEFAULT 0,
                    notes text,
                    created_at timestamptz NOT NULL DEFAULT NOW(),
                    updated_at timestamptz
                );
            ");

            Execute.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_fmb_entries_fmb
                ON field_measurement_entries (fmb_id);
            ");
            Execute.Sql(@"
                CREATE INDEX IF NOT EXISTS ix_fmb_entries_line_item
                ON field_measurement_entries (line_item_id);
            ");
        }

        // ============================================================
        // 3) contract_line_item_progress — denormalized % per BOQ
        //    line. Auto-updated when an FMB is approved.
        // ============================================================
        if (!Schema.Table("contract_line_item_progress").Exists())
        {
            Create.Table("contract_line_item_progress")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("line_item_id").AsGuid().NotNullable()
                    .ForeignKey("contract_line_items", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("project_id").AsGuid().NotNullable()
                    .ForeignKey("projects", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("progress_percent").AsDecimal(5, 2).NotNullable().WithDefaultValue(0)
                .WithColumn("quantity_done").AsDecimal(18, 3).NotNullable().WithDefaultValue(0)
                .WithColumn("last_updated").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentDateTime)
                .WithColumn("notes").AsString(int.MaxValue).Nullable();

            Create.Index("ux_line_item_progress_line")
                .OnTable("contract_line_item_progress")
                .OnColumn("line_item_id").Ascending()
                .WithOptions().Unique();
            Create.Index("ix_line_item_progress_project")
                .OnTable("contract_line_item_progress")
                .OnColumn("project_id");
        }
    }

    public override void Down()
    {
        if (Schema.Table("contract_line_item_progress").Exists())
            Delete.Table("contract_line_item_progress");

        if (Schema.Table("field_measurement_entries").Exists())
            Delete.Table("field_measurement_entries");

        if (Schema.Table("field_measurement_books").Exists())
            Delete.Table("field_measurement_books");
    }
}
