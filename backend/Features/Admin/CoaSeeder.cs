using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Admin;

/// <summary>
/// Sprint 31 — the unified Chart of Accounts seeder. Drops all existing
/// accounts and re-inserts the full 4-level hierarchy for the given
/// company. This is the only correct way to "reset COA" — partial
/// updates leave orphans and inconsistent parent_id FKs.
///
/// Design:
///   L1: 1 digit (1=Asset, 2=Liability, 3=Equity, 4=Revenue, 5=Expense, 0=Audit)
///   L2: 2 digits total (1+1 for sub-classification)
///   L3: 4 digits total (1+1+2 for control account code)
///   L4: variable (L3_code + "-" + sub_id, e.g. "1103-CUST-001")
///
/// L1/L2/L3 are NOT postable. L4 is the only postable level.
/// </summary>
public class CoaSeeder
{
    private readonly IDbConnectionFactory _db;

    public CoaSeeder(IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// Drops all accounts (and via CASCADE, all dependent rows
    /// like journal_lines which FK back to accounts) for the given
    /// company, then re-inserts the full standard COA. Returns a
    /// summary of what was inserted.
    /// </summary>
    public async Task<CoaSeedResult> ReseedAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();

        // 1) Drop existing accounts. CASCADE will clean up journal_lines
        //    and any other rows that reference accounts. We also do an
        //    explicit cleanup of dependent tables that may not have
        //    ON DELETE CASCADE.
        //
        // NOTE: business_rules is GLOBAL (no company_id column), so we
        // DISABLE all rules instead of deleting them. The seed flow will
        // re-enable the ones we want via the regular seed endpoint. This
        // is safer than nuking all rules across all companies.
        await conn.ExecuteAsync(@"
            UPDATE business_rules SET enabled = false;
            DELETE FROM account_contact_links WHERE account_id IN (SELECT id FROM accounts WHERE company_id = @companyId);
            UPDATE contacts SET sub_ledger_account_id = NULL WHERE company_id = @companyId;
            DELETE FROM journal_lines WHERE account_id IN (SELECT id FROM accounts WHERE company_id = @companyId);
            DELETE FROM accounts WHERE company_id = @companyId;",
            new { companyId });

        // 2) Insert the standard COA in order (L1 → L2 → L3, L4 = none for now)
        int l1Count = 0, l2Count = 0, l3Count = 0;
        var idByCode = new Dictionary<string, Guid>();

        // ─── L1: Account classes ───
        var l1Defs = new[]
        {
            ("1", "Assets",                "الأصول"),
            ("2", "Liabilities",           "الالتزامات"),
            ("3", "Equity",                "حقوق الملكية"),
            ("4", "Revenue",               "الإيرادات"),
            ("5", "Expenses",              "المصروفات"),
            ("0", "Audit / Control",       "حسابات المراجعة والرقابة"), // for auditors' use
        };
        foreach (var (code, name, nameAr) in l1Defs)
        {
            var id = Guid.NewGuid();
            idByCode[code] = id;
            await conn.ExecuteAsync(@"
                INSERT INTO accounts (id, company_id, code, name, name_ar, parent_id, account_type, nature, level, account_class, is_control_account, is_postable, is_active, balance)
                VALUES (@id, @cid, @code, @name, @nameAr, NULL, @type, @nature, 1, 'header', false, false, true, 0);",
                new
                {
                    id, cid = companyId, code, name, nameAr,
                    type = code == "1" ? "Asset" :
                           code == "2" ? "Liability" :
                           code == "3" ? "Equity" :
                           code == "4" ? "Revenue" :
                           code == "5" ? "Expense" : "Equity",
                    nature = code == "1" || code == "5" ? "Debit" : "Credit"
                });
            l1Count++;
        }

        // ─── L2: Sub-classes ───
        var l2Defs = new (string code, string name, string nameAr, string type, string nature)[]
        {
            // Assets (1)
            ("11", "Current Assets",       "أصول متداولة",         "Asset",      "Debit"),
            ("12", "Non-current Assets",   "أصول غير متداولة",     "Asset",      "Debit"),
            // Liabilities (2)
            ("21", "Current Liabilities",  "التزامات متداولة",     "Liability",  "Credit"),
            ("22", "Non-current Liabilities", "التزامات غير متداولة", "Liability", "Credit"),
            // Equity (3)
            ("31", "Capital",              "رأس المال",            "Equity",     "Credit"),
            ("32", "Retained Earnings",    "الأرباح المحتجزة",     "Equity",     "Credit"),
            ("33", "Reserves",             "الاحتياطيات",           "Equity",     "Credit"),
            // Revenue (4)
            ("41", "Operating Revenue",    "إيرادات تشغيلية",       "Revenue",    "Credit"),
            ("42", "Non-operating Revenue","إيرادات غير تشغيلية",  "Revenue",    "Credit"),
            // Expenses (5)
            ("51", "Operating Expenses",   "مصاريف تشغيلية",       "Expense",    "Debit"),
            ("52", "Administrative Expenses","مصاريف إدارية وعمومية","Expense",   "Debit"),
            ("53", "Cost of Sales",        "تكلفة المبيعات",        "Expense",    "Debit"),
            ("54", "Project Costs",        "تكاليف المشاريع",      "Expense",    "Debit"),
        };
        foreach (var (code, name, nameAr, type, nature) in l2Defs)
        {
            var parentId = idByCode[code.Substring(0, 1)];
            var id = Guid.NewGuid();
            idByCode[code] = id;
            await conn.ExecuteAsync(@"
                INSERT INTO accounts (id, company_id, code, name, name_ar, parent_id, account_type, nature, level, account_class, is_control_account, is_postable, is_active, balance)
                VALUES (@id, @cid, @code, @name, @nameAr, @parentId, @type, @nature, 2, 'header', false, false, true, 0);",
                new { id, cid = companyId, code, name, nameAr, parentId, type, nature });
            l2Count++;
        }

        // ─── L3: Control / General Ledger accounts ───
        var l3Defs = new (string code, string name, string nameAr, string type, string nature, bool isControl)[]
        {
            // 11 — Current Assets
            ("1101", "Cash",                          "الصندوق",                 "Asset",     "Debit",   false),
            ("1102", "Bank",                          "البنك",                   "Asset",     "Debit",   false),
            ("1103", "Accounts Receivable",           "المدينون — عملاء",        "Asset",     "Debit",   true),
            ("1104", "Due from Sister Companies",     "مدينون — شركات شقيقة",    "Asset",     "Debit",   true),
            ("1105", "Inventory",                     "المخزون",                 "Asset",     "Debit",   false),
            ("1106", "Prepaid Expenses",              "مصاريف مقدمة",            "Asset",     "Debit",   false),
            ("1107", "Input VAT Receivable",          "ضريبة مدخلات قابلة للاسترداد", "Asset","Debit", false),
            ("1108", "Employee Advances",             "سلف الموظفين",            "Asset",     "Debit",   false),
            ("1109", "Notes Receivable",              "أوراق قبض",               "Asset",     "Debit",   false),
            // 12 — Non-current Assets
            ("1201", "Equipment",                     "المعدات",                 "Asset",     "Debit",   false),
            ("1202", "Accumulated Depreciation — Equipment", "مجمع إهلاك المعدات", "Asset", "Credit", false),
            ("1203", "Furniture and Fixtures",         "أثاث ومفروشات",          "Asset",     "Debit",   false),
            ("1204", "Accumulated Depreciation — Furniture", "مجمع إهلاك الأثاث", "Asset", "Credit", false),
            ("1205", "Intangible Assets",             "أصول غير ملموسة",         "Asset",     "Debit",   false),
            ("1206", "Long-term Investments",         "استثمارات طويلة الأجل",   "Asset",     "Debit",   false),
            // 21 — Current Liabilities
            ("2101", "Accounts Payable",              "الدائنون — موردون",       "Liability", "Credit",  true),
            ("2102", "Due to Sister Companies",       "دائنون — شركات شقيقة",    "Liability", "Credit",  true),
            ("2103", "Short-term Loans",              "قروض قصيرة الأجل",        "Liability", "Credit",  false),
            ("2104", "Output VAT Payable",            "ضريبة مخرجات مستحقة",     "Liability", "Credit",  false),
            ("2105", "Accrued Expenses",              "مصاريف مستحقة",           "Liability", "Credit",  false),
            ("2106", "Notes Payable",                 "أوراق دفع",               "Liability", "Credit",  false),
            // 22 — Non-current Liabilities
            ("2201", "Long-term Loans",               "قروض طويلة الأجل",        "Liability", "Credit",  false),
            ("2202", "Deferred Tax",                  "ضريبة مؤجلة",             "Liability", "Credit",  false),
            // 31 — Capital
            ("3101", "Share Capital",                 "رأس المال",               "Equity",    "Credit",  false),
            ("3102", "Additional Paid-in Capital",    "علاوة إصدار",             "Equity",    "Credit",  false),
            // 32 — Retained Earnings
            ("3201", "Retained Earnings — Prior Years","أرباح سنوات سابقة",      "Equity",    "Credit",  false),
            ("3202", "Current Year P&L",              "صافي ربح/خسارة السنة",    "Equity",    "Credit",  false),
            // 33 — Reserves
            ("3301", "Statutory Reserve",             "احتياطي قانوني",          "Equity",    "Credit",  false),
            ("3302", "General Reserve",               "احتياطي عام",             "Equity",    "Credit",  false),
            ("3303", "Dividends",                     "توزيعات أرباح",           "Equity",    "Debit",   false),
            // 41 — Operating Revenue
            ("4101", "Sales of Goods",                "إيراد بيع بضاعة",         "Revenue",   "Credit",  false),
            ("4102", "Service Revenue",               "إيراد خدمات",             "Revenue",   "Credit",  false),
            ("4103", "Project Revenue",               "إيراد مشاريع",            "Revenue",   "Credit",  false),
            ("4104", "Sales Returns & Allowances",    "مردودات ومسموحات مبيعات", "Revenue",   "Debit",   false),
            ("4105", "Purchase Discounts Earned",     "خصم مكتسب",               "Revenue",   "Debit",   false),
            // 42 — Non-operating Revenue
            ("4201", "Interest Income",               "إيراد فوائد",             "Revenue",   "Credit",  false),
            ("4202", "Other Income",                  "إيرادات أخرى",            "Revenue",   "Credit",  false),
            // 51 — Operating Expenses
            ("5101", "Salaries and Wages",            "رواتب وأجور",             "Expense",   "Debit",   false),
            ("5102", "Rent Expense",                  "إيجار",                   "Expense",   "Debit",   false),
            ("5103", "Utilities",                     "مرافق",                   "Expense",   "Debit",   false),
            ("5104", "Insurance",                     "تأمين",                   "Expense",   "Debit",   false),
            ("5105", "Maintenance",                   "صيانة",                   "Expense",   "Debit",   false),
            ("5106", "Depreciation Expense",          "مصاريف إهلاك",            "Expense",   "Debit",   false),
            ("5107", "Advertising",                   "دعاية وإعلان",            "Expense",   "Debit",   false),
            // 52 — Administrative Expenses
            ("5201", "Office Supplies",               "مستلزمات مكتبية",         "Expense",   "Debit",   false),
            ("5202", "Communications",                "اتصالات",                 "Expense",   "Debit",   false),
            ("5203", "Hospitality",                   "ضيافة",                   "Expense",   "Debit",   false),
            ("5204", "Travel",                        "سفر وانتقال",             "Expense",   "Debit",   false),
            ("5205", "Government Fees",               "رسوم حكومية",             "Expense",   "Debit",   false),
            // 53 — Cost of Sales
            ("5301", "Cost of Goods Sold",            "تكلفة البضاعة المباعة",   "Expense",   "Debit",   false),
            ("5302", "Direct Labor — Production",     "أجور عمال إنتاج",         "Expense",   "Debit",   false),
            ("5303", "Manufacturing Overhead",        "مصاريف صناعية غير مباشرة","Expense",   "Debit",   false),
            // 54 — Project Costs (NEW)
            ("5401", "Project Materials",             "مواد خام مشروع",          "Expense",   "Debit",   true),
            ("5402", "Project Labor",                 "أجور عمال مشروع",         "Expense",   "Debit",   true),
            ("5403", "Project Subcontractors",         "مقاولين باطن",             "Expense",   "Debit",   true),
            ("5404", "Project Equipment Rental",      "إيجار معدات مشروع",       "Expense",   "Debit",   true),
            ("5405", "Project Overhead Allocation",   "مصاريف عمومية مخصصة",     "Expense",   "Debit",   true),
            ("5406", "Project Transportation",        "نقل وشحن",                "Expense",   "Debit",   true),
            ("5407", "Project Other Costs",           "مصاريف مشاريع أخرى",      "Expense",   "Debit",   true),
        };
        foreach (var (code, name, nameAr, type, nature, isControl) in l3Defs)
        {
            var parentCode = code.Substring(0, 2);
            var parentId = idByCode[parentCode];
            var id = Guid.NewGuid();
            idByCode[code] = id;
            await conn.ExecuteAsync(@"
                INSERT INTO accounts (id, company_id, code, name, name_ar, parent_id, account_type, nature, level, account_class, is_control_account, is_postable, is_active, balance)
                VALUES (@id, @cid, @code, @name, @nameAr, @parentId, @type, @nature, 3, 'header', @isControl, false, true, 0);",
                new { id, cid = companyId, code, name, nameAr, parentId, type, nature, isControl });
            l3Count++;
        }

        return new CoaSeedResult(l1Count, l2Count, l3Count);
    }
}

public record CoaSeedResult(int L1Count, int L2Count, int L3Count);
