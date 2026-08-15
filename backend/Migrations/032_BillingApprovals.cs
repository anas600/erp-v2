using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 57 — 4-party approval workflow for progress billings.
///
/// Per the Libyan construction contract model (Excel
/// `99fbbb98__*.xlsx`), every progress billing goes through 4
/// approval stages before payment:
///
///   1. المقاول    (contractor) — submits the billing
///   2. الاستشاري  (consultant) — certifies the work done
///   3. إدارة المشروعات (pmo / holding) — verifies contract compliance
///   4. المالك     (owner / government) — final approval for payment
///
/// Each role gets ONE row per billing in `progress_billing_approvals`.
/// The unique index on (billing_id, role) prevents duplicates.
/// Once all 4 are 'approved', the billing's `final_approved_at`
/// column is set (by the application code, not a trigger — we want
/// the user's transaction to control the write).
///
/// Migration is fully idempotent (IF NOT EXISTS guards on every
/// DDL) so re-running on Render is safe.
/// </summary>
[Migration(20260815000032)]
public class BillingApprovals : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) progress_billing_approvals — the 4 rows per billing
        // ============================================================
        if (!Schema.Table("progress_billing_approvals").Exists())
        {
            Create.Table("progress_billing_approvals")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("billing_id").AsGuid().NotNullable()
                    .ForeignKey("progress_billings", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("role").AsString(20).NotNullable()
                // Values: contractor | consultant | pmo | owner
                .WithColumn("approver_user_id").AsGuid().Nullable()
                    .ForeignKey("users", "id")
                .WithColumn("approver_name").AsString(200).Nullable()
                .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("pending")
                // Values: pending | approved | rejected
                .WithColumn("approved_at").AsDateTime().Nullable()
                .WithColumn("rejection_reason").AsString(int.MaxValue).Nullable()
                .WithColumn("notes").AsString(int.MaxValue).Nullable()
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
                .WithColumn("updated_at").AsDateTime().Nullable();

            // One row per (billing, role) — prevents accidental duplicates
            Create.Index("ux_billing_approvals_billing_role")
                .OnTable("progress_billing_approvals")
                .OnColumn("billing_id").Ascending()
                .OnColumn("role").Ascending()
                .WithOptions().Unique();

            Create.Index("ix_billing_approvals_company")
                .OnTable("progress_billing_approvals")
                .OnColumn("company_id");
        }

        // ============================================================
        // 2) progress_billings — add final_approved_at
        // ============================================================
        if (!Schema.Table("progress_billings").Column("final_approved_at").Exists())
            Alter.Table("progress_billings").AddColumn("final_approved_at")
                .AsDateTime().Nullable();
    }

    public override void Down()
    {
        if (Schema.Table("progress_billings").Column("final_approved_at").Exists())
            Delete.Column("final_approved_at").FromTable("progress_billings");

        if (Schema.Table("progress_billing_approvals").Exists())
            Delete.Table("progress_billing_approvals");
    }
}
