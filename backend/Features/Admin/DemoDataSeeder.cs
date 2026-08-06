using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Contacts;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Payments;
using ErpV2.Features.Receipts;

namespace ErpV2.Features.Admin;

/// <summary>
/// Sprint 26 — Demo data seeder for fresh deployments / demos.
///
/// Creates a realistic slice of an ERP-V2 tenant so the user can
/// click around the dashboard and see real numbers in every report:
///   - 5 customers (أسس 3, Alpha, Al-Noor, Palm, Libyan Supply Co)
///   - 3 suppliers (Modern Tech, Audit Office, Spare Parts Co)
///   - 10 invoices (8 sales + 3 purchase, mix of dates in 2026)
///   - 5 receipts (mix of full / partial / advance)
///   - 2 payments (1 full to Modern Tech, 1 partial to Audit Office)
///
/// All amounts are in LYD (the project's base currency).
///
/// Why this lives in code, not a migration:
///   Migrations are for schema and seed STRUCTURE. Demo data is
///   demo-shaped: it changes per sprint, it's safe to wipe and
///   re-create, and we want a callable endpoint (POST /api/admin/seed-demo-data)
///   that the admin can re-run after `cleanup-data` to reset the
///   dashboard to a known state.
///
/// Atomicity:
///   The seeder wraps each invoice in a try/catch and skips failures
///   so one bad invoice doesn't take down the whole seed. The endpoint
///   returns a summary (counts + per-step status) so the caller can
///   see exactly which steps succeeded.
/// </summary>
public class DemoDataSeeder
{
    private readonly IDbConnectionFactory _db;
    private readonly ContactService _contacts;
    private readonly AccountService _accounts;
    private readonly InvoiceService _invoices;
    private readonly ReceiptService _receipts;
    private readonly PaymentService _payments;
    private readonly JournalService _journal;

    public DemoDataSeeder(
        IDbConnectionFactory db,
        ContactService contacts,
        AccountService accounts,
        InvoiceService invoices,
        ReceiptService receipts,
        PaymentService payments,
        JournalService journal)
    {
        _db = db;
        _contacts = contacts;
        _accounts = accounts;
        _invoices = invoices;
        _receipts = receipts;
        _payments = payments;
    }

