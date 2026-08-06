using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;
using ErpV2.Features.Rules;

namespace ErpV2.Features.Receipts;

/// <summary>
/// Receipt voucher service (سندات القبض).
///
/// Sprint 25 — Rules Engine + Auto-link:
///   * PostAsync now fires the "ReceiptVoucherApproved" event so a
///     user-configurable rule can:
///       (a) approve+post the auto-generated draft journal entry, or
///       (b) replace it with a custom one (e.g. split the cash
///           across multiple sub-ledgers).
///     The service's auto-generated draft remains in place regardless.
///   * PostAsync now auto-links the receipt to a sales invoice for
///     the same contact with the exact same amount. If a match is
///     found, invoice.invoice_id is stamped on the receipt, the
///     invoice's amount_paid goes up by receipt.amount, and when
///     amount_paid reaches total, the invoice flips to status='paid'
///     and paid_at is set.
///   * The CreateRequest accepts an optional invoiceId (set by the
///     UI's dropdown). If the user picked one, we use that instead
///     of the heuristic. If not, we run the auto-link heuristic.
///   * If the user-provided invoiceId doesn't match an unpaid
///     invoice (e.g. it was already paid by an earlier receipt),
///     we still honour the user's pick (no auto-override) but the
///     amount_paid update is guarded by the chk_amount_paid_le_total
///     CHECK so it can't push amount_paid over total.
/// </summary>
public class ReceiptService
{
    private readonly IDbConnectionFactory _db;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;
    private readonly RuleEvaluator _rules;
    private readonly ILogger<ReceiptService> _log;

    public ReceiptService(
        IDbConnectionFactory db,
        AccountService accounts,
        JournalService journal,
        RuleEvaluator rules,
        ILogger<ReceiptService> log)
    {
        _db = db;
        _accounts = accounts;
        _journal = journal;
        _rules = rules;
        _log = log;
    }

    public async Task<List<ReceiptVoucherDto>> GetByCompanyAsync(Guid companyId, string? status = null)
    {
        using var conn = _db.CreateConnection();
        var sql = @"
            SELECT r.id, r.company_id, r.voucher_number, r.voucher_date, r.contact_id,
                   c.name AS contact_name, c.code AS contact_code,
                   r.amount, r.payment_method, r.bank_account_id,
                   r.check_number, r.check_date, r.reference, r.narration,
                   r.status, r.posted_at, r.journal_entry_id,
                   r.invoice_id,
                   inv.invoice_number AS invoice_number,
                   inv.status AS invoice_status,
                   r.created_at, r.created_by,
                   u.full_name_ar AS created_by_name
            FROM receipt_vouchers r
            JOIN contacts c ON c.id = r.contact_id
            LEFT JOIN users u ON u.id = r.created_by
            LEFT JOIN invoices inv ON inv.id = r.invoice_id
            WHERE r.company_id = @companyId" +
            (status is not null ? " AND r.status = @status" : "") + @"
            ORDER BY r.voucher_date DESC, r.created_at DESC
            LIMIT 200;";
        var rows = await conn.QueryAsync<ReceiptRow>(sql, new { companyId, status });
        return rows.Select(Map).ToList();
    }

