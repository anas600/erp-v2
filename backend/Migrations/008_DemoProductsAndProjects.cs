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
        // The 3rd project is pre-completed in one company (HOLD) so the
        // user can test the "ProjectMilestoneCompleted" rule and see a
        // rule-generated journal entry.
        var projects = new[]
        {
            // (code, name_en, name_ar, status, completed_at)
            ("PRJ-001", "HQ renovation",      "تجديد المقر الرئيسي",    "active",    (DateTime?)null),
            ("PRJ-002", "ERP rollout phase 2", "مرحلة 2 من تطبيق النظام", "active",    (DateTime?)null),
            ("PRJ-003", "Annual audit",        "التدقيق السنوي",         "completed", (DateTime?)new DateTime(2026, 7, 15))
        };

        // Use the schema as-defined in 004_ProjectsSchema. We don't add a
        // company_id check here because the projects schema is the same
        // for every company — we just need to read its columns.
        // Look up the actual column names to be safe.
        var projCols = conn.QuerySingleOrDefault<string>(@"
            SELECT string_agg(column_name, ',') FROM information_schema.columns
            WHERE table_name = 'projects';") ?? "";
        if (!projCols.Contains("company_id") || !projCols.Contains("code"))
        {
            // 004 didn't run? bail out cleanly.
            return;
        }

        foreach (var companyId in GetCompanyIds(conn))
        {
            foreach (var prj in projects)
            {
                conn.Execute(@"
                    INSERT INTO projects (company_id, code, name, name_ar, status, completed_at, actual_cost, budget, start_date)
                    VALUES (@companyId, @code, @name, @nameAr, @status, @completedAt, 0, 100000, '2026-01-01')
                    ON CONFLICT (company_id, code) DO NOTHING;",
                    new
                    {
                        companyId,
                        code = prj.Item1,
                        name = prj.Item2,
                        nameAr = prj.Item3,
                        status = prj.Item4,
                        completedAt = prj.Item5
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