    public async Task<SeedResult> SeedAsync(Guid companyId, Guid? userId = null)
    {
        var result = new SeedResult { CompanyId = companyId };

        // ============================================================
        // 0) Pre-flight: the control accounts 1200 (AR) and 2000 (AP)
        //    must exist. The seed migration creates them; we just
        //    double-check so the seeder fails fast with a clear
        //    Arabic error instead of throwing a Postgres FK violation
        //    inside EnsureSubLedgerAsync.
        // ============================================================
        using (var conn = _db.CreateConnection())
        {
            var controlCodes = new[] { "1200", "2000" };
            foreach (var code in controlCodes)
            {
                var exists = await conn.ExecuteScalarAsync<bool>(@"
                    SELECT EXISTS (
                        SELECT 1 FROM accounts
                        WHERE company_id = @companyId AND code = @code
                    );",
                    new { companyId, code });
                if (!exists)
                {
                    throw new InvalidOperationException(
                        $"حساب التحكم {code} غير موجود. الرجاء إعداد دليل الحسابات قبل تشغيل البذر.");
                }
            }
        }

        // ============================================================
        // 1) Customers
        // ============================================================
        var customerDefs = new (string Code, string Name, string NameAr, decimal CreditLimit)[]
        {
            ("CUST-001", "Usus Group",                    "أسس 3",                   1500m),
            ("CUST-002", "Alpha Co. for Contracting",     "شركة الفا للمقاولات",     5000m),
            ("CUST-003", "Al-Noor Trading",               "نور تريدنغ",              3000m),
            ("CUST-004", "Palm Establishment",            "مؤسسة النخيل",            2500m),
            ("CUST-005", "Libyan Supply Company",         "الشركة الليبية للتوريدات", 4000m)
        };

        var customerIds = new Dictionary<string, Guid>();
        foreach (var (code, name, nameAr, _) in customerDefs)
        {
            try
            {
                var existing = await _contacts.GetByCompanyAsync(companyId, "customer");
                var already = existing.FirstOrDefault(c => c.Code == code);
                if (already is not null)
                {
                    customerIds[code] = already.Id;
                }
                else
                {
                    var c = await _contacts.CreateAsync(new CreateContactRequest(
                        companyId, "customer", code, name, nameAr,
                        null, null, null));
                    customerIds[code] = c.Id;
                }
                result.CustomersCreated++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Customer {code}: {ex.Message}");
            }
        }

        // ============================================================
        // 2) Suppliers
        // ============================================================
        var supplierDefs = new (string Code, string Name, string NameAr)[]
        {
            ("SUPP-001", "Modern Tech Supplies",    "التقنية الحديثة"),
            ("SUPP-002", "Audit Office",            "مكتب المراجعة المحاسبي"),
            ("SUPP-003", "Spare Parts Co.",         "موردي قطع الغيار")
        };

        var supplierIds = new Dictionary<string, Guid>();
        foreach (var (code, name, nameAr) in supplierDefs)
        {
            try
            {
                var existing = await _contacts.GetByCompanyAsync(companyId, "supplier");
                var already = existing.FirstOrDefault(c => c.Code == code);
                if (already is not null)
                {
                    supplierIds[code] = already.Id;
                }
                else
                {
                    var s = await _contacts.CreateAsync(new CreateContactRequest(
                        companyId, "supplier", code, name, nameAr,
                        null, null, null));
                    supplierIds[code] = s.Id;
                }
                result.SuppliersCreated++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Supplier {code}: {ex.Message}");
            }
        }

        // ============================================================
        // 3) Auto-create sub-ledgers for every contact
        // ============================================================
        // EnsureSubLedgerAsync creates a 1200-CUST-001 / 2000-SUPP-001
        // style account + account_contact_links row if missing.
        foreach (var (_, id) in customerIds)
        {
            try
            {
                await _accounts.EnsureSubLedgerAsync(companyId, id);
                result.SubLedgersCreated++;
            }
            catch (Exception ex) { result.Errors.Add($"Sub-ledger cust {id}: {ex.Message}"); }
        }
        foreach (var (_, id) in supplierIds)
        {
            try
            {
                await _accounts.EnsureSubLedgerAsync(companyId, id);
                result.SubLedgersCreated++;
            }
            catch (Exception ex) { result.Errors.Add($"Sub-ledger supp {id}: {ex.Message}"); }
        }

        // ============================================================
        // 4) Invoices
        // ============================================================
        // Mix of sales and purchase, spread across the 2026 calendar
        // year. We post each invoice so the journal entries and
        // account balances are realistic.
        //
        // Invoice plan (each tuple: customer/supplier code, type, date, total LYD):
        //   3 sales (CUST-001, CUST-003, CUST-005): 800, 1500, 2200
        //   2 sales (CUST-002, CUST-004): 3500, 1200
        //   3 purchase (SUPP-001, SUPP-002, SUPP-003): 600, 800, 1500
        //   2 sales (extra, smaller): 450, 900
        var invoiceDefs = new (string ContactCode, string Type, DateTime Date, decimal SubTotal, decimal TaxRate, string Notes)[]
        {
            ("CUST-001", "sales",    new DateTime(2026, 1, 15),  695.65m, 0.15m, "توريد معدات - يناير"),
            ("CUST-003", "sales",    new DateTime(2026, 2, 10), 1304.35m, 0.15m, "خدمات استشارية - فبراير"),
            ("CUST-005", "sales",    new DateTime(2026, 3, 5),  1913.04m, 0.15m, "صيانة دورية - مارس"),
            ("CUST-002", "sales",    new DateTime(2026, 3, 22), 3043.48m, 0.15m, "مشروع توريد - مارس"),
            ("CUST-004", "sales",    new DateTime(2026, 4, 12), 1043.48m, 0.15m, "استشارات هندسية - أبريل"),
            ("SUPP-001", "purchase", new DateTime(2026, 1, 25),  521.74m, 0.15m, "شراء أجهزة كمبيوتر"),
            ("SUPP-002", "purchase", new DateTime(2026, 2, 18),  695.65m, 0.15m, "خدمات تدقيق"),
            ("SUPP-003", "purchase", new DateTime(2026, 4, 3),  1304.35m, 0.15m, "قطع غيار ومعدات"),
            ("CUST-001", "sales",    new DateTime(2026, 5, 8),   391.30m, 0.15m, "صيانة طارئة"),
            ("CUST-003", "sales",    new DateTime(2026, 5, 20),  782.61m, 0.15m, "دعم فني إضافي")
        };

        var invoiceIds = new Dictionary<int, Guid>(); // index -> id
        var invoiceByContact = new Dictionary<string, List<(Guid Id, decimal Total)>>();

        for (int i = 0; i < invoiceDefs.Length; i++)
        {
            var (code, type, date, subTotal, taxRate, notes) = invoiceDefs[i];
            try
            {
                var contactMap = type == "sales" ? customerIds : supplierIds;
                if (!contactMap.TryGetValue(code, out var contactId))
                {
                    result.Errors.Add($"Invoice {i+1}: contact {code} missing");
                    continue;
                }
                var contactDto = type == "sales"
                    ? (await _contacts.GetByIdAsync(contactId))!
                    : (await _contacts.GetByIdAsync(contactId))!;

                // Use a product line so the invoice total is realistic.
                // We pull any active product for this company.
                Guid? productId;
                using (var conn = _db.CreateConnection())
                {
                    productId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                        SELECT id FROM products
                        WHERE company_id = @companyId AND is_active = true
                        ORDER BY code LIMIT 1;",
                        new { companyId });
                }
                if (productId is null)
                {
                    result.Errors.Add($"Invoice {i+1}: no product seeded for company {companyId}");
                    continue;
                }

                // One line with the product, quantity 1, overridden unit price = subtotal.
                var req = new CreateInvoiceRequest(
                    companyId,
                    type,
                    date,
                    contactDto.Name,
                    contactDto.NameAr,
                    null,
                    notes,
                    taxRate,
                    IntercompanyCompanyId: null,
                    Lines: new List<CreateInvoiceLineRequest>
                    {
                        new CreateInvoiceLineRequest(
                            AccountId: null,
                            ProductId: productId,
                            Description: notes,
                            Quantity: 1m,
                            UnitPrice: subTotal,
                            TaxRate: taxRate)
                    });

                var draft = await _invoices.CreateDraftAsync(req, userId);
                var posted = await _invoices.PostAsync(draft.Id);
                invoiceIds[i] = posted.Id;

                if (!invoiceByContact.TryGetValue(code, out var list))
                {
                    list = new List<(Guid, decimal)>();
                    invoiceByContact[code] = list;
                }
                list.Add((posted.Id, posted.Total));

                result.InvoicesCreated++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Invoice {i+1} ({code} {type} {date:yyyy-MM-dd}): {ex.Message}");
            }
        }

