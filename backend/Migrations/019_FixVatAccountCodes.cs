using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 32 hotfix — the rule account codes 2200 and 1400 (used in
/// VAT lines) don't exist in the new standard COA. The correct codes
/// are 2104 (Output VAT Payable) and 1107 (Input VAT Receivable).
/// This migration updates the rules' jsonb accordingly.
/// </summary>
[Migration(20260807000004)]
public class FixVatAccountCodes : Migration
{
    public override void Up()
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
                                WHEN '2200' THEN '""2104""'::jsonb
                                WHEN '1400' THEN '""1107""'::jsonb
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
                                WHEN '2104' THEN '""2200""'::jsonb
                                WHEN '1107' THEN '""1400""'::jsonb
                                ELSE line->'accountCode'
                            END)
                    )
                    FROM jsonb_array_elements(rule_json->'actions'->0->'lines') AS line
                )
            )
            WHERE rule_json->'actions'->0->'lines' IS NOT NULL;");
    }
}
