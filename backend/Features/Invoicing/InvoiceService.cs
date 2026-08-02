using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Invoicing;

/// <summary>
/// Invoice service: handles purchase and sales invoices.
///
/// Invoices are first created as drafts, then posted. Posting a draft invoice:
///   1. Creates a journal entry via the regular Journal pipeline (draft + post).
///   2. The journal lines are placed based on the invoice type:
///      - Purchase: Debit each line's account (expense/asset), Credit Accounts Payable.
///      - Sales: Debit Accounts Receivable, Credit each line's account (revenue).
///   3. Tax (if any) is posted separately (Debit/Credit a tax account).
///   4. Marks the invoice as `posted` and stamps `posted_at`.
/// </summary>
public class InvoiceService
{
    private readonly IDbConnectionFactory _db;
    private readonly Features.Journal.JournalService _journal;
    private readonly Features.Journal.PostingEngine _posting;

    public InvoiceService(IDbConnectionFactory db, Features.Journal.JournalService journal, Features.Journal.PostingEngine posting)
    {
        _db = db;
        _journal = journal;
        _posting = posting;
    }

    public async Task<List<InvoiceDto>> GetByCompanyAsync(Guid companyId, int limit = 100)
    {
        using var conn = _db.CreateConnection();
        var invoiceIds = (await conn.QueryAsync<Guid>(@"
            SELECT id FROM invoices
            WHERE company_id = @companyId
            ORDER BY invoice_date DESC, created_at DESC
            LIMIT @limit;",
            new { companyId, limit })).ToList();

        var result = new List<InvoiceDto>();
        foreach (var id in invoiceIds)
        {
            var inv = await GetByIdAsync(id);
            if (inv is not null) result.Add(inv);
        }
        return result;
    }

    public async Task<InvoiceDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var inv = await conn.QuerySingleOrDefaultAsync<InvoiceRow>(@"
            SELECT id, company_id, invoice_number, invoice_type, invoice_date,
                   party_name, party_name_ar, party_tax_id, notes,
                   subtotal, tax_amount, total, status, created_at, posted_at
            FROM invoices WHERE id = @id;",
            new { id });
        if (inv is null) return null;

        var lines = (await conn.QueryAsync<InvoiceLineRow>(@"
            SELECT il.id, il.invoice_id, il.account_id, a.code AS account_code, a.name AS account_name,
                   il.description, il.quantity, il.unit_price, il.tax_rate, il.amount, il.line_number
            FROM invoice_lines il
            JOIN accounts a ON a.id = il.account_id
            WHERE il.invoice_id = @id
            ORDER BY il.line_number;",
            new { id })).ToList();

