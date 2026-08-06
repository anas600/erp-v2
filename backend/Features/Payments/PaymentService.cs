using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;
using ErpV2.Features.Rules;

namespace ErpV2.Features.Payments;

/// <summary>
/// Payment voucher service (سندات الصرف).
///
/// Sprint 25 — Rules Engine + Auto-link:
///   Symmetric to ReceiptService. The post flow:
///   1. Build + create a draft journal entry (DR AP sub-ledger,
///      CR Cash/Bank). Stays as a draft for accountant review.
///   2. Mark the payment as 'posted' and stamp the journal_entry_id.
///   3. Auto-link to a purchase invoice for the same supplier with
///      the exact same amount. Bump invoice.amount_paid; if it
///      reaches total, flip to 'paid' + stamp paid_at.
///   4. Fire the "PaymentVoucherApproved" event so a custom rule
///      can approve+post the draft, replace it, or follow up.
/// </summary>
public class PaymentService
{
    private readonly IDbConnectionFactory _db;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;
    private readonly RuleEvaluator _rules;
    private readonly ILogger<PaymentService> _log;

    public PaymentService(
        IDbConnectionFactory db,
        AccountService accounts,
        JournalService journal,
        RuleEvaluator rules,
        ILogger<PaymentService> log)
    {
        _db = db;
        _accounts = accounts;
        _journal = journal;
        _rules = rules;
        _log = log;
    }

    public async Task<List<PaymentVoucherDto>> GetByCompanyAsync(Guid companyId, string? status = null)
    {
        using var conn = _db.CreateConnection();
        var sql = @"
            SELECT p.id, p.company_id, p.voucher_number, p.voucher_date, p.contact_id,
                   c.name AS contact_name, c.code AS contact_code,
                   p.amount, p.payment_method, p.bank_account_id,
                   p.check_number, p.check_date, p.reference, p.narration,
                   p.status, p.posted_at, p.journal_entry_id,
                   p.invoice_id,
                   inv.invoice_number AS invoice_number,
                   inv.status AS invoice_status,
                   p.created_at, p.created_by,
                   u.full_name_ar AS created_by_name
            FROM payment_vouchers p
            JOIN contacts c ON c.id = p.contact_id
            LEFT JOIN users u ON u.id = p.created_by
            LEFT JOIN invoices inv ON inv.id = p.invoice_id
            WHERE p.company_id = @companyId" +
            (status is not null ? " AND p.status = @status" : "") + @"
            ORDER BY p.voucher_date DESC, p.created_at DESC
            LIMIT 200;";
        var rows = await conn.QueryAsync<PaymentRow>(sql, new { companyId, status });
        return rows.Select(Map).ToList();
    }

