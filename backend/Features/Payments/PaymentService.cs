using Dapper;
using ErpV2.Common;
using ErpV2.Features.Accounts;
using ErpV2.Features.Journal;

namespace ErpV2.Features.Payments;

public class PaymentService
{
    private readonly IDbConnectionFactory _db;
    private readonly AccountService _accounts;
    private readonly JournalService _journal;

    public PaymentService(IDbConnectionFactory db, AccountService accounts, JournalService journal)
    {
        _db = db;
        _accounts = accounts;
        _journal = journal;
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
                   p.created_at, p.created_by,
                   u.full_name_ar AS created_by_name
            FROM payment_vouchers p
            JOIN contacts c ON c.id = p.contact_id
            LEFT JOIN users u ON u.id = p.created_by
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
                   p.created_at, p.created_by,
                   u.full_name_ar AS created_by_name
            FROM payment_vouchers p
            JOIN contacts c ON c.id = p.contact_id
            LEFT JOIN users u ON u.id = p.created_by
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
            "SELECT status FROM payment_vouchers WHERE id = @id;", new { id });
        if (status != "draft") throw new InvalidOperationException("لا يمكن حذف سند مرحّل");
        await conn.ExecuteAsync("DELETE FROM payment_vouchers WHERE id = @id;", new { id });
        return true;
    }

    /// <summary>
    /// Post the payment: DR AP sub-ledger for supplier,
    /// CR Cash/Bank. Symmetric to receipt posting.
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

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                UPDATE payment_vouchers SET
                    status = 'posted',
                    posted_at = NOW(),
                    journal_entry_id = @journalEntryId
                WHERE id = @id;",
                new { id, journalEntryId = journalEntry.Id });
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
        r.created_at, r.created_by, r.created_by_name);

    private record PaymentRow(
        Guid id, Guid company_id, string voucher_number, DateTime voucher_date, Guid contact_id,
        string? contact_name, string? contact_code,
        decimal amount, string payment_method, Guid? bank_account_id,
        string? check_number, DateTime? check_date, string? reference, string? narration,
        string status, DateTime? posted_at, Guid? journal_entry_id,
        DateTime created_at, Guid? created_by, string? created_by_name);
}
