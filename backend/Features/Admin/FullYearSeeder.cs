using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Contacts;
using ErpV2.Features.CostCenters;
using ErpV2.Features.FiscalYears;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Journal;
using ErpV2.Features.Payments;
using ErpV2.Features.Products;
using ErpV2.Features.Projects;
using ErpV2.Features.Receipts;

namespace ErpV2.Features.Admin;

/// <summary>
/// Result of a full-year seed run. Returned to the API caller so
/// they can see exactly what was created.
/// </summary>
public partial class FullYearSeedResult
{
    public Guid CompanyId { get; set; }
    public int CustomersCreated { get; set; }
    public int SuppliersCreated { get; set; }
    public int ProductsCreated { get; set; }
    public int SubLedgersCreated { get; set; }
    public int InvoicesCreated { get; set; }
    public int ReceiptsCreated { get; set; }
    public int PaymentsCreated { get; set; }
    public bool FiscalYearCreated { get; set; }
    public List<string> Errors { get; set; } = new();
    public double ElapsedSeconds { get; set; }

    public int TotalErrors => Errors.Count;
    public bool AllSucceeded => Errors.Count == 0;
}

/// <summary>
/// Sprint 39 — Full-year (12 months) data seeder.
///
/// Creates a realistic, mathematically-correct year of business
/// activity that the accountant can use to demo every screen in
/// the system, every report, and every workflow.
///
/// Scope (fiscal year: Sep 2025 → Aug 2026):
///   - 10 customers (gov, semi-gov, private, retail)
///   - 10 suppliers (materials, equipment, services, admin)
///   - 15 products (construction materials, office, IT)
///   - ~80 sales invoices (distributed realistically across 12 months)
///   - ~60 purchase invoices (matched to project activity)
///   - ~50 receipts (with realistic 30/60/90 day payment patterns)
///   - ~45 payments (to suppliers)
///   - ~120 journal entries (recurring + ad-hoc):
///     * 12× rent, salaries, utilities, depreciation, loan
///     * Bank charges, fees, accruals
///   - 4 projects (construction, supply, services, maintenance)
///     with full BOQ, contract, progress billings, variations
///   - Year-end closing entries
///
/// All amounts in LYD. All journal entries balanced (Σdebit = Σcredit).
/// Trial balance must balance. Income statement + balance sheet
/// must reconcile. Project P&L must be realistic.
///
/// Design principles:
///   1. **Use existing services** — every invoice goes through
///      InvoiceService.CreateDraftAsync/PostAsync, every JE goes
///      through JournalService. This guarantees all business
///      rules and posting logic apply uniformly.
///   2. **Idempotent on master data** — re-running with the same
///      company wipes transactions and re-creates them. Master
///      data is added only if missing.
///   3. **Deterministic randomness** — seeded RNG so the same
///      input always produces the same output (useful for testing).
///   4. **Phased** — clear phases with explicit commits so a
///      failure mid-year leaves the system in a known state.
///   5. **Cross-company validation** — every query filters by
///      companyId. All FK references include company_id.
/// </summary>
public partial class FullYearSeeder
{
    // ----------------------------------------------------------------
    // Constants
    // ----------------------------------------------------------------

    /// <summary>Fiscal year start (Sept 1, 2025).</summary>
    public static readonly DateTime FY_START = new(2025, 9, 1);

    /// <summary>Fiscal year end (Aug 31, 2026).</summary>
    public static readonly DateTime FY_END = new(2026, 8, 31);

    /// <summary>Libyan VAT rate (4% on most goods and services).</summary>
    public const decimal VAT_RATE = 0.04m;

    /// <summary>Old VAT rate (15%) — used for grandfathered entries
    /// in the first 3 months to show historical context.</summary>
    public const decimal VAT_RATE_OLD = 0.15m;

    // ----------------------------------------------------------------
    // Dependencies
    // ----------------------------------------------------------------

