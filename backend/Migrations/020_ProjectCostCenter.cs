using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 35 — Project cost-center foundation.
///
/// 1) Extends <c>projects</c> with the fields needed to track real
///    engagements (type, customer, contract value, dates, manager,
///    location, notes). All nullable so existing rows are unaffected.
///
/// 2) Adds a nullable <c>project_id</c> FK to four transaction tables
///    (invoices, journal_entries, payments, receipts) so individual
///    transactions can be tagged with a project for cost-allocation
///    and P&amp;L reporting.
///
/// The project is a TAG, not a sub-ledger — the COA is unchanged.
/// We add an index on each new project_id column for the bulk
/// allocation lookups and P&amp;L queries.
///
/// Down() reverses all changes. The order matters: drop the indexes
/// before the columns (FluentMigrator handles DROP COLUMN
/// automatically; we drop the indexes explicitly so the migration
/// is symmetric with Up()).
/// </summary>
[Migration(20260807000005)]
public class ProjectCostCenter : Migration
{
    public override void Up()
    {
        // ---------- 1) Extend projects ----------
        if (!Schema.Table("projects").Column("type").Exists())
        {
            Alter.Table("projects")
                .AddColumn("type").AsString(20).Nullable();
        }
        if (!Schema.Table("projects").Column("customer_id").Exists())
        {
            // FK to contacts (Sprint 9). ON DELETE SET NULL so a
            // contact deletion does not cascade-wipe a project.
            Alter.Table("projects")
                .AddColumn("customer_id").AsGuid().Nullable()
                .ForeignKey("contacts", "id").OnDelete(System.Data.Rule.SetNull);
        }
        if (!Schema.Table("projects").Column("contract_value").Exists())
        {
            Alter.Table("projects")
                .AddColumn("contract_value").AsDecimal(18, 3).Nullable();
        }
        if (!Schema.Table("projects").Column("expected_end_date").Exists())
        {
            Alter.Table("projects")
                .AddColumn("expected_end_date").AsDateTime().Nullable();
        }
        if (!Schema.Table("projects").Column("actual_end_date").Exists())
        {
            Alter.Table("projects")
                .AddColumn("actual_end_date").AsDateTime().Nullable();
        }
        if (!Schema.Table("projects").Column("project_manager").Exists())
        {
            Alter.Table("projects")
                .AddColumn("project_manager").AsString(200).Nullable();
        }
        if (!Schema.Table("projects").Column("location").Exists())
        {
            Alter.Table("projects")
                .AddColumn("location").AsString(500).Nullable();
        }
        // Note: `notes` already exists (varchar 500). We re-use the
        // existing column; no need to add a duplicate. The
        // ProjectDto.Notes binding continues to work.

        if (!Schema.Table("projects").Column("updated_at").Exists())
        {
            Alter.Table("projects")
                .AddColumn("updated_at").AsDateTime().Nullable();
        }

        // Default status to 'draft' for new projects (was 'active'
        // before). Existing rows are unaffected — DEFAULT only
        // applies to INSERTs that don't specify status.
        Execute.Sql("ALTER TABLE projects ALTER COLUMN status SET DEFAULT 'draft';");

        if (!Schema.Table("projects").Index("ix_projects_customer").Exists())
        {
            Create.Index("ix_projects_customer").OnTable("projects").OnColumn("customer_id");
        }

        // ---------- 2) Add project_id to invoices ----------
        if (!Schema.Table("invoices").Column("project_id").Exists())
        {
            // ON DELETE SET NULL — a project deletion should not
            // cascade-wipe historical invoices. The P&L history
            // stays intact (with a null project tag).
            Alter.Table("invoices")
                .AddColumn("project_id").AsGuid().Nullable()
                .ForeignKey("projects", "id").OnDelete(System.Data.Rule.SetNull);
        }
        if (!Schema.Table("invoices").Index("ix_invoices_project").Exists())
        {
            Create.Index("ix_invoices_project").OnTable("invoices").OnColumn("project_id");
        }

        // ---------- 3) Add project_id to journal_entries ----------
        if (!Schema.Table("journal_entries").Column("project_id").Exists())
        {
            Alter.Table("journal_entries")
                .AddColumn("project_id").AsGuid().Nullable()
                .ForeignKey("projects", "id").OnDelete(System.Data.Rule.SetNull);
        }
        if (!Schema.Table("journal_entries").Index("ix_je_project").Exists())
        {
            Create.Index("ix_je_project").OnTable("journal_entries").OnColumn("project_id");
        }

        // ---------- 4) Add project_id to payment_vouchers ----------
        if (!Schema.Table("payment_vouchers").Column("project_id").Exists())
        {
            Alter.Table("payment_vouchers")
                .AddColumn("project_id").AsGuid().Nullable()
                .ForeignKey("projects", "id").OnDelete(System.Data.Rule.SetNull);
        }
        if (!Schema.Table("payment_vouchers").Index("ix_payments_project").Exists())
        {
            Create.Index("ix_payments_project").OnTable("payment_vouchers").OnColumn("project_id");
        }

        // ---------- 5) Add project_id to receipt_vouchers ----------
        if (!Schema.Table("receipt_vouchers").Column("project_id").Exists())
        {
            Alter.Table("receipt_vouchers")
                .AddColumn("project_id").AsGuid().Nullable()
                .ForeignKey("projects", "id").OnDelete(System.Data.Rule.SetNull);
        }
        if (!Schema.Table("receipt_vouchers").Index("ix_receipts_project").Exists())
        {
            Create.Index("ix_receipts_project").OnTable("receipt_vouchers").OnColumn("project_id");
        }
    }

