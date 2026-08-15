using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 54 — Multi-party project support.
///
/// Real construction projects have 4 distinct parties:
///   - Owner (مالك)        — already in projects.customer_id
///   - Contractor (مقاول)   — الجهة المنفذة (was contacts.type='supplier')
///   - Consultant (استشاري) — الجهة المشرفة
///   - Holding (إدارة)      — الشركة القابضة (the company itself)
///
/// This migration:
///   1. Extends contacts.type constraint to allow 'contractor' and
///      'consultant' (in addition to 'customer' / 'supplier').
///   2. Adds projects.contractor_id (FK contacts) — الجهة المنفذة.
///   3. Adds projects.consultant_id (FK contacts) — الجهة المشرفة.
///   4. Adds contracts.site_handover_date — تاريخ استلام الموقع.
///   5. Adds contracts.original_contract_value — القيمة الأصلية قبل
///      الأمر التعديلي (for the 15% tax deduction in Sprint 53).
/// </summary>
[Migration(20260815000029)]
public class MultiPartyProject : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) Extend contacts.type to allow contractor/consultant
        // ============================================================
        // Drop the old check if it exists, then add a new one.
        // Using Execute.Sql because FluentMigrator's constraint API
        // is verbose for this case.
        Execute.Sql(@"
            ALTER TABLE contacts DROP CONSTRAINT IF EXISTS contacts_type_check;
        ");

        Execute.Sql(@"
            ALTER TABLE contacts ADD CONSTRAINT contacts_type_check
            CHECK (type IN ('customer', 'supplier', 'contractor', 'consultant'));
        ");

        // ============================================================
        // 2) projects — 2 new columns
        // ============================================================
        if (!Schema.Table("projects").Column("contractor_id").Exists())
            Alter.Table("projects").AddColumn("contractor_id")
                .AsGuid().Nullable()
                .ForeignKey("contacts", "id");

        if (!Schema.Table("projects").Column("consultant_id").Exists())
            Alter.Table("projects").AddColumn("consultant_id")
                .AsGuid().Nullable()
                .ForeignKey("contacts", "id");

        // ============================================================
        // 3) contracts — 2 new columns
        // ============================================================
        if (!Schema.Table("contracts").Column("site_handover_date").Exists())
            Alter.Table("contracts").AddColumn("site_handover_date")
                .AsDateTime().Nullable();

        if (!Schema.Table("contracts").Column("original_contract_value").Exists())
            Alter.Table("contracts").AddColumn("original_contract_value")
                .AsDecimal(18, 3).Nullable();
    }

    public override void Down()
    {
        if (Schema.Table("contracts").Column("original_contract_value").Exists())
            Delete.Column("original_contract_value").FromTable("contracts");
        if (Schema.Table("contracts").Column("site_handover_date").Exists())
            Delete.Column("site_handover_date").FromTable("contracts");

        if (Schema.Table("projects").Column("consultant_id").Exists())
            Delete.Column("consultant_id").FromTable("projects");
        if (Schema.Table("projects").Column("contractor_id").Exists())
            Delete.Column("contractor_id").FromTable("projects");

        Execute.Sql(@"
            ALTER TABLE contacts DROP CONSTRAINT IF EXISTS contacts_type_check;
        ");
        Execute.Sql(@"
            ALTER TABLE contacts ADD CONSTRAINT contacts_type_check
            CHECK (type IN ('customer', 'supplier'));
        ");
    }
}
