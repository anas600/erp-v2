using Dapper;
using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Adds the contacts (customers + suppliers) catalogue.
///
/// Before this migration, invoices had to be created with a free-text
/// `party_name` and `party_name_ar` — a common source of typos and
/// duplicates ("ABC Co" vs "ABC Co." vs "شركة ABC").
///
/// With contacts, the invoice form's "customer/supplier" field can
/// either pick from the catalogue (preferred) or stay free-form
/// (still supported for one-off parties). Sprint 17 only seeds the
/// catalogue — the UI integration (dropdown) lands in Sprint 16.2.
/// </summary>
[Migration(20260804000007)]
public class Contacts : Migration
{
    public override void Up()
    {
        Create.Table("contacts")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable()
                .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("type").AsString(20).NotNullable()     // 'customer' | 'supplier'
            .WithColumn("code").AsString(50).NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("tax_id").AsString(50).Nullable()
            .WithColumn("phone").AsString(50).Nullable()
            .WithColumn("email").AsString(200).Nullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("is_demo_data").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);

        // Unique per (company, type, code) — the same code can mean
        // a customer and a supplier in different contexts.
        Create.Index("uk_contacts_company_type_code").OnTable("contacts")
            .OnColumn("company_id").Ascending()
            .OnColumn("type").Ascending()
            .OnColumn("code").Ascending()
            .WithOptions().Unique();

        // Soft delete: filter by is_active in queries.
        Create.Index("ix_contacts_company_type").OnTable("contacts")
            .OnColumn("company_id").Ascending()
            .OnColumn("type").Ascending();

        // ============= SEED demo data for each company =============
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=db;Port=5432;Database=erp;Username=erp;Password=erp_secret";

        using (var conn = new Npgsql.NpgsqlConnection(connectionString))
        {
            conn.Open();

            // 5 customers + 5 suppliers per company. is_demo_data=true
            // so the user can DELETE WHERE is_demo_data = true if they
            // want to start from a clean slate (or this seed will
            // re-run idempotently on every deploy).
            var contacts = new[]
            {
                // Customers
                ("customer", "CUST-001", "Usus Group",       "أسس 3",         "TAX-10001", "+218911000001", "[email protected]"),
                ("customer", "CUST-002", "Al-Arabia Group",  "المجموعة العربية", "TAX-10002", "+218911000002", "[email protected]"),
                ("customer", "CUST-003", "Al-Noor Trading",  "النور التجارية",  "TAX-10003", "+218911000003", "[email protected]"),
                ("customer", "CUST-004", "Al-Fajr Co.",      "الفجر",          "TAX-10004", "+218911000004", "[email protected]"),
                ("customer", "CUST-005", "Al-Emaar Holding", "الإعمار القابضة", "TAX-10005", "+218911000005", "[email protected]"),
                // Suppliers
                ("supplier", "SUPP-001", "ABC Trading Co.",       "شركة ABC التجارية",      "TAX-20001", "+218921000001", "[email protected]"),
                ("supplier", "SUPP-002", "XYZ Industries",        "مجموعة XYZ الصناعية",   "TAX-20002", "+218921000002", "[email protected]"),
                ("supplier", "SUPP-003", "Official Supplies",     "المورد الرسمي",          "TAX-20003", "+218921000003", "[email protected]"),
                ("supplier", "SUPP-004", "Tech Solutions",        "حلول التقنية",            "TAX-20004", "+218921000004", "[email protected]"),
                ("supplier", "SUPP-005", "Modern Tech Supplies",  "التقنية الحديثة",         "TAX-20005", "+218921000005", "[email protected]")
            };

            foreach (var companyId in GetCompanyIds(conn))
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
                            type = c.Item1,
                            code = c.Item2,
                            name = c.Item3,
                            nameAr = c.Item4,
                            taxId = c.Item5,
                            phone = c.Item6,
                            email = c.Item7
                        });
                }
            }
        }
    }

    public override void Down()
    {
        // Forward-only: the user can drop manually if they want to.
        Delete.Index("ix_contacts_company_type").OnTable("contacts");
        Delete.Index("uk_contacts_company_type_code").OnTable("contacts");
        Delete.Table("contacts");
    }

    private static List<Guid> GetCompanyIds(System.Data.IDbConnection conn)
    {
        return conn.Query<Guid>("SELECT id FROM companies;").ToList();
    }
}