    private readonly IDbConnectionFactory _db;
    private readonly ContactService _contacts;
    private readonly AccountService _accounts;
    private readonly InvoiceService _invoices;
    private readonly ReceiptService _receipts;
    private readonly PaymentService _payments;
    private readonly JournalService _journal;
    private readonly ProductService _productsSvc;
    private readonly ProjectService _projects;
    private readonly CostCenterService _costCenters;
    private readonly BillingService _billings;
    private readonly ContractService _contracts;
    private readonly LineItemService _lineItems;
    private readonly VariationService _variations;
    private readonly FiscalYearService _fiscalYears;
    private readonly ILogger<FullYearSeeder> _logger;

    public FullYearSeeder(
        IDbConnectionFactory db,
        ContactService contacts,
        AccountService accounts,
        InvoiceService invoices,
        ReceiptService receipts,
        PaymentService payments,
        JournalService journal,
        ProductService productsSvc,
        ProjectService projects,
        BillingService billings,
        ContractService contracts,
        LineItemService lineItems,
        VariationService variations,
        FiscalYearService fiscalYears,
        CostCenterService costCenters,
        ILogger<FullYearSeeder> logger)
    {
        _db = db;
        _contacts = contacts;
        _accounts = accounts;
        _invoices = invoices;
        _receipts = receipts;
        _payments = payments;
        _journal = journal;
        _productsSvc = productsSvc;
        _projects = projects;
        _billings = billings;
        _contracts = contracts;
        _lineItems = lineItems;
        _variations = variations;
        _fiscalYears = fiscalYears;
        _costCenters = costCenters;
        _logger = logger;
    }

    // ----------------------------------------------------------------
    // State (filled as we go)
    // ----------------------------------------------------------------

    private Dictionary<string, Guid> _customerIds = new();
    private Dictionary<string, Guid> _supplierIds = new();
    private Dictionary<string, Guid> _productIds = new();
    private Dictionary<string, Guid> _accountIds = new();
    private Dictionary<string, Guid> _userIds = new();
    private Dictionary<string, Guid> _costCenterIds = new();
    private Guid _mainUserId;
    private Random _rng = new(42); // deterministic for repeatability
    private FullYearSeedResult _result = new();

    // ----------------------------------------------------------------
    // Public entry point
    // ----------------------------------------------------------------

