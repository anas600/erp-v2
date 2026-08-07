using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 32 — updates the default business rules to use the new
/// standard COA account codes introduced in Sprint 31.
///
/// Old → New mapping:
///   1000 (Cash)              → 1101 (Cash)
///   1200 (AR)                → 1103 (Accounts Receivable)
///   2000 (AP)                → 2101 (Accounts Payable)
///   4000 (Revenue)           → 4101 (Sales of Goods)
///   5000 (COGS)              → 5301 (Cost of Goods Sold)
///
/// The rule_json column is jsonb — we use a single UPDATE with jsonb_set
/// to update every accountCode reference in every rule. Cleaner than
/// 50 separate string-replace operations.
/// </summary>
[Migration(20260807000003)]
public class UpdateRuleAccountCodes : Migration
{
    public override void Up()
    {
        // The pattern is: every accountCode reference in the rules'
        // jsonb uses these old codes. We replace them with the new
        // standard codes. The operation is idempotent — if the
        // codes are already new, the jsonb_set is a no-op.
        //
        // We use jsonb_set to surgically replace the accountCode
        // field in each line, preserving the rest of the structure.
        Execute.Sql(@"
            UPDATE business_rules
            SET rule_json = jsonb_set(
                rule_json,
                '{actions,0,lines}',
                (
                    SELECT jsonb_agg(
                        jsonb_set(line, '{accountCode}',
                            CASE line->>'accountCode'
                                WHEN '1000' THEN '""1101""'::jsonb
                                WHEN '1200' THEN '""1103""'::jsonb
                                WHEN '2000' THEN '""2101""'::jsonb
                                WHEN '4000' THEN '""4101""'::jsonb
                                WHEN '5000' THEN '""5301""'::jsonb
                                ELSE line->'accountCode'
                            END)
                    )
                    FROM jsonb_array_elements(rule_json->'actions'->0->'lines') AS line
                )
            )
            WHERE rule_json->'actions'->0->'lines' IS NOT NULL;");
    }

    public override void Down()
    {
        Execute.Sql(@"
            UPDATE business_rules
            SET rule_json = jsonb_set(
                rule_json,
                '{actions,0,lines}',
                (
                    SELECT jsonb_agg(
                        jsonb_set(line, '{accountCode}',
                            CASE line->>'accountCode'
                                WHEN '1101' THEN '""1000""'::jsonb
                                WHEN '1103' THEN '""1200""'::jsonb
                                WHEN '2101' THEN '""2000""'::jsonb
                                WHEN '4101' THEN '""4000""'::jsonb
                                WHEN '5301' THEN '""5000""'::jsonb
                                ELSE line->'accountCode'
                            END)
                    )
                    FROM jsonb_array_elements(rule_json->'actions'->0->'lines') AS line
                )
            )
            WHERE rule_json->'actions'->0->'lines' IS NOT NULL;");
    }
}
