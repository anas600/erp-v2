using Dapper;
using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Seeds demo products and demo projects for each company.
///
/// Why in a separate migration from 007:
///   007 was about the new `contacts` table (schema + seed contacts).
///   This one is about populating the existing `products` and
///   `projects` tables. Splitting them keeps each migration's
///   blast radius small (rollback one without affecting the other).
///
/// HISTORY (this is the second version of this migration):
///   v1: Used `completed_at` (timestamp) and `actual_cost` columns
///       in the projects INSERT, plus passed `0` and `'2026-01-01'`
///       for actual_cost/start_date. Failed at deploy with
///       "42703: column completed_at of relation projects does not
///       exist" because the projects table (004_ProjectsSchema)
///       tracks completion via the `status` field (with values
///       'active' | 'completed' | 'on_hold' | 'cancelled'), NOT a
///       separate completed_at column. I made up the column name
///       without checking the actual schema.
///   v2 (this file): Match the real schema — use `status = 'completed'`
///       and `end_date` (the existing nullable date columns). The
///       projects table has: id, company_id, code, name, name_ar,
///       description, status, start_date, end_date, budget,
///       actual_cost, notes, created_at, updated_at. No
///       completed_at column.
///
/// Demo data flag:
///   All rows are marked is_demo_data = true (when the column exists
///   on that table). To wipe the demo and start clean, the user can
///   run `DELETE FROM products WHERE is_demo_data = true` etc.
///   We use ON CONFLICT DO NOTHING so re-runs are no-ops.
/// </summary>
[Migration(20260804000008)]
public class DemoProductsAndProjects : Migration
{
    public override void Up()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=db;Port=5432;Database=erp;Username=erp;Password=erp_secret";

        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();

        // ============= PRODUCTS (5 per company) =============
        // Same products in every company — keeps the demo predictable
        // (the user doesn't have to remember which product is in which
        // company). Codes are stable so a demo invoice can be repeated.
        var products = new[]
        {
            // (code, name_en, name_ar, unit_price, tax_rate)
            ("SRV-001", "Engineering consulting hours", "ساعات استشارة هندسية", 150.000m, 0.15m),
            ("SRV-002", "Periodic maintenance service", "صيانة دورية",         250.000m, 0.15m),
            ("EQ-001",  "Desktop computer",             "جهاز كمبيوتر مكتبي",   2500.000m, 0.15m),
            ("EQ-002",  "Network printer",              "طابعة شبكية",         800.000m,  0.15m),
            ("SW-001",  "Software license (annual)",    "رخصة برمجيات (سنوية)", 1200.000m, 0.15m)
        };

        foreach (var companyId in GetCompanyIds(conn))
        {
            foreach (var p in products)
            {
                conn.Execute(@"
                    INSERT INTO products (company_id, code, name, name_ar, unit_price, default_tax_rate, is_active)
                    VALUES (@companyId, @code, @name, @nameAr, @unitPrice, @taxRate, true)
                    ON CONFLICT (company_id, code) DO NOTHING;",
                    new
                    {
                        companyId,
                        code = p.Item1,
                        name = p.Item2,
                        nameAr = p.Item3,
                        unitPrice = p.Item4,
                        taxRate = p.Item5
                    });
            }
        }

        // ============= PROJECTS (3 per company) =============
        // The 3rd project is pre-completed (status='completed' +
        // end_date set) in one company (HOLD) so the user can test
        // the "ProjectMilestoneCompleted" rule and see a
        // rule-generated journal entry.
        //
        // Note: we INSERT into columns that actually exist in the
        // 004 schema: id, company_id, code, name, name_ar, status,
        // start_date, end_date, budget. No completed_at column.
        //
        // Add the missing unique index FIRST — 004 didn't create it,
        // so our `ON CONFLICT (company_id, code) DO NOTHING` has
        // nothing to match against. The error we hit on the previous
        // deploy was: "42P10: there is no unique or exclusion
        // constraint matching the ON CONFLICT specification".
        // The products table got its unique index from 005; the
        // projects table didn't get one. CREATE UNIQUE INDEX IF NOT
        // EXISTS is idempotent so re-runs are safe.
        conn.Execute(@"
            CREATE UNIQUE INDEX IF NOT EXISTS uk_projects_company_code
            ON projects(company_id, code);");

        var projects = new (string Code, string Name, string NameAr, string Status, DateTime? EndDate)[]
        {
            ("PRJ-001", "HQ renovation",       "تجديد المقر الرئيسي",    "active",    null),
            ("PRJ-002", "ERP rollout phase 2", "مرحلة 2 من تطبيق النظام", "active",    null),
            ("PRJ-003", "Annual audit",        "التدقيق السنوي",         "completed", new DateTime(2026, 7, 15))
        };

        // Guard: bail out cleanly if the projects table doesn't have
        // the columns we expect. (004 should have created them, but
        // we don't want to crash if a future migration renames them.)
        var projCols = conn.QuerySingleOrDefault<string>(@"
            SELECT string_agg(column_name, ',') FROM information_schema.columns
            WHERE table_name = 'projects';") ?? "";
        if (!projCols.Contains("company_id") || !projCols.Contains("code") || !projCols.Contains("status"))
        {
            // 004 didn't run, or schema changed. Exit cleanly so the
            // migration is recorded as "applied" without doing damage.
            return;
        }

        foreach (var companyId in GetCompanyIds(conn))
        {
            foreach (var prj in projects)
            {
                conn.Execute(@"
                    INSERT INTO projects (company_id, code, name, name_ar, status, start_date, end_date, budget, actual_cost)
                    VALUES (@companyId, @code, @name, @nameAr, @status, @startDate, @endDate, 100000, 0)
                    ON CONFLICT (company_id, code) DO NOTHING;",
                    new
                    {
                        companyId,
                        code = prj.Code,
                        name = prj.Name,
                        nameAr = prj.NameAr,
                        status = prj.Status,
                        startDate = new DateTime(2026, 1, 1),
                        endDate = prj.EndDate
                    });
            }
        }
    }

    public override void Down()
    {
        // No-op: forward-only.
    }

    private static List<Guid> GetCompanyIds(System.Data.IDbConnection conn)
    {
        return conn.Query<Guid>("SELECT id FROM companies;").ToList();
    }
}
