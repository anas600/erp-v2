using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Receipts;

public class ReceiptService
{
    private readonly IDbConnectionFactory _db;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;

    public ReceiptService(IDbConnectionFactory db, AccountService accounts, JournalService journal)
    {
        _db = db;
        _accounts = accounts;
        _journal = journal;
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
                   r.created_at, r.created_by,
                   u.full_name_ar AS created_by_name
            FROM receipt_vouchers r
            JOIN contacts c ON c.id = r.contact_id
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
                   r.created_at, r.created_by,
                   u.full_name_ar AS created_by_name
            FROM receipt_vouchers r
            JOIN contacts c ON c.id = r.contact_id
            LEFT JOIN users u ON u.id = r.created_by
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
                reference, narration, status, created_by
            )
            VALUES (
                @id, @companyId, @voucherNumber, @voucherDate, @contactId,
                @amount, @paymentMethod, @bankAccountId, @checkNumber, @checkDate,
                @reference, @narration, 'draft', @createdBy
            );",
            new
            {
                id, companyId = req.CompanyId, voucherNumber, voucherDate = req.VoucherDate,
                contactId = req.ContactId, amount = req.Amount, paymentMethod = req.PaymentMethod,
                bankAccountId = req.BankAccountId, checkNumber = req.CheckNumber,
                checkDate = req.CheckDate, reference = req.Reference, narration = req.Narration,
                createdBy
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
                check_date = @checkDate, reference = @reference, narration = @narration
            WHERE id = @id;",
            new
            {
                id, voucherDate = req.VoucherDate, contactId = req.ContactId,
                amount = req.Amount, paymentMethod = req.PaymentMethod,
                bankAccountId = req.BankAccountId, checkNumber = req.CheckNumber,
                checkDate = req.CheckDate, reference = req.Reference, narration = req.Narration
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
    /// regular Posting Engine. The AR sub-ledger is found via
    /// account_contact_links; if no sub-ledger exists yet, the
    /// receipt fails with a clear Arabic error.
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
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                UPDATE receipt_vouchers SET
                    status = 'posted',
                    posted_at = NOW(),
                    journal_entry_id = @journalEntryId
                WHERE id = @id;",
                new { id, journalEntryId = journalEntry.Id });
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
        r.created_at, r.created_by, r.created_by_name);

    private record ReceiptRow(
        Guid id, Guid company_id, string voucher_number, DateTime voucher_date, Guid contact_id,
        string? contact_name, string? contact_code,
        decimal amount, string payment_method, Guid? bank_account_id,
        string? check_number, DateTime? check_date, string? reference, string? narration,
        string status, DateTime? posted_at, Guid? journal_entry_id,
        DateTime created_at, Guid? created_by, string? created_by_name);
}