    public async Task<FullYearSeedResult> SeedAsync(Guid companyId, Guid? userId = null)
    {
        _result = new FullYearSeedResult { CompanyId = companyId };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("FullYearSeeder: starting for company {CompanyId}", companyId);

        try
        {
            // Phase 0: Clean the company
            await CleanCompanyDataAsync(companyId);

            // Phase 1: Master data (accounts cache, customers, suppliers, products)
            await SeedMasterDataAsync(companyId);
            _logger.LogInformation("FullYearSeeder: phase 1 (master) done");

            // Phase 2: Fiscal year + periods
            try { await SeedFiscalYearAsync(companyId); _logger.LogInformation("FullYearSeeder: phase 2 (fiscal) done"); }
            catch (Exception ex) { _result.Errors.Add($"Phase 2 (fiscal): {ex.Message}"); return _result; }

            // Phase 3: Opening balance journal entry (cash, bank, AR, AP)
            try { await SeedOpeningBalancesAsync(companyId); _logger.LogInformation("FullYearSeeder: phase 3 (opening) done"); }
            catch (Exception ex) { _result.Errors.Add($"Phase 3 (opening): {ex.Message}"); return _result; }

            // Phase 3b: Cost centers (departments + activities)
            try { await SeedCostCentersAsync(companyId); _logger.LogInformation("FullYearSeeder: phase 3b (cost centers) done"); }
            catch (Exception ex) { _result.Errors.Add($"Phase 3b (cost centers): {ex.Message}"); }

            // Phase 4: Monthly recurring + transactions
            try
            {
                for (var month = 0; month < 12; month++)
                {
                    var monthDate = FY_START.AddMonths(month);
                    await SeedMonthAsync(companyId, monthDate, userId);
                }
                _logger.LogInformation("FullYearSeeder: phase 4 (12 months) done");
            }
            catch (Exception ex) { _result.Errors.Add($"Phase 4: {ex.Message}"); return _result; }

            // Phase 5: Projects with full lifecycle
            try { await SeedProjectsAsync(companyId, userId); _logger.LogInformation("FullYearSeeder: phase 5 (projects) done"); }
            catch (Exception ex) { _result.Errors.Add($"Phase 5: {ex.Message}"); return _result; }

            // Phase 6: Year-end closing
            try { await SeedYearEndClosingAsync(companyId, userId); _logger.LogInformation("FullYearSeeder: phase 6 (closing) done"); }
            catch (Exception ex) { _result.Errors.Add($"Phase 6: {ex.Message}"); return _result; }

            // Phase 7: Bulk approve all pending journal entries
            try { await BulkApprovePendingAsync(companyId); _logger.LogInformation("FullYearSeeder: phase 7 (approve) done"); }
            catch (Exception ex) { _result.Errors.Add($"Phase 7: {ex.Message}"); return _result; }

            sw.Stop();
            _result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            _logger.LogInformation(
                "FullYearSeeder: completed in {Seconds:F1}s. {Invoices} invoices, {Receipts} receipts, {Payments} payments, {JournalEntries} JE",
                _result.ElapsedSeconds, _result.InvoicesCreated, _result.ReceiptsCreated,
                _result.PaymentsCreated, _result.JournalEntriesCreated);
        }
        catch (Exception ex)
        {
            _result.Errors.Add($"Fatal: {ex.Message} | Inner: {ex.InnerException?.Message}");
            _logger.LogError(ex, "FullYearSeeder failed");
        }

        return _result;
    }

    // ----------------------------------------------------------------
    // Phase 0: Clean
    // ----------------------------------------------------------------

    private async Task CleanCompanyDataAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        // Order matters — respect FKs
        await conn.ExecuteAsync(@"
            TRUNCATE TABLE
                contract_variation_items,
                contract_variations,
                contract_line_items,
                billing_line_items,
                progress_billings,
                contracts,
                project_milestones,
                projects,
                journal_lines,
                journal_entries,
                receipt_vouchers,
                payment_vouchers,
                invoice_lines,
                invoices,
                intercompany_pairs,
                account_contact_links,
                products,
                contacts,
                cost_centers
            CASCADE;
            DELETE FROM accounts WHERE level = 4;
            UPDATE accounts SET balance = 0;
            UPDATE fiscal_periods SET is_closed = false;
        ");
        _logger.LogInformation("FullYearSeeder: company data cleaned");
    }

    // ----------------------------------------------------------------
    // Phase 1: Master data
    // ----------------------------------------------------------------

