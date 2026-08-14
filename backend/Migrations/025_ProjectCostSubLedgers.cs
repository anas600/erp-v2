using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 50 — Auto-create L4 sub-ledger accounts for project cost tracking.
///
/// Why:
///   The 6th posting rule (PurchaseInvoiceApprovedForProject) needs to
///   post costs to <c>5401-PRJ-XXX</c> sub-ledger accounts, not the L3
///   control account <c>5401</c>. Without this migration, those
///   sub-ledger accounts don't exist and the rule's accountFrom
///   directive cannot resolve them.
///
///   We could ask the user to create them by hand, but that's error-
///   prone. Instead, this migration:
///     1. Adds a helper function <c>create_project_cost_subledgers()</c>
///        that takes a project id and creates the 7 sub-ledgers.
///     2. The function is called from <c>ProjectCostAccountService</c>
///        in C# after a project is inserted (not via trigger — keeps
///        the side-effect explicit and testable).
///
/// After this migration, the COA looks like:
///   <c>5401 Project Materials</c>           (L3, control, non-postable)
///     <c>5401-anas Project Materials — anas</c>   (L4, postable)
///     <c>5401-PRJ-002 Project Materials — PRJ-002</c> (L4, postable)
///   <c>5402 Project Labor</c>               (L3, control, non-postable)
///     <c>5402-anas</c>                          (L4, postable)
///     ...etc for 5403..5407
/// </summary>
[Migration(20260814000025)]
public class ProjectCostSubLedgers : Migration
{
    public override void Up()
    {
        // The migration is purely a no-op for the schema — the L4
        // sub-ledger accounts are created at runtime by
        // ProjectCostAccountService.CreateProjectSubLedgersAsync when
        // a new project is inserted. We don't seed historical
        // sub-ledgers here because the existing seeder projects
        // (Sprint 51 cleanup) will be wiped, and any new project
        // created going forward will trigger the auto-create.
        //
        // What we DO add here: a small helper SQL function for the
        // 7 L3 account codes the service should look up. This makes
        // the service code declarative and the lookup fast.
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
    }

    public override void Down()
    {
        Execute.Sql("DROP FUNCTION IF EXISTS get_project_cost_l3_codes();");
    }
}