    public override void Down()
    {
        // Reverse in opposite order. Drop indexes first, then
        // columns (FluentMigrator's Delete.Column also drops the
        // implicit FK constraint).

        if (Schema.Table("receipt_vouchers").Index("ix_receipts_project").Exists())
        {
            Delete.Index("ix_receipts_project").OnTable("receipt_vouchers");
        }
        if (Schema.Table("receipt_vouchers").Column("project_id").Exists())
        {
            Delete.Column("project_id").FromTable("receipt_vouchers");
        }

        if (Schema.Table("payment_vouchers").Index("ix_payments_project").Exists())
        {
            Delete.Index("ix_payments_project").OnTable("payment_vouchers");
        }
        if (Schema.Table("payment_vouchers").Column("project_id").Exists())
        {
            Delete.Column("project_id").FromTable("payment_vouchers");
        }

        if (Schema.Table("journal_entries").Index("ix_je_project").Exists())
        {
            Delete.Index("ix_je_project").OnTable("journal_entries");
        }
        if (Schema.Table("journal_entries").Column("project_id").Exists())
        {
            Delete.Column("project_id").FromTable("journal_entries");
        }

        if (Schema.Table("invoices").Index("ix_invoices_project").Exists())
        {
            Delete.Index("ix_invoices_project").OnTable("invoices");
        }
        if (Schema.Table("invoices").Column("project_id").Exists())
        {
            Delete.Column("project_id").FromTable("invoices");
        }

        // Restore the projects.status default to 'active' so Down()
        // is symmetric with the original schema. (Up set it to
        // 'draft'.)
        Execute.Sql("ALTER TABLE projects ALTER COLUMN status SET DEFAULT 'active';");

        if (Schema.Table("projects").Index("ix_projects_customer").Exists())
        {
            Delete.Index("ix_projects_customer").OnTable("projects");
        }

        // Drop the new projects columns in reverse order.
        if (Schema.Table("projects").Column("updated_at").Exists())
        {
            Delete.Column("updated_at").FromTable("projects");
        }
        if (Schema.Table("projects").Column("location").Exists())
        {
            Delete.Column("location").FromTable("projects");
        }
        if (Schema.Table("projects").Column("project_manager").Exists())
        {
            Delete.Column("project_manager").FromTable("projects");
        }
        if (Schema.Table("projects").Column("actual_end_date").Exists())
        {
            Delete.Column("actual_end_date").FromTable("projects");
        }
        if (Schema.Table("projects").Column("expected_end_date").Exists())
        {
            Delete.Column("expected_end_date").FromTable("projects");
        }
        if (Schema.Table("projects").Column("contract_value").Exists())
        {
            Delete.Column("contract_value").FromTable("projects");
        }
        if (Schema.Table("projects").Column("customer_id").Exists())
        {
            Delete.Column("customer_id").FromTable("projects");
        }
        if (Schema.Table("projects").Column("type").Exists())
        {
            Delete.Column("type").FromTable("projects");
        }
    }
}