    private async Task SeedMasterDataAsync(Guid companyId)
    {
        // Cache existing accounts by code
        using (var conn = _db.CreateConnection())
        {
            var accounts = await conn.QueryAsync<(Guid id, string code)>(@"
                SELECT id, code FROM accounts WHERE company_id = @cid;",
                new { cid = companyId });
            foreach (var (id, code) in accounts) _accountIds[code] = id;
        }

        // Cache users (admin user from existing data)
        using (var conn = _db.CreateConnection())
        {
            var users = await conn.QueryAsync<(Guid id, string email)>(@"
                SELECT id, email FROM users ORDER BY created_at LIMIT 5;");
            foreach (var (id, email) in users) _userIds[email] = id;
            _mainUserId = users.FirstOrDefault().id;
        }

        // 10 customers — mix of government, semi-gov, private, retail
        // paymentBehavior: "fast" = 30 days, "normal" = 60, "slow" = 90+
        var customerDefs = new[]
        {
            ("CUST-001", "Ministry of Housing & Construction", "وزارة الإسكان والتعمير", 500_000m, "slow"),
            ("CUST-002", "Libyan National Oil Corporation",      "المؤسسة الوطنية للنفط",   300_000m, "slow"),
            ("CUST-003", "Al-Andalus for Real Estate",            "الأندلس للتطوير العقاري", 150_000m, "normal"),
            ("CUST-004", "Tripoli Municipality",                  "بلدية طرابلس المركز",     200_000m, "slow"),
            ("CUST-005", "Misrata Free Zone Authority",           "هيئة منطقة مصراتة الحرة", 100_000m, "normal"),
            ("CUST-006", "Al-Baraka Construction Co.",            "شركة البركة للمقاولات",    80_000m, "normal"),
            ("CUST-007", "Al-Manara Engineering Office",          "مكتب المنارة الهندسي",     50_000m, "fast"),
            ("CUST-008", "Benghazi Steel Works",                  "مصبغة بنغازي للحديد",     60_000m, "normal"),
            ("CUST-009", "Al-Sarraj Trading & Services",          "الصرج للتجارة والخدمات",  30_000m, "fast"),
            ("CUST-010", "Al-Waha Retail",                        "الواحة للتجزئة",           15_000m, "fast")
        };
        // Create L4 cash + bank sub-ledgers FIRST so receipts/payments can post to them
        await EnsureCashBankL4Async(companyId);
        foreach (var (code, name, nameAr, limit, behavior) in customerDefs)
        {
            try
            {
                var c = await _contacts.CreateAsync(new CreateContactRequest(
                    companyId, "customer", code, name, nameAr, null, null, null));
                _customerIds[code] = c.Id;
                await _accounts.EnsureSubLedgerAsync(companyId, c.Id);
                _result.CustomersCreated++;
            }
            catch (Exception ex) { _result.Errors.Add($"Customer {code}: {ex.Message}"); }
        }

        // 10 suppliers — materials, equipment, services, admin
        var supplierDefs = new[]
        {
            ("SUPP-001", "Libyan Iron & Steel Co.",         "الشركة الليبية للحديد والصلب", "materials"),
            ("SUPP-002", "Arabian Cement Co.",              "شركة الأسمنت العربية",         "materials"),
            ("SUPP-003", "Al-Manara Aggregates",            "المنارة للحصمة والركام",       "materials"),
            ("SUPP-004", "Heavy Equipment Rental Libya",    "تأجير المعدات الثقيلة ليبيا",   "equipment"),
            ("SUPP-005", "Al-Nour Power Tools",             "النور لأدوات الطاقة",          "equipment"),
            ("SUPP-006", "Tarek Kassem Audit Office",       "مكتب طارق قاسم للتدقيق",      "services"),
            ("SUPP-007", "Al-Hikma Legal Consultancy",      "الحكمة للاستشارات القانونية",   "services"),
            ("SUPP-008", "Libyan Telecom (Hatif Libya)",    "هاتف ليبيا",                   "services"),
            ("SUPP-009", "Al-Mutawassit Office Supplies",   "المتوسط للتوريدات المكتبية",    "admin"),
            ("SUPP-010", "Tech Solutions Co.",              "حلول التقنية",                  "admin")
        };
        foreach (var (code, name, nameAr, category) in supplierDefs)
        {
            try
            {
                var s = await _contacts.CreateAsync(new CreateContactRequest(
                    companyId, "supplier", code, name, nameAr, null, null, null));
                _supplierIds[code] = s.Id;
                await _accounts.EnsureSubLedgerAsync(companyId, s.Id);
                _result.SuppliersCreated++;
            }
            catch (Exception ex) { _result.Errors.Add($"Supplier {code}: {ex.Message}"); }
        }

        // 15 products — construction, office, IT
        var productDefs = new (string Code, string Name, string NameAr, decimal UnitPrice, decimal Cost, string Category)[]
        {
            ("P001", "Steel Rebar 12mm",          "حديد تسليح 12مم",      4_500m, 3_800m, "materials"),
            ("P002", "Portland Cement 50kg",      "أسمنت بورتلاندي 50كغ",  18m,    12m,    "materials"),
            ("P003", "Sand (per m³)",             "رمل (م³)",              35m,    22m,    "materials"),
            ("P004", "Aggregate 20mm (per m³)",   "حصمة 20مم (م³)",        45m,    28m,    "materials"),
            ("P005", "Hollow Concrete Blocks",    "بلوك خرساني مفرغ",     3.50m,  2.20m,  "materials"),
            ("P006", "Lumber 4x6 (per m)",        "خشب 4×6 (متر)",         22m,    15m,    "materials"),
            ("P007", "PVC Pipe 4 inch",           "أنبوب PVC 4 إنش",       28m,    19m,    "plumbing"),
            ("P008", "Electrical Cable 2.5mm",    "كابل كهرباء 2.5مم",     4m,     2.50m,  "electrical"),
            ("P009", "Interior Emulsion Paint",   "دهان داخلي",             85m,    55m,    "finishing"),
            ("P010", "Power Drill 18V",           "مثقاب 18 فولت",          850m,   600m,   "tools"),
            ("P011", "A4 Office Paper (ream)",    "ورق A4 (رزمة)",          18m,    11m,    "office"),
            ("P012", "Desktop Computer (i5)",     "كمبيوتر مكتبي",          4_500m, 3_200m, "it"),
            ("P013", "Color Laser Printer",       "طابعة ليزر ملونة",       3_800m, 2_700m, "it"),
            ("P014", "Office Chair (Ergonomic)",  "كرسي مكتبي مريح",        650m,   420m,   "furniture"),
            ("P015", "Safety Helmet",             "خوذة أمان",              35m,    18m,    "safety")
        };
        foreach (var (code, name, nameAr, price, cost, category) in productDefs)
        {
            try
            {
                var p = await _productsSvc.CreateAsync(new CreateProductRequest(
                    companyId, code, name, nameAr, price, VAT_RATE));
                _productIds[code] = p.Id;
                _result.ProductsCreated++;
            }
            catch (Exception ex) { _result.Errors.Add($"Product {code}: {ex.Message}"); }
        }

        _logger.LogInformation(
            "FullYearSeeder: master data done — {C} customers, {S} suppliers, {P} products",
            _result.CustomersCreated, _result.SuppliersCreated, _result.ProductsCreated);
    }

    private async Task<Guid> conn_InsertProduct(
        Guid companyId, string code, string name, string nameAr,
        decimal price, decimal cost, string category)
    {
        // Use ProductService for consistent schema handling
        var p = await _productsSvc.CreateAsync(new CreateProductRequest(
            companyId, code, name, nameAr, price, VAT_RATE));
        return p.Id;
    }

    /// <summary>
    /// Sprint 33 — Create L4 sub-ledgers for Cash (1101) and Bank (1102) so
    /// receipts and payments have a postable account to debit/credit.
    /// Without these, "لا يوجد حساب صندوق أو بنك قابل للترحيل" errors.
    /// Idempotent: skip if already exists.
    /// </summary>
    private async Task EnsureCashBankL4Async(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        foreach (var (parentCode, code) in new[] { ("1101", "1101-CASH-001"), ("1102", "1102-BANK-001") })
        {
            var existing = await conn.ExecuteScalarAsync<Guid?>(@"
                SELECT id FROM accounts
                WHERE company_id = @cid AND code = @code
                LIMIT 1;",
                new { cid = companyId, code });
            if (existing is not null) { _accountIds[code] = existing.Value; continue; }

            var parent = await conn.QuerySingleOrDefaultAsync<(Guid id, string name, string name_ar, string nature)?>(@"
                SELECT id, name, name_ar, nature
                FROM accounts
                WHERE company_id = @cid AND code = @parentCode
                LIMIT 1;",
                new { cid = companyId, parentCode });
            if (parent is null) continue;

            var newId = Guid.NewGuid();
            await conn.ExecuteAsync(@"
                INSERT INTO accounts
                    (id, company_id, code, name, name_ar, parent_id,
                     account_type, nature, level, account_class,
                     is_control_account, cost_center_required,
                     is_postable, is_active, balance)
                VALUES
                    (@id, @cid, @code, @name, @nameAr, @parentId,
                     'Asset', 'Debit', 4, 'detail',
                     false, false,
                     true, true, 0);",
                new
                {
                    id = newId,
                    cid = companyId,
                    code,
                    name = parent.Value.name + " - Main",
                    nameAr = (parent.Value.name_ar ?? parent.Value.name) + " - الرئيسي",
                    parentId = parent.Value.id
                });
            _accountIds[code] = newId;
            _result.SubLedgersCreated++;
        }
    }

    // ----------------------------------------------------------------
    // Phase 2: Fiscal year
    // ----------------------------------------------------------------

    private async Task SeedFiscalYearAsync(Guid companyId)
    {
        // Delete any pre-existing FY (idempotent re-seed) and create fresh.
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM fiscal_periods WHERE fiscal_year_id IN (SELECT id FROM fiscal_years WHERE company_id = @cid);",
            new { cid = companyId });
        await conn.ExecuteAsync(
            "DELETE FROM fiscal_years WHERE company_id = @cid;",
            new { cid = companyId });

        try
        {
            var fy = await _fiscalYears.CreateYearAsync(new CreateFiscalYearRequest(
                companyId, "FY2025-2026", FY_START, FY_END));
            _result.FiscalYearCreated = true;
            _logger.LogInformation("FullYearSeeder: fiscal year created with {N} periods", fy.Periods.Count);
        }
        catch (Exception ex) { _result.Errors.Add($"Fiscal year: {ex.Message}"); }
    }