        // ============================================================
        // 5) Receipts
        // ============================================================
        // 2 receipts: CUST-001 and CUST-003 — full payment of one invoice.
        // 2 receipts: CUST-005 and CUST-002 — partial payment of one invoice.
        // 1 receipt: CUST-004 — advance (no invoice link).
        var receiptDefs = new (string ContactCode, DateTime Date, decimal Amount, int? InvoiceIndex, string Method, string? Reference, string? Narration)[]
        {
            ("CUST-001", new DateTime(2026, 2,  1),  800m, 0, "cash", "RV-001", "تسديد كامل - يناير"),
            ("CUST-003", new DateTime(2026, 2, 28), 1500m, 1, "bank", "RV-002", "تسديد كامل - فبراير"),
            ("CUST-005", new DateTime(2026, 3, 15), 1000m, 2, "cash", "RV-003", "تسديد جزئي - مارس"),
            ("CUST-002", new DateTime(2026, 4,  5), 2000m, 3, "bank", "RV-004", "تسديد جزئي - أبريل"),
            ("CUST-004", new DateTime(2026, 5,  1),  500m, null, "cash", "RV-005", "دفعة مقدمة - مايو")
        };

        foreach (var (code, date, amount, invIdx, method, reference, narration) in receiptDefs)
        {
            try
            {
                if (!customerIds.TryGetValue(code, out var contactId))
                {
                    result.Errors.Add($"Receipt for {code}: contact missing");
                    continue;
                }
                Guid? invoiceId = (invIdx.HasValue && invoiceIds.TryGetValue(invIdx.Value, out var iid))
                    ? iid : (Guid?)null;
                var req = new CreateReceiptVoucherRequest(
                    companyId,
                    date,
                    contactId,
                    amount,
                    method,
                    BankAccountId: null,
                    CheckNumber: null,
                    CheckDate: null,
                    Reference: reference,
                    Narration: narration,
                    InvoiceId: invoiceId);
                var draft = await _receipts.CreateAsync(req, userId);
                await _receipts.PostAsync(draft.Id, userId);
                result.ReceiptsCreated++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Receipt {reference}: {ex.Message}");
            }
        }

