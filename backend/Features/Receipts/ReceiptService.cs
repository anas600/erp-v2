using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Receipts;

public class ReceiptService
{
    private readonly IDbConnectionFactory _db;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;
    private readonly InvoiceService _invoices;

    public ReceiptService(
        IDbConnectionFactory db,
        AccountService accounts,
        JournalService journal,
        InvoiceService invoices)
    {
        _db = db;
        _accounts = accounts;
        _journal = journal;
        _invoices = invoices;
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
                   r.invoice_id, i.invoice_number AS invoice_number,
                   r.created_at, r.created_by,
                   u.full_name_ar AS created_by_name
            FROM receipt_vouchers r
            JOIN contacts c ON c.id = r.contact_id
            LEFT JOIN invoices i ON i.id = r.invoice_id
            LEFT JOIN users u ON u.id = r.created_by
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
                   r.invoice_id, i.invoice_number AS invoice_number,
                   r.created_at, r.created_by,
                   u.full_name_ar AS created_by_name
            FROM receipt_vouchers r
            JOIN contacts c ON c.id = r.contact_id
            LEFT JOIN invoices i ON i.id = r.invoice_id
            LEFT JOIN users u ON u.id = r.created_by
            WHERE r.id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<ReceiptVoucherDto> CreateAsync(CreateReceiptVoucherRequest req, Guid? createdBy)
    {
        ValidateRequest(req);
        if (req.InvoiceId.HasValue)
            await ValidateInvoiceLinkAsync(req.CompanyId, req.InvoiceId.Value, req.ContactId);

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
        if (req.InvoiceId.HasValue)
            await ValidateInvoiceLinkAsync(req.CompanyId, req.InvoiceId.Value, req.ContactId);

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
    /// Post the receipt: build a journal entry with two lines
    /// (DR Cash/Bank, CR AR sub-ledger) and post it via the
    /// regular Posting Engine. The AR sub-ledger is found (and
    /// created on demand) via <see cref="AccountService.EnsureSubLedgerAsync"/>.
    ///
    /// Sprint 25 — atomic settlement:
    ///   If the receipt has an <c>invoice_id</c>, the same transaction
    ///   calls <see cref="InvoiceService.ApplyPaymentInTxAsync"/> to
    ///   bump <c>invoices.amount_paid</c>. If the invoice update fails
    ///   (wrong contact, wrong company, over-payment, locked invoice,
    ///   etc.), the entire transaction rolls back — the journal entry
    ///   is NOT created and the receipt stays in 'draft' state.
    ///
    /// Sprint 26 — auto-create sub-ledger:
    ///   Replaced GetSubLedgerForContactAsync + manual error with
    ///   EnsureSubLedgerAsync. If the customer has no sub-ledger yet,
    ///   we create one in the same call (under the AR control account
    ///   1200). The user no longer needs to provision sub-ledgers
    ///   manually from the accounts page before they can post a
    ///   receipt.
    /// </summary>
    public async Task<ReceiptVoucherDto?> PostAsync(Guid id, Guid? userId)
    {
        var receipt = await GetByIdAsync(id);
        if (receipt is null) return null;
        if (receipt.Status == "posted") return receipt;
        if (receipt.Status != "draft")
            throw new InvalidOperationException("لا يمكن ترحيل سند في هذه الحالة");

        // Find or auto-create the customer's AR sub-ledger.
        // EnsureSubLedgerAsync picks 1200 as the parent for customers
        // and creates the detail account + link on the fly.
        var subLedger = await _accounts.EnsureSubLedgerAsync(receipt.CompanyId, receipt.ContactId);

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

        // Sprint 25 — wrap the whole post in a single transaction so the
        // JE, the receipt status update, and (if linked) the invoice
        // amount_paid update either all happen or none do.
        using var conn2 = _db.CreateConnection();
        using var tx = conn2.BeginTransaction();
        try
        {
            // 1) Create the journal entry on the open connection/tx.
            var journalEntryId = await _journal.CreateDraftInTxAsync(conn2, tx, jeReq, userId);

            // 2) Stamp the receipt as posted.
            await conn2.ExecuteAsync(@"
                UPDATE receipt_vouchers SET
                    status = 'posted',
                    posted_at = NOW(),
                    journal_entry_id = @journalEntryId
                WHERE id = @id;",
                new { id, journalEntryId }, tx);

            // 3) If the receipt is tied to a specific invoice, apply
            //    the payment on the SAME transaction. Any failure here
            //    rolls back the JE and the receipt status flip.
            if (receipt.InvoiceId.HasValue)
            {
                await _invoices.ApplyPaymentInTxAsync(
                    conn2, tx,
                    receipt.InvoiceId.Value,
                    receipt.Amount,
                    receipt.VoucherDate,
                    id);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return await GetByIdAsync(id);
    }

    /// <summary>
    /// Validates that the linked invoice belongs to the same company and
    /// the same contact as the receipt. This is a friendlier early check
    /// than the error you'd get from the FK / status check inside
    /// ApplyPaymentInTxAsync; we surface it at create-time so the user
    /// doesn't have to fill a receipt, save, post, and only then discover
    /// the invoice doesn't match.
    /// </summary>
    private async Task ValidateInvoiceLinkAsync(Guid companyId, Guid invoiceId, Guid contactId)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<(Guid? company_id, string? party_name, string? status)>(@"
            SELECT company_id, party_name, status
            FROM invoices WHERE id = @id;",
            new { id = invoiceId });
        if (row.company_id is null)
            throw new InvalidOperationException("الفاتورة المرتبطة بالسند غير موجودة");
        if (row.company_id != companyId)
            throw new InvalidOperationException("الفاتورة لا تنتمي لنفس الشركة");
        if (row.status == "cancelled")
            throw new InvalidOperationException("الفاتورة ملغاة — لا يمكن تسديدها");

        // Match the invoice's party to the contact's name. Same JOIN
        // trick the aging reports use (no contact_id FK on invoices).
        var contact = await conn.QuerySingleOrDefaultAsync<(string? name, string? type)>(@"
            SELECT name, type FROM contacts WHERE id = @id AND company_id = @companyId;",
            new { id = contactId, companyId });
        if (contact.name is null)
            throw new InvalidOperationException("العميل غير موجود");
        if (!string.Equals(contact.name, row.party_name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "الفاتورة لا تخص هذا العميل. " +
                $"الفاتورة باسم '{row.party_name}'، السند للعميل '{contact.name}'.");
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
        r.invoice_id, r.invoice_number,
        r.created_at, r.created_by, r.created_by_name);

    private record ReceiptRow(
        Guid id, Guid company_id, string voucher_number, DateTime voucher_date, Guid contact_id,
        string? contact_name, string? contact_code,
        decimal amount, string payment_method, Guid? bank_account_id,
        string? check_number, DateTime? check_date, string? reference, string? narration,
        string status, DateTime? posted_at, Guid? journal_entry_id,
        Guid? invoice_id, string? invoice_number,
        DateTime created_at, Guid? created_by, string? created_by_name);
}