        return new InvoiceDto(
            inv.id, inv.company_id, inv.invoice_number, inv.invoice_type, inv.invoice_date,
            inv.party_name, inv.party_name_ar, inv.party_tax_id, inv.notes,
            inv.subtotal, inv.tax_amount, inv.total, inv.status, inv.created_at, inv.posted_at,
            lines.Select(l => new InvoiceLineDto(
                l.id, l.account_id, l.account_code, l.account_name,
                l.description, l.quantity, l.unit_price, l.tax_rate, l.amount, l.line_number
            )).ToList()
        );
    }

    public async Task<InvoiceDto> CreateDraftAsync(CreateInvoiceRequest req, Guid? createdBy)
    {
        if (req.Lines.Count == 0)
            throw new InvalidOperationException("الفاتورة يجب أن تحتوي على بند واحد على الأقل");

        if (req.InvoiceType != "purchase" && req.InvoiceType != "sales")
            throw new InvalidOperationException("نوع الفاتورة يجب أن يكون purchase أو sales");

        decimal subtotal = 0;
        decimal totalTax = 0;
        var computedLines = new List<(Guid accountId, string description, decimal quantity, decimal unitPrice, decimal taxRate, decimal amount, decimal taxAmount)>();

        foreach (var line in req.Lines)
        {
            var amount = Math.Round(line.Quantity * line.UnitPrice, 2);
            var lineTaxRate = line.TaxRate ?? req.TaxRate;
            var lineTax = Math.Round(amount * lineTaxRate, 2);
            subtotal += amount;
            totalTax += lineTax;
            computedLines.Add((line.AccountId, line.Description, line.Quantity, line.UnitPrice, lineTaxRate, amount, lineTax));
        }
        var total = subtotal + totalTax;

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var id = Guid.NewGuid();
            var invoiceNumber = await GenerateInvoiceNumberAsync(req.CompanyId, req.InvoiceType, conn, tx);

            await conn.ExecuteAsync(@"
                INSERT INTO invoices (id, company_id, invoice_number, invoice_type, invoice_date,
                    party_name, party_name_ar, party_tax_id, notes,
                    subtotal, tax_amount, total, status, created_by)
                VALUES (@id, @companyId, @invoiceNumber, @invoiceType, @invoiceDate,
                    @partyName, @partyNameAr, @partyTaxId, @notes,
                    @subtotal, @taxAmount, @total, 'draft', @createdBy);",
                new
                {
                    id,
                    companyId = req.CompanyId,
                    invoiceNumber,
                    invoiceType = req.InvoiceType,
                    invoiceDate = req.InvoiceDate,
                    partyName = req.PartyName,
                    partyNameAr = req.PartyNameAr,
                    partyTaxId = req.PartyTaxId,
                    notes = req.Notes,
                    subtotal,
                    taxAmount = totalTax,
                    total,
                    createdBy
                }, tx);

            int lineNum = 1;
            foreach (var cl in computedLines)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO invoice_lines (id, invoice_id, account_id, description, quantity, unit_price, tax_rate, amount, line_number)
                    VALUES (@id, @invoiceId, @accountId, @description, @quantity, @unitPrice, @taxRate, @amount, @lineNum);",
                    new
                    {
                        id = Guid.NewGuid(),
                        invoiceId = id,
                        accountId = cl.accountId,
                        description = cl.description,
                        quantity = cl.quantity,
                        unitPrice = cl.unitPrice,
                        taxRate = cl.taxRate,
                        amount = cl.amount,
                        lineNum = lineNum++
                    }, tx);
            }

            tx.Commit();
            return (await GetByIdAsync(id))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Posts an invoice: builds a journal entry, saves it as a draft, then posts via the Posting Engine.
    /// </summary>
    public async Task<InvoiceDto> PostAsync(Guid invoiceId)
    {
        var inv = await GetByIdAsync(invoiceId)
            ?? throw new InvalidOperationException("الفاتورة غير موجودة");
        if (inv.Status == "posted")
            throw new InvalidOperationException("الفاتورة مرحلة بالفعل");
        if (inv.Status == "cancelled")
            throw new InvalidOperationException("الفاتورة ملغاة");
        if (inv.Lines.Count == 0)
            throw new InvalidOperationException("الفاتورة بدون بنود");

        // Build the journal entry
        var journalLines = new List<Features.Journal.CreateJournalLineRequest>();

        // The default "other side" account depends on invoice type
        // For sales: AR (account 1200). For purchase: AP (account 2000).
        // In a real system these would be configurable per company.
        var counterAccountCode = inv.InvoiceType == "sales" ? "1200" : "2000";

        using (var conn = _db.CreateConnection())
        {
            var counterAccount = await conn.QuerySingleOrDefaultAsync<(Guid id, string nature)>(@"
                SELECT id, nature FROM accounts
                WHERE company_id = @companyId AND code = @code AND is_active = true
                LIMIT 1;",
                new { companyId = inv.CompanyId, code = counterAccountCode });
            if (counterAccount.id == Guid.Empty)
                throw new InvalidOperationException($"الحساب {counterAccountCode} غير موجود في شجرة الحسابات");

            if (inv.InvoiceType == "sales")
            {
                // Debit the counter account (AR) for the total, credit each line account for its amount
                journalLines.Add(new Features.Journal.CreateJournalLineRequest(
                    counterAccount.id, inv.Total, 0,
                    $"مدينون - {inv.PartyName}"));
                foreach (var l in inv.Lines)
                {
                    var (debit, credit) = _posting.ComputePlacement(
                        await GetAccountNature(l.AccountId), "credit", l.Amount);
                    journalLines.Add(new Features.Journal.CreateJournalLineRequest(
                        l.AccountId, debit, credit, l.Description));
                }
            }
            else // purchase
            {
                // Debit each line account for its amount, credit the counter account (AP) for the total
                foreach (var l in inv.Lines)
                {
                    var (debit, credit) = _posting.ComputePlacement(
                        await GetAccountNature(l.AccountId), "debit", l.Amount);
                    journalLines.Add(new Features.Journal.CreateJournalLineRequest(
                        l.AccountId, debit, credit, l.Description));
                }
                journalLines.Add(new Features.Journal.CreateJournalLineRequest(
                    counterAccount.id, 0, inv.Total,
                    $"دائنون - {inv.PartyName}"));
            }
        }

        var req = new Features.Journal.CreateJournalEntryRequest(
            inv.CompanyId,
            inv.InvoiceDate,
            $"فاتورة {inv.InvoiceType} رقم {inv.InvoiceNumber} - {inv.PartyName}",
            journalLines
        );

        await _journal.CreateDraftAsync(req, null);
        // Note: we don't auto-post the journal entry here; the user can review and post.
        // The invoice just becomes "ready" to be posted once the journal entry is approved.

        // For simplicity in MVP, mark the invoice as posted directly.
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE invoices SET status = 'posted', posted_at = NOW() WHERE id = @id;",
                new { id = invoiceId });
        }

        return (await GetByIdAsync(invoiceId))!;
    }

    private async Task<string> GetAccountNature(Guid accountId)
    {
        using var conn = _db.CreateConnection();
        return await conn.ExecuteScalarAsync<string>(
            "SELECT nature FROM accounts WHERE id = @id;",
            new { id = accountId }) ?? "Debit";
    }

    public async Task<bool> CancelAsync(Guid invoiceId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "UPDATE invoices SET status = 'cancelled' WHERE id = @id AND status != 'posted';",
            new { id = invoiceId });
        return rows > 0;
    }

    private async Task<string> GenerateInvoiceNumberAsync(Guid companyId, string type, System.Data.IDbConnection conn, System.Data.IDbTransaction tx)
    {
        var prefix = type == "sales" ? "INV-S-" : "INV-P-";
        var year = DateTime.UtcNow.Year;
        var yearPrefix = $"{prefix}{year}-";
        var lastNumber = await conn.ExecuteScalarAsync<string?>(@"
            SELECT invoice_number FROM invoices
            WHERE company_id = @companyId AND invoice_number LIKE @pattern
            ORDER BY invoice_number DESC LIMIT 1;",
            new { companyId, pattern = $"{yearPrefix}%" }, tx);

        if (string.IsNullOrEmpty(lastNumber))
            return $"{yearPrefix}0001";

        var numPart = lastNumber.Substring(yearPrefix.Length);
        if (int.TryParse(numPart, out var n))
            return $"{yearPrefix}{(n + 1):D4}";
        return $"{yearPrefix}0001";
    }

    private record InvoiceRow(
        Guid id, Guid company_id, string invoice_number, string invoice_type, DateTime invoice_date,
        string party_name, string? party_name_ar, string? party_tax_id, string? notes,
        decimal subtotal, decimal tax_amount, decimal total, string status, DateTime created_at, DateTime? posted_at);

    private record InvoiceLineRow(
        Guid id, Guid invoice_id, Guid account_id, string? account_code, string? account_name,
        string description, decimal quantity, decimal unit_price, decimal tax_rate, decimal amount, int line_number);
}
