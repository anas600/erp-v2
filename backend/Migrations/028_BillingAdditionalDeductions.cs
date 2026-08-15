using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 53 — Billing additional deductions.
///
/// Adds 3 new deduction fields to mirror the Libyan construction
/// contract settlement model (المستخلص):
///
///   1. final_insurance_percent (2% default in most contracts)
///      Deducted from the gross and held as a liability until the
///      maintenance / warranty period ends (typically 12 months
///      after project delivery). Account 2107-FIN.
///
///   2. admin_fee_percent (1.5% default in our client's contracts)
///      Administrative fees paid to the OWNER (الجهة المالكة) for
///      managing the contract. Account 2108-ADM.
///
///   3. original_contract_deduction (15% of original contract value)
///      One-time deduction applied on the FIRST billing only.
///      It's typically a tax / withholding on the original
///      (pre-variation) contract value. Account 2109-TAX (handled
///      separately — see posting rules in Sprint 57).
///
/// All 6 columns (3 on contracts + 3 on progress_billings) are added
/// with IF NOT EXISTS / column-exists guards so re-running is safe.
/// </summary>
[Migration(20260815000028)]
public class BillingAdditionalDeductions : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) contracts — 3 new columns
        // ============================================================
        if (!Schema.Table("contracts").Column("final_insurance_percent").Exists())
            Alter.Table("contracts").AddColumn("final_insurance_percent")
                .AsDecimal(5, 2).NotNullable().WithDefaultValue(0);

        if (!Schema.Table("contracts").Column("admin_fee_percent").Exists())
            Alter.Table("contracts").AddColumn("admin_fee_percent")
                .AsDecimal(5, 2).NotNullable().WithDefaultValue(0);

        if (!Schema.Table("contracts").Column("final_insurance_release_date").Exists())
            Alter.Table("contracts").AddColumn("final_insurance_release_date")
                .AsDateTime().Nullable();

        // ============================================================
        // 2) progress_billings — 3 new columns
        // ============================================================
        if (!Schema.Table("progress_billings").Column("final_insurance_deducted").Exists())
            Alter.Table("progress_billings").AddColumn("final_insurance_deducted")
                .AsDecimal(18, 3).NotNullable().WithDefaultValue(0);

        if (!Schema.Table("progress_billings").Column("admin_fees_deducted").Exists())
            Alter.Table("progress_billings").AddColumn("admin_fees_deducted")
                .AsDecimal(18, 3).NotNullable().WithDefaultValue(0);

        if (!Schema.Table("progress_billings").Column("original_contract_deduction").Exists())
            Alter.Table("progress_billings").AddColumn("original_contract_deduction")
                .AsDecimal(18, 3).NotNullable().WithDefaultValue(0);
    }

    public override void Down()
    {
        if (Schema.Table("progress_billings").Column("original_contract_deduction").Exists())
            Delete.Column("original_contract_deduction").FromTable("progress_billings");
        if (Schema.Table("progress_billings").Column("admin_fees_deducted").Exists())
            Delete.Column("admin_fees_deducted").FromTable("progress_billings");
        if (Schema.Table("progress_billings").Column("final_insurance_deducted").Exists())
            Delete.Column("final_insurance_deducted").FromTable("progress_billings");

        if (Schema.Table("contracts").Column("final_insurance_release_date").Exists())
            Delete.Column("final_insurance_release_date").FromTable("contracts");
        if (Schema.Table("contracts").Column("admin_fee_percent").Exists())
            Delete.Column("admin_fee_percent").FromTable("contracts");
        if (Schema.Table("contracts").Column("final_insurance_percent").Exists())
            Delete.Column("final_insurance_percent").FromTable("contracts");
    }
}