    public async Task<ReceiptVoucherDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ReceiptRow>(@"
            SELECT r.id, r.company_id, r.voucher_number, r.voucher_date, r.contact_id,
                   c.name AS contact_name, c.code AS contact_code,
                   r.amount, r.payment_method, r.bank_account_id,
                   r.check_number, r.check_date, r.reference, r.narration,
                   r.status, r.posted_at, r.journal_entry_id,
                   r.invoice_id,
                   inv.invoice_number AS invoice_number,
                   inv.status AS invoice_status,
                   r.created_at, r.created_by,
                   u.full_name_ar AS created_by_name
            FROM receipt_vouchers r
            JOIN contacts c ON c.id = r.contact_id
            LEFT JOIN users u ON u.id = r.created_by
            LEFT JOIN invoices inv ON inv.id = r.invoice_id
            WHERE r.id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<ReceiptVoucherDto> CreateAsync(CreateReceiptVoucherRequest req, Guid? createdBy)
    {
        ValidateRequest(req);

        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        var voucherNumber = await GenerateVoucherNumberAsync(req.CompanyId, conn);

        await conn.ExecuteAsync(@"
            INSERT INTO receipt_vouchers (
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

    public async Task<ReceiptVoucherDto?> UpdateAsync(Guid id, CreateReceiptVoucherRequest req)
    {
        ValidateRequest(req);

        using var conn = _db.CreateConnection();
        var status = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT status FROM receipt_vouchers WHERE id = @id;", new { id });
        if (status != "draft") throw new InvalidOperationException("لا يمكن تعديل سند مرحّل");

        await conn.ExecuteAsync(@"
            UPDATE receipt_vouchers SET
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
            "SELECT status FROM receipt_vouchers WHERE id = @id;", new { id });
        if (status != "draft") throw new InvalidOperationException("لا يمكن حذف سند مرحّل");

        await conn.ExecuteAsync("DELETE FROM receipt_vouchers WHERE id = @id;", new { id });
        return true;
    }

    /// <summary>
    /// Post the receipt:
    ///   1. Build + create a draft journal entry (DR Cash/Bank, CR AR
    ///      sub-ledger) via the regular Posting Engine. The entry
    ///      stays as a draft so the accountant can review and
    ///      approve — matching the pattern set in Sprint 15.
    ///   2. Mark the receipt as 'posted' and stamp the journal_entry_id.
    ///   3. Auto-link to a sales invoice:
    ///        a. If req.InvoiceId was set, honour that pick.
    ///        b. Otherwise look for an unpaid sales invoice for the
    ///           same contact with amount = receipt.amount (exact
    ///           match). If found, link to it.
    ///        c. Bump invoice.amount_paid by receipt.amount.
    ///        d. If amount_paid >= total, flip status='paid' and
    ///           stamp paid_at.
    ///   4. Fire the "ReceiptVoucherApproved" event so a custom rule
    ///      can approve+post the draft journal entry, replace it
    ///      with a custom one, or trigger a follow-up workflow.
    ///      The event is informational — the auto-link happens
    ///      before the event fires so any rule that reads the
    ///      invoice's updated status sees the new state.
    /// </summary>
    public async Task<ReceiptVoucherDto?> PostAsync(Guid id, Guid? userId)
    {
        var receipt = await GetByIdAsync(id);
        if (receipt is null) return null;
        if (receipt.Status == "posted") return receipt;
        if (receipt.Status != "draft")
            throw new InvalidOperationException("لا يمكن ترحيل سند في هذه الحالة");

        // Find the customer's AR sub-ledger account
        var subLedger = await _accounts.GetSubLedgerForContactAsync(receipt.CompanyId, receipt.ContactId);
        if (subLedger is null)
            throw new InvalidOperationException(
                "لا يوجد حساب تفصيلي (sub-ledger) لهذا العميل. " +
                "الرجاء إنشاء الحساب التفصيلي من صفحة /dashboard/accounts أولاً.");

        // Determine the cash/bank account
        var cashAccountId = receipt.BankAccountId;
        if (cashAccountId is null)
        {
            // Default: use 1000 (Cash) if it exists, else 1100 (Bank)
            using var conn = _db.CreateConnection();
            cashAccountId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                SELECT id FROM accounts
                WHERE company_id = @companyId AND code IN ('1000', '1100') AND is_active = true
                ORDER BY code LIMIT 1;",
                new { companyId = receipt.CompanyId });
            if (cashAccountId is null)
                throw new InvalidOperationException("لا يوجد حساب صندوق أو بنك. الرجاء إعداد دليل الحسابات.");
        }

        // Build the journal entry
        var lines = new List<CreateJournalLineRequest>
        {
            new(cashAccountId.Value, receipt.Amount, 0,
                $"تحصيل من {receipt.ContactName} - {receipt.Reference ?? receipt.VoucherNumber}"),
            new(subLedger.Id, 0, receipt.Amount,
                $"تسوية حساب العميل {receipt.ContactName}")
        };

        var jeReq = new CreateJournalEntryRequest(
            receipt.CompanyId,
            receipt.VoucherDate,
            $"سند قبض {receipt.VoucherNumber} - {receipt.ContactName}" +
                (receipt.Reference != null ? $" ({receipt.Reference})" : ""),
            lines,
            Source: "receipt"
        );

        // Create the journal entry as a draft (not auto-post) so
        // the accountant can review via the standard journal flow
        var journalEntry = await _journal.CreateDraftAsync(jeReq, userId);

        // Mark the receipt as posted and link the journal entry
        Guid? linkedInvoiceId;
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                UPDATE receipt_vouchers SET
                    status = 'posted',
                    posted_at = NOW(),
                    journal_entry_id = @journalEntryId
                WHERE id = @id;",
                new { id, journalEntryId = journalEntry.Id });

            // Auto-link: if the request didn't pin a specific invoice,
            // find the first unpaid sales invoice for this contact
            // with total = receipt.amount. We pick the OLDEST such
            // invoice (ORDER BY invoice_date ASC) so the user clears
            // the most-aged receivable first.
            linkedInvoiceId = receipt.InvoiceId;
            if (linkedInvoiceId is null)
            {
                linkedInvoiceId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                    SELECT id FROM invoices
                    WHERE company_id = @companyId
                      AND contact_id = @contactId
                      AND invoice_type = 'sales'
                      AND status NOT IN ('paid', 'cancelled')
                      AND amount_paid < total
                      AND total = @amount
                    ORDER BY invoice_date ASC, created_at ASC
                    LIMIT 1;",
                    new { companyId = receipt.CompanyId, contactId = receipt.ContactId, amount = receipt.Amount });
            }

            if (linkedInvoiceId is not null)
            {
                // Stamp the bi-directional link + bump amount_paid.
                // If the bump fills the invoice, flip to 'paid' and
                // stamp paid_at. The chk_amount_paid_le_total CHECK
                // ensures we can't overshoot (a second receipt for
                // the same invoice just hits the constraint cleanly).
                await conn.ExecuteAsync(@"
                    UPDATE receipt_vouchers
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
                    new { invoiceId = linkedInvoiceId, amount = receipt.Amount });

                _log.LogInformation(
                    "Receipt {Id} auto-linked to invoice {InvId} (amount {Amount})",
                    id, linkedInvoiceId, receipt.Amount);
            }
        }

        // Fire the ReceiptVoucherApproved event. Any user-enabled
        // rule for this event runs in priority order. The service's
        // auto-generated draft journal entry remains in place
        // regardless of what the rule does — a rule can approve it
        // (via a future ApprovePending action), replace it, or no-op.
        // We use a fresh connection to avoid coupling the event to
        // any in-flight transaction.
        try
        {
            var eventPayload = new Dictionary<string, object>
            {
                ["receipt"] = new Dictionary<string, object>
                {
                    ["id"] = receipt.Id,
                    ["voucherNumber"] = receipt.VoucherNumber,
                    ["amount"] = receipt.Amount,
                    ["contactId"] = receipt.ContactId,
                    ["contactName"] = receipt.ContactName,
                    ["date"] = receipt.VoucherDate,
                    ["paymentMethod"] = receipt.PaymentMethod
                }
            };
            await _rules.TriggerEventAsync(receipt.CompanyId, userId, "ReceiptVoucherApproved", eventPayload);
        }
        catch (Exception ex)
        {
            // The event trigger is best-effort. A failing rule should
            // not roll back a successful receipt post — the rule's
            // exception is already logged inside RuleEvaluator. We
            // log here too for visibility but do NOT re-throw.
            _log.LogError(ex, "ReceiptVoucherApproved event trigger failed for {Id} (non-fatal)", id);
        }

        return await GetByIdAsync(id);
    }