        // ============================================================
        // 6) Payments
        // ============================================================
        // 1 full payment to SUPP-001 (purchase invoice index 5 = 600 LYD).
        // 1 partial payment to SUPP-002 (purchase invoice index 6 = 800 LYD,
        //   paying only 400 LYD).
        var paymentDefs = new (string ContactCode, DateTime Date, decimal Amount, int? InvoiceIndex, string Method, string? Reference, string? Narration)[]
        {
            ("SUPP-001", new DateTime(2026, 2,  5),  600m, 5, "bank", "PV-001", "تسديد كامل - شراء كمبيوتر"),
            ("SUPP-002", new DateTime(2026, 3, 10),  400m, 6, "cash", "PV-002", "تسديد جزئي - خدمات تدقيق")
        };

        foreach (var (code, date, amount, invIdx, method, reference, narration) in paymentDefs)
        {
            try
            {
                if (!supplierIds.TryGetValue(code, out var contactId))
                {
                    result.Errors.Add($"Payment for {code}: contact missing");
                    continue;
                }
                Guid? invoiceId = (invIdx.HasValue && invoiceIds.TryGetValue(invIdx.Value, out var iid))
                    ? iid : (Guid?)null;
                var req = new CreatePaymentVoucherRequest(
                    companyId,
                    date,
                    contactId,
                    amount,
                    method,
                    BankAccountId: null,
                    CheckNumber: null,
                    CheckDate: null,
                    Reference: reference,
                    Narration: narration,
                    InvoiceId: invoiceId);
                var draft = await _payments.CreateAsync(req, userId);
                await _payments.PostAsync(draft.Id, userId);
                result.PaymentsCreated++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Payment {reference}: {ex.Message}");
            }
        }

        // ============================================================
        // 7) Auto-approve + post all pending journal entries
        // ============================================================
        // The rule engine (Sprint 15) creates entries in "pending"
        // status so the accountant can review them before they hit
        // the books. For demo data, we don't want the trial balance
        // and balance sheet to be empty just because the accountant
        // hasn't clicked "Approve" on each entry — so the seed
        // approves them all in one shot.
        //
        // Each approval goes through PostingEngine.PostAsync, which
        // also updates the account balances — so the trial balance
        // will reflect the seeded postings as soon as the seed
        // returns.
        try
        {
            using var conn = _db.CreateConnection();
            var pendingIds = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM journal_entries
                WHERE company_id = @companyId AND status = 'pending'
                ORDER BY entry_date, created_at;",
                new { companyId })).ToList();

            int approved = 0;
            foreach (var id in pendingIds)
            {
                try
                {
                    await _journal.ApproveAsync(id, userId);
                    approved++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Approve {id}: {ex.Message}");
                }
            }
            // Stash the count in the result so the response can show
            // it (we don't add a new field, just record in the message
            // chain via errors if any).
            if (approved > 0)
            {
                // No-op: success is silent. If you want to surface
                // this in the API response, add an int field to
                // SeedResult.
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Auto-approve: {ex.Message}");
        }

        return result;
    }
}

public class SeedResult
{
    public Guid CompanyId { get; set; }
    public int CustomersCreated { get; set; }
    public int SuppliersCreated { get; set; }
    public int SubLedgersCreated { get; set; }
    public int InvoicesCreated { get; set; }
    public int ReceiptsCreated { get; set; }
    public int PaymentsCreated { get; set; }
    public List<string> Errors { get; set; } = new();

    public int TotalErrors => Errors.Count;
    public bool AllSucceeded => Errors.Count == 0;
}
