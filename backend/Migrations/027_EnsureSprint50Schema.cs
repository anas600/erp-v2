using FluentMigrator;
using Dapper;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 50 — Safety-net migration. Ensures the Sprint 50 schema
/// changes from migrations 024/025/026 are present even if those
/// migrations didn't run (which happened on Render — the deploy
/// pipeline stopped at migration 023).
///
/// This migration is fully idempotent: every step uses IF NOT EXISTS,
/// OR REPLACE, or WHERE NOT EXISTS so re-running is a no-op. We
/// deliberately avoid FluentMigrator's Schema.Table(...).Column(...).Exists()
/// pattern because it requires an open DbConnection during the
/// migration which complicates transactions.
///
/// After this migration, the system has:
///   1. products.category (VARCHAR 50, nullable)
///   2. products.default_account_id (UUID, nullable)
///   3. ix_products_category, ix_products_default_account indexes
///   4. get_project_cost_l3_codes() helper function
///   5. 15 demo products seeded with category + default_account_id
///   6. The 5 original rules re-enabled
///   7. The 6th rule 'Purchase Invoice for Project' inserted
///   8. PurchaseInvoiceApproved rule updated to skip project-tagged
///      invoices
/// </summary>
[Migration(20260814000027)]
public class EnsureSprint50Schema : Migration
{
    public override void Up()
    {
        // ---- 1) Products table: add category + default_account_id ----
        // ADD COLUMN IF NOT EXISTS (Postgres 9.6+) — safe to re-run.
        Execute.Sql(@"
            ALTER TABLE products ADD COLUMN IF NOT EXISTS category VARCHAR(50);
        ");

        Execute.Sql(@"
            ALTER TABLE products ADD COLUMN IF NOT EXISTS default_account_id UUID;
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_products_category
                ON products (category);
        ");

        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_products_default_account
                ON products (default_account_id);
        ");

        // ---- 2) Helper function for project cost L3 codes ----
        Execute.Sql(@"
            CREATE OR REPLACE FUNCTION get_project_cost_l3_codes()
            RETURNS TABLE(code VARCHAR, name_ar VARCHAR) AS $$
            BEGIN
                RETURN QUERY
                SELECT '5401'::VARCHAR, 'Project Materials'::VARCHAR
                UNION ALL SELECT '5402', 'Project Labor'
                UNION ALL SELECT '5403', 'Project Subcontractors'
                UNION ALL SELECT '5404', 'Project Equipment Rental'
                UNION ALL SELECT '5405', 'Project Overhead Allocation'
                UNION ALL SELECT '5406', 'Project Transportation'
                UNION ALL SELECT '5407', 'Project Other Costs';
            END;
            $$ LANGUAGE plpgsql STABLE;
        ");

        // ---- 3) Re-enable the 5 original rules (in case 026 didn't run) ----
        Execute.Sql(@"
            UPDATE business_rules
            SET enabled = true,
                description = CASE
                    WHEN description LIKE '%[disabled by Sprint 40%' THEN
                        REGEXP_REPLACE(description, ' \[disabled by Sprint 40 — manual posting\]', '', 'g')
                    ELSE description
                END,
                updated_at = NOW()
            WHERE event_name IN (
                'PurchaseInvoiceApproved',
                'SalesInvoiceApproved',
                'CustomerReceiptReceived',
                'SupplierPaymentMade',
                'ProjectBillingIssued'
            )
            AND enabled = false;
        ");

        // ---- 4) Insert the 6th rule (idempotent) ----
        // Build the JSON via string concatenation to avoid the
        // C# verbatim-string + JSON quote escape mess.
        var ruleJson =
            "{" +
            "\"conditions\":{\"all\":[" +
            "{\"field\":\"invoice.type\",\"op\":\"==\",\"value\":\"purchase\"}," +
            "{\"field\":\"invoice.projectId\",\"op\":\"!=\",\"value\":null}" +
            "]}," +
            "\"actions\":[{" +
            "\"type\":\"PostJournalEntry\"," +
            "\"projectFrom\":\"invoice.projectId\"," +
            "\"narration\":\"فاتورة مشتريات {invoice.number} - مشروع {project.name} - {supplier.name}\"," +
            "\"lines\":[" +
            "{\"nature\":\"debit\",\"accountFrom\":\"line.accountCode\",\"amountFormula\":\"line.amount\",\"description\":\"{line.description} - {project.name}\"}," +
            "{\"nature\":\"debit\",\"accountCode\":\"1107\",\"amountFormula\":\"invoice.tax\",\"description\":\"ضريبة مدخلات - {project.name}\"}," +
            "{\"nature\":\"credit\",\"accountFrom\":\"contact.subLedger\",\"amountFormula\":\"invoice.total\",\"description\":\"دائنون - {supplier.name}\"}" +
            "]" +
            "}]" +
            "}";

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
        ", ruleJson.Replace("'", "''"));

        Execute.Sql(insertRuleSql);

        // ---- 5) Update PurchaseInvoiceApproved to skip project-tagged ----
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
              AND rule_json->'conditions'->>'all' <> '{0}';
        ", newConditions.Replace("'", "''"));

        Execute.Sql(updateSql);

        // ---- 6) Seed product categories (idempotent) ----
        // UPDATE ... FROM (VALUES ...) updates existing products'
        // category + default_account_id. New products added later
        // won't have categories unless the user sets them in the UI.
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
            WHERE p.code = m.code
              AND (p.category IS NULL OR p.category <> m.category);
        ");
    }

    public override void Down()
    {
        // Reverse in opposite order
        Execute.Sql(@"
            DELETE FROM business_rules
            WHERE event_name = 'PurchaseInvoiceApprovedForProject'
              AND name = 'Purchase Invoice for Project (sub-ledger aware)';
        ");

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

        Execute.Sql("UPDATE products SET category = NULL, default_account_id = NULL;");
        Execute.Sql("DROP INDEX IF EXISTS ix_products_default_account;");
        Execute.Sql("DROP INDEX IF EXISTS ix_products_category;");
        Execute.Sql("ALTER TABLE products DROP COLUMN IF EXISTS default_account_id;");
        Execute.Sql("ALTER TABLE products DROP COLUMN IF EXISTS category;");
        Execute.Sql("DROP FUNCTION IF EXISTS get_project_cost_l3_codes();");
    }
}