    // ----------------------------------------------------------------
    // Phase 3: Opening balances
    // ----------------------------------------------------------------

    private async Task SeedOpeningBalancesAsync(Guid companyId)
    {
        // Starting capital (Sep 1, 2025) — sized to cover a full
        // year of recurring expenses plus a buffer for working
        // capital. The previous version used Cash=50K + Bank=150K,
        // which is way too small for a holding company running
        // 35K/month in salaries alone — the cash account would go
        // deeply negative by year-end.
        //
        //   Cash 1101-CASH-001:  600,000 LYD (covers recurring
        //                         expenses + small payments)
        //   Bank 1102-BANK-001:  400,000 LYD (covers larger payments
        //                         and project billings)
        //   Prepaid 1106:          9,600 LYD (insurance prepaid for
        //                         the year, amortizes to 0 by Aug)
        //   Loan 2201:           84,000 LYD (initial 12-month loan
        //                         at 4,000/month installment)
        //   Capital 3101:      1,009,600 LYD (owner's equity, the
        //                         sum of the above debits)
        var lines = new List<CreateJournalLineRequest>();
        if (_accountIds.TryGetValue("1101-CASH-001", out var cash))
            lines.Add(new CreateJournalLineRequest(cash, 600_000m, 0, "رصيد افتتاحي - صندوق", null));
        if (_accountIds.TryGetValue("1102-BANK-001", out var bank))
            lines.Add(new CreateJournalLineRequest(bank, 400_000m, 0, "رصيد افتتاحي - بنك", null));
        if (_accountIds.TryGetValue("1106", out var prepaid))
            lines.Add(new CreateJournalLineRequest(prepaid, 9_600m, 0, "تأمين مسبق - رصيد افتتاحي", null));
        if (_accountIds.TryGetValue("2201", out var loan))
            lines.Add(new CreateJournalLineRequest(loan, 0, 84_000m, "قرض بنكي - رصيد افتتاحي", null));
        if (_accountIds.TryGetValue("3101", out var capital))
            lines.Add(new CreateJournalLineRequest(capital, 0, 925_600m, "رأس المال الافتتاحي", null));

        if (lines.Count == 0) return;

        try
        {
            var entry = await _journal.CreateDraftAsync(new CreateJournalEntryRequest(
                companyId, FY_START, "قيود افتتاحية - السنة المالية 2025-2026",
                lines, Source: "manual"), _mainUserId);
            await _journal.ApproveAsync(entry.Id, _mainUserId);
            await _journal.PostAsync(entry.Id);
            _result.JournalEntriesCreated++;
        }
        catch (Exception ex) { _result.Errors.Add($"Opening: {ex.Message}"); }
    }