    public async Task<PaymentVoucherDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PaymentRow>(@"
            SELECT p.id, p.company_id, p.voucher_number, p.voucher_date, p.contact_id,
                   c.name AS contact_name, c.code AS contact_code,
                   p.amount, p.payment_method, p.bank_account_id,
                   p.check_number, p.check_date, p.reference, p.narration,
                   p.status, p.posted_at, p.journal_entry_id,
                   p.invoice_id,
                   inv.invoice_number AS invoice_number,
                   inv.status AS invoice_status,
                   p.created_at, p.created_by,
                   u.full_name_ar AS created_by_name
            FROM payment_vouchers p
            JOIN contacts c ON c.id = p.contact_id
            LEFT JOIN users u ON u.id = p.created_by
            LEFT JOIN invoices inv ON inv.id = p.invoice_id
            WHERE p.id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<PaymentVoucherDto> CreateAsync(CreatePaymentVoucherRequest req, Guid? createdBy)
    {
        ValidateRequest(req);
        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        var voucherNumber = await GenerateVoucherNumberAsync(req.CompanyId, conn);

        await conn.ExecuteAsync(@"
            INSERT INTO payment_vouchers (
                id, company_id, voucher_number, voucher_date, contact_id,
                amount, payment_method, bank_account_id, check_number, check_date,
                reference, narration, status, created_by, invoice_id
            )
            VALUES (
                @id, @companyId, @voucherNumber, @voucherDate, @contactId,
                @amount, @paymentMethod, @bankAccountId, @checkNumber, @checkDate,
                @reference, @narration, 'draft', @createdBy, @invoiceId
            );",
            new
            {
                id, companyId = req.CompanyId, voucherNumber, voucherDate = req.VoucherDate,
                contactId = req.ContactId, amount = req.Amount, paymentMethod = req.PaymentMethod,
                bankAccountId = req.BankAccountId, checkNumber = req.CheckNumber,
                checkDate = req.CheckDate, reference = req.Reference, narration = req.Narration,
                createdBy, invoiceId = req.InvoiceId
            });

        return (await GetByIdAsync(id))!;
    }

    public async Task<PaymentVoucherDto?> UpdateAsync(Guid id, CreatePaymentVoucherRequest req)
    {
        ValidateRequest(req);
        using var conn = _db.CreateConnection();
        var status = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT status FROM payment_vouchers WHERE id = @id;", new { id });
        if (status != "draft") throw new InvalidOperationException("لا يمكن تعديل سند مرحّل");

        await conn.ExecuteAsync(@"
            UPDATE payment_vouchers SET
                voucher_date = @voucherDate, contact_id = @contactId,
                amount = @amount, payment_method = @paymentMethod,
                bank_account_id = @bankAccountId, check_number = @checkNumber,
                check_date = @checkDate, reference = @reference, narration = @narration,
                invoice_id = @invoiceId
            WHERE id = @id;",
            new
            {
                id, voucherDate = req.VoucherDate, contactId = req.ContactId,
                amount = req.Amount, paymentMethod = req.PaymentMethod,
                bankAccountId = req.BankAccountId, checkNumber = req.CheckNumber,
                checkDate = req.CheckDate, reference = req.Reference, narration = req.Narration,
                invoiceId = req.InvoiceId
            });
        return await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var status = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT status FROM payment_vouchers WHERE id = @id;", new { id });
        if (status != "draft") throw new InvalidOperationException("لا يمكن حذف سند مرحّل");

        await conn.ExecuteAsync("DELETE FROM payment_vouchers WHERE id = @id;", new { id });
        return true;
    }

    /// <summary>
    /// Post the payment: DR AP sub-ledger, CR Cash/Bank. Symmetric
    /// to receipt posting, with auto-link to a purchase invoice
    /// (Sprint 25) and event trigger (Sprint 25).
    /// </summary>
    public async Task<PaymentVoucherDto?> PostAsync(Guid id, Guid? userId)
    {
        var payment = await GetByIdAsync(id);
        if (payment is null) return null;
        if (payment.Status == "posted") return payment;
        if (payment.Status != "draft")
            throw new InvalidOperationException("لا يمكن ترحيل سند في هذه الحالة");

        var subLedger = await _accounts.GetSubLedgerForContactAsync(payment.CompanyId, payment.ContactId);
        if (subLedger is null)
            throw new InvalidOperationException(
                "لا يوجد حساب تفصيلي (sub-ledger) لهذا المورّد. " +
                "الرجاء إنشاء الحساب التفصيلي من صفحة /dashboard/accounts أولاً.");

        var cashAccountId = payment.BankAccountId;
        if (cashAccountId is null)
        {
            using var conn = _db.CreateConnection();
            cashAccountId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                SELECT id FROM accounts
                WHERE company_id = @companyId AND code IN ('1000', '1100') AND is_active = true
                ORDER BY code LIMIT 1;",
                new { companyId = payment.CompanyId });
            if (cashAccountId is null)
                throw new InvalidOperationException("لا يوجد حساب صندوق أو بنك.");
        }

        // Build journal entry (DR AP sub-ledger, CR Cash/Bank)
        var lines = new List<CreateJournalLineRequest>
        {
            new(subLedger.Id, payment.Amount, 0,
                $"تسوية حساب المورّد {payment.ContactName}"),
            new(cashAccountId.Value, 0, payment.Amount,
                $"دفع إلى {payment.ContactName} - {payment.Reference ?? payment.VoucherNumber}")
        };

        var jeReq = new CreateJournalEntryRequest(
            payment.CompanyId,
            payment.VoucherDate,
            $"سند صرف {payment.VoucherNumber} - {payment.ContactName}" +
                (payment.Reference != null ? $" ({payment.Reference})" : ""),
            lines,
            Source: "payment"
        );

        var journalEntry = await _journal.CreateDraftAsync(jeReq, userId);

        // Mark posted + auto-link to a purchase invoice. Same shape
        // as ReceiptService.PostAsync but for invoice_type = 'purchase'.
        Guid? linkedInvoiceId;
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                UPDATE payment_vouchers SET
                    status = 'posted',
                    posted_at = NOW(),
                    journal_entry_id = @journalEntryId
                WHERE id = @id;",
                new { id, journalEntryId = journalEntry.Id });

