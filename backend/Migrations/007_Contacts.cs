using Dapper;
using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Adds the contacts (customers + suppliers) catalogue.
///
/// HISTORY (this is the third version of this migration):
///   v1: Used Create.Table() in the main migration transaction, then
///       seeded via a separate Npgsql.NpgsqlConnection. Failed at
///       deploy with "42P01: relation 'contacts' does not exist"
///       because the autocommit connection couldn't see the
///       uncommitted table from the main transaction. Same bug
///       pattern as the 005_ProductsAndInvoiceItems migration
///       (which we fixed in a follow-up commit).
///   v2 (this file): Bypass FluentMigrator's table abstractions
///       entirely — use raw SQL in a single autocommit connection
///       for both the schema and the seed. This is the only
///       approach that works for migrations that create a NEW table
///       AND seed it in the same Up() call.
///
/// WHY raw SQL instead of Create.Table:
///   FluentMigrator's Create.Table runs in a per-migration
///   transaction. A separate Npgsql connection opened inside Up()
///   cannot see the uncommitted table from that transaction — even
///   with the connection-string autocommit pattern, the DDL hasn't
///   been committed yet when the seed runs.
///
/// WHY not split into two migrations:
///   That would force the schema and the seed to live in separate
///   numbered files, which is noisy for a small change. The 002
///   migration handles a similar pattern (DDL in autocommit + seed
///   in main connection) but the contacts seed here is small
///   enough to keep in one file.
///
/// Schema and seed are both idempotent: CREATE TABLE IF NOT EXISTS,
/// CREATE INDEX IF NOT EXISTS, ON CONFLICT (company_id, type, code)
/// DO NOTHING. Re-running the migration after a partial failure is
/// safe.
/// </summary>
[Migration(20260804000007)]
public class Contacts : Migration
{
    public override void Up()
    {
        // Open a single autocommit connection. Every CREATE and INSERT
        // below runs in this same connection (and is committed
        // immediately, statement by statement). No transaction is
        // needed because every statement is idempotent on its own.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=db;Port=5432;Database=erp;Username=erp;Password=erp_secret";

        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();

        // ============= SCHEMA =============
        // CREATE TABLE IF NOT EXISTS — safe re-run.
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS contacts (
                id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
                type varchar(20) NOT NULL,
                code varchar(50) NOT NULL,
                name varchar(200) NOT NULL,
                name_ar varchar(200),
                tax_id varchar(50),
                phone varchar(50),
                email varchar(200),
                is_active boolean NOT NULL DEFAULT true,
                is_demo_data boolean NOT NULL DEFAULT false,
                created_at timestamp NOT NULL DEFAULT now()
            );");

        conn.Execute(@"
            CREATE UNIQUE INDEX IF NOT EXISTS uk_contacts_company_type_code
            ON contacts(company_id, type, code);");

        conn.Execute(@"
            CREATE INDEX IF NOT EXISTS ix_contacts_company_type
            ON contacts(company_id, type);");

        // ============= SEED =============
        // Same connection — sees the table we just created.
        var companyIds = GetCompanyIds(conn);
        if (companyIds.Count == 0)
        {
            // 002 seed hasn't run? Nothing to seed against. Exit cleanly.
            return;
        }

        var contacts = new (string Type, string Code, string Name, string NameAr, string TaxId, string Phone, string Email)[]
        {
            ("customer", "CUST-001", "Usus Group",       "أسس 3",             "TAX-10001", "+218911000001", "[email protected]"),
            ("customer", "CUST-002", "Al-Arabia Group",  "المجموعة العربية",   "TAX-10002", "+218911000002", "[email protected]"),
            ("customer", "CUST-003", "Al-Noor Trading",  "النور التجارية",     "TAX-10003", "+218911000003", "[email protected]"),
            ("customer", "CUST-004", "Al-Fajr Co.",      "الفجر",              "TAX-10004", "+218911000004", "[email protected]"),
            ("customer", "CUST-005", "Al-Emaar Holding", "الإعمار القابضة",    "TAX-10005", "+218911000005", "[email protected]"),
            ("supplier", "SUPP-001", "ABC Trading Co.",       "شركة ABC التجارية",     "TAX-20001", "+218921000001", "[email protected]"),
            ("supplier", "SUPP-002", "XYZ Industries",        "مجموعة XYZ الصناعية",  "TAX-20002", "+218921000002", "[email protected]"),
            ("supplier", "SUPP-003", "Official Supplies",     "المورد الرسمي",          "TAX-20003", "+218921000003", "[email protected]"),
            ("supplier", "SUPP-004", "Tech Solutions",        "حلول التقنية",            "TAX-20004", "+218921000004", "[email protected]"),
            ("supplier", "SUPP-005", "Modern Tech Supplies",  "التقنية الحديثة",         "TAX-20005", "+218921000005", "[email protected]")
        };

        foreach (var companyId in companyIds)
        {
            foreach (var c in contacts)
            {
                conn.Execute(@"
                    INSERT INTO contacts (company_id, type, code, name, name_ar, tax_id, phone, email, is_active, is_demo_data)
                    VALUES (@companyId, @type, @code, @name, @nameAr, @taxId, @phone, @email, true, true)
                    ON CONFLICT (company_id, type, code) DO NOTHING;",
                    new
                    {
                        companyId,
                        type = c.Type,
                        code = c.Code,
                        name = c.Name,
                        nameAr = c.NameAr,
                        taxId = c.TaxId,
                        phone = c.Phone,
                        email = c.Email
                    });
            }
        }
    }

    public override void Down()
    {
        // Forward-only: the user can drop the table manually.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=db;Port=5432;Database=erp;Username=erp;Password=erp_secret";

        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        conn.Execute("DROP INDEX IF EXISTS ix_contacts_company_type;");
        conn.Execute("DROP INDEX IF EXISTS uk_contacts_company_type_code;");
        conn.Execute("DROP TABLE IF EXISTS contacts;");
    }

    private static List<Guid> GetCompanyIds(System.Data.IDbConnection conn)
    {
        return conn.Query<Guid>("SELECT id FROM companies;").ToList();
    }
}
