using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Contacts;
using ErpV2.Features.FiscalYears;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Journal;
using ErpV2.Features.Payments;
using ErpV2.Features.Projects;
using ErpV2.Features.Receipts;

namespace ErpV2.Features.Admin;

/// <summary>
/// FullYearSeeder partial — monthly phase.
/// Generates recurring journal entries, sales/purchase invoices,
/// receipts and payments for each of the 12 months.
/// </summary>
public partial class FullYearSeedResult
{
    public int JournalEntriesCreated { get; set; }
    public int RecurringEntriesCreated { get; set; }
}

public partial class FullYearSeeder
{
    // ----------------------------------------------------------------
    // Phase 4: Month
    // ----------------------------------------------------------------

    /// <summary>
    /// Seasonal weights per month (index 0 = Sep, 11 = Aug).
    /// Higher = more activity. Tuned for construction + services mix:
    /// Q4 (Dec-Feb) and Q2 (Mar-May) are busier than summer.
    /// </summary>
    private static readonly double[] MONTHLY_WEIGHT =
    {
        1.0,  // Sep — startup
        1.1,  // Oct
        1.2,  // Nov
        1.5,  // Dec — year-end push
        1.3,  // Jan
        1.0,  // Feb
        1.4,  // Mar — Q1 close
        1.2,  // Apr
        1.5,  // May — Q2 close
        1.0,  // Jun
        1.2,  // Jul — mid-year
        1.3   // Aug — year-end prep
    };

    /// <summary>
    /// Per-customer monthly invoice targets.
    /// (customerCode, avgPerMonth, avgAmountLYD)
    /// </summary>
    private static readonly (string Code, double AvgPerMonth, decimal AvgAmount, string PaymentBehavior)[] CUSTOMER_PROFILE =
    {
        ("CUST-001", 0.6, 45_000m, "slow"),  // Ministry — 7-8 large invoices/year
        ("CUST-002", 0.5, 35_000m, "slow"),  // NOC
        ("CUST-003", 1.2, 12_000m, "normal"), // Real estate
        ("CUST-004", 0.7, 25_000m, "slow"),  // Municipality
        ("CUST-005", 0.8, 18_000m, "normal"), // Free zone
        ("CUST-006", 1.5, 8_500m,  "normal"), // Construction
        ("CUST-007", 1.8, 4_500m,  "fast"),   // Engineering office
        ("CUST-008", 1.3, 6_500m,  "normal"), // Steel works
        ("CUST-009", 2.0, 3_200m,  "fast"),   // Trading
        ("CUST-010", 2.5, 1_800m,  "fast")    // Retail
    };

    /// <summary>
    /// Per-supplier monthly invoice targets.
    /// </summary>
    private static readonly (string Code, double AvgPerMonth, decimal AvgAmount, string Category)[] SUPPLIER_PROFILE =
    {
        ("SUPP-001", 1.5, 28_000m, "materials"), // Steel
        ("SUPP-002", 2.0, 9_500m,  "materials"), // Cement
        ("SUPP-003", 1.5, 7_200m,  "materials"), // Aggregates
        ("SUPP-004", 0.4, 12_000m, "equipment"), // Heavy rental
        ("SUPP-005", 0.5, 3_500m,  "equipment"), // Power tools
        ("SUPP-006", 0.3, 8_000m,  "services"),  // Audit (quarterly)
        ("SUPP-007", 0.3, 4_500m,  "services"),  // Legal
        ("SUPP-008", 1.0, 1_200m,  "services"),  // Telecom
        ("SUPP-009", 0.8, 2_200m,  "admin"),     // Office supplies
        ("SUPP-010", 0.3, 6_500m,  "admin")      // IT
    };

    private async Task SeedMonthAsync(Guid companyId, DateTime month, Guid? userId)
    {
        var monthIdx = ((month.Year - FY_START.Year) * 12) + (month.Month - FY_START.Month);
        var weight = MONTHLY_WEIGHT[monthIdx];
        var monthLabel = month.ToString("yyyy-MM");

        // ---- Recurring entries (always) ----
        await SeedRecurringEntriesAsync(companyId, month, monthLabel);

        // ---- Sales invoices ----
        var salesIds = await SeedSalesInvoicesAsync(companyId, month, weight, monthLabel, userId);

        // ---- Purchase invoices ----
        var purchaseIds = await SeedPurchaseInvoicesAsync(companyId, month, weight, monthLabel, userId);

        // ---- Receipts (based on prior invoices) ----
        await SeedReceiptsForMonthAsync(companyId, month, salesIds, monthLabel, userId);

        // ---- Payments ----
        await SeedPaymentsForMonthAsync(companyId, month, purchaseIds, monthLabel, userId);

        _logger.LogInformation(
            "FullYearSeeder: {Month} done — {Sales} sales, {Purch} purchase, {Recur} recurring",
            monthLabel, salesIds.Count, purchaseIds.Count, 9);
    }

