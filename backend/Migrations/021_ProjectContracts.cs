using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 36 — Contracting workflow.
///
/// Adds two new tables that power the project-billing lifecycle:
///
///   <c>contracts</c>
///     One row per project (enforced by UNIQUE (company_id, project_id)).
///     Holds the negotiated terms: total contract value, advance
///     percentage, retention percentage, and which billing number starts
///     retaining. The contract is the anchor for every progress
///     billing — no billing can be created without a contract.
///
///   <c>progress_billings</c>
///     One row per billing (مستخلص). The DRAFT → INVOICED → CANCELLED
///     lifecycle is driven by BillingService.ApproveAsync. The invoice
///     and journal entry created on approval are back-linked via
///     <c>invoice_id</c> and <c>journal_entry_id</c> so the user can
///     drill from a billing to the resulting accounting documents.
///
/// The migration is fully idempotent: every column / table / index is
/// guarded by <c>Schema.Table().Column().Exists()</c> /
/// <c>Schema.Table().Table().Exists()</c> / <c>Schema.Table().Index().Exists()</c>.
/// Re-running it is a no-op.
/// </summary>
[Migration(20260807000006)]
public class ProjectContracts : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) contracts
        // ============================================================
        if (!Schema.Table("contracts").Exists())
        {
            Create.Table("contracts")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("project_id").AsGuid().NotNullable()
                    .ForeignKey("projects", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("contract_number").AsString(50).Nullable()
                .WithColumn("contract_value").AsDecimal(18, 3).NotNullable()
                .WithColumn("advance_percent").AsDecimal(5, 2).NotNullable().WithDefaultValue(0)
                .WithColumn("retention_percent").AsDecimal(5, 2).NotNullable().WithDefaultValue(0)
                .WithColumn("retention_start_billing").AsInt32().NotNullable().WithDefaultValue(1)
                .WithColumn("start_date").AsDateTime().Nullable()
                .WithColumn("end_date").AsDateTime().Nullable()
                .WithColumn("notes").AsString(int.MaxValue).Nullable()
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
                .WithColumn("updated_at").AsDateTime().Nullable();

            // One contract per project (per company). Mirrors the
            // "1 contract per engagement" business rule. If a project
            // needs a new contract, the user must end the old one and
            // create a new row — but for now the unique constraint
            // makes the data model self-enforcing.
            Create.Index("ux_contracts_company_project")
                .OnTable("contracts")
                .OnColumn("company_id").Ascending()
                .OnColumn("project_id").Ascending()
                .WithOptions().Unique();
        }
        else
        {
            // Table exists — apply forward-compatible column adds so
            // older DBs that ran a partial Sprint 36 migration still
            // pick up new fields.
            if (!Schema.Table("contracts").Column("contract_number").Exists())
                Alter.Table("contracts").AddColumn("contract_number").AsString(50).Nullable();
            if (!Schema.Table("contracts").Column("advance_percent").Exists())
                Alter.Table("contracts").AddColumn("advance_percent").AsDecimal(5, 2).NotNullable().WithDefaultValue(0);
            if (!Schema.Table("contracts").Column("retention_percent").Exists())
                Alter.Table("contracts").AddColumn("retention_percent").AsDecimal(5, 2).NotNullable().WithDefaultValue(0);
            if (!Schema.Table("contracts").Column("retention_start_billing").Exists())
                Alter.Table("contracts").AddColumn("retention_start_billing").AsInt32().NotNullable().WithDefaultValue(1);
            if (!Schema.Table("contracts").Column("start_date").Exists())
                Alter.Table("contracts").AddColumn("start_date").AsDateTime().Nullable();
            if (!Schema.Table("contracts").Column("end_date").Exists())
                Alter.Table("contracts").AddColumn("end_date").AsDateTime().Nullable();
            if (!Schema.Table("contracts").Column("notes").Exists())
                Alter.Table("contracts").AddColumn("notes").AsString(int.MaxValue).Nullable();
            if (!Schema.Table("contracts").Column("updated_at").Exists())
                Alter.Table("contracts").AddColumn("updated_at").AsDateTime().Nullable();
        }

        // Index for the most common access pattern: "get the contract
        // for this project" (covered by the unique index, but we add
        // a plain project_id index so the reverse "list contracts in
        // this project across companies" stays fast too).
        if (!Schema.Table("contracts").Index("ix_contracts_project").Exists())
        {
            Create.Index("ix_contracts_project").OnTable("contracts").OnColumn("project_id");
        }

        // ============================================================
        // 2) progress_billings
        // ============================================================
        if (!Schema.Table("progress_billings").Exists())
        {
            Create.Table("progress_billings")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("project_id").AsGuid().NotNullable()
                    .ForeignKey("projects", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("contract_id").AsGuid().NotNullable()
                    .ForeignKey("contracts", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("billing_number").AsString(50).NotNullable()
                .WithColumn("billing_date").AsDateTime().NotNullable()
                .WithColumn("period_from").AsDateTime().Nullable()
                .WithColumn("period_to").AsDateTime().Nullable()
                .WithColumn("work_completed_percent").AsDecimal(5, 2).NotNullable()
                .WithColumn("gross_amount").AsDecimal(18, 3).NotNullable()
                .WithColumn("advance_deducted").AsDecimal(18, 3).NotNullable().WithDefaultValue(0)
                .WithColumn("retention_deducted").AsDecimal(18, 3).NotNullable().WithDefaultValue(0)
                .WithColumn("net_amount").AsDecimal(18, 3).NotNullable()
                // DRAFT (editable) → INVOICED (after approve) → CANCELLED
                .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("DRAFT")
                .WithColumn("invoice_id").AsGuid().Nullable()
                    .ForeignKey("invoices", "id")
                .WithColumn("journal_entry_id").AsGuid().Nullable()
                    .ForeignKey("journal_entries", "id")
                .WithColumn("notes").AsString(int.MaxValue).Nullable()
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
                .WithColumn("updated_at").AsDateTime().Nullable();

            // billing_number is unique per company (so two companies
            // can each have their own "B-2026-0001" without colliding).
            Create.Index("ux_progress_billings_company_billing_number")
                .OnTable("progress_billings")
                .OnColumn("company_id").Ascending()
                .OnColumn("billing_number").Ascending()
                .WithOptions().Unique();
        }
        else
        {
            // Forward-compat: pick up any fields added in later
            // revisions of this migration.
            if (!Schema.Table("progress_billings").Column("period_from").Exists())
                Alter.Table("progress_billings").AddColumn("period_from").AsDateTime().Nullable();
            if (!Schema.Table("progress_billings").Column("period_to").Exists())
                Alter.Table("progress_billings").AddColumn("period_to").AsDateTime().Nullable();
            if (!Schema.Table("progress_billings").Column("advance_deducted").Exists())
                Alter.Table("progress_billings").AddColumn("advance_deducted").AsDecimal(18, 3).NotNullable().WithDefaultValue(0);
            if (!Schema.Table("progress_billings").Column("retention_deducted").Exists())
                Alter.Table("progress_billings").AddColumn("retention_deducted").AsDecimal(18, 3).NotNullable().WithDefaultValue(0);
            if (!Schema.Table("progress_billings").Column("notes").Exists())
                Alter.Table("progress_billings").AddColumn("notes").AsString(int.MaxValue).Nullable();
            if (!Schema.Table("progress_billings").Column("updated_at").Exists())
                Alter.Table("progress_billings").AddColumn("updated_at").AsDateTime().Nullable();
        }

        if (!Schema.Table("progress_billings").Index("ix_billings_project").Exists())
        {
            Create.Index("ix_billings_project").OnTable("progress_billings").OnColumn("project_id");
        }
        if (!Schema.Table("progress_billings").Index("ix_billings_contract").Exists())
        {
            Create.Index("ix_billings_contract").OnTable("progress_billings").OnColumn("contract_id");
        }
        if (!Schema.Table("progress_billings").Index("ix_billings_status").Exists())
        {
            Create.Index("ix_billings_status").OnTable("progress_billings").OnColumn("status");
        }
    }

    public override void Down()
    {
        // Reverse in opposite order: drop indexes, then tables.
        if (Schema.Table("progress_billings").Index("ix_billings_status").Exists())
            Delete.Index("ix_billings_status").OnTable("progress_billings");
        if (Schema.Table("progress_billings").Index("ix_billings_contract").Exists())
            Delete.Index("ix_billings_contract").OnTable("progress_billings");
        if (Schema.Table("progress_billings").Index("ix_billings_project").Exists())
            Delete.Index("ix_billings_project").OnTable("progress_billings");
        if (Schema.Table("progress_billings").Index("ux_progress_billings_company_billing_number").Exists())
            Delete.Index("ux_progress_billings_company_billing_number").OnTable("progress_billings");

        if (Schema.Table("progress_billings").Exists())
            Delete.Table("progress_billings");

        if (Schema.Table("contracts").Index("ix_contracts_project").Exists())
            Delete.Index("ix_contracts_project").OnTable("contracts");
        if (Schema.Table("contracts").Index("ux_contracts_company_project").Exists())
            Delete.Index("ux_contracts_company_project").OnTable("contracts");

        if (Schema.Table("contracts").Exists())
            Delete.Table("contracts");
    }
}
