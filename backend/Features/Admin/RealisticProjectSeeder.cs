using Dapper;
using ErpV2.Common;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Projects;

namespace ErpV2.Features.Admin;

/// <summary>
/// Sprint 50 / Sprint 51 — Realistic Project Scenario Seeder.
///
/// Creates a clean, demonstrable scenario for ONE project over FOUR
/// monthly billings, plus the project-tagged purchase invoices that
/// fund it. This is the "demo" data the user wants to navigate with
/// the accountant role and verify the full project P&L chain works.
///
/// Scope (deliberately small — the user said "keep the number of
/// entries low so I can follow the audit trail"):
///   - 1 customer (Ministry of Housing & Construction — the project
///     owner)
///   - 4 suppliers (steel, cement, labor agency, electrical) with
///     their sub-ledgers
///   - 5 products (4 materials + 1 labor service) with categories +
///     default accounts wired up
///   - 1 project (مدرسة الحكمة) with a 4M LYD contract split into 7
///     line items
///   - 4 monthly billings (BIL-001 to BIL-004) at 10%, 30%, 60%, 90%
///     completion. Each one creates one posted invoice + one posted
///     journal entry on the AR sub-ledger + 4103 project revenue.
///   - 4 project-tagged purchase invoices (steel, cement, labor,
///     electrical) — each creating one posted journal entry on a
///     54xx project sub-ledger + 2101 supplier sub-ledger.
///   - 2 regular (non-project) sales invoices for the same customer
///     to verify the sales rule still works without a project.
///   - 2 regular (non-project) purchase invoices to verify the
///     PurchaseInvoiceApproved rule (COGS 5101/5102) still fires
///     when projectId is null.
///
/// This seeder assumes the COA is intact (L1-L3 levels). The 7
/// project-specific L4 sub-ledgers are auto-created by
/// ProjectCostAccountService when the project is inserted (Cycle 7).
/// </summary>
public class RealisticProjectSeeder
{
    private readonly IDbConnectionFactory _db;
    private readonly ProjectService _projectSvc;
    private readonly InvoiceService _invoiceSvc;
    private readonly BillingService _billingSvc;
    private readonly ErpV2.Features.Accounts.AccountService _accounts;
    private readonly ILogger<RealisticProjectSeeder> _log;

    public RealisticProjectSeeder(
        IDbConnectionFactory db,
        ProjectService projectSvc,
        InvoiceService invoiceSvc,
        BillingService billingSvc,
        ErpV2.Features.Accounts.AccountService accounts,
        ILogger<RealisticProjectSeeder> log)
    {
        _db = db;
        _projectSvc = projectSvc;
        _invoiceSvc = invoiceSvc;
        _billingSvc = billingSvc;
        _accounts = accounts;
        _log = log;
    }

