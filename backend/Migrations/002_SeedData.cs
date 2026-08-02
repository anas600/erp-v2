using Dapper;
using ErpV2.Common;
using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Seeds the initial data for a fresh deployment.
///
/// ## Why this migration is idempotent
///
/// On Render (and any cloud), the same migration can be applied more than
/// once: a previous deploy may have partially succeeded, the pod may have
/// been restarted, or a user may have triggered a manual re-run. Every
/// INSERT here uses `ON CONFLICT (unique_col) DO NOTHING` and re-reads the
/// actual row id afterwards, so re-running never fails and never
/// duplicates rows.
///
/// We also wrap the whole seed in a single transaction (via the
/// injected `IMigrationContext`) so a failure mid-way rolls back the
/// whole seed cleanly. We do NOT use `SystemMethods.NewGuid` for new
/// rows: every id is generated as `Guid.NewGuid()` in C#, then written
/// through `ON CONFLICT` and read back. This is the only way to keep
/// `user_companies.user_id` consistent across reruns.
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

        // ============================================================
        // 0. Schema fixes — RUN FIRST, IN A SEPARATE CONNECTION
        // ============================================================
        // Two problems with the original 001_InitialSchema:
        //   1. accounts has a global unique index on `code` alone, which
        //      prevents two companies from sharing an account code
        //      (e.g. both companies need a 1000-Cash account).
        //   2. business_rules has no unique constraint on
        //      (name, event_name), so the seed's ON CONFLICT clause
        //      fails with SQLSTATE 42P10.
        //
        // We fix both here, BUT in a separate connection without an
        // explicit transaction. Why? Because this migration runs as
        // part of a FluentMigrator transaction, and the seed below
        // will fail on a re-deploy (the database has rows from a
        // previous partial run). If the DDL is inside the same
        // transaction, the rollback on seed failure would also revert
        // the DDL — and we'd be back where we started.
        //
        // Running in autocommit mode (no explicit transaction) makes
        // the DDL permanent even if the seed below fails. The DDL
        // statements are themselves idempotent (DROP INDEX IF EXISTS,
        // CREATE UNIQUE INDEX IF NOT EXISTS), so it's safe to re-run
        // them on every deploy.
        //
        // Note: uk_accounts_code is a UNIQUE INDEX (not a constraint),
        // so we use DROP INDEX, not DROP CONSTRAINT. The FluentMigrator
        // .Unique() method on Create.Index() generates CREATE UNIQUE
        // INDEX, which creates an index, not an ALTER TABLE constraint.
        using (var schemaConn = new Npgsql.NpgsqlConnection(connectionString))
        {
            schemaConn.Open();
            schemaConn.Execute("DROP INDEX IF EXISTS uk_accounts_code;");
            schemaConn.Execute(@"
                CREATE UNIQUE INDEX IF NOT EXISTS uk_accounts_company_code
                ON accounts(company_id, code);");
            schemaConn.Execute(@"
                CREATE UNIQUE INDEX IF NOT EXISTS uk_business_rules_name_event
                ON business_rules(name, event_name);");
        }

        // Now the actual seed, in its own connection+transaction so the
        // whole thing rolls back on any single failure.
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();

        var hasher = new BcryptPasswordHasher();

        try
        {
            // ============================================================
            // 1. Permissions
            // ============================================================
            var permissions = new[]
            {
                ("finance.read",    "Finance",    "View Financial Data",   "عرض البيانات المالية"),
                ("finance.write",   "Finance",    "Create/Edit Entries",   "إنشاء/تعديل القيود"),
                ("finance.post",    "Finance",    "Post Journal Entries",  "ترحيل القيود"),
                ("projects.read",   "Projects",   "View Projects",         "عرض المشاريع"),
                ("projects.write",  "Projects",   "Manage Projects",       "إدارة المشاريع"),
                ("companies.read",  "Companies",  "View Companies",        "عرض الشركات"),
                ("companies.write", "Companies",  "Manage Companies",      "إدارة الشركات"),
                ("users.read",      "Users",      "View Users",            "عرض المستخدمين"),
                ("users.write",     "Users",      "Manage Users",          "إدارة المستخدمين"),
                ("rules.read",      "Rules",      "View Business Rules",   "عرض قواعد العمل"),
                ("rules.write",     "Rules",      "Manage Business Rules", "إدارة قواعد العمل"),
                ("reports.read",    "Reports",    "View Reports",          "عرض التقارير"),
            };

            foreach (var (code, module, en, ar) in permissions)
            {
                conn.Execute(@"
                    INSERT INTO permissions (code, module, display_name, display_name_ar)
                    VALUES (@code, @module, @en, @ar)
                    ON CONFLICT (code) DO NOTHING;",
                    new { code, module, en, ar }, tx);
            }

            // ============================================================
            // 2. Roles
            // ============================================================
            var roles = new (string name, string en, string ar, string[] perms)[]
            {
                ("super_admin", "Super Administrator", "المدير العام", new[] {
                    "finance.read", "finance.write", "finance.post",
                    "projects.read", "projects.write",
                    "companies.read", "companies.write",
                    "users.read", "users.write",
                    "rules.read", "rules.write",
                    "reports.read"
                }),
                ("holding_admin", "Holding Administrator", "مدير القابضة", new[] {
                    "finance.read", "finance.write", "finance.post",
                    "projects.read", "projects.write",
                    "companies.read", "companies.write",
                    "users.read", "users.write",
                    "rules.read", "rules.write",
                    "reports.read"
                }),
                ("company_admin", "Company Administrator", "مدير الشركة", new[] {
                    "finance.read", "finance.write", "finance.post",
                    "projects.read", "projects.write",
                    "companies.read",
                    "users.read", "users.write",
                    "rules.read",
                    "reports.read"
                }),
                ("accountant", "Accountant", "محاسب", new[] {
                    "finance.read", "finance.write", "finance.post",
                    "companies.read",
                    "reports.read"
                }),
                ("project_engineer", "Project Engineer", "مهندس مشاريع", new[] {
                    "projects.read", "projects.write",
                    "companies.read"
                }),
                ("viewer", "Viewer", "مشاهد", new[] {
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
                    new { name, en, ar }, tx);

                // Re-read the role id (it may have existed already).
                var roleId = conn.ExecuteScalar<Guid>(
                    "SELECT id FROM roles WHERE name = @name;", new { name }, tx);

                foreach (var permCode in perms)
                {
                    var permId = conn.ExecuteScalar<Guid>(
                        "SELECT id FROM permissions WHERE code = @code;",
                        new { code = permCode }, tx);
                    conn.Execute(@"
                        INSERT INTO role_permissions (role_id, permission_id)
                        VALUES (@roleId, @permId)
                        ON CONFLICT DO NOTHING;",
                        new { roleId, permId }, tx);
                }
            }

            // ============================================================
            // 3. Companies (Holding + 2 subsidiaries)
            // ============================================================
            var holdingId = conn.ExecuteScalar<Guid?>(@"
                SELECT id FROM companies WHERE code = 'HOLD';", transaction: tx) ?? Guid.NewGuid();

            conn.Execute(@"
                INSERT INTO companies (id, code, name, name_ar, is_holding, base_currency, is_active)
                VALUES (@id, 'HOLD', 'Holding Company', 'الشركة القابضة', true, 'LYD', true)
                ON CONFLICT (code) DO NOTHING;",
                new { id = holdingId }, tx);

            // Re-read the holding id (it may have existed already).
            holdingId = conn.ExecuteScalar<Guid>(
                "SELECT id FROM companies WHERE code = 'HOLD';", transaction: tx);

            void UpsertCompany(string code, string name, string nameAr, bool isHolding)
            {
                var id = conn.ExecuteScalar<Guid?>(@"
                    SELECT id FROM companies WHERE code = @code;",
                    new { code }, tx) ?? Guid.NewGuid();
                conn.Execute(@"
                    INSERT INTO companies (id, code, name, name_ar, parent_id, is_holding, base_currency, is_active)
                    VALUES (@id, @code, @name, @nameAr, @parent, @isHolding, 'LYD', true)
                    ON CONFLICT (code) DO NOTHING;",
                    new { id, code, name, nameAr, parent = holdingId, isHolding }, tx);
            }

            UpsertCompany("CO-A", "Company Alpha", "شركة ألف", false);
            UpsertCompany("CO-B", "Company Beta", "شركة باء", false);

            // ============================================================
            // 4. Users
            // ============================================================
            void UpsertUser(string email, string password, string? fullName, string? fullNameAr)
            {
                var id = conn.ExecuteScalar<Guid?>(@"
                    SELECT id FROM users WHERE email = @email;",
                    new { email }, tx) ?? Guid.NewGuid();
                conn.Execute(@"
                    INSERT INTO users (id, email, password_hash, full_name, full_name_ar, is_super_admin, is_active)
                    VALUES (@id, @email, @hash, @fullName, @fullNameAr, false, true)
                    ON CONFLICT (email) DO NOTHING;",
                    new { id, email, hash = hasher.Hash(password), fullName, fullNameAr }, tx);
            }

            UpsertUser("admin@holding.ly", "admin123", "Super Admin", "المدير العام");
            UpsertUser("accountant@company-a.ly", "acc123", "Ahmad Accountant", "أحمد المحاسب");
            UpsertUser("engineer@company-a.ly", "eng123", "Khaled Engineer", "خالد المهندس");

            // ============================================================
            // 5. User-Company Memberships
            // ============================================================
            Guid IdByEmail(string email) => conn.ExecuteScalar<Guid>(
                "SELECT id FROM users WHERE email = @email;", new { email }, tx);

            Guid IdByCode(string code) => conn.ExecuteScalar<Guid>(
                "SELECT id FROM companies WHERE code = @code;", new { code }, tx);

            Guid RoleId(string name) => conn.ExecuteScalar<Guid>(
                "SELECT id FROM roles WHERE name = @name;", new { name }, tx);

            void UpsertMembership(string email, string companyCode, string roleName, bool isPrimary)
            {
                conn.Execute(@"
                    INSERT INTO user_companies (user_id, company_id, role_id, is_primary)
                    VALUES (@userId, @companyId, @roleId, @isPrimary)
                    ON CONFLICT (user_id, company_id) DO NOTHING;",
                    new
                    {
                        userId = IdByEmail(email),
                        companyId = IdByCode(companyCode),
                        roleId = RoleId(roleName),
                        isPrimary
                    }, tx);
            }

            // Super admin: all companies
            UpsertMembership("admin@holding.ly", "HOLD", "super_admin", true);
            UpsertMembership("admin@holding.ly", "CO-A", "super_admin", false);
            UpsertMembership("admin@holding.ly", "CO-B", "super_admin", false);
            // Accountant: company A (primary) + company B
            UpsertMembership("accountant@company-a.ly", "CO-A", "accountant", true);
            UpsertMembership("accountant@company-a.ly", "CO-B", "accountant", false);
            // Engineer: company A only
            UpsertMembership("engineer@company-a.ly", "CO-A", "project_engineer", true);

            // ============================================================
            // 6. Chart of Accounts (per company)
            // ============================================================
            var accounts = new[]
            {
                ("1000", "Cash",                  "الصندوق",                "Asset",     "Debit"),
                ("1100", "Bank",                  "البنك",                  "Asset",     "Debit"),
                ("1200", "Accounts Receivable",   "المدينون",               "Asset",     "Debit"),
                ("1300", "Inventory",             "المخزون",                "Asset",     "Debit"),
                ("1500", "Equipment",             "المعدات",                "Asset",     "Debit"),
                ("1510", "Accumulated Depreciation", "مجمع الإهلاك",        "Asset",     "Credit"),
                ("2000", "Accounts Payable",      "الدائنون",               "Liability", "Credit"),
                ("2100", "Loans Payable",         "القروض المستحقة",        "Liability", "Credit"),
                ("3000", "Capital",               "رأس المال",              "Equity",    "Credit"),
                ("3100", "Retained Earnings",     "الأرباح المحتجزة",       "Equity",    "Credit"),
                ("4000", "Sales Revenue",         "إيرادات المبيعات",       "Revenue",   "Credit"),
                ("4100", "Service Revenue",       "إيرادات الخدمات",        "Revenue",   "Credit"),
                ("5000", "Cost of Goods Sold",    "تكلفة البضاعة المباعة",  "Expense",   "Debit"),
                ("5100", "Salaries Expense",      "مصروف الرواتب",          "Expense",   "Debit"),
                ("5200", "Rent Expense",          "مصروف الإيجار",          "Expense",   "Debit"),
                ("5300", "Utilities Expense",     "مصروف المرافق",          "Expense",   "Debit"),
                ("5400", "Depreciation Expense",  "مصروف الإهلاك",          "Expense",   "Debit"),
            };

            var companyIds = new[] { holdingId, IdByCode("CO-A"), IdByCode("CO-B") };
            foreach (var cid in companyIds)
            {
                foreach (var (code, name, nameAr, type, nature) in accounts)
                {
                    conn.Execute(@"
                        INSERT INTO accounts (company_id, code, name, name_ar, account_type, nature, is_active, balance)
                        VALUES (@companyId, @code, @name, @nameAr, @type, @nature, true, 0)
                        ON CONFLICT (company_id, code) DO NOTHING;",
                        new { companyId = cid, code, name, nameAr, type, nature }, tx);
                }
            }

            // ============================================================
            // 7. Business Rule Templates
            // ============================================================
            var rules = new (string name, string desc, string eventName, int prio, string json)[]
            {
                ("ترحيل فاتورة مشتريات", "عند اعتماد فاتورة مشتريات، ينشأ قيد: مدين مصروفات + مدين ضريبة، دائن دائنون",
                    "PurchaseInvoiceApproved", 10,
                    @"{
                        ""conditions"": { ""all"": [{ ""field"": ""invoice.total"", ""op"": "">"", ""value"": 0 }] },
                        ""actions"": [
                            {
                                ""type"": ""PostJournalEntry"",
                                ""narration"": ""فاتورة مشتريات رقم {invoice.number}"",
                                ""lines"": [
                                    { ""accountCode"": ""5000"", ""nature"": ""debit"",  ""amountFormula"": ""invoice.total - invoice.tax"", ""description"": ""تكلفة المشتريات"" },
                                    { ""accountCode"": ""2000"", ""nature"": ""credit"", ""amountFormula"": ""invoice.total"",                ""description"": ""دائنون - {supplier.name}"" }
                                ]
                            }
                        ]
                    }"),
                ("ترحيل فاتورة مبيعات", "عند اعتماد فاتورة مبيعات، ينشأ قيد: مدين مدينون، دائن إيرادات",
                    "SalesInvoiceApproved", 10,
                    @"{
                        ""conditions"": { ""all"": [] },
                        ""actions"": [
                            {
                                ""type"": ""PostJournalEntry"",
                                ""narration"": ""فاتورة مبيعات رقم {invoice.number}"",
                                ""lines"": [
                                    { ""accountCode"": ""1200"", ""nature"": ""debit"",  ""amountFormula"": ""invoice.total"", ""description"": ""مدينون - {customer.name}"" },
                                    { ""accountCode"": ""4000"", ""nature"": ""credit"", ""amountFormula"": ""invoice.total"", ""description"": ""إيرادات المبيعات"" }
                                ]
                            }
                        ]
                    }"),
                ("دفع مورد (نقدي/بنكي)", "عند دفع مبلغ لمورد، ينشأ قيد: مدين دائنون، دائن صندوق/بنك",
                    "SupplierPaymentMade", 10,
                    @"{
                        ""conditions"": { ""all"": [] },
                        ""actions"": [
                            {
                                ""type"": ""PostJournalEntry"",
                                ""narration"": ""دفع لمورد {supplier.name}"",
                                ""lines"": [
                                    { ""accountCode"": ""2000"", ""nature"": ""debit"",  ""amountFormula"": ""payment.amount"", ""description"": ""تسوية حساب المورد"" },
                                    { ""accountCode"": ""1000"", ""nature"": ""credit"", ""amountFormula"": ""payment.amount"", ""description"": ""الصندوق"" }
                                ]
                            }
                        ]
                    }"),
                ("تحصيل من عميل", "عند تحصيل دفعة من عميل، ينشأ قيد: مدين صندوق، دائن مدينون",
                    "CustomerReceiptReceived", 10,
                    @"{
                        ""conditions"": { ""all"": [] },
                        ""actions"": [
                            {
                                ""type"": ""PostJournalEntry"",
                                ""narration"": ""تحصيل من عميل {customer.name}"",
                                ""lines"": [
                                    { ""accountCode"": ""1000"", ""nature"": ""debit"",  ""amountFormula"": ""receipt.amount"", ""description"": ""الصندوق"" },
                                    { ""accountCode"": ""1200"", ""nature"": ""credit"", ""amountFormula"": ""receipt.amount"", ""description"": ""تسوية حساب العميل"" }
                                ]
                            }
                        ]
                    }"),
                ("إهلاك أصول شهري", "عند إقفال الفترة، يسجل قيد إهلاك شهري",
                    "PeriodClose", 5,
                    @"{
                        ""conditions"": { ""all"": [] },
                        ""actions"": [
                            {
                                ""type"": ""PostJournalEntry"",
                                ""narration"": ""إهلاك شهري للمعدات"",
                                ""lines"": [
                                    { ""accountCode"": ""5400"", ""nature"": ""debit"",  ""amountFormula"": ""depreciation.amount"", ""description"": ""مصروف إهلاك"" },
                                    { ""accountCode"": ""1510"", ""nature"": ""credit"", ""amountFormula"": ""depreciation.amount"", ""description"": ""مجمع الإهلاك"" }
                                ]
                            }
                        ]
                    }"),
                ("إيراد مشروع (Milestone)", "عند إنجاز مرحلة من مشروع، يُرحّل الإيراد",
                    "ProjectMilestoneCompleted", 10,
                    @"{
                        ""conditions"": { ""all"": [] },
                        ""actions"": [
                            {
                                ""type"": ""PostJournalEntry"",
                                ""narration"": ""إيراد مرحلة {milestone.name} من مشروع {project.name}"",
                                ""lines"": [
                                    { ""accountCode"": ""1200"", ""nature"": ""debit"",  ""amountFormula"": ""milestone.amount"", ""description"": ""مدينون"" },
                                    { ""accountCode"": ""4100"", ""nature"": ""credit"", ""amountFormula"": ""milestone.amount"", ""description"": ""إيرادات الخدمات"" }
                                ]
                            }
                        ]
                    }")
            };

            foreach (var (name, desc, eventName, prio, json) in rules)
            {
                conn.Execute(@"
                    INSERT INTO business_rules (name, description, event_name, enabled, priority, rule_json, is_template)
                    VALUES (@name, @description, @eventName, true, @priority, @json::jsonb, true)
                    ON CONFLICT (name, event_name) DO NOTHING;",
                    new { name, description = desc, eventName, priority = prio, json }, tx);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public override void Down()
    {
        // No-op for seed data. The schema migrations above handle drops.
    }
}
