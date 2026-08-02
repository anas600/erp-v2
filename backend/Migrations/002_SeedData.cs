using Dapper;
using ErpV2.Common;
using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Seeds demo data:
///  - 1 holding + 2 companies
///  - 6 system roles + 12 permissions
///  - 3 users (super admin, accountant, engineer)
///  - Chart of accounts for each company (typical accounting tree)
///  - 6 rule templates
/// </summary>
[Migration(20260729000002)]
public class SeedData : Migration
{
    public override void Up()
    {
        // Read the connection string from the same env var that Program.cs
        // uses for the runtime. Falls back to the docker-compose hostname
        // for local development where the env var is not set.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=db;Port=5432;Database=erp;Username=erp;Password=erp_secret";

        var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();

        var hasher = new BcryptPasswordHasher();

        // ============================================================
        // 1. Permissions
        // ============================================================
        var permissions = new[]
        {
            // Finance module
            ("finance.read",    "Finance",    "View Financial Data",   "عرض البيانات المالية"),
            ("finance.write",   "Finance",    "Create/Edit Entries",   "إنشاء/تعديل القيود"),
            ("finance.post",    "Finance",    "Post Journal Entries",  "ترحيل القيود"),
            // Projects module
            ("projects.read",   "Projects",   "View Projects",         "عرض المشاريع"),
            ("projects.write",  "Projects",   "Manage Projects",       "إدارة المشاريع"),
            // Companies module
            ("companies.read",  "Companies",  "View Companies",        "عرض الشركات"),
            ("companies.write", "Companies",  "Manage Companies",      "إدارة الشركات"),
            // Users module
            ("users.read",      "Users",      "View Users",            "عرض المستخدمين"),
            ("users.write",     "Users",      "Manage Users",          "إدارة المستخدمين"),
            // Rules module
            ("rules.read",      "Rules",      "View Business Rules",   "عرض قواعد العمل"),
            ("rules.write",     "Rules",      "Manage Business Rules", "إدارة قواعد العمل"),
            // Reports module
            ("reports.read",    "Reports",    "View Reports",          "عرض التقارير"),
        };

        foreach (var (code, module, en, ar) in permissions)
        {
            conn.Execute(@"
                INSERT INTO permissions (code, module, display_name, display_name_ar)
                VALUES (@code, @module, @en, @ar)
                ON CONFLICT (code) DO NOTHING;",
                new { code, module, en, ar });
        }

        // ============================================================
        // 2. Roles
        // ============================================================
        var roles = new[]
        {
            ("super_admin",       "Super Administrator",     "المدير العام",       new[] {
                "finance.read", "finance.write", "finance.post",
                "projects.read", "projects.write",
                "companies.read", "companies.write",
                "users.read", "users.write",
                "rules.read", "rules.write",
                "reports.read"
            }),
            ("holding_admin",     "Holding Administrator",   "مدير القابضة",       new[] {
                "finance.read", "finance.write", "finance.post",
                "projects.read", "projects.write",
                "companies.read", "companies.write",
                "users.read", "users.write",
                "rules.read", "rules.write",
                "reports.read"
            }),
            ("company_admin",     "Company Administrator",   "مدير الشركة",        new[] {
                "finance.read", "finance.write", "finance.post",
                "projects.read", "projects.write",
                "companies.read",
                "users.read", "users.write",
                "rules.read",
                "reports.read"
            }),
            ("accountant",        "Accountant",              "محاسب",              new[] {
                "finance.read", "finance.write", "finance.post",
                "companies.read",
                "reports.read"
            }),
            ("project_engineer",  "Project Engineer",        "مهندس مشاريع",       new[] {
                "projects.read", "projects.write",
                "companies.read"
            }),
            ("viewer",            "Viewer",                  "مشاهد",              new[] {
                "finance.read",
                "projects.read",
                "companies.read",
                "reports.read"
            }),
        };

        foreach (var (name, en, ar, perms) in roles)
        {
            conn.Execute(@"
                INSERT INTO roles (name, display_name, display_name_ar, is_system)
                VALUES (@name, @en, @ar, true)
                ON CONFLICT (name) DO NOTHING;",
                new { name, en, ar });

            var roleId = conn.ExecuteScalar<Guid>("SELECT id FROM roles WHERE name = @name", new { name });

            foreach (var permCode in perms)
            {
                var permId = conn.ExecuteScalar<Guid>("SELECT id FROM permissions WHERE code = @code", new { code = permCode });
                conn.Execute(@"
                    INSERT INTO role_permissions (role_id, permission_id)
                    VALUES (@roleId, @permId)
                    ON CONFLICT DO NOTHING;",
                    new { roleId, permId });
            }
        }

        // ============================================================
        // 3. Companies (Holding + 2 subsidiaries)
        // ============================================================
        var holdingId = Guid.NewGuid();
        conn.Execute(@"
            INSERT INTO companies (id, code, name, name_ar, is_holding, base_currency, is_active)
            VALUES (@id, 'HOLD', 'Holding Company', 'الشركة القابضة', true, 'LYD', true);",
            new { id = holdingId });

        var companyAId = Guid.NewGuid();
        var companyBId = Guid.NewGuid();

        conn.Execute(@"
            INSERT INTO companies (id, code, name, name_ar, parent_id, is_holding, base_currency, is_active)
            VALUES (@id, 'CO-A', 'Company Alpha', 'شركة ألف', @parent, false, 'LYD', true);",
            new { id = companyAId, parent = holdingId });

        conn.Execute(@"
            INSERT INTO companies (id, code, name, name_ar, parent_id, is_holding, base_currency, is_active)
            VALUES (@id, 'CO-B', 'Company Beta', 'شركة باء', @parent, false, 'LYD', true);",
            new { id = companyBId, parent = holdingId });

        // ============================================================
        // 4. Users
        // ============================================================
        var superAdminId = Guid.NewGuid();
        conn.Execute(@"
            INSERT INTO users (id, email, password_hash, full_name, full_name_ar, is_super_admin, is_active)
            VALUES (@id, 'admin@holding.ly', @hash, 'Super Admin', 'المدير العام', true, true);",
            new { id = superAdminId, hash = hasher.Hash("admin123") });

        var accountantId = Guid.NewGuid();
        conn.Execute(@"
            INSERT INTO users (id, email, password_hash, full_name, full_name_ar, is_super_admin, is_active)
            VALUES (@id, 'accountant@company-a.ly', @hash, 'Ahmad Accountant', 'أحمد المحاسب', false, true);",
            new { id = accountantId, hash = hasher.Hash("acc123") });

        var engineerId = Guid.NewGuid();
        conn.Execute(@"
            INSERT INTO users (id, email, password_hash, full_name, full_name_ar, is_super_admin, is_active)
            VALUES (@id, 'engineer@company-a.ly', @hash, 'Khaled Engineer', 'خالد المهندس', false, true);",
            new { id = engineerId, hash = hasher.Hash("eng123") });

        // User-Company mapping
        var superAdminRoleId = conn.ExecuteScalar<Guid>("SELECT id FROM roles WHERE name = 'super_admin'");
        var accountantRoleId = conn.ExecuteScalar<Guid>("SELECT id FROM roles WHERE name = 'accountant'");
        var engineerRoleId = conn.ExecuteScalar<Guid>("SELECT id FROM roles WHERE name = 'project_engineer'");

        // Super admin has access to all
        foreach (var cid in new[] { holdingId, companyAId, companyBId })
        {
            conn.Execute(@"
                INSERT INTO user_companies (user_id, company_id, role_id, is_primary)
                VALUES (@uid, @cid, @rid, @isPrimary);",
                new { uid = superAdminId, cid, rid = superAdminRoleId, isPrimary = cid == holdingId });
        }

        // Accountant: Company A (primary) + Company B
        conn.Execute(@"
            INSERT INTO user_companies (user_id, company_id, role_id, is_primary)
            VALUES (@uid, @cid, @rid, true);",
            new { uid = accountantId, cid = companyAId, rid = accountantRoleId });

        conn.Execute(@"
            INSERT INTO user_companies (user_id, company_id, role_id, is_primary)
            VALUES (@uid, @cid, @rid, false);",
            new { uid = accountantId, cid = companyBId, rid = accountantRoleId });

        // Engineer: Company A only
        conn.Execute(@"
            INSERT INTO user_companies (user_id, company_id, role_id, is_primary)
            VALUES (@uid, @cid, @rid, true);",
            new { uid = engineerId, cid = companyAId, rid = engineerRoleId });

        // ============================================================
        // 5. Chart of Accounts (per company)
        //    Standard accounting tree:
        //      1000-1999  Assets           (Debit)
        //      2000-2999  Liabilities      (Credit)
        //      3000-3999  Equity           (Credit)
        //      4000-4999  Revenue          (Credit)
        //      5000-5999  Expenses         (Debit)
        // ============================================================
        var accounts = new[]
        {
            // Assets (1xxx) - Debit
            ("1000", "Cash",                  "الصندوق",                "Asset",     "Debit"),
            ("1100", "Bank",                  "البنك",                  "Asset",     "Debit"),
            ("1200", "Accounts Receivable",   "المدينون",               "Asset",     "Debit"),
            ("1300", "Inventory",             "المخزون",                "Asset",     "Debit"),
            ("1500", "Equipment",             "المعدات",                "Asset",     "Debit"),
            ("1510", "Accumulated Depreciation", "مجمع الإهلاك",        "Asset",     "Credit"),
            // Liabilities (2xxx) - Credit
            ("2000", "Accounts Payable",      "الدائنون",               "Liability", "Credit"),
            ("2100", "Loans Payable",         "القروض المستحقة",        "Liability", "Credit"),
            // Equity (3xxx) - Credit
            ("3000", "Capital",               "رأس المال",              "Equity",    "Credit"),
            ("3100", "Retained Earnings",     "الأرباح المحتجزة",       "Equity",    "Credit"),
            // Revenue (4xxx) - Credit
            ("4000", "Sales Revenue",         "إيرادات المبيعات",       "Revenue",   "Credit"),
            ("4100", "Service Revenue",       "إيرادات الخدمات",        "Revenue",   "Credit"),
            // Expenses (5xxx) - Debit
            ("5000", "Cost of Goods Sold",    "تكلفة البضاعة المباعة",  "Expense",   "Debit"),
            ("5100", "Salaries Expense",      "مصروف الرواتب",          "Expense",   "Debit"),
            ("5200", "Rent Expense",          "مصروف الإيجار",          "Expense",   "Debit"),
            ("5300", "Utilities Expense",     "مصروف المرافق",          "Expense",   "Debit"),
            ("5400", "Depreciation Expense",  "مصروف الإهلاك",          "Expense",   "Debit"),
        };

        foreach (var cid in new[] { holdingId, companyAId, companyBId })
        {
            foreach (var (code, name, nameAr, type, nature) in accounts)
            {
                conn.Execute(@"
                    INSERT INTO accounts (company_id, code, name, name_ar, account_type, nature, is_active, balance)
                    VALUES (@cid, @code, @name, @nameAr, @type, @nature, true, 0);",
                    new { cid, code, name, nameAr, type, nature });
            }
        }

        // ============================================================
        // 6. Business Rule Templates
        // ============================================================
        var rules = new[]
        {
            new
            {
                name = "ترحيل فاتورة مشتريات",
                description = "عند اعتماد فاتورة مشتريات، ينشأ قيد: مدين مصروفات + مدين ضريبة، دائن دائنون",
                event_name = "PurchaseInvoiceApproved",
                priority = 10,
                rule_json = @"{
                    ""conditions"": {
                        ""all"": [
                            { ""field"": ""invoice.total"", ""op"": "">"", ""value"": 0 }
                        ]
                    },
                    ""actions"": [
                        {
                            ""type"": ""PostJournalEntry"",
                            ""narration"": ""فاتورة مشتريات رقم {invoice.number}"",
                            ""lines"": [
                                { ""accountCode"": ""5000"", ""nature"": ""debit"", ""amountFormula"": ""invoice.total - invoice.tax"", ""description"": ""تكلفة المشتريات"" },
                                { ""accountCode"": ""2000"", ""nature"": ""credit"", ""amountFormula"": ""invoice.total"", ""description"": ""دائنون - {supplier.name}"" }
                            ]
                        }
                    ]
                }"
            },
            new
            {
                name = "ترحيل فاتورة مبيعات",
                description = "عند اعتماد فاتورة مبيعات، ينشأ قيد: مدين مدينون، دائن إيرادات",
                event_name = "SalesInvoiceApproved",
                priority = 10,
                rule_json = @"{
                    ""conditions"": { ""all"": [] },
                    ""actions"": [
                        {
                            ""type"": ""PostJournalEntry"",
                            ""narration"": ""فاتورة مبيعات رقم {invoice.number}"",
                            ""lines"": [
                                { ""accountCode"": ""1200"", ""nature"": ""debit"", ""amountFormula"": ""invoice.total"", ""description"": ""مدينون - {customer.name}"" },
                                { ""accountCode"": ""4000"", ""nature"": ""credit"", ""amountFormula"": ""invoice.total"", ""description"": ""إيرادات المبيعات"" }
                            ]
                        }
                    ]
                }"
            },
            new
            {
                name = "دفع مورد (نقدي/بنكي)",
                description = "عند دفع مبلغ لمورد، ينشأ قيد: مدين دائنون، دائن صندوق/بنك",
                event_name = "SupplierPaymentMade",
                priority = 10,
                rule_json = @"{
                    ""conditions"": { ""all"": [] },
                    ""actions"": [
                        {
                            ""type"": ""PostJournalEntry"",
                            ""narration"": ""دفع لمورد {supplier.name}"",
                            ""lines"": [
                                { ""accountCode"": ""2000"", ""nature"": ""debit"", ""amountFormula"": ""payment.amount"", ""description"": ""تسوية حساب المورد"" },
                                { ""accountCode"": ""1000"", ""nature"": ""credit"", ""amountFormula"": ""payment.amount"", ""description"": ""الصندوق"" }
                            ]
                        }
                    ]
                }"
            },
            new
            {
                name = "تحصيل من عميل",
                description = "عند تحصيل دفعة من عميل، ينشأ قيد: مدين صندوق، دائن مدينون",
                event_name = "CustomerReceiptReceived",
                priority = 10,
                rule_json = @"{
                    ""conditions"": { ""all"": [] },
                    ""actions"": [
                        {
                            ""type"": ""PostJournalEntry"",
                            ""narration"": ""تحصيل من عميل {customer.name}"",
                            ""lines"": [
                                { ""accountCode"": ""1000"", ""nature"": ""debit"", ""amountFormula"": ""receipt.amount"", ""description"": ""الصندوق"" },
                                { ""accountCode"": ""1200"", ""nature"": ""credit"", ""amountFormula"": ""receipt.amount"", ""description"": ""تسوية حساب العميل"" }
                            ]
                        }
                    ]
                }"
            },
            new
            {
                name = "إهلاك أصول شهري",
                description = "عند إقفال الفترة، يسجل قيد إهلاك شهري",
                event_name = "PeriodClose",
                priority = 5,
                rule_json = @"{
                    ""conditions"": { ""all"": [] },
                    ""actions"": [
                        {
                            ""type"": ""PostJournalEntry"",
                            ""narration"": ""إهلاك شهري للمعدات"",
                            ""lines"": [
                                { ""accountCode"": ""5400"", ""nature"": ""debit"", ""amountFormula"": ""depreciation.amount"", ""description"": ""مصروف إهلاك"" },
                                { ""accountCode"": ""1510"", ""nature"": ""credit"", ""amountFormula"": ""depreciation.amount"", ""description"": ""مجمع الإهلاك"" }
                            ]
                        }
                    ]
                }"
            },
            new
            {
                name = "إيراد مشروع (Milestone)",
                description = "عند إنجاز مرحلة من مشروع، يُرحّل الإيراد",
                event_name = "ProjectMilestoneCompleted",
                priority = 10,
                rule_json = @"{
                    ""conditions"": { ""all"": [] },
                    ""actions"": [
                        {
                            ""type"": ""PostJournalEntry"",
                            ""narration"": ""إيراد مرحلة {milestone.name} من مشروع {project.name}"",
                            ""lines"": [
                                { ""accountCode"": ""1200"", ""nature"": ""debit"", ""amountFormula"": ""milestone.amount"", ""description"": ""مدينون"" },
                                { ""accountCode"": ""4100"", ""nature"": ""credit"", ""amountFormula"": ""milestone.amount"", ""description"": ""إيرادات الخدمات"" }
                            ]
                        }
                    ]
                }"
            }
        };

        foreach (var r in rules)
        {
            conn.Execute(@"
                INSERT INTO business_rules (name, description, event_name, enabled, priority, rule_json, is_template)
                VALUES (@name, @description, @event_name, true, @priority, @rule_json::jsonb, true);",
                r);
        }
    }

    public override void Down()
    {
        // No-op for seed data
    }
}