    public async Task<RealisticSeedResult> RunAsync(Guid companyId, bool trustedMode = true)
    {
        var result = new RealisticSeedResult { CompanyId = companyId };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Force the trusted-accountant runtime override so that every
        // posting auto-approves and posts. The user wants the
        // scenario to be navigable end-to-end without manual
        // approvals.
        TrustedAccountantMode.SetOverride(trustedMode);

        // Cleanup (separate transaction — drop everything cleanly)
        using (var conn = _db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await CleanupAsync(conn, tx, companyId);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        // ---- 1. Customer (needed before project for CustomerId link) ----
        // Sprint 52 — the project's CustomerId is required by the
        // BillingService.ApproveAsync (it throws "لا يوجد عميل مرتبط
        // بالمشروع" if the project has no customer). The previous
        // order created the project first, then created the customer
        // later in CreateRegularSalesInvoicesAsync — but by then the
        // billings step had already failed. Move the customer
        // creation to step 1 so the project can reference it.
        var customerId = await EnsureCustomerAsync(companyId);

        // ---- 2. Project (triggers auto-create of 7 L4 sub-ledgers) ----
        var projectId = await CreateProjectAsync(companyId, customerId);
        result.ProjectId = projectId;

        // ---- 3. Suppliers (with sub-ledgers via service) ----
        var suppliers = await CreateSuppliersAsync(companyId);
        result.SuppliersCreated = suppliers.Count;

        // ---- 4. Products (categories + default accounts) ----
        var products = await CreateProductsAsync(companyId);
        result.ProductsCreated = products.Count;

        // ---- 5. Project-tagged purchase invoices (4 invoices) ----
        var purchaseInvoices = await CreateProjectPurchaseInvoicesAsync(
            companyId, projectId, suppliers, products);
        result.PurchaseInvoicesCreated = purchaseInvoices.Count;
        result.PurchaseInvoiceJEsPosted = purchaseInvoices.Count;

        // ---- 6. The 4 monthly billings ----
        var billings = await CreateBillingsAsync(companyId, projectId);
        result.BillingsCreated = billings.Count;
        result.BillingJEsPosted = billings.Count;

        // ---- 7. Two regular sales invoices (non-project) ----
        // Reuses the customer created in step 1.
        var salesInvoices = await CreateRegularSalesInvoicesAsync(companyId, customerId);
        result.SalesInvoicesCreated = salesInvoices.Count;

        // ---- 7. Two regular purchase invoices (non-project, 5101/5102) ----
        var regPurchaseInvoices = await CreateRegularPurchaseInvoicesAsync(
            companyId, suppliers);
        result.RegularPurchaseInvoicesCreated = regPurchaseInvoices.Count;

        result.ElapsedSeconds = sw.Elapsed.TotalSeconds;
        result.AllSucceeded = true;
        result.Message = $"تم إدخال السيناريو بنجاح. {result.BillingsCreated} مستخلصات + {result.PurchaseInvoicesCreated} فواتير مخصصة + {result.RegularPurchaseInvoicesCreated} فواتير عادية + {result.SalesInvoicesCreated} فواتير مبيعات.";
        _log.LogInformation(result.Message);
        return result;
    }

    // ----------------- Step implementations -----------------

    private async Task CleanupAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid companyId)
    {
        _log.LogInformation("RealisticProjectSeeder: cleanup start for company {CompanyId}", companyId);

        // The cleanup order is important because the schema has
        // RESTRICT foreign keys (e.g. receipt_vouchers.invoice_id
        // → invoices.id with no cascade). We delete in REVERSE
        // dependency order: dependent rows first, parents last.

        // 1) Billings (depend on project + contract)
        await conn.ExecuteAsync(@"
            DELETE FROM billing_line_items
            WHERE billing_id IN (
                SELECT pb.id FROM progress_billings pb
                JOIN projects p ON p.id = pb.project_id
                WHERE p.company_id = @companyId
            );", new { companyId }, tx);
        await conn.ExecuteAsync(@"
            DELETE FROM progress_billings
            WHERE project_id IN (
                SELECT id FROM projects WHERE company_id = @companyId
            );", new { companyId }, tx);

        // 2) Vouchers FIRST (they reference invoices, and are
        //    deleted before invoices to satisfy the FK)
        await conn.ExecuteAsync(@"
            DELETE FROM receipt_vouchers WHERE company_id = @companyId;", new { companyId }, tx);
        await conn.ExecuteAsync(@"
            DELETE FROM payment_vouchers WHERE company_id = @companyId;", new { companyId }, tx);

        // 3) Intercompany pairs (they reference invoices)
        // NOTE: the schema columns are primary_invoice_id and
        // mirror_invoice_id, NOT invoice_a_id / invoice_b_id.
        await conn.ExecuteAsync(@"
            DELETE FROM intercompany_pairs
            WHERE primary_company_id = @companyId
               OR mirror_company_id = @companyId;",
            new { companyId }, tx);

        // 4) Invoices (and their line items)
        await conn.ExecuteAsync(@"
            DELETE FROM invoice_lines
            WHERE invoice_id IN (
                SELECT id FROM invoices WHERE company_id = @companyId
            );", new { companyId }, tx);
        await conn.ExecuteAsync(@"
            DELETE FROM invoices WHERE company_id = @companyId;", new { companyId }, tx);

        // 5) JEs (and their lines)
        await conn.ExecuteAsync(@"
            DELETE FROM journal_lines
            WHERE journal_entry_id IN (
                SELECT id FROM journal_entries WHERE company_id = @companyId
            );", new { companyId }, tx);
        await conn.ExecuteAsync(@"
            DELETE FROM journal_entries WHERE company_id = @companyId;", new { companyId }, tx);

        // 6) L4 sub-ledger accounts (the project + supplier ones)
        await conn.ExecuteAsync(@"
            DELETE FROM account_contact_links
            WHERE account_id IN (
                SELECT id FROM accounts
                WHERE company_id = @companyId AND level = 4
            );", new { companyId }, tx);
        await conn.ExecuteAsync(@"
            DELETE FROM accounts
            WHERE company_id = @companyId AND level = 4;", new { companyId }, tx);

        // 7) Contracts (and their line items)
        await conn.ExecuteAsync(@"
            DELETE FROM contract_line_items
            WHERE contract_id IN (
                SELECT id FROM contracts WHERE company_id = @companyId
            );", new { companyId }, tx);
        await conn.ExecuteAsync(@"
            DELETE FROM contracts WHERE company_id = @companyId;", new { companyId }, tx);

        // 8) Projects (cascades milestones, variations, costs, revenue)
        await conn.ExecuteAsync(@"
            DELETE FROM projects WHERE company_id = @companyId;", new { companyId }, tx);

        // 9) Customers + suppliers
        await conn.ExecuteAsync(@"
            DELETE FROM contacts WHERE company_id = @companyId
              AND type IN ('customer', 'supplier');",
            new { companyId }, tx);

        // 10) Products
        await conn.ExecuteAsync(@"
            DELETE FROM products WHERE company_id = @companyId;", new { companyId }, tx);

        // 11) Reset account balances
        await conn.ExecuteAsync(@"
            UPDATE accounts SET balance = 0 WHERE company_id = @companyId;", new { companyId }, tx);

        // 12) Re-open fiscal periods
        await conn.ExecuteAsync(@"
            UPDATE fiscal_periods SET is_closed = false
            WHERE fiscal_year_id IN (
                SELECT id FROM fiscal_years WHERE company_id = @companyId
            );", new { companyId }, tx);

        _log.LogInformation("RealisticProjectSeeder: cleanup complete");
    }

    private async Task<Guid> CreateProjectAsync(Guid companyId, Guid customerId)
    {
        var req = new CreateProjectRequest(
            CompanyId: companyId,
            Code: "PRJ-2026-100",
            Name: "مشروع إنشاء مدرسة الحكمة",
            NameAr: "مشروع إنشاء مدرسة الحكمة",
            Description: "مبنى مدرسي يتكون من 3 طوابق + ملعب + سور خارجي",
            StartDate: new DateTime(2026, 1, 1),
            EndDate: new DateTime(2026, 12, 31),
            Budget: 4000000m,
            Notes: null,
            Type: "construction",
            // Sprint 52 — the project must reference a customer so
            // billings can be approved. The customer is created
            // earlier in the seeder (step 1) so its id is available here.
            CustomerId: customerId,
            ContractValue: 4000000m,  // 4M LYD
            ExpectedEndDate: new DateTime(2026, 12, 31),
            ProjectManager: "م. أحمد الفيتوري",
            Location: "طرابلس - حي الأندلس"
        );
        var proj = await _projectSvc.CreateAsync(req);
        _log.LogInformation("Created project {Code} (auto-L4-sub-ledgers created)", proj.Code);

        // Create a contract with 7 line items summing to 4M
        // (The contracts table has project_id; the projects table
        // doesn't carry a contract_id back — it's a one-way
        // relationship.)
        var contractId = await CreateContractAsync(companyId, proj.Id, proj.ContractValue);

        return proj.Id;
    }

    private async Task<Guid> CreateContractAsync(Guid companyId, Guid projectId, decimal contractValue)
    {
        using var conn = _db.CreateConnection();
        var contractId = Guid.NewGuid();
        // Sprint 53: include the 3 new deduction columns so the
        // seeded contract matches the Libyan construction contract
        // model (final insurance 2% + admin fees 1.5%). Original
        // contract deduction 15% is hard-coded in BillingService for
        // the first billing (not stored here).
        await conn.ExecuteAsync(@"
            INSERT INTO contracts (id, company_id, project_id, contract_number, contract_value,
                                   advance_percent, retention_percent, retention_start_billing,
                                   final_insurance_percent, admin_fee_percent,
                                   start_date, end_date)
            VALUES (@id, @companyId, @projectId, @number, @value, 20, 5, 1,
                    2, 1.5,
                    '2026-01-01', '2026-12-31');",
            new
            {
                id = contractId,
                companyId,
                projectId,
                number = "CONT-2026-100",
                value = contractValue
            });

        // 7 line items summing to exactly 4,000,000 LYD
        // (item #7's unit price is auto-adjusted to fit the total)
        var items = new List<(string desc, string unit, decimal qty, decimal price)>
        {
            ("حفر الأساسات",                "m3",  500m,   500m),    // 250,000
            ("صب الخرسانة المسلحة",         "m3",  400m,  1500m),    // 600,000
            ("بناء الجدران (بلوك)",        "m2", 1200m,   250m),    // 300,000
            ("أعمال الحديد والتسليح",      "ton",  80m,  8000m),    // 640,000
            ("أعمال السباكة",                "lot",   1m, 400000m),  // 400,000
            ("أعمال الكهرباء",               "lot",   1m, 350000m),  // 350,000
        };
        decimal running = 0;
        for (int i = 0; i < items.Count; i++)
            running += items[i].qty * items[i].price;
        // Last item: التشطيبات (finishing/paint) — fill the remainder
        var lastTotal = contractValue - running;
        var lastQty = 1500m;  // 1500 m2 of finishing work
        var lastPrice = Math.Round(lastTotal / lastQty, 2);
        items.Add(("التشطيبات والدهانات", "m2", lastQty, lastPrice));

        int lineNum = 1;
        foreach (var (desc, unit, qty, price) in items)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO contract_line_items
                    (id, company_id, contract_id, line_number, description, unit,
                     quantity, unit_price, total_price, notes)
                VALUES (@id, @companyId, @contractId, @lineNumber, @description, @unit,
                        @quantity, @unitPrice, @total, NULL);",
                new
                {
                    id = Guid.NewGuid(),
                    companyId,
                    contractId,
                    lineNumber = lineNum++,
                    description = desc,
                    unit,
                    quantity = qty,
                    unitPrice = price,
                    total = qty * price
                });
        }
        return contractId;
    }

    // Sprint 52 — ensure the customer "وزارة الإسكان والتعمير" exists
    // and return its id. The seeder creates the customer early so the
    // project can reference it (project.CustomerId is required by
    // BillingService.ApproveAsync).
    //
    // The customer lookup is on the Arabic name_ar column. The seeder
    // re-uses this helper twice (here + later) so any change to the
    // customer name stays in one place.
    private async Task<Guid> EnsureCustomerAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT id FROM contacts
            WHERE company_id = @companyId AND type = 'customer' AND is_active = true
            LIMIT 1;",
            new { companyId });
        if (existing.HasValue) return existing.Value;

        // contacts.code is NOT NULL UNIQUE per (company_id, type, code).
        // Use a stable CUS-001 code so re-runs don't conflict.
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contacts
                (id, company_id, type, code, name, name_ar, tax_id, phone, email,
                 is_active, is_demo_data, created_at)
            VALUES
                (@id, @companyId, 'customer', 'CUS-001', 'Ministry of Housing', 'وزارة الإسكان والتعمير',
                 NULL, NULL, NULL, true, false, NOW());",
            new { id, companyId });
        // Sprint 52 — auto-create L4 sub-ledger (1103-CUS-001).
        await _accounts.EnsureSubLedgerAsync(companyId, id);
        return id;
    }

    private async Task<string> GetCustomerNameAsync(Guid companyId, Guid customerId)
    {
        using var conn = _db.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<string?>(@"
            SELECT name_ar FROM contacts
            WHERE id = @id AND company_id = @companyId AND type = 'customer';",
            new { id = customerId, companyId }) ?? "";
    }

    private async Task<List<(Guid id, string name)>> CreateSuppliersAsync(Guid companyId)
    {
        var suppliers = new[]
        {
            ("الشركة الليبية للحديد والصلب", "Libyan Iron & Steel Co."),
            ("شركة الأسمنت العربية",         "Arabian Cement Co."),
            ("مكتب المقاولات العامة",         "General Contractors Office"),
            ("شركة الأدوات الكهربائية",       "Electrical Tools Co."),
        };
        var result = new List<(Guid id, string name)>();
        using var conn = _db.CreateConnection();
        int codeNum = 1;
        foreach (var (nameAr, nameEn) in suppliers)
        {
            var id = Guid.NewGuid();
            // contacts.code is NOT NULL UNIQUE per (company_id, type, code).
            // We synthesize SUP-001..SUP-NNN codes from the index. The
            // contacts table is a per-company catalogue so codes only
            // need to be unique within this company.
            var code = $"SUP-{codeNum++:D3}";
            await conn.ExecuteAsync(@"
                INSERT INTO contacts
                    (id, company_id, type, code, name, name_ar, tax_id, phone, email,
                     is_active, is_demo_data, created_at)
                VALUES
                    (@id, @companyId, 'supplier', @code, @name, @nameAr, NULL, NULL, NULL,
                     true, false, NOW());",
                new { id, companyId, code, name = nameEn, nameAr });
            // Sprint 52 — auto-create the L4 sub-ledger
            // (2101-SUP-XXX) for each supplier. Without this, the
            // rule engine's contact.subLedger directive in the
            // 5 default rules + the 6th rule can't resolve to a
            // postable account, and the rule's exception is
            // swallowed by the rule loop, leaving entries.Count=0
            // which the InvoiceService reports as
            // "لا توجد قواعد محاسبية مفعّلة".
            await _accounts.EnsureSubLedgerAsync(companyId, id);
            result.Add((id, nameAr));
        }
        return result;
    }

    private async Task<List<(Guid id, string code)>> CreateProductsAsync(Guid companyId)
    {
        var products = new (string code, string name, string category, decimal price)[]
        {
            ("MAT-001", "حديد تسليح 12مم",      "materials",  4500m),
            ("MAT-002", "أسمنت بورتلاندي 50كغ", "materials",    18m),
            ("MAT-003", "بلوك خرساني مفرغ",     "materials",   3.5m),
            ("MAT-004", "كابل كهرباء 2.5مم",     "materials",     4m),
            ("SVC-001", "أجور عمال بناء (يومية)","labor",      120m),
        };

        var result = new List<(Guid id, string code)>();
        using var conn = _db.CreateConnection();
        // Look up the 54xx L3 control account ids
        var l3Accounts = (await conn.QueryAsync<(string code, Guid id)>(@"
            SELECT code, id FROM accounts
            WHERE company_id = @companyId AND level = 3 AND code LIKE '54%';",
            new { companyId })).ToDictionary(x => x.code, x => x.id);

        foreach (var (code, name, category, price) in products)
        {
            var id = Guid.NewGuid();
            // 5401 = materials, 5402 = labor (Sprint 50 mapping)
            string l3Code = category == "labor" ? "5402" : "5401";
            Guid? defaultAcct = l3Accounts.TryGetValue(l3Code, out var acctId) ? acctId : (Guid?)null;
            await conn.ExecuteAsync(@"
                INSERT INTO products (id, company_id, code, name, name_ar, unit_price,
                                       default_tax_rate, is_active, category, default_account_id)
                VALUES (@id, @companyId, @code, @name, @nameAr, @price, 0.04, true,
                        @category, @defaultAccountId);",
                new { id, companyId, code, name, nameAr = name, price, category, defaultAccountId = defaultAcct });
            result.Add((id, code));
        }
        return result;
    }

    private async Task<List<Guid>> CreateProjectPurchaseInvoicesAsync(
        Guid companyId, Guid projectId,
        List<(Guid id, string name)> suppliers,
        List<(Guid id, string code)> products)
    {
        // 4 project-tagged purchase invoices
        var inv1 = await CreateSingleProjectInvoiceAsync(
            companyId, projectId, suppliers[0], new[] { (products[0].id, 50m) },
            "2026-02-15", "INV-P-2026-101", "حديد تسليح 50 طن");
        var inv2 = await CreateSingleProjectInvoiceAsync(
            companyId, projectId, suppliers[1], new[] { (products[1].id, 5000m) },
            "2026-03-10", "INV-P-2026-102", "أسمنت 5000 كيس");
        var inv3 = await CreateSingleProjectInvoiceAsync(
            companyId, projectId, suppliers[2], new[] {
                (products[2].id, 4000m),  // 4000 بلوك
                (products[4].id, 200m)    // 200 يومية أجور
            }, "2026-04-20", "INV-P-2026-103", "بلوك + أجور عمال");
        var inv4 = await CreateSingleProjectInvoiceAsync(
            companyId, projectId, suppliers[3], new[] { (products[3].id, 2000m) },
            "2026-05-15", "INV-P-2026-104", "كابلات كهرباء");

        return new List<Guid> { inv1, inv2, inv3, inv4 };
    }

    private async Task<Guid> CreateSingleProjectInvoiceAsync(
        Guid companyId, Guid projectId, (Guid id, string name) supplier,
        (Guid id, decimal qty)[] lineItems, string date, string invNumber, string description)
    {
        using var conn = _db.CreateConnection();
        // Look up the project's L4 sub-ledgers (auto-created by
        // ProjectCostAccountService when the project was inserted).
        var subLedgers = (await conn.QueryAsync<(string code, Guid id)>(@"
            SELECT a.code, a.id FROM accounts a
            JOIN accounts parent ON parent.id = a.parent_id
            WHERE a.company_id = @companyId
              AND a.level = 4
              AND parent.code LIKE '54%'
              AND a.code LIKE '%' || @projectCode
            ORDER BY a.code;",
            new { companyId, projectCode = "PRJ-2026-100" })).ToList();
        if (subLedgers.Count < 2)
            throw new InvalidOperationException(
                $"Project sub-ledgers not found (got {subLedgers.Count}). The project L4 sub-ledgers must be auto-created at project insert time.");

        // Look up each product's category
        var products = (await conn.QueryAsync<(Guid id, string category, decimal unit_price)>(
            "SELECT id, category, unit_price FROM products WHERE id = ANY(@ids);",
            new { ids = lineItems.Select(x => x.id).ToArray() })).ToList();
        var prodMap = products.ToDictionary(x => x.id);

        var lines = new List<CreateInvoiceLineRequest>();
        foreach (var (pid, qty) in lineItems)
        {
            var prod = prodMap[pid];
            string l3Code = prod.category == "labor" ? "5402" : "5401";
            string subCode = $"{l3Code}-PRJ-2026-100";
            var sub = subLedgers.FirstOrDefault(s => s.code == subCode);
            if (sub.id == Guid.Empty)
                throw new InvalidOperationException(
                    $"Sub-ledger {subCode} not found — the project L4 sub-ledgers were not auto-created.");

            lines.Add(new CreateInvoiceLineRequest(
                AccountId: sub.id,
                ProductId: pid,
                Description: description,
                Quantity: qty,
                UnitPrice: prod.unit_price,
                TaxRate: null
            ));
        }

        var req = new CreateInvoiceRequest(
            CompanyId: companyId,
            InvoiceType: "purchase",
            InvoiceDate: DateTime.Parse(date),
            PartyName: supplier.name,
            PartyNameAr: supplier.name,
            PartyTaxId: null,
            Notes: $"تكلفة مخصصة لمشروع المدرسة — {description}",
            TaxRate: 0.04m,
            IntercompanyCompanyId: null,
            ProjectId: projectId,
            Lines: lines
        );

        var draft = await _invoiceSvc.CreateDraftAsync(req, null);
        // Post — in trusted mode the rule fires and auto-approves.
        var posted = await _invoiceSvc.PostAsync(draft.Id);
        return posted.Id;
    }

    private async Task<List<Guid>> CreateBillingsAsync(Guid companyId, Guid projectId)
    {
        // 4 monthly billings. Each one calls BillingService.ApproveAsync
        // which in trusted mode auto-posts the journal entry.
        var dates = new[] { "2026-02-28", "2026-05-31", "2026-08-31", "2026-11-30" };
        var numbers = new[] { "BIL-2026-001", "BIL-2026-002", "BIL-2026-003", "BIL-2026-004" };
        var notes = new[] {
            "مستخلص شهر فبراير — أعمال الأساسات والخرسانة",
            "مستخلص شهر مايو — الجدران والتسليح",
            "مستخلص شهر أغسطس — السباكة والكهرباء",
            "مستخلص شهر نوفمبر — التشطيبات النهائية"
        };

        using var conn = _db.CreateConnection();
        // The contract is the one whose project_id = this project. The
        // projects table doesn't store the back-pointer.
        var contractId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT id FROM contracts WHERE project_id = @id LIMIT 1;",
            new { id = projectId });
        if (contractId == null || contractId == Guid.Empty)
            throw new InvalidOperationException("Project has no contract — cannot create billings.");

        var result = new List<Guid>();
        // Each billing represents a cumulative % of the contract work done.
        // The seeder creates billings at 30%, 50%, 80%, 100% — each one
        // advances the work_completed_percent.
        //
        // Why not 10%? Because the BillingService recovers the
        // contract advance (20% of contract value = 800K LYD) from
        // the FIRST billing's gross. With 10% (gross=400K), the
        // advance recovered = 400K and retention = 20K, giving
        // net = -20K (negative). The journal validator rejects
        // negative debit/credit lines. Starting at 30% (gross=1.2M)
        // gives net = 1.2M - 800K - 60K = 340K (positive), and
        // subsequent billings have remainingAdvance = 0 so no
        // advance is deducted.
        //
        // Sprint 52 (work-flow fix #1) — pre-populate the
        // billing_line_items with quantities proportional to the
        // cumulative %. The user pointed out that the billings UI
        // showed all "السابق" and "هذه الفترة" as 0 — because the
        // seeder passed an empty LineItems list. The BillingService
        // %-based path synthesizes ONE line item (a lump), but the
        // contract has 7 BOQ line items. We pre-populate here so the
        // user can see per-line quantities (e.g. "حفر الأساسات
        // 150 m3 of 500 m3 = 30%") in the billings UI.
        //
        // Example for billing 1 (30% cumulative):
        //   contract_line_item #1 (500 m3 × 500 LYD) → this billing 150 m3
        //   contract_line_item #2 (400 m3 × 1500 LYD) → 120 m3
        //   ...etc
        var cumulativePercents = new decimal[] { 30m, 50m, 80m, 100m };
        // Load the contract_line_items once for use across all 4 billings.
        var contractLineItems = (await conn.QueryAsync<(Guid id, int line_number, decimal quantity, decimal unit_price)>(@"
            SELECT id, line_number, quantity, unit_price FROM contract_line_items
            WHERE contract_id = @contractId
            ORDER BY line_number;",
            new { contractId })).ToList();
        if (contractLineItems.Count == 0)
            throw new InvalidOperationException("Contract has no line items — cannot compute billing quantities.");

        for (int i = 0; i < numbers.Length; i++)
        {
            // For each billing, compute this-period quantities for each BOQ line.
            // The cumulative percent maps to a cumulative quantity, and
            // quantity_this_period = cumulative - previous_cumulative.
            var cumulative = cumulativePercents[i] / 100m;
            var previousCumulative = i == 0 ? 0m : cumulativePercents[i - 1] / 100m;
            var lineItems = new List<CreateBillingLineItemRequest>();
            foreach (var li in contractLineItems)
            {
                var cumulativeQty = Math.Round(li.quantity * cumulative, 3);
                var previousQty = Math.Round(li.quantity * previousCumulative, 3);
                var thisPeriodQty = cumulativeQty - previousQty;
                if (thisPeriodQty > 0)
                {
                    lineItems.Add(new CreateBillingLineItemRequest(
                        LineItemId: li.id,
                        QuantityThisPeriod: thisPeriodQty,
                        Notes: null
                    ));
                }
            }

            var req = new CreateBillingRequest(
                ContractId: contractId!.Value,
                BillingNumber: numbers[i],
                BillingDate: DateTime.Parse(dates[i]),
                PeriodFrom: i == 0 ? new DateTime(2026, 1, 1) : DateTime.Parse(dates[i - 1]),
                PeriodTo: DateTime.Parse(dates[i]),
                // Pass WorkCompletedPercent as a fallback (the system
                // uses the per-line quantities to compute the gross,
                // not the percent). The percent is still used for
                // header display and downstream % calculations.
                WorkCompletedPercent: cumulativePercents[i],
                Notes: notes[i],
                LineItems: lineItems
            );
            var draft = await _billingSvc.CreateAsync(projectId, req);
            var approved = await _billingSvc.ApproveAsync(draft.Id,
                new ApproveBillingRequest(
                    BillingDate: DateTime.Parse(dates[i]),
                    Notes: notes[i]));
            result.Add(approved.Id);
            _log.LogInformation(
                "Billing {Num} approved: gross={Gross}, net={Net}, JE={Je}, lines={LineCount}",
                numbers[i], approved.GrossAmount, approved.NetAmount, approved.JournalEntryId, lineItems.Count);
        }
        return result;
    }

    private async Task<List<Guid>> CreateRegularSalesInvoicesAsync(Guid companyId, Guid customerId)
    {
        // Sprint 52 — customer is created earlier in the seeder (step 1)
        // so the project can reference it. This method just looks up the
        // customer name from the pre-created id and creates 2 sales
        // invoices against it.
        var customerName = await GetCustomerNameAsync(companyId, customerId);
        if (string.IsNullOrEmpty(customerName))
            throw new InvalidOperationException("Customer not found — step 1 should have created it");

        var inv1 = await CreateRegularSalesInvoiceAsync(companyId, customerName, "2026-03-15", "INV-S-2026-001", 10000m, "بيع أجهزة مكتبية");
        var inv2 = await CreateRegularSalesInvoiceAsync(companyId, customerName, "2026-06-20", "INV-S-2026-002", 25000m, "بيع معدات ورشة");
        return new List<Guid> { inv1, inv2 };
    }

    private async Task<Guid> CreateRegularSalesInvoiceAsync(
        Guid companyId, string customerName, string date, string number, decimal total, string description)
    {
        var req = new CreateInvoiceRequest(
            CompanyId: companyId,
            InvoiceType: "sales",
            InvoiceDate: DateTime.Parse(date),
            PartyName: customerName,
            PartyNameAr: customerName,
            PartyTaxId: null,
            Notes: description,
            TaxRate: 0.04m,
            IntercompanyCompanyId: null,
            ProjectId: null,
            Lines: new List<CreateInvoiceLineRequest>
            {
                new(AccountId: null, ProductId: null, Description: description,
                    Quantity: 1, UnitPrice: total / 1.04m, TaxRate: 0.04m)
            }
        );
        var draft = await _invoiceSvc.CreateDraftAsync(req, null);
        var posted = await _invoiceSvc.PostAsync(draft.Id);
        return posted.Id;
    }

    private async Task<List<Guid>> CreateRegularPurchaseInvoicesAsync(
        Guid companyId, List<(Guid id, string name)> suppliers)
    {
        // Two regular (non-project) purchase invoices to verify the
        // PurchaseInvoiceApproved rule (5101/5102) still fires when
        // projectId is null.
        var inv1 = await CreateRegularPurchaseInvoiceAsync(
            companyId, suppliers[3], "2026-01-31", "INV-P-2026-001", 8000m,
            "إيجار معدات المكتب", "5102");
        var inv2 = await CreateRegularPurchaseInvoiceAsync(
            companyId, suppliers[2], "2026-04-15", "INV-P-2026-002", 5000m,
            "استشارات هندسية", "5101");
        return new List<Guid> { inv1, inv2 };
    }

    private async Task<Guid> CreateRegularPurchaseInvoiceAsync(
        Guid companyId, (Guid id, string name) supplier, string date, string number,
        decimal total, string description, string accountCode)
    {
        using var conn = _db.CreateConnection();
        var accountId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT id FROM accounts WHERE company_id = @companyId AND code = @code AND level = 3;",
            new { companyId, code = accountCode });
        var req = new CreateInvoiceRequest(
            CompanyId: companyId,
            InvoiceType: "purchase",
            InvoiceDate: DateTime.Parse(date),
            PartyName: supplier.name,
            PartyNameAr: supplier.name,
            PartyTaxId: null,
            Notes: description,
            TaxRate: 0.04m,
            IntercompanyCompanyId: null,
            ProjectId: null,
            Lines: new List<CreateInvoiceLineRequest>
            {
                new(AccountId: accountId, ProductId: null, Description: description,
                    Quantity: 1, UnitPrice: total / 1.04m, TaxRate: 0.04m)
            }
        );
        var draft = await _invoiceSvc.CreateDraftAsync(req, null);
        var posted = await _invoiceSvc.PostAsync(draft.Id);
        return posted.Id;
    }
}

public class RealisticSeedResult
{
    public Guid CompanyId { get; set; }
    public Guid ProjectId { get; set; }
    public int SuppliersCreated { get; set; }
    public int ProductsCreated { get; set; }
    public int SubLedgersCreated { get; set; }
    public int PurchaseInvoicesCreated { get; set; }
    public int PurchaseInvoiceJEsPosted { get; set; }
    public int BillingsCreated { get; set; }
    public int BillingJEsPosted { get; set; }
    public int SalesInvoicesCreated { get; set; }
    public int RegularPurchaseInvoicesCreated { get; set; }
    public double ElapsedSeconds { get; set; }
    public bool AllSucceeded { get; set; }
    public string Message { get; set; } = "";
}