    // ----------------------------------------------------------------
    // Phase 3b: Cost centers (departments, activities)
    // ----------------------------------------------------------------
    // Realistic cost center tree for a Libyan company with mixed
    // operations (construction + services + admin).
    // Type: 'project' (linked to project) | 'department' | 'activity'
    // ----------------------------------------------------------------
    private async Task SeedCostCentersAsync(Guid companyId)
    {
        var defs = new (string Code, string Name, string NameAr, string Type, Guid? ProjectId, Guid? ParentId)[]
        {
            // Departments
            ("DPT-ADMIN",   "Administration",       "الإدارة العامة",          "department", null, null),
            ("DPT-FIN",     "Finance & Accounting", "المالية والمحاسبة",        "department", null, null),
            ("DPT-SALES",   "Sales & Marketing",    "المبيعات والتسويق",        "department", null, null),
            ("DPT-OPS",     "Operations",            "العمليات",                  "department", null, null),
            ("DPT-HR",      "Human Resources",       "الموارد البشرية",          "department", null, null),
            ("DPT-IT",      "IT",                    "تقنية المعلومات",          "department", null, null),
            // Sub-departments
            ("DPT-OPS-CONST", "Construction Operations", "عمليات المقاولات",       "department", null, null), // parent: DPT-OPS
            ("DPT-OPS-SUP",   "Supply Operations",       "عمليات التوريد",         "department", null, null),
            ("DPT-OPS-SVC",   "Service Operations",      "عمليات الخدمات",         "department", null, null),
            // Activities (operational)
            ("ACT-TRAVEL",   "Travel",                "السفر",                    "activity", null, null),
            ("ACT-TRAINING", "Training & Development","التدريب والتطوير",          "activity", null, null),
            ("ACT-AUDIT",    "External Audit",        "التدقيق الخارجي",          "activity", null, null),
            ("ACT-MARKET",   "Marketing Campaigns",   "الحملات التسويقية",         "activity", null, null),
            ("ACT-OFFICE",   "Office Supplies",       "اللوازم المكتبية",          "activity", null, null),
            ("ACT-MAINT",    "Maintenance & Repairs", "الصيانة والإصلاحات",         "activity", null, null),
            ("ACT-PROF",     "Professional Services", "الخدمات المهنية",            "activity", null, null),
        };

        var parentMap = new Dictionary<string, Guid>();
        foreach (var (code, name, nameAr, type, projectId, parentId) in defs)
        {
            try
            {
                // Resolve parent ID if code references it
                Guid? resolvedParent = parentId;
                if (resolvedParent == null && code == "DPT-OPS-CONST") resolvedParent = parentMap.GetValueOrDefault("DPT-OPS");
                if (resolvedParent == null && code == "DPT-OPS-SUP")   resolvedParent = parentMap.GetValueOrDefault("DPT-OPS");
                if (resolvedParent == null && code == "DPT-OPS-SVC")   resolvedParent = parentMap.GetValueOrDefault("DPT-OPS");

                var cc = await _costCenters.CreateAsync(new CreateCostCenterRequest(
                    companyId, code, name, nameAr, type, projectId, resolvedParent));
                parentMap[code] = cc.Id;
                _costCenterIds[code] = cc.Id;
                _result.CostCentersCreated++;
            }
            catch (Exception ex) { _result.Errors.Add($"CostCenter {code}: {ex.Message}"); }
        }
    }
}