    private static void ValidateRequest(CreateReceiptVoucherRequest req)
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
        var prefix = $"RV-{year}-";
        var lastNumber = await conn.ExecuteScalarAsync<string?>(@"
            SELECT voucher_number FROM receipt_vouchers
            WHERE company_id = @companyId AND voucher_number LIKE @pattern
            ORDER BY voucher_number DESC LIMIT 1;",
            new { companyId, pattern = $"{prefix}%" });
        if (string.IsNullOrEmpty(lastNumber)) return $"{prefix}0001";
        var numPart = lastNumber.Substring(prefix.Length);
        if (int.TryParse(numPart, out var n)) return $"{prefix}{(n + 1):D4}";
        return $"{prefix}0001";
    }

    private static ReceiptVoucherDto Map(ReceiptRow r) => new(
        r.id, r.company_id, r.voucher_number, r.voucher_date, r.contact_id,
        r.contact_name ?? "", r.contact_code ?? "",
        r.amount, r.payment_method, r.bank_account_id,
        r.check_number, r.check_date, r.reference, r.narration,
        r.status, r.posted_at, r.journal_entry_id,
        r.invoice_id, r.invoice_number, r.invoice_status,
        r.created_at, r.created_by, r.created_by_name);

    private record ReceiptRow(
        Guid id, Guid company_id, string voucher_number, DateTime voucher_date, Guid contact_id,
        string? contact_name, string? contact_code,
        decimal amount, string payment_method, Guid? bank_account_id,
        string? check_number, DateTime? check_date, string? reference, string? narration,
        string status, DateTime? posted_at, Guid? journal_entry_id,
        Guid? invoice_id, string? invoice_number, string? invoice_status,
        DateTime created_at, Guid? created_by, string? created_by_name);
}
