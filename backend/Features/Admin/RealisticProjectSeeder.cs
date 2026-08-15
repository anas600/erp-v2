using Dapper;
using ErpV2.Common;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Projects;

namespace ErpV2.Features.Admin;

/// <summary>
/// Sprint 58 — Real Project Data Seeder (نقطة تعبئة الغاز سرت).
///
/// Creates a clean, demonstrable scenario for ONE real Libyan
/// construction project based on the actual contract documents
/// provided by the user (Excel files 99fbbb98, f10c7be4, 774deafd,
/// 4ee91b60):
///
///   - Project: تنفيذ نقطة تعبئة الغاز (سرت) — Gas Filling Station
///     at Sirte (Libya)
///   - Owner: الجهاز الوطني للتنمية (National Development Authority)
///   - Contractor: شركة أمجاد للمقاولات العامة
///   - Consultant: شركة دار التقنية للاستشارات الهندسية
///   - Contract number: ج.و.ت/35/120
///   - Duration: 45 days
///   - Site handover: 2024-03-30
///   - Period: 2024-03-30 → 2024-05-20
///   - Original contract value: 2,369,048 LYD
///   - Variation value: 4,192,399.494 LYD (الأمر التعديلي)
///   - Current (effective) contract value: 6,561,447.494 LYD
///
/// Scope (deliberately small):
///   - 1 customer (الجهاز الوطني للتنمية)
///   - 4 suppliers (steel, cement, electrical, generator) with
///     their sub-ledgers
///   - 5 products (4 materials + 1 labor service)
///   - 1 project with 7 BOQ line items (matching the FMB structure)
///   - 1 contract + 1 variation order (الأمر التعديلي)
///   - 4 monthly billings with realistic % distribution:
///     * Billing 1: 15% (early works — demolition + site prep + foundations)
///     * Billing 2: 35% (structural + walls + plastering)
///     * Billing 3: 30% (painting + MEP)
///     * Billing 4: 20% (final equipment + handover)
///   - 4 project-tagged purchase invoices
///   - 2 regular sales invoices + 2 regular purchase invoices
///
/// Sprint 53-54 deduction model applied to each billing:
///   - خصم 15% من قيمة العقد الأصلي (one-time, first billing only)
///   - خصم 5% ضمان أعمال (Retention, every billing)
///   - خصم 2% تأمين نهائي (Final Insurance, every billing)
///   - خصم 1.5% خدمات لصالح الجهاز (Admin Fees, every billing)
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

        // Sprint 54 — 4-party model: also create the contractor
        // (المقاول) and consultant (الاستشاري) contacts. The seeder
        // mirrors the Libyan construction project: client is the
        // government, contractor is an external construction firm,
        // consultant is an external engineering firm.
        var contractorId = await EnsureContractorAsync(companyId);
        var consultantId = await EnsureConsultantAsync(companyId);

        // ---- 2. Project (triggers auto-create of 7 L4 sub-ledgers) ----
        var projectId = await CreateProjectAsync(companyId, customerId, contractorId, consultantId);
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

    private async Task<Guid> CreateProjectAsync(Guid companyId, Guid customerId, Guid contractorId, Guid consultantId)
    {
        // Sprint 58 — Real project: تنفيذ نقطة تعبئة الغاز (سرت).
        // Per the user's Excel files (99fbbb98, f10c7be4, 774deafd):
        //   - Original contract value: 2,369,048 LYD
        //   - Variation value: 4,192,399.494 LYD (الأمر التعديلي)
        //   - Effective contract value: 6,561,447.494 LYD
        //   - Duration: 45 days
        //   - Site handover: 2024-03-30
        //   - Project period: 2024-03-30 → 2024-05-20
        var req = new CreateProjectRequest(
            CompanyId: companyId,
            Code: "PRJ-SRT-2024-001",
            Name: "تنفيذ نقطة تعبئة الغاز (سرت)",
            NameAr: "تنفيذ نقطة تعبئة الغاز (سرت)",
            Description: "مشروع تنفيذ نقطة تعبئة الغاز (سرت) — تكليف رقم ج.و.ت/35/120",
            StartDate: new DateTime(2024, 3, 30),
            EndDate: new DateTime(2024, 5, 20),
            Budget: 6561447.494m,
            Notes: "المشروع الأصلي: 2,369,048 د.ل + الأمر التعديلي: 4,192,399.494 د.ل = الإجمالي: 6,561,447.494 د.ل",
            Type: "construction",
            // Sprint 52 — the project must reference a customer so
            // billings can be approved. The customer is created
            // earlier in the seeder (step 1) so its id is available here.
            CustomerId: customerId,
            // Effective contract value (after variation)
            ContractValue: 6561447.494m,
            ExpectedEndDate: new DateTime(2024, 5, 20),
            ProjectManager: "م. سالم الشريف",
            Location: "سرت - منطقة تعبئة الغاز",
            // Sprint 54 — 4-party model
            ContractorId: contractorId,
            ConsultantId: consultantId
        );
        var proj = await _projectSvc.CreateAsync(req);
        _log.LogInformation("Created project {Code} (auto-L4-sub-ledgers created)", proj.Code);

        // Create the original contract (2,369,048 LYD) — the seeder
        // then adds a variation order to bring it up to 6,561,447.494.
        // The contracts table has project_id; the projects table
        // doesn't carry a contract_id back — it's a one-way
        // relationship.
        var contractId = await CreateContractAsync(companyId, proj.Id);

        return proj.Id;
    }

    private async Task<Guid> CreateContractAsync(Guid companyId, Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var contractId = Guid.NewGuid();
        // Sprint 58 — Real contract: ج.و.ت/35/120
        // Original value: 2,369,048 LYD
        // (The variation of 4,192,399.494 is added separately as a
        // contract_variations row in CreateVariationAsync below.)
        //
        // Sprint 53-54 deduction model:
        //   - 5% retention (ضمان أعمال)
        //   - 2% final insurance (تأمين نهائي)
        //   - 1.5% admin fees (خدمات لصالح الجهاز)
        //   - 15% original contract deduction (one-time, on first billing)
        //   - 20% advance payment
        await conn.ExecuteAsync(@"
            INSERT INTO contracts (id, company_id, project_id, contract_number, contract_value,
                                   advance_percent, retention_percent, retention_start_billing,
                                   final_insurance_percent, admin_fee_percent,
                                   start_date, end_date,
                                   site_handover_date, original_contract_value)
            VALUES (@id, @companyId, @projectId, @number, @value, 20, 5, 1,
                    2, 1.5,
                    '2024-03-30', '2024-05-14',
                    '2024-03-30', 2369048.00);",
            new
            {
                id = contractId,
                companyId,
                projectId,
                number = "ج.و.ت/35/120",
                value = 2369048.00m   // original value
            });

        // BOQ items based on the actual Excel BOQ (774deafd)
        // Total: 2,369,048 LYD (the original contract value)
        var items = new List<(string desc, string unit, decimal qty, decimal price, decimal total)>
        {
            ("بالمتر المربع / إزالة جزء من السور لزوم استحداث أبواب السحاب",    "م²",    514m,  550m,  282700m),     // Row 1
            ("بالمقطوعي / إزالة تحت الانشاء حوض خزان الوقود",                 "مقطوعية",  1m, 16500m, 16500m),       // Row 2
            ("بالمقطوعية / نقل المخلفات الي المقالب العمومية",                  "مقطوعية",  1m, 75000m, 75000m),       // Row 3
            ("بالمتر المكعب / الردم بأتربة صالحة للردم",                        "م³",   819.85m, 445m, 364833.25m),    // Row 4
            ("توريد و فرش و دمك مادة الاساس الحبيبي مدمكة",                    "م²",  1306m,    45m,  58770m),       // Row 5
            ("حفر في أرض سبخية لزوم الأساسات والسملات",                          "م³",   860.328m, 42m, 36133.776m),   // Row 6
            ("بالمتر المكعب / الردم بأتربة صالحة للردم للقواعد والأساسات",    "م³",   741.316m, 39m, 28911.324m),   // Row 7
            ("خرسانة عادية بإجهاد كسر 20 نيوتن / مم²",                          "م²",    77m,    52m,   4004m),       // Row 8
            ("خرسانة مسلحة (C25~C30) وحديد تسليح 80 كجم / م³ للقواعد",         "م³",    19.712m, 950m, 18726.4m),     // Row 9
            ("خرسانة مسلحة وحديد تسليح 80 كجم / م³ للسملات",                    "م³",    27.312m, 1120m, 30589.44m),   // Row 10
            ("خرسانة مسلحة وحديد تسليح 115 كجم / م³ للأعمدة",                  "م³",    19.09m, 1460m, 27871.4m),     // Row 11
            ("خرسانة مسلحة وحديد تسليح 80 كجم / م³ للقرنيزة",                   "م³",    10.242m, 1585m, 16233.57m),   // Row 12
            ("توريد وبناء حوائط من الطوب الاسمنتي المفرغ سمك 20 سم",           "م²",  1016.98m, 175m, 177971.5m),    // Row 13
            ("توريد ودهان بمادة البيتومين المقاوم للأملاح 3 أوجه",              "م²",   558.36m,  40m, 22334.4m),     // Row 14
            ("توريد وعمل لياسة عمومية بمونة اسمنتية 450 كجم",                  "م²",  2660m,  245m, 651700m),        // Row 15
            ("توريد وتنفيذ اعمال الجرافيت ناعم الملمس حسب العينة",              "م²",  1746.48m, 145m, 253239.6m),    // Row 16
            ("توريد وتنفيذ بلاطات خرسانية مسلحة لزوم أرضية نقطة التعبئة",        "م²",  1063m,  350m, 372050m),        // Row 17
            ("توريد وتركيب بردورة خرسانية",                                    "م.ط", 223m,  165m,  36795m),         // Row 18
            ("توريد وتنفيذ بلاط اسمنتي معشق للأرضية",                            "م²",  319.48m, 135m, 43129.8m),     // Row 19
            ("توريد وتركيب أبواب الحديد (سحاب)",                                "م²",   38.5m, 1450m, 55825m),         // Row 20
            ("توريد وتركيب خزان مياه معدني 10,000 لتر",                          "مقطوعية", 1m, 7500m, 7500m),        // Row 21
        };

        int lineNum = 1;
        foreach (var (desc, unit, qty, price, total) in items)
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
                    total = total
                });
        }

        // Now add the variation order (الأمر التعديلي): 4,192,399.494 LYD
        // This brings the effective contract value up to 6,561,447.494
        await CreateVariationAsync(companyId, contractId);

        return contractId;
    }

    /// <summary>
    /// Sprint 58 — Add the variation order (الأمر التعديلي) to the
    /// contract. This represents additional work approved during
    /// the project execution. Per the user's Excel files, the
    /// variation is 4,192,399.494 LYD.
    /// </summary>
    private async Task CreateVariationAsync(Guid companyId, Guid contractId)
    {
        using var conn = _db.CreateConnection();
        var variationId = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contract_variations
                (id, company_id, contract_id, variation_number, description,
                 variation_date, status, approved_at, approved_by, notes, created_at)
            VALUES (@id, @companyId, @contractId, 1, @description,
                    '2024-05-15', 'approved', '2024-05-15', NULL, @notes, NOW());",
            new
            {
                id = variationId,
                companyId,
                contractId,
                description = "الأمر التعديلي رقم 1 — إضافة أعمال خرسانة مسلحة ومبانٍ إضافية",
                notes = "موافق عليه من الجهاز الوطني للتنمية + الاستشاري"
            });

        // Variation line items (sample — matching the Excel structure)
        var variationItems = new (string desc, string unit, decimal qty, decimal price, bool isAddition)[]
        {
            ("أعمال حفر إضافية لزوم نقطة التعبئة الجديدة",                       "م³",   148.592m,    42m, true),   // 6,240.864
            ("توريد و ردم باتربة صالحة للردم",                                    "م³",   151.152m,   445m, true),   // 67,262.64
            ("خرسانة عادية 20 نيوتن / مم²",                                        "م²",    78.92m,     65m, true),   // 5,129.8
            ("توريد وصب خرسانة ميول الاسطح (الباتوتة)",                          "م²",    78.24m,     52m, true),   // 4,068.48
            ("خرسانة مسلحة وحديد تسليح 115 كجم / م³",                              "م³",     4.112m,  1120m, true),   // 4,605.44
            ("خرسانة مسلحة وحديد تسليح 115 كجم / م³",                              "م³",     5.264m,  1285m, true),   // 6,764.24
            ("خرسانة مسلحة وحديد تسليح 115 كجم / م³",                              "م³",     3.2m,    1460m, true),   // 4,672
            ("هدم وإزالة المباني القائمة شاملا الأساسات والحوائط",                 "م³",   231.66m,    42m, true),   // 9,729.72
            ("إزالة السقف المعدني المتهالك للهنجر الموجود",                       "م²",   546m,      245m, true),   // 133,770
            ("توريد مولد كهرباء بقوة 200 kVA مع التركيب والضمانة",                  "عدد",    1m,   290000m, true),   // 290,000
        };
        int lineNum = 1;
        foreach (var (desc, unit, qty, price, isAddition) in variationItems)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO contract_variation_items
                    (id, company_id, variation_id, line_number, description, unit,
                     custom_unit, quantity, unit_price, total_price, is_addition, notes, created_at)
                VALUES (@id, @companyId, @variationId, @lineNumber, @description, @unit,
                        NULL, @quantity, @unitPrice, @total, @isAddition, NULL, NOW());",
                new
                {
                    id = Guid.NewGuid(),
                    companyId,
                    variationId,
                    lineNumber = lineNum++,
                    description = desc,
                    unit,
                    quantity = qty,
                    unitPrice = price,
                    total = qty * price,
                    isAddition
                });
        }
    }

    // Sprint 58 — REAL project data: الجهاز الوطني للتنمية is the
    // owner (المالك) of the gas filling station project at Sirte.
    // Code CUS-001 stays stable for re-runs.
    private async Task<Guid> EnsureCustomerAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT id FROM contacts
            WHERE company_id = @companyId AND type = 'customer' AND is_active = true
            LIMIT 1;",
            new { companyId });
        if (existing.HasValue) return existing.Value;

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contacts
                (id, company_id, type, code, name, name_ar, tax_id, phone, email,
                 is_active, is_demo_data, created_at)
            VALUES
                (@id, @companyId, 'customer', 'CUS-001', 'National Development Authority',
                 'الجهاز الوطني للتنمية', NULL, NULL, NULL, true, false, NOW());",
            new { id, companyId });
        // Sprint 52 — auto-create L4 sub-ledger (1103-CUS-001).
        await _accounts.EnsureSubLedgerAsync(companyId, id);
        return id;
    }

    // Sprint 54 — 4-party model: the contractor (المقاول / الجهة المنفذة).
    // Mirrors EnsureCustomerAsync but with type='contractor'.
    private async Task<Guid> EnsureContractorAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT id FROM contacts
            WHERE company_id = @companyId AND type = 'contractor' AND is_active = true
            LIMIT 1;",
            new { companyId });
        if (existing.HasValue) return existing.Value;

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contacts
                (id, company_id, type, code, name, name_ar, tax_id, phone, email,
                 is_active, is_demo_data, created_at)
            VALUES
                (@id, @companyId, 'contractor', 'CON-001', 'Amjad Construction Co.',
                 'شركة أمجاد للمقاولات العامة والاستثمار العقاري',
                 NULL, NULL, NULL, true, false, NOW());",
            new { id, companyId });
        // Auto-create L4 sub-ledger (2101-CON-001 — AP sub-ledger)
        await _accounts.EnsureSubLedgerAsync(companyId, id);
        return id;
    }

    // Sprint 54 — 4-party model: the consultant (الاستشاري / الجهة المشرفة).
    private async Task<Guid> EnsureConsultantAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT id FROM contacts
            WHERE company_id = @companyId AND type = 'consultant' AND is_active = true
            LIMIT 1;",
            new { companyId });
        if (existing.HasValue) return existing.Value;

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contacts
                (id, company_id, type, code, name, name_ar, tax_id, phone, email,
                 is_active, is_demo_data, created_at)
            VALUES
                (@id, @companyId, 'consultant', 'CST-001', 'Dar Al-Taqnia Consulting',
                 'شركة دار التقنية للاستشارات والأعمال الهندسية',
                 NULL, NULL, NULL, true, false, NOW());",
            new { id, companyId });
        // Consultants don't have a sub-ledger (they don't post to GL directly)
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
        // Sprint 58 — Real project: 4 billings with realistic % distribution
        // (per the user's request: مقسم علي 4 مستخلصات موزع فيها نسب الانجاز بشكل واقعي)
        //
        // The original Excel (99fbbb98) showed a SINGLE final billing at 25% financial progress.
        // The user wants 4 billings with realistic construction progression:
        //   - Billing 1: 15% — early works (demolition, site prep, foundations)
        //   - Billing 2: 35% — structural (walls, plastering, painting starts)
        //   - Billing 3: 30% — MEP + finishing (electrical, plumbing, paint)
        //   - Billing 4: 20% — final (equipment, generator, handover)
        // Total: 100% of effective contract value (6,561,447.494 LYD)
        //
        // The 15% original contract deduction applies on Billing 1 only
        // (one-time, against original value 2,369,048 LYD = 355,357 LYD).
        // The 5% retention, 2% final insurance, and 1.5% admin fees apply on every billing.
        var dates = new[] { "2024-04-15", "2024-04-30", "2024-05-15", "2024-05-20" };
        var numbers = new[] { "BIL-SRT-2024-001", "BIL-SRT-2024-002", "BIL-SRT-2024-003", "BIL-SRT-2024-004" };
        var notes = new[] {
            "المستخلص الأول — أعمال الإزالة وتجهيز الموقع والأساسات",
            "المستخلص الثاني — أعمال الخرسانة المسلحة والبلوك واللياسة",
            "المستخلص الثالث — أعمال السباكة والكهرباء والطلاء",
            "المستخلص الرابع والأخير — تركيب مولد الكهرباء والتسليم النهائي"
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
        // 4 billings at 15%, 35%, 30%, 20% — realistic construction progression
        // (the user said "بنسب انجاز واقعية" — not the old 30/50/80/100 which
        // was too aggressive for a 45-day project).
        var cumulativePercents = new decimal[] { 15m, 35m, 30m, 20m };
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
                PeriodFrom: i == 0 ? new DateTime(2024, 3, 30) : DateTime.Parse(dates[i - 1]),
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
            // Sprint 58 — note: in trust mode this auto-posts the JE
            // and auto-approves the billing.
            var approved = await _billingSvc.ApproveAsync(draft.Id,
                new ApproveBillingRequest(
                    BillingDate: DateTime.Parse(dates[i]),
                    Notes: notes[i]));
            result.Add(approved.Id);
            _log.LogInformation(
                "Billing {Num} ({Pct}%) approved: gross={Gross}, net={Net}, JE={Je}, lines={LineCount}",
                numbers[i], cumulativePercents[i], approved.GrossAmount, approved.NetAmount, approved.JournalEntryId, lineItems.Count);
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