            linkedInvoiceId = payment.InvoiceId;
            if (linkedInvoiceId is null)
            {
                // Find the oldest unpaid purchase invoice for this
                // supplier with total = payment.amount. Exact-match
                // only — the same simplification used on the receipt
                // side; partial settlements are not auto-linked.
                linkedInvoiceId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                    SELECT id FROM invoices
                    WHERE company_id = @companyId
                      AND contact_id = @contactId
                      AND invoice_type = 'purchase'
                      AND status NOT IN ('paid', 'cancelled')
                      AND amount_paid < total
                      AND total = @amount
                    ORDER BY invoice_date ASC, created_at ASC
                    LIMIT 1;",
                    new { companyId = payment.CompanyId, contactId = payment.ContactId, amount = payment.Amount });
            }

            if (linkedInvoiceId is not null)
            {
                await conn.ExecuteAsync(@"
                    UPDATE payment_vouchers
                    SET invoice_id = @invoiceId
                    WHERE id = @id;",
                    new { id, invoiceId = linkedInvoiceId });

                await conn.ExecuteAsync(@"
                    UPDATE invoices
                    SET amount_paid = amount_paid + @amount,
                        status = CASE WHEN amount_paid + @amount >= total THEN 'paid' ELSE status END,
                        paid_at  = CASE WHEN amount_paid + @amount >= total AND paid_at IS NULL THEN NOW() ELSE paid_at END
                    WHERE id = @invoiceId
                      AND amount_paid + @amount <= total;",
                    new { invoiceId = linkedInvoiceId, amount = payment.Amount });

                _log.LogInformation(
                    "Payment {Id} auto-linked to invoice {InvId} (amount {Amount})",
                    id, linkedInvoiceId, payment.Amount);
            }
        }

        // Fire PaymentVoucherApproved. Best-effort: a rule failure
        // does not roll back the post (the auto-link + draft entry
        // are already committed).
        try
        {
            var eventPayload = new Dictionary<string, object>
            {
                ["payment"] = new Dictionary<string, object>
                {
                    ["id"] = payment.Id,
                    ["voucherNumber"] = payment.VoucherNumber,
                    ["amount"] = payment.Amount,
                    ["contactId"] = payment.ContactId,
                    ["contactName"] = payment.ContactName,
                    ["date"] = payment.VoucherDate,
                    ["paymentMethod"] = payment.PaymentMethod
                }
            };
            await _rules.TriggerEventAsync(payment.CompanyId, userId, "PaymentVoucherApproved", eventPayload);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PaymentVoucherApproved event trigger failed for {Id} (non-fatal)", id);
        }

        return await GetByIdAsync(id);
    }

    private static void ValidateRequest(CreatePaymentVoucherRequest req)
    {
        if (req.CompanyId == Guid.Empty) throw new ArgumentException("companyId required");
        if (req.ContactId == Guid.Empty) throw new ArgumentException("contactId required");
        if (req.Amount <= 0) throw new ArgumentException("amount must be > 0");
        if (string.IsNullOrWhiteSpace(req.PaymentMethod)) throw new ArgumentException("paymentMethod required");
        var validMethods = new[] { "cash", "bank", "check" };
        if (!validMethods.Contains(req.PaymentMethod)) throw new ArgumentException($"paymentMethod must be one of: {string.Join(", ", validMethods)}");
    }

    private async Task<string> GenerateVoucherNumberAsync(Guid companyId, System.Data.IDbConnection conn)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"PV-{year}-";
        var lastNumber = await conn.ExecuteScalarAsync<string?>(@"
            SELECT voucher_number FROM payment_vouchers
            WHERE company_id = @companyId AND voucher_number LIKE @pattern
            ORDER BY voucher_number DESC LIMIT 1;",
            new { companyId, pattern = $"{prefix}%" });
        if (string.IsNullOrEmpty(lastNumber)) return $"{prefix}0001";
        var numPart = lastNumber.Substring(prefix.Length);
        if (int.TryParse(numPart, out var n)) return $"{prefix}{(n + 1):D4}";
        return $"{prefix}0001";
    }

    private static PaymentVoucherDto Map(PaymentRow r) => new(
        r.id, r.company_id, r.voucher_number, r.voucher_date, r.contact_id,
        r.contact_name ?? "", r.contact_code ?? "",
        r.amount, r.payment_method, r.bank_account_id,
        r.check_number, r.check_date, r.reference, r.narration,
        r.status, r.posted_at, r.journal_entry_id,
        r.invoice_id, r.invoice_number, r.invoice_status,
        r.created_at, r.created_by, r.created_by_name);

    private record PaymentRow(
        Guid id, Guid company_id, string voucher_number, DateTime voucher_date, Guid contact_id,
        string? contact_name, string? contact_code,
        decimal amount, string payment_method, Guid? bank_account_id,
        string? check_number, DateTime? check_date, string? reference, string? narration,
        string status, DateTime? posted_at, Guid? journal_entry_id,
        Guid? invoice_id, string? invoice_number, string? invoice_status,
        DateTime created_at, Guid? created_by, string? created_by_name);
}