    // ----------------------------------------------------------------
    // Recurring entries (rent, salaries, utilities, depreciation, loan)
    // ----------------------------------------------------------------

    private async Task SeedRecurringEntriesAsync(Guid companyId, DateTime month, string monthLabel)
    {
        var lastDay = new DateTime(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
        var entries = new List<(string desc, List<CreateJournalLineRequest> lines)>();

        // 1) Rent — 5,000 LYD (Cash → Rent Expense 5102)
        if (_accountIds.TryGetValue("1101-CASH-001", out var cash) &&
            _accountIds.TryGetValue("5102", out var rent))
        {
            entries.Add(("إيجار شهري", new List<CreateJournalLineRequest>
            {
                new(rent, 5_000m, 0, "إيجار الشهر", null),
                new(cash, 0, 5_000m, "دفع إيجار", null)
            }));
        }

        // 2) Salaries — 35,000 LYD total (5 employees × 7,000)
        // Dr Salaries Expense 5101, Cr Cash
        if (_accountIds.TryGetValue("5101", out var salaries) && _accountIds.TryGetValue("1101-CASH-001", out cash))
        {
            var salaryLines = new List<CreateJournalLineRequest>
            {
                new(salaries, 35_000m, 0, "رواتب الشهر", null)
            };
            salaryLines.Add(new CreateJournalLineRequest(cash, 0, 35_000m, "صرف رواتب", null));
            entries.Add(("رواتب شهرية", salaryLines));
        }

        // 3) Electricity — 1,200 LYD (Utilities 5103)
        if (_accountIds.TryGetValue("5103", out var utilities) && _accountIds.TryGetValue("1102-BANK-001", out var bank1))
        {
            entries.Add(("فاتورة كهرباء", new List<CreateJournalLineRequest>
            {
                new(utilities, 1_200m, 0, "كهرباء الشهر", null),
                new(bank1, 0, 1_200m, "خصم كهرباء", null)
            }));
        }

        // 4) Water — 300 LYD
        if (_accountIds.TryGetValue("5103", out utilities) && _accountIds.TryGetValue("1101-CASH-001", out cash))
        {
            entries.Add(("فاتورة ماء", new List<CreateJournalLineRequest>
            {
                new(utilities, 300m, 0, "ماء الشهر", null),
                new(cash, 0, 300m, "دفع فاتورة الماء", null)
            }));
        }

        // 5) Internet + Phone — 500 LYD
        if (_accountIds.TryGetValue("5103", out utilities) && _accountIds.TryGetValue("1102-BANK-001", out var bank2))
        {
            entries.Add(("إنترنت وهاتف", new List<CreateJournalLineRequest>
            {
                new(utilities, 500m, 0, "إنترنت + هاتف", null),
                new(bank2, 0, 500m, "خصم اشتراك", null)
            }));
        }

        // 6) Depreciation — 2,500 LYD (Accumulated Depreciation 1202 / Depreciation Expense 5106)
        if (_accountIds.TryGetValue("5106", out var depExp) &&
            _accountIds.TryGetValue("1202", out var accDep))
        {
            entries.Add(("إهلاك شهري", new List<CreateJournalLineRequest>
            {
                new(depExp, 2_500m, 0, "إهلاك الأصول الثابتة", null),
                new(accDep, 0, 2_500m, "مجمع الإهلاك", null)
            }));
        }

        // 7) Loan installment — 4,000 LYD (3,500 principal + 500 interest).
        //    Note: the standard COA has no specific "interest expense"
        //    account — we map both the interest and the bank fees
        //    onto 5203 (Hospitality) as a generic admin-expense
        //    bucket. A real chart would add 5206 "Bank Charges &
        //    Interest" — out of scope for the demo.
        if (_accountIds.TryGetValue("2201", out var loan) &&
            _accountIds.TryGetValue("5203", out var intExp) &&
            _accountIds.TryGetValue("1102-BANK-001", out var bank3))
        {
            entries.Add(("قسط قرض بنكي", new List<CreateJournalLineRequest>
            {
                new(loan, 3_500m, 0, "أصل القسط", null),
                new(intExp, 500m, 0, "فائدة القسط", null),
                new(bank3, 0, 4_000m, "خصم قسط القرض", null)
            }));
        }

        // 8) Bank service charges — 50 LYD (mapped to 5203 for the
        //    same reason as the loan interest above).
        if (_accountIds.TryGetValue("5203", out var bankChg) &&
            _accountIds.TryGetValue("1102-BANK-001", out var bank4))
        {
            entries.Add(("رسوم بنكية", new List<CreateJournalLineRequest>
            {
                new(bankChg, 50m, 0, "رسوم شهرية", null),
                new(bank4, 0, 50m, "خصم رسوم", null)
            }));
        }

        // 9) Insurance prepaid amortization — 800 LYD (1205 Intangible/Prepaid → 5104 Insurance)
        if (_accountIds.TryGetValue("1106", out var prepaidIns) &&
            _accountIds.TryGetValue("5104", out var insExp))
        {
            entries.Add(("إطفاء تأمين", new List<CreateJournalLineRequest>
            {
                new(insExp, 800m, 0, "تأمين شهري", null),
                new(prepaidIns, 0, 800m, "إطفاء تأمين مسبق", null)
            }));
        }

        // Insert all
        foreach (var (desc, lines) in entries)
        {
            try
            {
                var entry = await _journal.CreateDraftAsync(new CreateJournalEntryRequest(
                    companyId, lastDay, $"{desc} - {monthLabel}",
                    lines, Source: "manual"), _mainUserId);
                await _journal.ApproveAsync(entry.Id, _mainUserId);
                await _journal.PostAsync(entry.Id);
                _result.JournalEntriesCreated++;
                _result.RecurringEntriesCreated++;
            }
            catch (Exception ex) { _result.Errors.Add($"Recurring {desc} {monthLabel}: {ex.Message}"); }
        }
    }

    // ----------------------------------------------------------------
    // Sales invoices for a month
    // ----------------------------------------------------------------

    private async Task<Dictionary<string, List<Guid>>> SeedSalesInvoicesAsync(
        Guid companyId, DateTime month, double weight, string monthLabel, Guid? userId)
    {
        var byCustomer = new Dictionary<string, List<Guid>>();
        var rnd = new Random((int)(month.Ticks & 0x7FFFFFFF));

        foreach (var (code, avg, avgAmt, behavior) in CUSTOMER_PROFILE)
        {
            // For each customer, roll dice * weight to determine count
            var count = (int)Math.Round(rnd.NextDouble() * avg * weight * 2);
            count = Math.Min(count, 3); // cap at 3 per customer per month
            if (count == 0) continue;
            if (!_customerIds.TryGetValue(code, out var contactId)) continue;

            for (int i = 0; i < count; i++)
            {
                var dayOfMonth = rnd.Next(1, DateTime.DaysInMonth(month.Year, month.Month) + 1);
                var invoiceDate = new DateTime(month.Year, month.Month, dayOfMonth);
                // Vary amount ±40%
                var variance = 0.6m + (decimal)(rnd.NextDouble() * 0.8);
                var amount = Math.Round(avgAmt * variance, 2);
                if (amount < 200m) amount = 200m; // minimum sensible invoice

                // Use old VAT rate (15%) for first 3 months (Sep-Nov 2025) to show
                // historical context; new 4% VAT from Dec 2025 onwards.
                var taxRate = month < new DateTime(2025, 12, 1) ? VAT_RATE_OLD : VAT_RATE;

                try
                {
                    // Pick a product for this invoice (rotate through)
                    var productCode = $"P{((dayOfMonth + i) % 15) + 1:D3}";
                    if (!_productIds.TryGetValue(productCode, out var productId)) continue;

                    var contact = await _contacts.GetByIdAsync(contactId);
                    if (contact is null) continue;

                    // Two lines for realism (main + accessory)
                    var mainLines = new List<CreateInvoiceLineRequest>
                    {
                        new(AccountId: null, ProductId: productId, Description: contact.NameAr,
                            Quantity: 1m, UnitPrice: amount, TaxRate: taxRate)
                    };
                    // Sometimes add a second small line (10% of main, service)
                    if (rnd.NextDouble() > 0.5)
                    {
                        var accCode = $"P{((dayOfMonth + i + 5) % 15) + 1:D3}";
                        if (_productIds.TryGetValue(accCode, out var accPid))
                            mainLines.Add(new CreateInvoiceLineRequest(
                                AccountId: null, ProductId: accPid, Description: "خدمة إضافية",
                                Quantity: 1m, UnitPrice: amount * 0.1m, TaxRate: taxRate));
                    }

                    var req = new CreateInvoiceRequest(
                        companyId, "sales", invoiceDate,
                        contact.Name, contact.NameAr, null,
                        $"فاتورة مبيعات - {monthLabel}",
                        taxRate, IntercompanyCompanyId: null, Lines: mainLines);
                    // Sprint 40 — manual posting. MarkAsPostedAsync skips
                    // the rules engine; we then build the proper
                    // sub-ledger journal entry by hand.
                    var draft = await _invoices.CreateDraftAsync(req, userId);
                    await _invoices.MarkAsPostedAsync(draft.Id);

                    // Use the exact subtotal / taxAmount / total that
                    // InvoiceService already computed (it does per-line
                    // rounding to 2dp that we must mirror exactly, or
                    // the JE will be off-pence and PostingEngine will
                    // reject it as "القيد غير متوازن").
                    var (subtotal, taxAmount, total) = (draft.SubTotal, draft.TaxAmount, draft.Total);

                    await PostSalesInvoiceAsync(
                        companyId, invoiceDate, draft.InvoiceNumber,
                        code, contact.NameAr,
                        subtotal, taxAmount, total,
                        projectId: null,
                        costCenterId: _costCenterIds.GetValueOrDefault("DPT-SALES"),
                        userId: userId);

                    if (!byCustomer.ContainsKey(code)) byCustomer[code] = new List<Guid>();
                    byCustomer[code].Add(draft.Id);
                    _result.InvoicesCreated++;
                }
                catch (Exception ex)
                {
                    _result.Errors.Add($"Sales {code} {monthLabel}: {ex.Message}");
                }
            }
        }
        return byCustomer;
    }

    // ----------------------------------------------------------------
    // Purchase invoices
    // ----------------------------------------------------------------

    private async Task<Dictionary<string, List<Guid>>> SeedPurchaseInvoicesAsync(
        Guid companyId, DateTime month, double weight, string monthLabel, Guid? userId)
    {
        var bySupplier = new Dictionary<string, List<Guid>>();
        var rnd = new Random((int)(month.Ticks & 0x7FFFFFFF) ^ 0x55AA);

        foreach (var (code, avg, avgAmt, category) in SUPPLIER_PROFILE)
        {
            var count = (int)Math.Round(rnd.NextDouble() * avg * weight * 2);
            count = Math.Min(count, 4);
            if (count == 0) continue;
            if (!_supplierIds.TryGetValue(code, out var contactId)) continue;

            for (int i = 0; i < count; i++)
            {
                var dayOfMonth = rnd.Next(1, DateTime.DaysInMonth(month.Year, month.Month) + 1);
                var invoiceDate = new DateTime(month.Year, month.Month, dayOfMonth);
                var variance = 0.6m + (decimal)(rnd.NextDouble() * 0.8);
                var amount = Math.Round(avgAmt * variance, 2);
                if (amount < 100m) amount = 100m;
                var taxRate = month < new DateTime(2025, 12, 1) ? VAT_RATE_OLD : VAT_RATE;

                try
                {
                    var productCode = $"P{((dayOfMonth + i + 3) % 15) + 1:D3}";
                    if (!_productIds.TryGetValue(productCode, out var productId)) continue;

                    var contact = await _contacts.GetByIdAsync(contactId);
                    if (contact is null) continue;

                    var lines = new List<CreateInvoiceLineRequest>
                    {
                        new(AccountId: null, ProductId: productId, Description: contact.NameAr,
                            Quantity: 1m, UnitPrice: amount, TaxRate: taxRate)
                    };
                    var req = new CreateInvoiceRequest(
                        companyId, "purchase", invoiceDate,
                        contact.Name, contact.NameAr, null,
                        $"فاتورة مشتريات - {monthLabel}",
                        taxRate, IntercompanyCompanyId: null, Lines: lines);
                    // Sprint 40 — manual posting with proper sub-ledger
                    // distribution. The previous code called PostAsync
                    // which fired the rules engine and posted to L3
                    // "2101 Accounts Payable" instead of "2101-SUPP-XXX".
                    var draft = await _invoices.CreateDraftAsync(req, userId);
                    await _invoices.MarkAsPostedAsync(draft.Id);

                    // Use the invoice's own rounded totals (see
                    // sales-invoice comment for why we can't compute
                    // them ourselves).
                    var (subtotal, taxAmount, total) = (draft.SubTotal, draft.TaxAmount, draft.Total);

                    // Pick the cost center that matches the supplier
                    // category — services go to "ACT-PROF", admin to
                    // "ACT-OFFICE", materials/equipment to "DPT-OPS-SUP".
                    var costCenterCode = category switch
                    {
                        "services"  => "ACT-PROF",
                        "admin"     => "ACT-OFFICE",
                        "materials" => "DPT-OPS-SUP",
                        "equipment" => "DPT-OPS-SUP",
                        _           => "DPT-OPS"
                    };

                    await PostPurchaseInvoiceAsync(
                        companyId, invoiceDate, draft.InvoiceNumber,
                        code, contact.NameAr,
                        subtotal, taxAmount, total,
                        category, projectId: null,
                        costCenterId: _costCenterIds.GetValueOrDefault(costCenterCode),
                        userId: userId);

                    if (!bySupplier.ContainsKey(code)) bySupplier[code] = new List<Guid>();
                    bySupplier[code].Add(draft.Id);
                    _result.InvoicesCreated++;
                }
                catch (Exception ex)
                {
                    _result.Errors.Add($"Purchase {code} {monthLabel}: {ex.Message}");
                }
            }
        }
        return bySupplier;
    }

    // ----------------------------------------------------------------
    // Receipts (30/60/90 day payment patterns)
    // ----------------------------------------------------------------

    private async Task SeedReceiptsForMonthAsync(
        Guid companyId, DateTime month,
        Dictionary<string, List<Guid>> salesIds,
        string monthLabel, Guid? userId)
    {
        // Receipts pattern: for every invoice from 1-3 months ago, ~70% paid
        // Plus occasional advance payments
        var rnd = new Random((int)(month.Ticks & 0x7FFFFFFF) ^ 0x1234);

        // For each customer, look at their invoices from previous months
        // and collect some payment patterns
        // Simplification: 1-2 receipts per customer per month for old invoices
        foreach (var (code, contactId) in _customerIds)
        {
            if (rnd.NextDouble() > 0.4) continue; // 60% chance customer has a receipt

            try
            {
                // Get unpaid invoices for this customer up to this month
                // Note: invoices table uses party_name (free text), not contact_id
                var contact = await _contacts.GetByIdAsync(contactId);
                if (contact is null) continue;
                using var conn = _db.CreateConnection();
                var openInvoices = (await conn.QueryAsync<(Guid id, decimal total, decimal paid, DateTime date)>(@"
                    SELECT i.id, i.total, COALESCE((SELECT SUM(amount) FROM receipt_vouchers
                        WHERE invoice_id = i.id AND status IN ('posted', 'approved')), 0) as paid,
                        i.invoice_date
                    FROM invoices i
                    WHERE i.company_id = @cid AND i.invoice_type = 'sales'
                      AND (i.party_name = @partyName OR i.party_name_ar = @partyName)
                      AND i.status = 'posted'
                      AND i.invoice_date < @asOf
                      AND (i.total - COALESCE((SELECT SUM(amount) FROM receipt_vouchers
                          WHERE invoice_id = i.id AND status IN ('posted', 'approved')), 0)) > 0
                    ORDER BY i.invoice_date
                    LIMIT 5;",
                    new { cid = companyId, partyName = contact.Name, asOf = month.AddDays(15) })).ToList();

                if (openInvoices.Count == 0) continue;

                // Pick 1-2 invoices to pay
                var toPay = openInvoices.Take(rnd.Next(1, Math.Min(3, openInvoices.Count + 1))).ToList();
                foreach (var inv in toPay)
                {
                    var outstanding = inv.total - inv.paid;
                    // 70% full payment, 30% partial
                    var payAmount = rnd.NextDouble() > 0.3
                        ? outstanding
                        : Math.Round(outstanding * (decimal)(0.3 + rnd.NextDouble() * 0.5), 2);
                    if (payAmount < 100m) continue;

                    var dayOfMonth = rnd.Next(1, DateTime.DaysInMonth(month.Year, month.Month) + 1);
                    var receiptDate = new DateTime(month.Year, month.Month, dayOfMonth);
                    var method = rnd.NextDouble() > 0.4 ? "bank" : "cash";
                    var refNum = $"RV-{monthLabel}-{code.Substring(5)}-{i_counter(_refNumCounter++)}";

                    var req = new CreateReceiptVoucherRequest(
                        companyId, receiptDate, contactId, payAmount, method,
                        BankAccountId: null, CheckNumber: null, CheckDate: null,
                        Reference: refNum,
                        Narration: $"تحصيل فاتورة {inv.date:yyyy-MM-dd}",
                        InvoiceId: inv.id);
                    var draft = await _receipts.CreateAsync(req, userId);
                    await _receipts.PostAsync(draft.Id, userId);
                    _result.ReceiptsCreated++;
                }
            }
            catch (Exception ex)
            {
                _result.Errors.Add($"Receipt {code} {monthLabel}: {ex.Message}");
            }
        }
    }

    private static int _refNumCounter = 0;
    private static int i_counter(int n) => n;

    // ----------------------------------------------------------------
    // Payments (to suppliers)
    // ----------------------------------------------------------------

    private async Task SeedPaymentsForMonthAsync(
        Guid companyId, DateTime month,
        Dictionary<string, List<Guid>> purchaseIds,
        string monthLabel, Guid? userId)
    {
        var rnd = new Random((int)(month.Ticks & 0x7FFFFFFF) ^ 0xABCD);

        foreach (var (code, contactId) in _supplierIds)
        {
            if (rnd.NextDouble() > 0.35) continue;

            try
            {
                using var conn = _db.CreateConnection();
                var supContact = await _contacts.GetByIdAsync(contactId);
                if (supContact is null) continue;
                var openInvoices = (await conn.QueryAsync<(Guid id, decimal total, decimal paid, DateTime date)>(@"
                    SELECT i.id, i.total, COALESCE((SELECT SUM(amount) FROM payment_vouchers
                        WHERE invoice_id = i.id AND status IN ('posted', 'approved')), 0) as paid,
                        i.invoice_date
                    FROM invoices i
                    WHERE i.company_id = @cid AND i.invoice_type = 'purchase'
                      AND (i.party_name = @partyName OR i.party_name_ar = @partyName)
                      AND i.status = 'posted'
                      AND i.invoice_date < @asOf
                      AND (i.total - COALESCE((SELECT SUM(amount) FROM payment_vouchers
                          WHERE invoice_id = i.id AND status IN ('posted', 'approved')), 0)) > 0
                    ORDER BY i.invoice_date
                    LIMIT 5;",
                    new { cid = companyId, partyName = supContact.Name, asOf = month.AddDays(15) })).ToList();

                if (openInvoices.Count == 0) continue;

                var toPay = openInvoices.Take(rnd.Next(1, Math.Min(3, openInvoices.Count + 1))).ToList();
                foreach (var inv in toPay)
                {
                    var outstanding = inv.total - inv.paid;
                    var payAmount = rnd.NextDouble() > 0.4
                        ? outstanding
                        : Math.Round(outstanding * (decimal)(0.3 + rnd.NextDouble() * 0.5), 2);
                    if (payAmount < 100m) continue;

                    var dayOfMonth = rnd.Next(1, DateTime.DaysInMonth(month.Year, month.Month) + 1);
                    var payDate = new DateTime(month.Year, month.Month, dayOfMonth);
                    var method = rnd.NextDouble() > 0.5 ? "bank" : "cash";
                    var refNum = $"PV-{monthLabel}-{code.Substring(5)}-{i_counter(_refNumCounter++)}";

                    var req = new CreatePaymentVoucherRequest(
                        companyId, payDate, contactId, payAmount, method,
                        BankAccountId: null, CheckNumber: null, CheckDate: null,
                        Reference: refNum,
                        Narration: $"دفع فاتورة {inv.date:yyyy-MM-dd}",
                        InvoiceId: inv.id);
                    var draft = await _payments.CreateAsync(req, userId);
                    await _payments.PostAsync(draft.Id, userId);
                    _result.PaymentsCreated++;
                }
            }
            catch (Exception ex)
            {
                _result.Errors.Add($"Payment {code} {monthLabel}: {ex.Message}");
            }
        }
    }

    // ----------------------------------------------------------------
    // Year-end closing
    // ----------------------------------------------------------------

    private async Task SeedYearEndClosingAsync(Guid companyId, Guid? userId)
    {
        // Close revenue accounts (41xx, 42xx) to retained earnings 3301
        // Close expense accounts (51xx, 52xx, 53xx, 54xx) to retained earnings 3301
        try
        {
            using var conn = _db.CreateConnection();
            var revenueAccounts = (await conn.QueryAsync<(Guid id, decimal balance)>(@"
                SELECT id, balance FROM accounts
                WHERE company_id = @cid AND level >= 3
                  AND (code LIKE '41%' OR code LIKE '42%')
                  AND balance != 0;",
                new { cid = companyId })).ToList();
            var expenseAccounts = (await conn.QueryAsync<(Guid id, decimal balance)>(@"
                SELECT id, balance FROM accounts
                WHERE company_id = @cid AND level >= 3
                  AND (code LIKE '51%' OR code LIKE '52%' OR code LIKE '53%' OR code LIKE '54%')
                  AND balance != 0;",
                new { cid = companyId })).ToList();
            var retained = _accountIds.GetValueOrDefault("3301");

            if (retained == Guid.Empty || (!revenueAccounts.Any() && !expenseAccounts.Any()))
            {
                _logger.LogInformation("FullYearSeeder: no closing entries needed");
                return;
            }

            var lines = new List<CreateJournalLineRequest>();
            // Revenue balances are credit (negative in our convention), so debit them
            foreach (var r in revenueAccounts)
            {
                if (r.balance < 0) // credit balance → debit to close
                    lines.Add(new CreateJournalLineRequest(r.id, Math.Abs(r.balance), 0, "إقفال إيرادات", null));
            }
            // Expense balances are debit (positive), so credit them
            foreach (var e in expenseAccounts)
            {
                if (e.balance > 0)
                    lines.Add(new CreateJournalLineRequest(e.id, 0, e.balance, "إقفال مصروفات", null));
            }
            // Net to retained earnings
            var totalRevenue = revenueAccounts.Sum(r => Math.Abs(Math.Min(r.balance, 0)));
            var totalExpense = expenseAccounts.Sum(e => Math.Max(e.balance, 0));
            var netIncome = totalRevenue - totalExpense;
            if (netIncome > 0)
                lines.Add(new CreateJournalLineRequest(retained, 0, netIncome, "صافي الدخل", null));
            else if (netIncome < 0)
                lines.Add(new CreateJournalLineRequest(retained, Math.Abs(netIncome), 0, "صافي خسارة", null));

            if (lines.Count < 2) return;

            var entry = await _journal.CreateDraftAsync(new CreateJournalEntryRequest(
                companyId, FY_END, "إقفال السنة المالية - قيود الإقفال",
                lines, Source: "year-end-closing"), _mainUserId);
            await _journal.ApproveAsync(entry.Id, _mainUserId);
            await _journal.PostAsync(entry.Id);
            _result.JournalEntriesCreated++;
            _result.YearEndClosingCreated = true;
        }
        catch (Exception ex) { _result.Errors.Add($"Year-end closing: {ex.Message}"); }
    }

    // ----------------------------------------------------------------
    // Bulk approve pending
    // ----------------------------------------------------------------

    private async Task BulkApprovePendingAsync(Guid companyId)
    {
        try
        {
            using var conn = _db.CreateConnection();
            var pending = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM journal_entries
                WHERE company_id = @cid AND status = 'PENDING';",
                new { cid = companyId })).ToList();
            int approved = 0, posted = 0;
            foreach (var id in pending)
            {
                try
                {
                    await _journal.ApproveAsync(id, _mainUserId);
                    await _journal.PostAsync(id);
                    approved++;
                    posted++;
                }
                catch { /* skip silently */ }
            }
            _result.EntriesApproved = approved;
            _result.EntriesPosted = posted;
            _logger.LogInformation("FullYearSeeder: approved {A} / posted {P} pending entries", approved, posted);
        }
        catch (Exception ex) { _result.Errors.Add($"Bulk approve: {ex.Message}"); }
    }
}
