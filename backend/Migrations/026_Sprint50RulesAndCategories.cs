using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 50 — adds the 6th posting rule: <c>PurchaseInvoiceApprovedForProject</c>.
///
/// Why a new rule instead of extending PurchaseInvoiceApproved:
///   The existing rule posts to <c>5301 Cost of Goods Sold</c>, which is
///   the right account for inventory purchases. But when a purchase
///   invoice is allocated to a project, the cost belongs in the
///   project's 54xx sub-ledger (e.g. <c>5401-PRJ-005</c>) so it shows
///   up in the project's P&L.
///
///   We keep the original rule for un-tagged invoices and add this new
///   one for project-tagged ones. <c>InvoiceService.PostAsync</c>
///   decides which event to fire based on whether
///   <c>invoice.projectId</c> is null.
///
/// The rule:
///   - Fires only on <c>PurchaseInvoiceApprovedForProject</c>
///   - Condition: <c>invoice.type == 'purchase'</c> AND
///                <c>invoice.projectId != null</c>
///   - Action: create a PENDING journal entry with one debit per
///     invoice line (using <c>accountFrom: "line.accountCode"</c> to
///     read each line's account code from the payload), one debit for
///     input VAT, and one credit for the supplier's sub-ledger.
///   - <c>projectFrom: "invoice.projectId"</c> stamps the project id
///     on the resulting <c>journal_entries.project_id</c>.
///
/// Also: ensures the 5 existing rules are enabled. Migration 023
/// (Sprint 40) had disabled them in favour of direct posting; the
/// project-aware flows now rely on the rules engine, so they must be
/// re-enabled.
/// </summary>
[Migration(20260814000026)]
public class Sprint50RulesAndCategories : Migration
{
    public override void Up()
    {
        // ---------- 1) Re-enable the 5 original rules ----------
        // Migration 023 (Sprint 40) had disabled all auto-posting rules
        // because they were posting to L3 control accounts instead of
        // L4 sub-ledgers. Sprint 45 fixed that (sub-ledger resolution)
        // but never re-enabled the rules. With Sprint 50's new
        // PurchaseInvoiceApprovedForProject rule, the rules engine is
        // the source of truth for ALL posting. Re-enable them all.
        Execute.Sql(@"
            UPDATE business_rules
            SET enabled = true,
                description = CASE
                    WHEN description LIKE '%[disabled by Sprint 40%' THEN
                        REGEXP_REPLACE(description, ' \[disabled by Sprint 40 — manual posting\]', '', 'g')
                    ELSE description
                END,
                updated_at = NOW()
            WHERE enabled = false
              AND event_name IN (
                  'PurchaseInvoiceApproved',
                  'SalesInvoiceApproved',
                  'CustomerReceiptReceived',
                  'SupplierPaymentMade',
                  'ProjectBillingIssued'
              );
        ");

        // ---------- 2) Insert the 6th rule ----------
        // Build the rule JSON via string.Format so we don't have to
        // fight with verbatim-string escape rules. The JSON itself
        // has no single quotes, so we use regular C# string escapes.
        var ruleJson = string.Format(
            "{{" +
            "\"conditions\":{{\"all\":[" +
            "{{\"field\":\"invoice.type\",\"op\":\"==\",\"value\":\"purchase\"}}," +
            "{{\"field\":\"invoice.projectId\",\"op\":\"!=\",\"value\":null}}" +
            "]}}," +
            "\"actions\":[{{" +
            "\"type\":\"PostJournalEntry\"," +
            "\"projectFrom\":\"invoice.projectId\"," +
            "\"narration\":\"فاتورة مشتريات {invoice.number} - مشروع {project.name} - {supplier.name}\"," +
            "\"lines\":[" +
            "{{\"nature\":\"debit\",\"accountFrom\":\"line.accountCode\",\"amountFormula\":\"line.amount\",\"description\":\"{line.description} - {project.name}\"}}," +
            "{{\"nature\":\"debit\",\"accountCode\":\"1107\",\"amountFormula\":\"invoice.tax\",\"description\":\"ضريبة مدخلات - {project.name}\"}}," +
            "{{\"nature\":\"credit\",\"accountFrom\":\"contact.subLedger\",\"amountFormula\":\"invoice.total\",\"description\":\"دائنون - {supplier.name}\"}}" +
            "]" +
            "}}]" +
            "}}"
        );

        // Idempotent insert: if the 6th rule already exists, skip.
        // We use a separate INSERT ... WHERE NOT EXISTS to keep the
        // SQL simple.
        var insertRuleSql = string.Format(@"
            INSERT INTO business_rules (
                id, name, description, event_name, enabled, priority, rule_json, is_template, created_at, updated_at
            )
            SELECT
                gen_random_uuid(),
                'Purchase Invoice for Project (sub-ledger aware)',
                'Sprint 50 — when a purchase invoice is allocated to a project, post: one debit per line to the project''s 54xx sub-ledger, debit input VAT, credit supplier sub-ledger. Stamps the project id on the resulting journal entry so the project P&L can find it.',
                'PurchaseInvoiceApprovedForProject',
                true,
                5,
                '{0}'::jsonb,
                true,
                NOW(),
                NOW()
            WHERE NOT EXISTS (
                SELECT 1 FROM business_rules
                WHERE event_name = 'PurchaseInvoiceApprovedForProject'
                  AND name = 'Purchase Invoice for Project (sub-ledger aware)'
            );
        ", ruleJson.Replace("'", "''"));  // SQL string-escape

        Execute.Sql(insertRuleSql);

        // ---------- 3) Update the existing PurchaseInvoiceApproved rule ----------
        // Make it skip project-tagged invoices (so the new
        // PurchaseInvoiceApprovedForProject handles those instead).
        // We add a second condition: projectId must be null.
        // The conditions array is replaced atomically.
        var oldConditions = "[{\"field\":\"invoice.type\",\"op\":\"==\",\"value\":\"purchase\"}]";
        var newConditions = "[{\"field\":\"invoice.type\",\"op\":\"==\",\"value\":\"purchase\"},{\"field\":\"invoice.projectId\",\"op\":\"==\",\"value\":null}]";
        var updateSql = string.Format(@"
            UPDATE business_rules
            SET rule_json = jsonb_set(
                rule_json,
                '{{conditions,all}}',
                '{0}'::jsonb,
                false
            ),
            description = COALESCE(description, '') || ' [Sprint 50: skip if projectId is set — the project rule handles those.]',
            updated_at = NOW()
            WHERE event_name = 'PurchaseInvoiceApproved'
              AND rule_json->'conditions'->>'all' <> '{1}';
        ", newConditions.Replace("'", "''"), oldConditions.Replace("'", "''"));

        Execute.Sql(updateSql);

        // ---------- 4) Seed product categories ----------
        // 15 demo products with their category + default L3 account.
        // Done as a single UPDATE ... FROM (VALUES ...) statement.
        Execute.Sql(@"
            UPDATE products p SET
                category = m.category,
                default_account_id = m.account_id
            FROM (VALUES
                ('P001', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P002', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P003', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P004', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P005', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P006', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P007', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P008', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P009', 'materials'::text,        (SELECT id FROM accounts WHERE code = '5401' AND level = 3 LIMIT 1)),
                ('P010', 'equipment_rental'::text, (SELECT id FROM accounts WHERE code = '5404' AND level = 3 LIMIT 1)),
                ('P011', 'overhead'::text,         (SELECT id FROM accounts WHERE code = '5405' AND level = 3 LIMIT 1)),
                ('P012', 'overhead'::text,         (SELECT id FROM accounts WHERE code = '5405' AND level = 3 LIMIT 1)),
                ('P013', 'overhead'::text,         (SELECT id FROM accounts WHERE code = '5405' AND level = 3 LIMIT 1)),
                ('P014', 'overhead'::text,         (SELECT id FROM accounts WHERE code = '5405' AND level = 3 LIMIT 1)),
                ('P015', 'other'::text,            (SELECT id FROM accounts WHERE code = '5407' AND level = 3 LIMIT 1))
            ) AS m(code, category, account_id)
            WHERE p.code = m.code;
        ");
    }

    public override void Down()
    {
        // Remove the 6th rule
        Execute.Sql(@"
            DELETE FROM business_rules
            WHERE event_name = 'PurchaseInvoiceApprovedForProject'
              AND name = 'Purchase Invoice for Project (sub-ledger aware)';
        ");

        // Restore the original PurchaseInvoiceApproved rule conditions
        // (single condition on invoice.type, no project check)
        var restoreRuleJson = @"[{""field"":""invoice.type"",""op"":""=="",""value"":""purchase""}]";
        var restoreSql = string.Format(@"
            UPDATE business_rules
            SET rule_json = jsonb_set(
                rule_json,
                '{{conditions,all}}',
                '{0}'::jsonb,
                false
            ),
            updated_at = NOW()
            WHERE event_name = 'PurchaseInvoiceApproved';
        ", restoreRuleJson.Replace("'", "''"));
        Execute.Sql(restoreSql);

        // Clear product categories
        Execute.Sql("UPDATE products SET category = NULL, default_account_id = NULL;");
    }
}
