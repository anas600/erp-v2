using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Fixes the two invoice-posting business rules that were silently
/// broken on the demo deploy (Sprint 14 verification).
///
/// Two bugs, one per rule:
///
///   1. Sales rule ("ترحيل فاتورة مبيعات"):
///      The legacy template in 002 used `{customer.name}` in
///      descriptions, but the InvoiceService payload only exposed
///      `party.name` — substitution produced "" (empty) and the
///      description was wrong. Fix: payload now exposes customer/
///      supplier/party as aliases (see InvoiceService.PostAsync).
///
///   2. Purchase rule ("ترحيل فاتورة مشتريات"):
///      The amount formulas created an UNBALANCED journal entry:
///
///        Debit  5000 (COGS)        = invoice.total - invoice.tax   (e.g. 1000)
///        Credit 2000 (Accounts Payable) = invoice.total              (e.g. 1150)
///
///      The 150 LYD difference (the tax) had no offsetting line, so
///      PostingEngine.PostAsync threw "Debits != Credits" — caught
///      silently by RuleEvaluator, leaving the invoice marked as
///      "posted" but with no journal entry. Users had to create
///      the entry by hand.
///
///      Fix: use `invoice.total` for BOTH lines (treat COGS as
///      gross-of-tax for MVP). This is balanced and matches the
///      simple template the user expects. A future Sprint can add
///      proper input-VAT / output-VAT accounts (1400 / 2200) and
///      split subtotal vs tax.
///
/// This migration is idempotent: it only UPDATEs the two rules
/// matching the seeded names. Re-running is safe (same effect).
/// </summary>
[Migration(20260803000006)]
public class FixInvoicePostingRules : Migration
{
    public override void Up()
    {
        // Sales invoice rule: balanced 2-line entry
        //   Debit  1200 AR    = invoice.total
        //   Credit 4000 Rev   = invoice.total
        // (gross-of-tax for MVP; see Sprint 15+ for VAT split)
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
                      ""amountFormula"": ""invoice.total""
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

        // Purchase invoice rule: balanced 2-line entry
        //   Debit  5000 COGS  = invoice.total
        //   Credit 2000 AP    = invoice.total
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
                      ""amountFormula"": ""invoice.total""
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
        // Forward-only: rolling back would re-introduce the unbalanced
        // formula. A human can edit the rules from the Business Rules
        // page if needed.
    }
}
