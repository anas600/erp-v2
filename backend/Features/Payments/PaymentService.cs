using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Invoicing;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Payments;

public class PaymentService
{
    private readonly IDbConnectionFactory _db;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;
    private readonly InvoiceService _invoices;

    public PaymentService(
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

    public async Task<List<PaymentVoucherDto>> GetByCompanyAsync(
        Guid companyId, string? status = null, Guid? contactId = null)
    {
        using var conn = _db.CreateConnection();
        var sql = @"
            SELECT p.id, p.company_id, p.voucher_number, p.voucher_date, p.contact_id,
                   c.name AS contact_name, c.code AS contact_code,
                   p.amount, p.payment_method, p.bank_account_id,
                   p.check_number, p.check_date, p.reference, p.narration,
                   p.status, p.posted_at, p.journal_entry_id,
                   p.invoice_id, i.invoice_number AS invoice_number,
                   p.created_at, p.created_by,
                   u.full_name_ar AS created_by_name
            FROM payment_vouchers p
            JOIN contacts c ON c.id = p.contact_id
            LEFT JOIN invoices i ON i.id = p.invoice_id
            LEFT JOIN users u ON u.id = p.created_by
            WHERE p.company_id = @companyId" +
            (status is not null ? " AND p.status = @status" : "") +
            (contactId.HasValue ? " AND p.contact_id = @contactId" : "") + @"
            ORDER BY p.voucher_date DESC, p.created_at DESC
            LIMIT 200;";
        var rows = await conn.QueryAsync<PaymentRow>(sql, new { companyId, status, contactId });
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
                   p.invoice_id, i.invoice_number AS invoice_number,
                   p.created_at, p.created_by,
                   u.full_name_ar AS created_by_name
            FROM payment_vouchers p
            JOIN contacts c ON c.id = p.contact_id
            LEFT JOIN invoices i ON i.id = p.invoice_id
            LEFT JOIN users u ON u.id = p.created_by
            WHERE p.id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<PaymentVoucherDto> CreateAsync(CreatePaymentVoucherRequest req, Guid? createdBy)
    {
        ValidateRequest(req);
        if (req.InvoiceId.HasValue)
            await ValidateInvoiceLinkAsync(req.CompanyId, req.InvoiceId.Value, req.ContactId);

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
        if (req.InvoiceId.HasValue)
            await ValidateInvoiceLinkAsync(req.CompanyId, req.InvoiceId.Value, req.ContactId);

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
    /// Post the payment: DR AP sub-ledger for supplier,
    /// CR Cash/Bank. Symmetric to receipt posting.
    ///
    /// Sprint 25 — atomic settlement (mirror of ReceiptService.PostAsync).
    /// If the payment has an <c>invoice_id</c>, the same transaction
    /// calls <see cref="InvoiceService.ApplyPaymentInTxAsync"/> to bump
    /// <c>invoices.amount_paid</c>. The whole thing rolls back if any
    /// step fails.
    ///
    /// Sprint 26 — auto-create sub-ledger (Sprint 26).
    /// Uses EnsureSubLedgerAsync, which picks 2000 (AP) as the parent
    /// for suppliers and creates the detail account + link on the fly.
    /// </summary>
    public async Task<PaymentVoucherDto?> PostAsync(Guid id, Guid? userId)
    {
        var payment = await GetByIdAsync(id);
        if (payment is null) return null;
        if (payment.Status == "posted") return payment;
        if (payment.Status != "draft")
            throw new InvalidOperationException("لا يمكن ترحيل سند في هذه الحالة");

        // Find or auto-create the supplier's AP sub-ledger.
        var subLedger = await _accounts.EnsureSubLedgerAsync(payment.CompanyId, payment.ContactId);

        var cashAccountId = payment.BankAccountId;
        if (cashAccountId is null)
        {
            // Sprint 33 hotfix v2 — match sub-ledgers (1101-CASH-001,
            // 1102-BANK-001, etc.) instead of the L3 control codes
            // which are non-postable. Same root cause as ReceiptService.
            using var conn = _db.CreateConnection();
            cashAccountId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                SELECT id FROM accounts
                WHERE company_id = @companyId
                  AND is_active = true
                  AND is_postable = true
                  AND (code LIKE '1101-%' OR code LIKE '1102-%')
                ORDER BY code LIMIT 1;",
                new { companyId = payment.CompanyId });
            if (cashAccountId is null)
                throw new InvalidOperationException("لا يوجد حساب صندوق أو بنك قابل للترحيل. الرجاء إعداد دليل الحسابات.");
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

        // Sprint 25 — wrap the whole post in a single transaction so the
        // JE, the payment status update, and (if linked) the invoice
        // amount_paid update either all happen or none do.
        using var conn2 = _db.CreateConnection();
        using var tx = conn2.BeginTransaction();
        try
        {
            // 1) Create the journal entry on the open connection/tx.
            var journalEntryId = await _journal.CreateDraftInTxAsync(conn2, tx, jeReq, userId);

            // 2) Stamp the payment as posted.
            await conn2.ExecuteAsync(@"
                UPDATE payment_vouchers SET
                    status = 'posted',
                    posted_at = NOW(),
                    journal_entry_id = @journalEntryId
                WHERE id = @id;",
                new { id, journalEntryId }, tx);

            // 3) If linked to a specific invoice, apply the payment
            //    on the SAME transaction.
            if (payment.InvoiceId.HasValue)
            {
                await _invoices.ApplyPaymentInTxAsync(
                    conn2, tx,
                    payment.InvoiceId.Value,
                    payment.Amount,
                    payment.VoucherDate,
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
    /// Validates that the linked purchase invoice belongs to the same
    /// company and the same supplier as the payment. Symmetric to the
    /// receipt-side check in ReceiptService.ValidateInvoiceLinkAsync.
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

        var contact = await conn.QuerySingleOrDefaultAsync<(string? name, string? type)>(@"
            SELECT name, type FROM contacts WHERE id = @id AND company_id = @companyId;",
            new { id = contactId, companyId });
        if (contact.name is null)
            throw new InvalidOperationException("المورّد غير موجود");
        if (!string.Equals(contact.name, row.party_name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "الفاتورة لا تخص هذا المورّد. " +
                $"الفاتورة باسم '{row.party_name}'، السند للمورّد '{contact.name}'.");
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
        r.invoice_id, r.invoice_number,
        r.created_at, r.created_by, r.created_by_name);

    private record PaymentRow(
        Guid id, Guid company_id, string voucher_number, DateTime voucher_date, Guid contact_id,
        string? contact_name, string? contact_code,
        decimal amount, string payment_method, Guid? bank_account_id,
        string? check_number, DateTime? check_date, string? reference, string? narration,
        string status, DateTime? posted_at, Guid? journal_entry_id,
        Guid? invoice_id, string? invoice_number,
        DateTime created_at, Guid? created_by, string? created_by_name);
}
