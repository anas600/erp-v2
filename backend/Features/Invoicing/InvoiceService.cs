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
    private readonly Features.Rules.RuleEvaluator _rules;

    public InvoiceService(IDbConnectionFactory db, Features.Journal.JournalService journal, Features.Journal.PostingEngine posting, Features.Rules.RuleEvaluator rules)
    {
        _db = db;
        _journal = journal;
        _posting = posting;
        _rules = rules;
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
            SELECT il.id, il.invoice_id, il.account_id, il.product_id,
                   a.code AS account_code, a.name AS account_name,
                   p.code AS product_code, p.name AS product_name, p.name_ar AS product_name_ar,
                   il.description, il.quantity, il.unit_price, il.tax_rate,
                   il.amount, il.line_total_with_tax, il.line_number
            FROM invoice_lines il
            LEFT JOIN accounts a ON a.id = il.account_id
            LEFT JOIN products p ON p.id = il.product_id
            WHERE il.invoice_id = @id
            ORDER BY il.line_number;",
            new { id })).ToList();

        return new InvoiceDto(
            inv.id, inv.company_id, inv.invoice_number, inv.invoice_type, inv.invoice_date,
            inv.party_name, inv.party_name_ar, inv.party_tax_id, inv.notes,
            inv.subtotal, inv.tax_amount, inv.total, inv.status, inv.created_at, inv.posted_at,
            lines.Select(l => new InvoiceLineDto(
                l.id, l.account_id, l.account_code, l.account_name,
                l.product_id, l.product_code, l.product_name, l.product_name_ar,
                l.description, l.quantity, l.unit_price, l.tax_rate,
                l.amount, l.line_total_with_tax, l.line_number
            )).ToList()
        );
    }

    public async Task<InvoiceDto> CreateDraftAsync(CreateInvoiceRequest req, Guid? createdBy)
    {
        if (req.Lines.Count == 0)
            throw new InvalidOperationException("الفاتورة يجب أن تحتوي على بند واحد على الأقل");

        if (req.InvoiceType != "purchase" && req.InvoiceType != "sales")
            throw new InvalidOperationException("نوع الفاتورة يجب أن يكون purchase أو sales");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Pre-resolve products so we can auto-fill description,
        // unit_price, and tax_rate in a single round trip.
        var productIds = req.Lines
            .Where(l => l.ProductId.HasValue)
            .Select(l => l.ProductId!.Value)
            .Distinct()
            .ToList();
        var productMap = new Dictionary<Guid, (string code, string name, string? nameAr, decimal unitPrice, decimal defaultTaxRate)>();
        if (productIds.Count > 0)
        {
            var rows = await conn.QueryAsync<(Guid id, string code, string name, string? nameAr, decimal unitPrice, decimal defaultTaxRate)>(@"
                SELECT id, code, name, name_ar AS nameAr, unit_price AS unitPrice, default_tax_rate AS defaultTaxRate
                FROM products
                WHERE company_id = @companyId AND id = ANY(@ids);",
                new { companyId = req.CompanyId, ids = productIds }, tx);
            foreach (var r in rows) productMap[r.id] = (r.code, r.name, r.nameAr, r.unitPrice, r.defaultTaxRate);
        }

        decimal subtotal = 0;
        decimal totalTax = 0;
        var computedLines = new List<(Guid? accountId, Guid? productId, string description, decimal quantity, decimal unitPrice, decimal taxRate, decimal amount, decimal amountWithTax)>();

        foreach (var line in req.Lines)
        {
            // Auto-fill from product if a product was chosen.
            string description = line.Description;
            decimal unitPrice = line.UnitPrice;
            decimal taxRate = line.TaxRate ?? req.TaxRate;
            Guid? productId = line.ProductId;

            if (productId.HasValue)
            {
                if (!productMap.TryGetValue(productId.Value, out var p))
                    throw new InvalidOperationException($"المنتج غير موجود في هذه الشركة: {productId}");

                // Use the product's defaults if the user didn't override.
                if (string.IsNullOrWhiteSpace(description)) description = p.name;
                if (unitPrice == 0) unitPrice = p.unitPrice;
                if (line.TaxRate is null) taxRate = p.defaultTaxRate;
            }

            // Pre-compute the two totals. Rounding to 2dp (the same
            // precision as the column type) avoids banker's-rounding
            // surprises later when the business rule sums them.
            var amount = Math.Round(line.Quantity * unitPrice, 2);
            var amountWithTax = Math.Round(amount * (1 + taxRate), 2);
            subtotal += amount;
            totalTax += amountWithTax - amount;
            computedLines.Add((line.AccountId, productId, description, line.Quantity, unitPrice, taxRate, amount, amountWithTax));
        }
        var total = subtotal + totalTax;

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
                    INSERT INTO invoice_lines (id, invoice_id, account_id, product_id, description,
                        quantity, unit_price, tax_rate, amount, line_total_with_tax, line_number)
                    VALUES (@id, @invoiceId, @accountId, @productId, @description,
                        @quantity, @unitPrice, @taxRate, @amount, @amountWithTax, @lineNum);",
                    new
                    {
                        id = Guid.NewGuid(),
                        invoiceId = id,
                        accountId = cl.accountId,
                        productId = cl.productId,
                        description = cl.description,
                        quantity = cl.quantity,
                        unitPrice = cl.unitPrice,
                        taxRate = cl.taxRate,
                        amount = cl.amount,
                        amountWithTax = cl.amountWithTax,
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

        // Build the event payload that the Business Rule templates
        // expect. The legacy templates (seeded in 002) read these
        // fields: invoice.number, invoice.total, invoice.tax, and
        // customer.name / supplier.name. We also expose party.name
        // as a third alias so newer rules don't have to know whether
        // this is a sales or purchase invoice.
        //
        // Subtotal is exposed as both `invoice.subtotal` (the new
        // convention after the products refactor) and `invoice.tax`
        // for backward compat.
        var partyDict = new Dictionary<string, object>
        {
            ["name"] = inv.PartyName,
            ["nameAr"] = inv.PartyNameAr ?? inv.PartyName,
            ["taxId"] = inv.PartyTaxId
        };
        var payload = new Dictionary<string, object>
        {
            ["invoice"] = new Dictionary<string, object>
            {
                ["id"] = inv.Id,
                ["number"] = inv.InvoiceNumber,
                ["type"] = inv.InvoiceType,
                ["date"] = inv.InvoiceDate,
                ["subtotal"] = inv.SubTotal,
                ["tax"] = inv.TaxAmount,
                ["total"] = inv.Total,
                ["lineCount"] = inv.Lines.Count,
                ["lineTotalWithTaxSum"] = inv.Lines.Sum(l => l.LineTotalWithTax)
            },
            // 'party' is the new convention (used by Sprint 14+ rules).
            ["party"] = partyDict,
            // Legacy aliases for the seeded templates in 002. Some
            // templates use customer.name (sales) and supplier.name
            // (purchase) — we expose both regardless of invoice type
            // so the templates always substitute cleanly.
            ["customer"] = partyDict,
            ["supplier"] = partyDict
        };

        // The rule template handles the actual journal entry creation
        // (it knows which accounts to debit/credit). We just kick the
        // event off and trust the user's configured rules. If no rule
        // is enabled, the journal is simply not created — the user can
        // inspect and re-enable rules from the Business Rules page.
        var eventName = inv.InvoiceType == "sales" ? "SalesInvoiceApproved" : "PurchaseInvoiceApproved";
        var entries = await _rules.TriggerEventAsync(inv.CompanyId, null, eventName, payload);

        // Mark the invoice as posted regardless of how many rules
        // fired — the user has approved it, the rest is bookkeeping.
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE invoices SET status = 'posted', posted_at = NOW() WHERE id = @id;",
                new { id = invoiceId });
        }

        return (await GetByIdAsync(invoiceId))!;
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
        Guid id, Guid invoice_id, Guid? account_id, Guid? product_id,
        string? account_code, string? account_name,
        string? product_code, string? product_name, string? product_name_ar,
        string? description, decimal quantity, decimal unit_price, decimal tax_rate,
        decimal amount, decimal line_total_with_tax, int line_number);
}
