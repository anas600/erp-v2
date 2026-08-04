using Dapper;
using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 15 follow-up: fixes the sales-invoice posting rule to use
/// proper 3-line accounting (DR AR / CR Revenue / CR VAT Payable),
/// and adds the missing VAT Payable account.
///
/// BEFORE this migration, the sales rule produced:
///   Debit  1200 (AR)       = invoice.total
///   Credit 4000 (Revenue)  = invoice.total
///
/// PROBLEM: revenue was overstated by the tax amount, and the VAT
/// collected from the customer was silently merged into revenue.
/// For a 1000 LYD invoice with 15% tax (total = 1150):
///   - Revenue was recorded as 1150 instead of 1000
///   - The 150 LYD VAT collected was "lost" — never tracked as a
///     separate liability to remit to the tax authority
///
/// AFTER this migration, the rule produces:
///   Debit  1200 (AR)               = invoice.total   (gross, what we owe to collect)
///   Credit 4000 (Sales Revenue)    = invoice.subtotal (net, the actual earned revenue)
///   Credit 2200 (VAT Payable)      = invoice.tax     (tax we owe to the government)
///
/// This requires a new account 2200 (VAT Payable). We add it here
/// to all 3 companies. The existing chart of accounts has 2100
/// (Loans Payable) but no VAT account — this is a real gap.
///
/// Idempotency:
///   - 2200 account: ON CONFLICT (company_id, code) DO NOTHING
///   - Rule update: re-applies the same UPDATE on every run
/// </summary>
[Migration(20260804000009)]
public class VatAndSalesRuleFix : Migration
{
    public override void Up()
    {
        // 1) Add the 2200 (VAT Payable) account to every company.
        //    Same pattern as 002_SeedData.accounts seed.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=db;Port=5432;Database=erp;Username=erp;Password=erp_secret";

        using (var conn = new Npgsql.NpgsqlConnection(connectionString))
        {
            conn.Open();

            var companyIds = conn.Query<Guid>("SELECT id FROM companies;").ToList();
            if (companyIds.Count == 0)
            {
                // 002 seed didn't run? bail out cleanly.
                return;
            }

            foreach (var companyId in companyIds)
            {
                conn.Execute(@"
                    INSERT INTO accounts (company_id, code, name, name_ar, account_type, nature, is_active, balance)
                    VALUES (@companyId, '2200', 'VAT Payable', 'ضريبة مخرجات مستحقة', 'Liability', 'Credit', true, 0)
                    ON CONFLICT (company_id, code) DO NOTHING;",
                    new { companyId });
            }
        }

        // 2) Update the sales rule to use 3 lines. The migration runs
        //    on every deploy, so a re-apply is safe (same effect).
        Execute.Sql(@"
            UPDATE business_rules
            SET rule_json = '{
              ""actions"": [
                {
                  ""type"": ""PostJournalEntry"",
                  ""lines"": [
                    {
                      ""nature"": ""debit"",
                      ""accountCode"": ""1200"",
                      ""description"": ""مدينون - {customer.name}"",
                      ""amountFormula"": ""invoice.total""
                    },
                    {
                      ""nature"": ""credit"",
                      ""accountCode"": ""4000"",
                      ""description"": ""إيرادات المبيعات - {customer.name}"",
                      ""amountFormula"": ""invoice.subtotal""
                    },
                    {
                      ""nature"": ""credit"",
                      ""accountCode"": ""2200"",
                      ""description"": ""ضريبة مخرجات مستحقة - INV {invoice.number}"",
                      ""amountFormula"": ""invoice.tax""
                    }
                  ],
                  ""narration"": ""فاتورة مبيعات رقم {invoice.number} - {customer.name}""
                }
              ],
              ""conditions"": { ""all"": [] }
            }'::jsonb,
            updated_at = NOW()
            WHERE name = 'ترحيل فاتورة مبيعات'
              AND event_name = 'SalesInvoiceApproved';
        ");

        // 3) Same fix for the purchase rule — should also use 3 lines:
        //    DR COGS (subtotal) + DR Input VAT (tax) + CR AP (total).
        //    Add account 1400 (Input VAT Receivable) on the way.
        using (var conn = new Npgsql.NpgsqlConnection(connectionString))
        {
            conn.Open();
            var companyIds = conn.Query<Guid>("SELECT id FROM companies;").ToList();
            foreach (var companyId in companyIds)
            {
                conn.Execute(@"
                    INSERT INTO accounts (company_id, code, name, name_ar, account_type, nature, is_active, balance)
                    VALUES (@companyId, '1400', 'Input VAT Receivable', 'ضريبة مدخلات قابلة للاسترداد', 'Asset', 'Debit', true, 0)
                    ON CONFLICT (company_id, code) DO NOTHING;",
                    new { companyId });
            }
        }

        Execute.Sql(@"
            UPDATE business_rules
            SET rule_json = '{
              ""actions"": [
                {
                  ""type"": ""PostJournalEntry"",
                  ""lines"": [
                    {
                      ""nature"": ""debit"",
                      ""accountCode"": ""5000"",
                      ""description"": ""تكلفة المشتريات - {supplier.name}"",
                      ""amountFormula"": ""invoice.subtotal""
                    },
                    {
                      ""nature"": ""debit"",
                      ""accountCode"": ""1400"",
                      ""description"": ""ضريبة مدخلات - INV {invoice.number}"",
                      ""amountFormula"": ""invoice.tax""
                    },
                    {
                      ""nature"": ""credit"",
                      ""accountCode"": ""2000"",
                      ""description"": ""دائنون - {supplier.name}"",
                      ""amountFormula"": ""invoice.total""
                    }
                  ],
                  ""narration"": ""فاتورة مشتريات رقم {invoice.number} - {supplier.name}""
                }
              ],
              ""conditions"": { ""all"": [] }
            }'::jsonb,
            updated_at = NOW()
            WHERE name = 'ترحيل فاتورة مشتريات'
              AND event_name = 'PurchaseInvoiceApproved';
        ");
    }

    public override void Down()
    {
        // Forward-only: rolling back would re-introduce the broken
        // 2-line rule. A human can edit rules from the Business Rules
        // page if needed.
    }
}
