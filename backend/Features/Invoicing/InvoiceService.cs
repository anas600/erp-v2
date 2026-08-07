using Dapper;
using ErpV2.Common;
using ErpV2.Features.Journal;

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
///
/// Sprint 24 — Intercompany (الشركات الشقيقة) extension:
///   When an invoice is created with `intercompany_company_id` set,
///   `PostAsync` also creates a mirror invoice in the sister company
///   (opposite type, same amounts, same date), triggers the same
///   business-rule event in the sister company, links both invoices
///   in `intercompany_pairs`, and stamps the pair id on both journal
///   entries so the consolidation report can pull them in one query.
/// </summary>
public class InvoiceService
{
    private readonly IDbConnectionFactory _db;
    private readonly Features.Journal.JournalService _journal;
    private readonly Features.Journal.PostingEngine _posting;
    private readonly Features.Rules.RuleEvaluator _rules;
    private readonly ILogger<InvoiceService> _log;

    public InvoiceService(IDbConnectionFactory db, Features.Journal.JournalService journal, Features.Journal.PostingEngine posting, Features.Rules.RuleEvaluator rules, ILogger<InvoiceService> log)
    {
        _db = db;
        _journal = journal;
        _posting = posting;
        _rules = rules;
        _log = log;
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
                   subtotal, tax_amount, total, status, created_at, posted_at,
                   intercompany_company_id, amount_paid, fully_paid_at
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
            inv.intercompany_company_id,
            inv.amount_paid, inv.fully_paid_at,
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

        // Intercompany self-company guard: a sister company cannot
        // be the company you're posting into. Catch this BEFORE
        // hitting the DB so the user gets a clear Arabic error.
        if (req.IntercompanyCompanyId.HasValue &&
            req.IntercompanyCompanyId.Value == req.CompanyId)
        {
            throw new InvalidOperationException(
                "لا يمكن أن تكون الشركة الشقيقة نفس الشركة الحالية");
        }

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
                    subtotal, tax_amount, total, status, created_by, intercompany_company_id)
                VALUES (@id, @companyId, @invoiceNumber, @invoiceType, @invoiceDate,
                    @partyName, @partyNameAr, @partyTaxId, @notes,
                    @subtotal, @taxAmount, @total, 'draft', @createdBy, @intercompanyCompanyId);",
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
                    createdBy,
                    intercompanyCompanyId = req.IntercompanyCompanyId
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
    /// Sprint 29 — Updates a DRAFT invoice in place. Replaces header
    /// fields and rewrites the lines (delete + re-insert). Throws if
    /// the invoice is not in 'draft' status (avoids touching posted
    /// invoices where the journal entry would be left dangling).
    /// </summary>
    public async Task<InvoiceDto> UpdateDraftAsync(Guid invoiceId, CreateInvoiceRequest req)
    {
        if (req.Lines.Count == 0)
            throw new InvalidOperationException("الفاتورة يجب أن تحتوي على بند واحد على الأقل");

        if (req.InvoiceType != "purchase" && req.InvoiceType != "sales")
            throw new InvalidOperationException("نوع الفاتورة يجب أن يكون purchase أو sales");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Load + lock the existing invoice. Refuse if not draft.
        var existing = await conn.QuerySingleOrDefaultAsync<(Guid id, string status, string invoice_type, Guid company_id)>(@"
            SELECT id, status, invoice_type, company_id
            FROM invoices WHERE id = @id FOR UPDATE;",
            new { id = invoiceId }, tx);
        if (existing.id == Guid.Empty)
            throw new InvalidOperationException("الفاتورة غير موجودة");
        if (existing.status != "draft")
            throw new InvalidOperationException("لا يمكن تعديل فاتورة مرحلة — اعكسها أولاً");

        // Intercompany self-company guard
        if (req.IntercompanyCompanyId.HasValue &&
            req.IntercompanyCompanyId.Value == req.CompanyId)
        {
            throw new InvalidOperationException(
                "لا يمكن أن تكون الشركة الشقيقة نفس الشركة الحالية");
        }

        // Pre-resolve products in the invoice's company (NOT req.CompanyId —
        // we trust the original company binding, which the type system
        // already enforces via the request validator).
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
                new { companyId = existing.company_id, ids = productIds }, tx);
            foreach (var r in rows) productMap[r.id] = (r.code, r.name, r.nameAr, r.unitPrice, r.defaultTaxRate);
        }

        decimal subtotal = 0;
        decimal totalTax = 0;
        var computedLines = new List<(Guid? accountId, Guid? productId, string description, decimal quantity, decimal unitPrice, decimal taxRate, decimal amount, decimal amountWithTax)>();

        foreach (var line in req.Lines)
        {
            string description = line.Description;
            decimal unitPrice = line.UnitPrice;
            decimal taxRate = line.TaxRate ?? req.TaxRate;
            Guid? productId = line.ProductId;

            if (productId.HasValue)
            {
                if (!productMap.TryGetValue(productId.Value, out var p))
                    throw new InvalidOperationException($"المنتج غير موجود في هذه الشركة: {productId}");

                if (string.IsNullOrWhiteSpace(description)) description = p.name;
                if (unitPrice == 0) unitPrice = p.unitPrice;
                if (line.TaxRate is null) taxRate = p.defaultTaxRate;
            }

            var amount = Math.Round(line.Quantity * unitPrice, 2);
            var amountWithTax = Math.Round(amount * (1 + taxRate), 2);
            subtotal += amount;
            totalTax += amountWithTax - amount;
            computedLines.Add((line.AccountId, productId, description, line.Quantity, unitPrice, taxRate, amount, amountWithTax));
        }
        var total = subtotal + totalTax;

        try
        {
            // Update header
            await conn.ExecuteAsync(@"
                UPDATE invoices SET
                    invoice_date = @invoiceDate,
                    party_name = @partyName,
                    party_name_ar = @partyNameAr,
                    party_tax_id = @partyTaxId,
                    notes = @notes,
                    subtotal = @subtotal,
                    tax_amount = @taxAmount,
                    total = @total,
                    intercompany_company_id = @intercompanyCompanyId
                WHERE id = @id;",
                new
                {
                    id = invoiceId,
                    invoiceDate = req.InvoiceDate,
                    partyName = req.PartyName,
                    partyNameAr = req.PartyNameAr,
                    partyTaxId = req.PartyTaxId,
                    notes = req.Notes,
                    subtotal,
                    taxAmount = totalTax,
                    total,
                    intercompanyCompanyId = req.IntercompanyCompanyId
                }, tx);

            // Delete old lines
            await conn.ExecuteAsync(@"DELETE FROM invoice_lines WHERE invoice_id = @id;",
                new { id = invoiceId }, tx);

            // Insert new lines
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
                        invoiceId = invoiceId,
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
            return (await GetByIdAsync(invoiceId))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }


    /// <summary>
    /// Posts an invoice: builds a journal entry, saves it as a draft, then posts via the Posting Engine.
    ///
    /// Sprint 24 — Intercompany side-effect: when the invoice has
    /// `intercompany_company_id` set, posting also creates a mirror
    /// invoice in the sister company (opposite type, same lines,
    /// same amounts, same date) and links both sides via an
    /// `intercompany_pairs` row. Both journal entries are stamped
    /// with the pair id so the elimination report can pull them
    /// in one query.
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

        // Self-company guard. Defensive — the same check runs in
        // CreateDraftAsync, but a malicious caller could PATCH the
        // column directly in the DB and bypass it. Better to fail
        // loudly than to post an invoice to itself.
        if (inv.IntercompanyCompanyId.HasValue &&
            inv.IntercompanyCompanyId.Value == inv.CompanyId)
        {
            throw new InvalidOperationException(
                "لا يمكن أن تكون الشركة الشقيقة نفس الشركة الحالية");
        }

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
        _log.LogInformation(
            "PostAsync: invoice {InvNum} type={Type} company={CoId} — triggering {Event}",
            inv.InvoiceNumber, inv.InvoiceType, inv.CompanyId, eventName);
        List<JournalEntryDto> entries;
        try
        {
            entries = await _rules.TriggerEventAsync(inv.CompanyId, null, eventName, payload);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PostAsync: rule trigger failed for invoice {InvId}: {Msg}", invoiceId, ex.Message);
            // Re-throw so the user sees the error (and the invoice
            // is NOT marked as posted — atomicity over silence).
            throw new InvalidOperationException(
                $"فشل توليد القيد المحاسبي: {ex.Message}", ex);
        }
        _log.LogInformation(
            "PostAsync: invoice {InvNum} — {Count} journal entries created",
            inv.InvoiceNumber, entries.Count);

        // Mark the invoice as posted only if a journal entry was
        // created. If no rules fired (entries.Count == 0), the
        // invoice stays in 'draft' so the user can investigate
        // (or manually create a journal entry).
        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                "لا توجد قواعد محاسبية مفعّلة لهذا النوع من الفواتير. " +
                "الرجاء تفعيل قاعدة 'ترحيل فاتورة مبيعات' أو 'ترحيل فاتورة مشتريات' " +
                "من صفحة 'قواعد العمل' قبل ترحيل الفاتورة.");
        }

        // ============================================================
        // Sprint 24 — Intercompany side-effect.
        // ============================================================
        // When this invoice has a sister company, we:
        //   1. Create a mirror invoice (opposite type) in the sister
        //      company with the same lines/amounts/date. The mirror's
        //      party is the primary company (HOLD records CO-A as
        //      customer; CO-A records HOLD as supplier).
        //   2. Post the mirror invoice so it generates its own journal
        //      entry via the same rule pipeline.
        //   3. Create an `intercompany_pairs` row linking both.
        //   4. Stamp `intercompany_pair_id` on both journal entries
        //      (the primary's and the mirror's) so the elimination
        //      report can pull both halves in a single query.
        //
        // Failure mode: if any step after the primary's journal entry
        // is created fails, we surface the error AND leave the primary
        // invoice in its current state (we have NOT yet updated
        // invoices.status to 'posted' — that happens after this
        // block). The user can retry; the rule is idempotent.
        IntercompanyPairDto? intercompanyPair = null;
        if (inv.IntercompanyCompanyId.HasValue)
        {
            try
            {
                intercompanyPair = await CreateIntercompanyMirrorAsync(inv, entries, payload);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "PostAsync: intercompany mirror creation FAILED for invoice {InvId} (sister={SisterId}): {Msg}",
                    invoiceId, inv.IntercompanyCompanyId, ex.Message);
                // Bubble up with a clear Arabic message. We DO NOT mark
                // the primary invoice as posted — atomicity is more
                // important than partial progress. The user fixes the
                // underlying issue (missing sister account, no rule
                // enabled, etc.) and retries the post.
                throw new InvalidOperationException(
                    $"فشل إنشاء فاتورة الشركة الشقيقة: {ex.Message}", ex);
            }
        }

        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE invoices SET status = 'posted', posted_at = NOW() WHERE id = @id;",
                new { id = invoiceId });
        }

        var result = (await GetByIdAsync(invoiceId))!;

        // If we created a pair, also flip its status from 'pending' to
        // 'posted' now that both sides are confirmed posted. The
        // 'pending' default is set at pair creation time so a partial
        // failure (e.g. mirror posted but pair row never updated) is
        // visible to the report.
        if (intercompanyPair is not null)
        {
            using var conn = _db.CreateConnection();
            await conn.ExecuteAsync(@"
                UPDATE intercompany_pairs SET status = 'posted' WHERE id = @id;",
                new { id = intercompanyPair.Id });
        }

        return result;
    }

    public async Task<bool> CancelAsync(Guid invoiceId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "UPDATE invoices SET status = 'cancelled' WHERE id = @id AND status != 'posted';",
            new { id = invoiceId });
        return rows > 0;
    }

    /// <summary>
    /// Sprint 25 — Apply a payment to an invoice. Increments
    /// <c>invoices.amount_paid</c> by <paramref name="amount"/> and recomputes
    /// the status:
    ///   - amount_paid == 0  → status = 'posted'
    ///   - 0 < amount_paid < total → status = 'partiallypaid'
    ///   - amount_paid == total  → status = 'paid' (and stamp fully_paid_at)
    ///
    /// The method is **atomic**: it opens its own connection + transaction,
    /// re-reads the invoice under a row lock via <c>SELECT ... FOR UPDATE</c>,
    /// and updates in a single statement so concurrent receipts against the
    /// same invoice can't double-spend the outstanding.
    ///
    /// Used by the public API (e.g. an admin tool) and by Receipt/Payment
    /// services. The two voucher services prefer the in-transaction
    /// overload <see cref="ApplyPaymentInTxAsync"/> so they can roll the
    /// invoice update back together with the journal entry creation.
    /// </summary>
    public async Task<InvoiceDto> ApplyPaymentAsync(
        Guid invoiceId, decimal amount, DateTime paymentDate, Guid voucherId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var updated = await ApplyPaymentInTxAsync(conn, tx, invoiceId, amount, paymentDate, voucherId);
            tx.Commit();
            return updated;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// In-transaction overload of <see cref="ApplyPaymentAsync"/>. The
    /// caller owns the connection and the transaction; the invoice update
    /// is rolled back if the caller rolls back.
    ///
    /// Locks the invoice row with <c>SELECT ... FOR UPDATE</c> so a
    /// concurrent receipt against the same invoice blocks until this
    /// transaction completes.
    ///
    /// <paramref name="voucherId"/> is currently unused but kept in the
    /// signature for future audit logging (e.g. <c>audit_logs</c> row
    /// "invoice {invoiceId} received {amount} via voucher {voucherId}").
    /// </summary>
    public async Task<InvoiceDto> ApplyPaymentInTxAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        Guid invoiceId, decimal amount, DateTime paymentDate, Guid voucherId)
    {
        if (amount <= 0)
            throw new InvalidOperationException("مبلغ التسديد يجب أن يكون أكبر من صفر");

        // Lock the invoice row for the duration of the transaction. This
        // prevents a second receipt from racing in and over-paying the
        // same invoice.
        var invoice = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id, decimal total, decimal amount_paid, string status)>(@"
            SELECT id, company_id, total, amount_paid, status
            FROM invoices WHERE id = @id FOR UPDATE;",
            new { id = invoiceId }, tx);
        if (invoice.id == Guid.Empty)
            throw new InvalidOperationException("الفاتورة غير موجودة");

        if (invoice.status != "posted" && invoice.status != "partiallypaid")
            throw new InvalidOperationException(
                $"لا يمكن تسديد فاتورة بحالة '{invoice.status}'. المتوقع: posted أو partiallypaid");

        var outstanding = invoice.total - invoice.amount_paid;
        if (amount > outstanding + 0.0001m)
            throw new InvalidOperationException(
                $"مبلغ التسديد ({amount:0.00}) يتجاوز المبلغ المستحق ({outstanding:0.00})");

        var newAmountPaid = invoice.amount_paid + amount;
        // Clamp to total to avoid tiny floating-point overshoots
        if (newAmountPaid > invoice.total) newAmountPaid = invoice.total;

        string newStatus;
        DateTime? newFullyPaidAt = null;
        if (newAmountPaid >= invoice.total - 0.0001m)
        {
            newStatus = "paid";
            newFullyPaidAt = paymentDate;
        }
        else if (newAmountPaid > 0)
        {
            newStatus = "partiallypaid";
        }
        else
        {
            newStatus = "posted";
        }

        // Use a single UPDATE so the status and amount_paid are consistent
        // (avoids the risk of two separate UPDATEs being interrupted
        // between them).
        await conn.ExecuteAsync(@"
            UPDATE invoices
            SET amount_paid = @amountPaid,
                status = @status,
                fully_paid_at = COALESCE(@fullyPaidAt, fully_paid_at)
            WHERE id = @id;",
            new
            {
                id = invoiceId,
                amountPaid = newAmountPaid,
                status = newStatus,
                fullyPaidAt = newFullyPaidAt
            }, tx);

        _log.LogInformation(
            "ApplyPayment: invoice {InvId} +{Amount} (voucher {VId}) → status={Status}, paid={Paid}/{Total}",
            invoiceId, amount, voucherId, newStatus, newAmountPaid, invoice.total);

        return (await GetByIdAsync(invoiceId))!;
    }

    /// <summary>
    /// Sprint 25 — List invoices for a single contact, filtered by status
    /// bucket. Used by ContactStatementEndpoints.GetInvoicesAsync.
    ///
    /// Matches on (company_id, name, type) — same as the aging reports —
    /// because <c>invoices</c> has no <c>contact_id</c> FK. Invoices with a
    /// manually-typed party name (no matching contact) are silently
    /// excluded; this is the same trade-off documented on the aging queries.
    /// </summary>
    public async Task<List<ContactInvoiceDto>> GetByContactAsync(
        Guid companyId, Guid contactId, string statusFilter, DateTime asOf)
    {
        using var conn = _db.CreateConnection();

        // Look up the contact (for its name + type). We do this in C#
        // because the JOIN logic in the WHERE clause is the same as the
        // aging queries; using C# here keeps the query plain and readable.
        var contact = await conn.QuerySingleOrDefaultAsync<(string name, string type)>(@"
            SELECT name, type FROM contacts WHERE id = @id AND company_id = @companyId;",
            new { id = contactId, companyId });
        if (contact.name is null) return new List<ContactInvoiceDto>();

        // statusFilter: 'outstanding' = posted + partiallypaid,
        //               'paid'       = paid,
        //               'all'        = posted + partiallypaid + paid.
        // We compute the bucket as a string list and use ANY(@statuses).
        string[] statuses = statusFilter?.ToLowerInvariant() switch
        {
            "outstanding" => new[] { "posted", "partiallypaid" },
            "paid"        => new[] { "paid" },
            _             => new[] { "posted", "partiallypaid", "paid" }
        };

        // FIX 2026-08-05: contacts.type is 'customer'/'supplier' but
        // invoices.invoice_type is 'sales'/'purchase'. They are NOT
        // the same enum — was comparing 'customer' = 'sales' which
        // never matched. Map correctly here.
        var invoiceType = contact.type == "customer" ? "sales" : "purchase";

        var rows = await conn.QueryAsync<ContactInvoiceRow>(@"
            SELECT
                id AS invoice_id,
                invoice_number AS number,
                invoice_date AS date,
                invoice_type AS type,
                total,
                amount_paid,
                (total - amount_paid) AS outstanding,
                status,
                (@asOf::date - invoice_date::date)::int AS age_days
            FROM invoices
            WHERE company_id = @companyId
              AND party_name = @partyName
              AND invoice_type = @invoiceType
              AND status = ANY(@statuses)
              AND status != 'cancelled'
            ORDER BY invoice_date DESC, created_at DESC;",
            new { companyId, partyName = contact.name, invoiceType, statuses, asOf });

        return rows.Select(r => new ContactInvoiceDto(
            r.invoice_id, r.number, r.date, r.type, r.total, r.amount_paid,
            r.outstanding, r.status, r.age_days)).ToList();
    }

    private record ContactInvoiceRow(
        Guid invoice_id, string number, DateTime date, string type,
        decimal total, decimal amount_paid, decimal outstanding,
        string status, int age_days);

    /// <summary>
    /// Sprint 24 — Creates the mirror invoice in the sister company,
    /// posts it, creates the intercompany_pairs link, and stamps
    /// `intercompany_pair_id` on both halves' journal entries.
    ///
    /// This is the workhorse of the intercompany flow. It is called
    /// only from `PostAsync` (after the primary's journal entry has
    /// been created) so failure here can be surfaced cleanly without
    /// leaving a half-posted primary invoice.
    /// </summary>
    private async Task<IntercompanyPairDto> CreateIntercompanyMirrorAsync(
        InvoiceDto primary, List<JournalEntryDto> primaryEntries, Dictionary<string, object> primaryPayload)
    {
        var sisterCompanyId = primary.IntercompanyCompanyId!.Value;

        // 1) Look up the primary company (we need its display name for
        //    the mirror's party_name — the sister's books record the
        //    primary as their supplier/customer).
        CompanyLite? primaryCompany;
        using (var conn = _db.CreateConnection())
        {
            primaryCompany = await conn.QuerySingleOrDefaultAsync<CompanyLite>(@"
                SELECT id, code, name, name_ar FROM companies WHERE id = @id;",
                new { id = primary.CompanyId });
        }
        if (primaryCompany is null)
            throw new InvalidOperationException("الشركة الأصلية غير موجودة");

        // 2) Map each primary line's account to the sister company's
        //    same-code account. If a code is missing in the sister
        //    company, we surface a clear Arabic error — the user must
        //    align the chart of accounts across sister companies.
        //    Same for products: we keep the product code in the
        //    description; if the sister has the same product code we
        //    use the sister product_id, otherwise we leave it null
        //    and let the description carry the human-readable name.
        using (var conn = _db.CreateConnection())
        {
            // First, validate that every primary line's account has a
            // counterpart in the sister. Throw early with a list of
            // missing codes so the user can fix the chart of accounts
            // in one go instead of one-error-at-a-time.
            var missingAccounts = new List<string>();
            foreach (var line in primary.Lines)
            {
                if (line.AccountCode is null) continue;
                var found = await conn.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM accounts
                    WHERE company_id = @companyId AND code = @code AND is_active = true;",
                    new { companyId = sisterCompanyId, code = line.AccountCode });
                if (found == 0) missingAccounts.Add(line.AccountCode);
            }
            if (missingAccounts.Count > 0)
            {
                throw new InvalidOperationException(
                    "الحسابات التالية غير موجودة في الشركة الشقيقة: " +
                    string.Join(", ", missingAccounts.Distinct()) +
                    ". الرجاء إعداد دليل الحسابات في الشركة الشقيقة بنفس الأكواد.");
            }
        }

        // 3) Build the mirror CreateInvoiceRequest. The mirror is the
        //    OPPOSITE invoice type (sales ↔ purchase) — that's the
        //    whole point of intercompany: one company records the
        //    customer side, the other records the supplier side of
        //    the same transaction.
        var mirrorType = primary.InvoiceType == "sales" ? "purchase" : "sales";
        var mirrorLines = new List<CreateInvoiceLineRequest>();
        using (var conn = _db.CreateConnection())
        {
            foreach (var line in primary.Lines)
            {
                // Re-resolve account by code in the sister company.
                var sisterAccountId = await conn.ExecuteScalarAsync<Guid?>(@"
                    SELECT id FROM accounts
                    WHERE company_id = @companyId AND code = @code AND is_active = true
                    LIMIT 1;",
                    new { companyId = sisterCompanyId, code = line.AccountCode });

                // Re-resolve product by code in the sister company.
                // Best-effort: leave null if not found. Description
                // still carries the human-readable name so the mirror
                // is not data-poor.
                Guid? sisterProductId = null;
                if (line.ProductCode is not null)
                {
                    sisterProductId = await conn.ExecuteScalarAsync<Guid?>(@"
                        SELECT id FROM products
                        WHERE company_id = @companyId AND code = @code
                        LIMIT 1;",
                        new { companyId = sisterCompanyId, code = line.ProductCode });
                }

                mirrorLines.Add(new CreateInvoiceLineRequest(
                    AccountId: sisterAccountId,
                    ProductId: sisterProductId,
                    Description: line.Description ?? "",
                    Quantity: line.Quantity,
                    UnitPrice: line.UnitPrice,
                    TaxRate: line.TaxRate
                ));
            }
        }

        var mirrorReq = new CreateInvoiceRequest(
            CompanyId: sisterCompanyId,
            InvoiceType: mirrorType,
            InvoiceDate: primary.InvoiceDate,
            PartyName: primaryCompany.name,                  // sister records primary as its counterparty
            PartyNameAr: primaryCompany.name_ar ?? primaryCompany.name,
            PartyTaxId: null,                                // sister company is not a tax-registered counterparty in the usual sense
            Notes: $"Intercompany mirror of {primary.InvoiceNumber} — {primary.InvoiceType}",
            TaxRate: 0m,                                     // tax was already on the primary; the mirror carries the same lines without re-taxing
            IntercompanyCompanyId: null,                     // the mirror itself is NOT mirrored back — that would loop
            Lines: mirrorLines
        );

        // 4) Create the mirror as a draft. Use the existing
        //    CreateDraftAsync so the same product auto-fill / line
        //    validation logic runs.
        var mirror = await CreateDraftAsync(mirrorReq, null);

        // 5) Post the mirror. The same rule event runs in the sister
        //    company and creates a journal entry. The mirror's party
        //    is the primary company, so:
        //      - HOLD's sales invoice → CO-A's purchase invoice
        //        (rule fires PurchaseInvoiceApproved; DR expense /
        //        CR AP — CO-A now owes HOLD).
        //      - CO-A's purchase invoice → HOLD's sales invoice
        //        (rule fires SalesInvoiceApproved; DR AR / CR revenue
        //        — HOLD now has a receivable from CO-A).
        //    Net effect: HOLD records CO-A as customer; CO-A records
        //    HOLD as supplier. Both with the same amount. This is
        //    the textbook intercompany pair setup.
        var mirrorEventName = mirror.InvoiceType == "sales"
            ? "SalesInvoiceApproved" : "PurchaseInvoiceApproved";
        // Build the mirror payload by copying the primary's payload
        // and overriding the three party blocks (party, customer,
        // supplier) with the primary company's identity. The rules'
        // `{supplier.name}` / `{customer.name}` tokens then resolve
        // to the correct legal entity.
        //
        // We start from the primary payload (Dictionary<string,
        // object>), then add a few overrides. The inner dicts are
        // constructed with the same Dictionary<string, object> type
        // so the warning pattern matches the existing code (CS8601
        // on line 285 — pre-existing). Empty string is used for
        // taxId rather than null to avoid CS8625 in this dictionary
        // type; the rules' SubstituteTokens returns "" for missing
        // values anyway.
        var mirrorPayload = new Dictionary<string, object>(primaryPayload)
        {
            ["party"] = new Dictionary<string, object>
            {
                ["name"] = primaryCompany.name,
                ["nameAr"] = primaryCompany.name_ar ?? primaryCompany.name,
                ["taxId"] = ""
            },
            ["customer"] = new Dictionary<string, object>
            {
                ["name"] = primaryCompany.name,
                ["nameAr"] = primaryCompany.name_ar ?? primaryCompany.name,
                ["taxId"] = ""
            },
            ["supplier"] = new Dictionary<string, object>
            {
                ["name"] = primaryCompany.name,
                ["nameAr"] = primaryCompany.name_ar ?? primaryCompany.name,
                ["taxId"] = ""
            }
        };

        _log.LogInformation(
            "Intercompany mirror: posting {MirrorNum} in sister company {SisterId} (type={Type})",
            mirror.InvoiceNumber, sisterCompanyId, mirrorType);

        var mirrorEntries = await _rules.TriggerEventAsync(
            sisterCompanyId, null, mirrorEventName, mirrorPayload);

        if (mirrorEntries.Count == 0)
        {
            throw new InvalidOperationException(
                "لم تنشأ أي قيود محاسبية في الشركة الشقيقة. " +
                "الرجاء التأكد من تفعيل قواعد الترحيل في الشركة الشقيقة.");
        }

        // Mark the mirror invoice as posted. We bypass PostAsync
        // because PostAsync would re-trigger the intercompany dance
        // (it sees the mirror's intercompany_company_id, which is
        // null, so it would simply no-op — but we still want the
        // explicit status update + journal stamping below).
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE invoices SET status = 'posted', posted_at = NOW() WHERE id = @id;",
                new { id = mirror.Id });
        }

        // 6) Create the intercompany_pairs row. We use the
        //    'pending' status here and flip to 'posted' after both
        //    sides are confirmed (the caller in PostAsync does the
        //    flip). This makes a half-mirrored state visible to the
        //    report: pairs stuck on 'pending' are an alert signal.
        var pairId = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                INSERT INTO intercompany_pairs (
                    id, primary_invoice_id, mirror_invoice_id,
                    primary_company_id, mirror_company_id,
                    amount, currency, status, created_at
                )
                VALUES (
                    @id, @primaryInvoiceId, @mirrorInvoiceId,
                    @primaryCompanyId, @mirrorCompanyId,
                    @amount, 'LYD', 'pending', NOW()
                );",
                new
                {
                    id = pairId,
                    primaryInvoiceId = primary.Id,
                    mirrorInvoiceId = mirror.Id,
                    primaryCompanyId = primary.CompanyId,
                    mirrorCompanyId = sisterCompanyId,
                    amount = primary.Total
                });
        }

        // 7) Stamp `intercompany_pair_id` on BOTH sides' journal
        //    entries. This is the back-pointer the elimination
        //    report uses to find both halves in one query.
        var primaryEntryIds = primaryEntries.Select(e => e.Id).ToList();
        var mirrorEntryIds = mirrorEntries.Select(e => e.Id).ToList();
        using (var conn = _db.CreateConnection())
        {
            if (primaryEntryIds.Count > 0)
            {
                await conn.ExecuteAsync(@"
                    UPDATE journal_entries
                    SET intercompany_pair_id = @pairId
                    WHERE id = ANY(@ids);",
                    new { pairId, ids = primaryEntryIds });
            }
            if (mirrorEntryIds.Count > 0)
            {
                await conn.ExecuteAsync(@"
                    UPDATE journal_entries
                    SET intercompany_pair_id = @pairId
                    WHERE id = ANY(@ids);",
                    new { pairId, ids = mirrorEntryIds });
            }
        }

        _log.LogInformation(
            "Intercompany pair {PairId} created: primary={Primary} mirror={Mirror}, amount={Amount} LYD",
            pairId, primary.Id, mirror.Id, primary.Total);

        return new IntercompanyPairDto(
            pairId, primary.Id, mirror.Id,
            primary.CompanyId, sisterCompanyId,
            primary.Total, "LYD", "pending", DateTime.UtcNow);
    }

    /// <summary>
    /// Returns intercompany pairs where either the primary or the
    /// mirror invoice belongs to the given company. Used by the
    /// "Intercompany Pairs" list page.
    /// </summary>
    public async Task<List<IntercompanyPairDto>> GetIntercompanyPairsAsync(
        Guid companyId, DateTime? fromDate = null, DateTime? toDate = null)
    {
        using var conn = _db.CreateConnection();
        var sql = @"
            SELECT id, primary_invoice_id, mirror_invoice_id,
                   primary_company_id, mirror_company_id,
                   amount, currency, status, created_at
            FROM intercompany_pairs
            WHERE (primary_company_id = @companyId OR mirror_company_id = @companyId)";
        if (fromDate.HasValue) sql += " AND created_at >= @fromDate";
        if (toDate.HasValue)   sql += " AND created_at <= @toDate";
        sql += " ORDER BY created_at DESC;";

        var rows = await conn.QueryAsync<IntercompanyPairRow>(sql, new { companyId, fromDate, toDate });
        return rows.Select(r => new IntercompanyPairDto(
            r.id, r.primary_invoice_id, r.mirror_invoice_id,
            r.primary_company_id, r.mirror_company_id,
            r.amount, r.currency, r.status, r.created_at
        )).ToList();
    }

    /// <summary>
    /// Returns a single intercompany pair by id, plus the two full
    /// invoices on each side. Used by the "Pair Detail" page.
    /// </summary>
    public async Task<IntercompanyPairDetailDto?> GetIntercompanyPairAsync(Guid pairId)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<IntercompanyPairRow>(@"
            SELECT id, primary_invoice_id, mirror_invoice_id,
                   primary_company_id, mirror_company_id,
                   amount, currency, status, created_at
            FROM intercompany_pairs WHERE id = @id;",
            new { id = pairId });
        if (row is null) return null;

        var primary = await GetByIdAsync(row.primary_invoice_id);
        InvoiceDto? mirror = null;
        if (row.mirror_invoice_id.HasValue)
            mirror = await GetByIdAsync(row.mirror_invoice_id.Value);

        return new IntercompanyPairDetailDto(
            new IntercompanyPairDto(
                row.id, row.primary_invoice_id, row.mirror_invoice_id,
                row.primary_company_id, row.mirror_company_id,
                row.amount, row.currency, row.status, row.created_at),
            primary, mirror);
    }

    /// <summary>
    /// Reverses an intercompany pair: creates a reversing journal
    /// entry in BOTH companies (the same direction the original
    /// pair's entries went) and marks the pair as 'reversed'. Both
    /// invoices are also flipped to 'reversed' status.
    ///
    /// Idempotency: reversing a pair that is already 'reversed' is
    /// a no-op (returns the current state, no double-reversals).
    /// </summary>
    public async Task<IntercompanyPairDto?> ReverseIntercompanyPairAsync(Guid pairId)
    {
        var detail = await GetIntercompanyPairAsync(pairId);
        if (detail is null) return null;
        if (detail.Pair.Status == "reversed") return detail.Pair;

        // Reverse the primary side's journal entry
        if (detail.Primary is not null && !string.IsNullOrEmpty(detail.Primary.Status))
        {
            // Find the latest posted journal entry for this invoice
            // (could be more than one in a rule fan-out). For
            // intercompany we stamp the pair id on every entry
            // generated by the rule, so we can find them directly.
            using var conn = _db.CreateConnection();
            var entryIds = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM journal_entries
                WHERE intercompany_pair_id = @pairId
                  AND company_id = @companyId
                  AND status = 'posted'
                ORDER BY created_at;",
                new { pairId, companyId = detail.Pair.PrimaryCompanyId })).ToList();

            foreach (var entryId in entryIds)
            {
                await _journal.ReverseAsync(entryId);
            }
        }

        // Reverse the mirror side's journal entry
        if (detail.Mirror is not null)
        {
            using var conn = _db.CreateConnection();
            var entryIds = (await conn.QueryAsync<Guid>(@"
                SELECT id FROM journal_entries
                WHERE intercompany_pair_id = @pairId
                  AND company_id = @companyId
                  AND status = 'posted'
                ORDER BY created_at;",
                new { pairId, companyId = detail.Pair.MirrorCompanyId })).ToList();

            foreach (var entryId in entryIds)
            {
                await _journal.ReverseAsync(entryId);
            }
        }

        // Flip both invoices + the pair to 'reversed' / 'posted' was
        // already set; the entries themselves are now 'reversed'.
        using (var conn = _db.CreateConnection())
        {
            await conn.ExecuteAsync(@"
                UPDATE intercompany_pairs SET status = 'reversed' WHERE id = @id;",
                new { id = pairId });
            await conn.ExecuteAsync(@"
                UPDATE invoices SET status = 'reversed' WHERE id = ANY(@ids);",
                new
                {
                    ids = new[] { detail.Pair.PrimaryInvoiceId }
                        .Concat(detail.Pair.MirrorInvoiceId.HasValue ? new[] { detail.Pair.MirrorInvoiceId.Value } : Array.Empty<Guid>())
                        .ToArray()
                });
        }

        _log.LogInformation("Intercompany pair {PairId} reversed", pairId);

        return (await GetIntercompanyPairsAsync(detail.Pair.PrimaryCompanyId))
            .FirstOrDefault(p => p.Id == pairId);
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
        decimal subtotal, decimal tax_amount, decimal total, string status, DateTime created_at, DateTime? posted_at,
        Guid? intercompany_company_id, decimal amount_paid, DateTime? fully_paid_at);

    private record InvoiceLineRow(
        Guid id, Guid invoice_id, Guid? account_id, Guid? product_id,
        string? account_code, string? account_name,
        string? product_code, string? product_name, string? product_name_ar,
        string? description, decimal quantity, decimal unit_price, decimal tax_rate,
        decimal amount, decimal line_total_with_tax, int line_number);

    private record IntercompanyPairRow(
        Guid id, Guid primary_invoice_id, Guid? mirror_invoice_id,
        Guid primary_company_id, Guid mirror_company_id,
        decimal amount, string currency, string status, DateTime created_at);

    private record CompanyLite(Guid id, string code, string name, string? name_ar);
}

/// <summary>
/// Full intercompany pair view: the pair record + the two invoice
/// DTOs on each side. Returned by GetIntercompanyPairAsync for the
/// "Pair Detail" page.
/// </summary>
public record IntercompanyPairDetailDto(
    IntercompanyPairDto Pair,
    InvoiceDto? Primary,
    InvoiceDto? Mirror
);
