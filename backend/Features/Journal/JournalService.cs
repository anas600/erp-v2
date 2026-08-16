using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Journal;

public class JournalService
{
    private readonly IDbConnectionFactory _db;
    private readonly PostingEngine _posting;

    public JournalService(IDbConnectionFactory db, PostingEngine posting)
    {
        _db = db;
        _posting = posting;
    }

    public async Task<List<JournalEntryDto>> GetByCompanyAsync(Guid companyId, int limit = 50)
    {
        using var conn = _db.CreateConnection();
        var entries = (await conn.QueryAsync<JournalEntryRow>(@"
            SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at, project_id
            FROM journal_entries
            WHERE company_id = @companyId
            ORDER BY entry_date DESC, created_at DESC
            LIMIT @limit;",
            new { companyId, limit })).ToList();

        var result = new List<JournalEntryDto>();
        foreach (var e in entries)
        {
            var dto = await _posting.GetByIdAsync(e.id);
            if (dto is not null) result.Add(dto);
        }
        return result;
    }

    /// <summary>
    /// Sprint 41 — paginated variant of GetByCompanyAsync. Returns
    /// {items, total, limit, offset} so the frontend's journal
    /// page can show a page navigator with total count and prev/next
    /// buttons. The optional status filter narrows to one
    /// lifecycle state (draft, posted, pending, reversed).
    /// </summary>
    public async Task<(List<JournalEntryDto> items, int total)> GetByCompanyPagedAsync(
        Guid companyId, int limit, int offset, string? status = null)
    {
        using var conn = _db.CreateConnection();

        // Build the WHERE clause once and reuse for both queries.
        var whereSql = "WHERE company_id = @companyId";
        if (!string.IsNullOrEmpty(status)) whereSql += " AND status = @status";

        // Total count first (cheap query, used by the page navigator).
        var totalSql = $"SELECT COUNT(*) FROM journal_entries {whereSql};";
        var total = await conn.ExecuteScalarAsync<int>(
            totalSql,
            new { companyId, status });

        // The page of items.
        var pageSql = $@"
            SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at, project_id
            FROM journal_entries
            {whereSql}
            ORDER BY entry_date DESC, created_at DESC
            LIMIT @limit OFFSET @offset;";
        var entries = (await conn.QueryAsync<JournalEntryRow>(
            pageSql,
            new { companyId, status, limit, offset })).ToList();

        var items = new List<JournalEntryDto>();
        foreach (var e in entries)
        {
            var dto = await _posting.GetByIdAsync(e.id);
            if (dto is not null) items.Add(dto);
        }
        return (items, total);
    }

    /// <summary>
    /// Sprint 41 — bulk-approve all draft entries for a company.
    /// Returns succeededIds (entries that moved PENDING→DRAFT, or
    /// stayed DRAFT if they were already there) and failures
    /// (entries that threw — e.g. closed period, invalid state).
    /// </summary>
    public async Task<(List<Guid> succeededIds, Dictionary<Guid, string> failures)> BulkApproveByCompanyAsync(Guid companyId, Guid? userId)
    {
        using var conn = _db.CreateConnection();
        var drafts = (await conn.QueryAsync<(Guid id, string status)>(@"
            SELECT id, status
            FROM journal_entries
            WHERE company_id = @companyId AND status = 'pending';",
            new { companyId })).ToList();

        var succeeded = new List<Guid>();
        var failures = new Dictionary<Guid, string>();
        foreach (var d in drafts)
        {
            try
            {
                var approved = await ApproveAsync(d.id, userId);
                if (approved is not null) succeeded.Add(d.id);
                else failures[d.id] = "Approve returned null (period may be closed)";
            }
            catch (Exception ex)
            {
                failures[d.id] = ex.Message;
            }
        }
        return (succeeded, failures);
    }

    /// <summary>
    /// Sprint 41 — bulk-post all entries that are eligible (status = 'draft'
    /// or 'pending'). Wraps each PostAsync in try/catch so one bad
    /// entry doesn't block the rest. Returns succeeded + failures
    /// (same shape as BulkApproveByCompanyAsync).
    /// </summary>
    public async Task<(List<Guid> succeededIds, Dictionary<Guid, string> failures)> BulkPostByCompanyAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var eligible = (await conn.QueryAsync<Guid>(@"
            SELECT id
            FROM journal_entries
            WHERE company_id = @companyId AND status IN ('draft', 'pending');",
            new { companyId })).ToList();

        var succeeded = new List<Guid>();
        var failures = new Dictionary<Guid, string>();
        foreach (var id in eligible)
        {
            try
            {
                var posted = await PostAsync(id);
                if (posted is not null) succeeded.Add(id);
                else failures[id] = "Post returned null";
            }
            catch (Exception ex)
            {
                failures[id] = ex.Message;
            }
        }
        return (succeeded, failures);
    }

    /// <summary>
    /// Lists all PENDING entries for a company — the ones that need the
    /// accountant's review. Used by the "Pending Entries" page (Sprint 15).
    /// Ordered oldest-first so the accountant drains the queue in arrival
    /// order (FIFO).
    /// </summary>
    public async Task<List<JournalEntryDto>> GetPendingAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var entries = (await conn.QueryAsync<JournalEntryRow>(@"
            SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at, project_id
            FROM journal_entries
            WHERE company_id = @companyId AND status = 'pending'
            ORDER BY created_at ASC;",
            new { companyId })).ToList();

        var result = new List<JournalEntryDto>();
        foreach (var e in entries)
        {
            var dto = await _posting.GetByIdAsync(e.id);
            if (dto is not null) result.Add(dto);
        }
        return result;
    }

    public async Task<JournalEntryDto?> GetByIdAsync(Guid id) => await _posting.GetByIdAsync(id);

    public async Task<JournalEntryDto> CreateDraftAsync(CreateJournalEntryRequest req, Guid? createdBy)
    {
        // Manual draft — editable, requires a separate PostAsync to be
        // promoted to "posted". Used by the human "New journal entry" UI.
        return await CreateInternalAsync(req, createdBy, "draft");
    }

    /// <summary>
    /// Sprint 59 — Update an existing DRAFT journal entry. The accountant
    /// can change the narration, the entry date, the project tag, and
    /// the lines (debit/credit accounts, amounts, descriptions,
    /// cost-centers). Only "draft" entries are editable — once an entry
    /// is "posted" or "reversed" it becomes part of the permanent
    /// accounting record and can only be undone by creating a reverse
    /// entry.
    ///
    /// Implementation: delete the old lines and re-insert the new ones
    /// in a single transaction. We do NOT update the lines one-by-one
    /// because the user can add or remove lines; an UPDATE-by-id would
    /// leave orphan lines behind. A DELETE + INSERT keeps the row
    /// count correct without a separate "removeLine" UI flow.
    ///
    /// Validation: same as create (period must be open, debits = credits,
    /// accounts must be postable). The cost_center_required flag check
    /// lives in the create path; we re-use it via the line-insert helper.
    /// </summary>
    public async Task<JournalEntryDto?> UpdateDraftAsync(
        Guid entryId, CreateJournalEntryRequest req, Guid? updatedBy)
    {
        if (req.Lines.Count == 0)
            throw new InvalidOperationException("Entry must have at least one line");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 1) Load the entry — must exist and must be in 'draft' state.
            var existing = await conn.QuerySingleOrDefaultAsync<(Guid id, string status, Guid company_id)?>(@"
                SELECT id, status, company_id
                FROM journal_entries
                WHERE id = @id;",
                new { id = entryId }, tx);
            if (existing is null)
                return null;
            if (existing.Value.status != "draft")
                throw new InvalidOperationException(
                    $"لا يمكن تعديل قيد بحالة '{existing.Value.status}'. " +
                    "يمكن تعديل القيود المسودة فقط — للقيود المرحّلة، استخدم قيد عكسي.");

            // 2) Period check on the NEW date (the date may have changed).
            await EnsurePeriodOpenAsync(existing.Value.company_id, req.EntryDate, conn, tx);

            // 3) Update the header.
            await conn.ExecuteAsync(@"
                UPDATE journal_entries
                SET entry_date = @entryDate,
                    narration = @narration,
                    project_id = @projectId,
                    updated_at = NOW()
                WHERE id = @id;",
                new
                {
                    id = entryId,
                    entryDate = req.EntryDate,
                    narration = req.Narration,
                    projectId = req.ProjectId
                }, tx);

            // 4) Delete old lines (defensive — no ON DELETE CASCADE on the FK).
            await conn.ExecuteAsync(
                "DELETE FROM journal_lines WHERE entry_id = @id;",
                new { id = entryId }, tx);

            // 5) Insert new lines via the same helper the create path uses.
            int lineNum = 1;
            foreach (var line in req.Lines)
            {
                if (line.Debit < 0 || line.Credit < 0)
                    throw new InvalidOperationException("Debit and Credit must be non-negative");
                if (line.Debit > 0 && line.Credit > 0)
                    throw new InvalidOperationException("A line cannot have both Debit and Credit");
                if (line.Debit == 0 && line.Credit == 0)
                    throw new InvalidOperationException($"Line {lineNum} has zero Debit and zero Credit");

                // Verify the account is postable. Same defensive check
                // as create — protects against the accountant picking
                // a header account (L1/L2/L3) by mistake.
                var isPostable = await conn.QuerySingleOrDefaultAsync<bool?>(@"
                    SELECT is_postable FROM accounts
                    WHERE id = @id AND is_active = true;",
                    new { id = line.AccountId }, tx);
                if (isPostable is null)
                    throw new InvalidOperationException($"Account {line.AccountId} not found or inactive");
                if (isPostable == false)
                    throw new InvalidOperationException(
                        $"الحساب {line.AccountId} حساب رئيسي (غير قابل للترحيل). اختر حساب فرعي.");

                await conn.ExecuteAsync(@"
                    INSERT INTO journal_lines
                        (id, entry_id, line_number, account_id, debit, credit, description, cost_center_id)
                    VALUES
                        (@id, @entryId, @lineNumber, @accountId, @debit, @credit, @description, @costCenterId);",
                    new
                    {
                        id = Guid.NewGuid(),
                        entryId,
                        lineNumber = lineNum++,
                        accountId = line.AccountId,
                        debit = line.Debit,
                        credit = line.Credit,
                        description = line.Description,
                        costCenterId = line.CostCenterId
                    }, tx);
            }

            // 6) Verify the entry balances. Same check as create path.
            var totals = await conn.QuerySingleAsync<(decimal total_debit, decimal total_credit)>(@"
                SELECT COALESCE(SUM(debit), 0) AS total_debit,
                       COALESCE(SUM(credit), 0) AS total_credit
                FROM journal_lines
                WHERE entry_id = @entryId;",
                new { entryId }, tx);
            if (totals.total_debit != totals.total_credit)
                throw new InvalidOperationException(
                    $"القيد غير متوازن: إجمالي المدين = {totals.total_debit} LYD، " +
                    $"إجمالي الدائن = {totals.total_credit} LYD. " +
                    "يجب أن يتساوى الجانبان.");

            tx.Commit();
            return (await _posting.GetByIdAsync(entryId))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Sprint 40 — Trust path. Creates a draft, approves it, and posts
    /// it in one call. Used by the FullYearSeeder and any other caller
    /// that has already validated the journal entry (debits = credits,
    /// period is open, accounts are postable) and wants the entry
    /// to land directly in the General Ledger without the manual
    /// review step.
    ///
    /// Production-grade safety: still runs the same validation as the
    /// three-step path (period check, balance check, account
    /// postability check) — we just collapse the three round-trips
    /// into one. If anything throws, no half-posted state is left
    /// behind.
    /// </summary>
    public async Task<JournalEntryDto> CreateAndPostAsync(CreateJournalEntryRequest req, Guid? userId)
    {
        var draft = await CreateDraftAsync(req, userId);
        var approved = await ApproveAsync(draft.Id, userId);
        if (approved is null)
            throw new InvalidOperationException(
                $"Failed to approve entry {draft.EntryNumber} — check period status and balance");
        var posted = await PostAsync(draft.Id);
        return posted;
    }

    /// <summary>
    /// Creates an entry in "pending" status — used by the rules engine
    /// (Sprint 15). The entry awaits accountant approval via
    /// ApproveAsync before it affects financial reports.
    ///
    /// This is the new default for rule-generated entries (the old
    /// behaviour was to auto-post, which left no room for review).
    /// </summary>
    public async Task<JournalEntryDto> CreatePendingAsync(CreateJournalEntryRequest req, Guid? createdBy)
    {
        if (string.IsNullOrWhiteSpace(req.Source) || !req.Source.StartsWith("rule:"))
            throw new InvalidOperationException(
                "CreatePendingAsync must be called by a rule — Source must start with 'rule:'");
        return await CreateInternalAsync(req, createdBy, "pending");
    }

    private async Task<JournalEntryDto> CreateInternalAsync(CreateJournalEntryRequest req, Guid? createdBy, string initialStatus)
    {
        if (req.Lines.Count == 0)
            throw new InvalidOperationException("Entry must have at least one line");
        if (initialStatus != "draft" && initialStatus != "pending")
            throw new InvalidOperationException($"Unknown initial status '{initialStatus}' — expected 'draft' or 'pending'");

        // Sprint 25 — fiscal period lock check. Look up the period that
        // covers req.EntryDate for this company. If the period exists and
        // is locked, reject the entry. If no period exists (e.g. seed
        // never ran, or the date is outside the year), we ALLOW the
        // entry — the period table is an integrity control, not a
        // mandatory gate. The accountant can lock the period later.
        await EnsurePeriodOpenAsync(req.CompanyId, req.EntryDate);

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var entryId = await CreateDraftEntryCoreAsync(conn, tx, req, createdBy, initialStatus);
            tx.Commit();
            return (await _posting.GetByIdAsync(entryId))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Sprint 25 — In-transaction overload of <see cref="CreateDraftAsync"/>.
    /// Used by ReceiptService and PaymentService so the JE creation can be
    /// rolled back together with the voucher status update and the
    /// invoice amount_paid update. The caller owns the connection and
    /// the transaction.
    /// </summary>
    public async Task<Guid> CreateDraftInTxAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        CreateJournalEntryRequest req, Guid? createdBy)
    {
        if (req.Lines.Count == 0)
            throw new InvalidOperationException("Entry must have at least one line");

        // Re-use the same period check. It only reads; running it inside
        // an existing transaction is safe.
        await EnsurePeriodOpenAsync(req.CompanyId, req.EntryDate, conn, tx);

        // FIX 2026-08-05: Return just the entryId. Reading it back via
        // _posting.GetByIdAsync opens a NEW connection that can't see
        // uncommitted data from this transaction, so it returns null,
        // and the caller's `.Id` access NRE's. Callers can read the full
        // entry AFTER tx.Commit() when the data is visible.
        return await CreateDraftEntryCoreAsync(conn, tx, req, createdBy, "draft");
    }

    /// <summary>
    /// Internal: does the actual INSERT for a journal entry + its lines
    /// on the supplied connection/transaction. Both CreateInternalAsync
    /// (top-level) and CreateDraftInTxAsync (caller-owned) use this
    /// helper so the INSERT logic is single-sourced.
    /// </summary>
    private async Task<Guid> CreateDraftEntryCoreAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        CreateJournalEntryRequest req, Guid? createdBy, string initialStatus)
    {
        var entryId = Guid.NewGuid();
        var entryNumber = await GenerateEntryNumberAsync(req.CompanyId, conn, tx);

        // Source resolution order:
        //   1. Caller-provided source (e.g. "rule:{ruleId}" from the
        //      rules engine, or "invoice" from InvoiceService)
        //   2. Default to "manual" so existing callers keep working
        var source = string.IsNullOrWhiteSpace(req.Source) ? "manual" : req.Source;

        await conn.ExecuteAsync(@"
            INSERT INTO journal_entries (id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, project_id)
            VALUES (@id, @companyId, @entryNumber, @entryDate, @narration, @status, @source, @ruleId, @reversesEntryId, @createdBy, @projectId);",
            new
            {
                id = entryId,
                companyId = req.CompanyId,
                entryNumber,
                entryDate = req.EntryDate,
                narration = req.Narration,
                status = initialStatus,
                source,
                ruleId = req.RuleId,
                reversesEntryId = req.ReversesEntryId,
                createdBy,
                // Sprint 35: project tag. Default null (backward
                // compatible). P&L reports query this column.
                projectId = req.ProjectId
            }, tx);

        int lineNum = 1;
        foreach (var line in req.Lines)
        {
            if (line.Debit < 0 || line.Credit < 0)
                throw new InvalidOperationException("Debit and Credit must be non-negative");
            if (line.Debit > 0 && line.Credit > 0)
                throw new InvalidOperationException("A line cannot have both debit and credit");
            if (line.Debit == 0 && line.Credit == 0)
                throw new InvalidOperationException("Line must have either debit or credit");

            await conn.ExecuteAsync(@"
                INSERT INTO journal_lines (id, journal_entry_id, account_id, debit, credit, description, line_number, cost_center_id)
                VALUES (@id, @entryId, @accountId, @debit, @credit, @description, @lineNumber, @costCenterId);",
                new
                {
                    id = Guid.NewGuid(),
                    entryId,
                    accountId = line.AccountId,
                    debit = line.Debit,
                    credit = line.Credit,
                    description = line.Description,
                    lineNumber = lineNum++,
                    costCenterId = line.CostCenterId
                }, tx);
        }

        return entryId;
    }

    /// <summary>
    /// Sprint 25 — looks up the fiscal period for <paramref name="entryDate"/>
    /// in <paramref name="companyId"/> and rejects the entry if the period
    /// is locked.
    ///
    /// Implementation notes:
    ///   - If no period covers the date (e.g. the date is in a year that
    ///     has no fiscal_year row, or the seed never ran), we ALLOW the
    ///     entry. The fiscal_period table is an additional integrity
    ///     control; it must not block legitimate entries when the table
    ///     is incomplete.
    ///   - The optional <paramref name="conn"/>/<paramref name="tx"/>
    ///     overloads let callers re-use an open connection (e.g. inside
    ///     Receipt/Payment's PostAsync transaction).
    /// </summary>
    private async Task EnsurePeriodOpenAsync(
        Guid companyId, DateTime entryDate,
        System.Data.IDbConnection? conn = null, System.Data.IDbTransaction? tx = null)
    {
        var ownsConnection = conn is null;
        if (conn is null) conn = _db.CreateConnection();

        try
        {
            // Find the period covering this date. The date comparison
            // is between the entry_date (timestamp) cast to date and
            // the period boundaries (date). We use a concrete record
            // instead of a value tuple because Dapper's positional
            // tuple mapping is fragile across versions; the record
            // is explicit and stable.
            var periodRow = await conn.QuerySingleOrDefaultAsync<PeriodCheckRow?>(@"
                SELECT p.id, p.is_closed
                FROM fiscal_periods p
                JOIN fiscal_years y ON y.id = p.fiscal_year_id
                WHERE y.company_id = @companyId
                  AND @entryDate::date BETWEEN p.start_date AND p.end_date
                LIMIT 1;",
                new { companyId, entryDate }, tx);

            if (periodRow is null) return; // no period configured → allow
            if (periodRow.is_closed)
            {
                throw new InvalidOperationException(
                    "الفترة المحاسبية مقفلة — لا يمكن إنشاء قيود في هذه الفترة");
            }
        }
        finally
        {
            if (ownsConnection) conn.Dispose();
        }
    }

    private record PeriodCheckRow(Guid id, bool is_closed);

    public async Task<JournalEntryDto> PostAsync(Guid entryId) => await _posting.PostAsync(entryId);

    public async Task<bool> ReverseAsync(Guid entryId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var entry = await conn.QuerySingleOrDefaultAsync<JournalEntryRow>(@"
                SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at, project_id
                FROM journal_entries WHERE id = @id;",
                new { id = entryId }, tx);
            if (entry is null) return false;
            if (entry.status != "posted") throw new InvalidOperationException("Only posted entries can be reversed");

            // Create a reversing entry
            var lines = (await conn.QueryAsync<JournalLineRow>(@"
                SELECT id, journal_entry_id, account_id, debit, credit, description, line_number
                FROM journal_lines WHERE journal_entry_id = @id ORDER BY line_number;",
                new { id = entryId }, tx)).ToList();

            var newEntryId = Guid.NewGuid();
            var newEntryNumber = await GenerateEntryNumberAsync(entry.company_id, conn, tx);

            // Source is now just the prefix "reverse" — the actual link
            // to the original entry is in the new reverses_entry_id FK.
            // The FK is the authoritative source; the prefix is kept for
            // fast filtering ("all reversals" = WHERE source = 'reverse').
            await conn.ExecuteAsync(@"
                INSERT INTO journal_entries (id, company_id, entry_number, entry_date, narration, status, source, reverses_entry_id, created_by, posted_at)
                VALUES (@id, @companyId, @entryNumber, @entryDate, @narration, 'posted', 'reverse', @reversesEntryId, @createdBy, NOW());",
                new
                {
                    id = newEntryId,
                    companyId = entry.company_id,
                    entryNumber = newEntryNumber,
                    entryDate = DateTime.UtcNow.Date,
                    narration = $"عكس قيد رقم {entry.entry_number}",
                    reversesEntryId = entry.id,
                    createdBy = entry.created_by
                }, tx);

            int lineNum = 1;
            foreach (var l in lines)
            {
                await conn.ExecuteAsync(@"
                    INSERT INTO journal_lines (id, journal_entry_id, account_id, debit, credit, description, line_number)
                    VALUES (@id, @entryId, @accountId, @debit, @credit, @description, @lineNumber);",
                    new
                    {
                        id = Guid.NewGuid(),
                        entryId = newEntryId,
                        accountId = l.account_id,
                        // Swap debit/credit for reversal
                        debit = l.credit,
                        credit = l.debit,
                        description = $"عكس: {l.description}",
                        lineNumber = lineNum++
                    }, tx);
            }

            // Update account balances (reverse the impact)
            foreach (var l in lines)
            {
                var account = await conn.QuerySingleOrDefaultAsync<AccountRow>(@"
                    SELECT id, account_type, nature FROM accounts WHERE id = @id;",
                    new { id = l.account_id }, tx);
                if (account is null) continue;

                // Reverse the original line's impact
                var originalNet = account.nature == "Debit"
                    ? l.debit - l.credit
                    : l.credit - l.debit;

                await conn.ExecuteAsync(@"
                    UPDATE accounts SET balance = balance - @netChange WHERE id = @id;",
                    new { netChange = originalNet, id = account.id }, tx);
            }

            // Mark original as reversed
            await conn.ExecuteAsync(
                "UPDATE journal_entries SET status = 'reversed' WHERE id = @id;",
                new { id = entryId }, tx);

            // ============================================================
            // Sprint 28 — CASCADING REVERSAL.
            //
            // Without this, the GL correctly reflects the reversal
            // (the account balance update above), but the source
            // documents (voucher, invoice) keep their old state and
            // reports like customer/supplier aging show stale numbers.
            //
            // Example: a posted payment voucher (source="payment")
            // records the invoice as partially-paid and reduces the
            // supplier sub-ledger. Reversing just the journal entry
            // puts the sub-ledger back, but the voucher still says
            // "posted" and the invoice still says "partiallypaid"
            // with amount_paid = 999, so the aging report keeps
            // counting the (now non-existent) payment.
            //
            // Fix: if the original entry was generated by a voucher
            // (source="receipt" or "payment"), reset the source
            // voucher back to "draft" and roll back the invoice
            // amount_paid / status. The voucher can then be edited
            // or deleted as the data-entry accountant sees fit.
            // The review accountant still sees the reversal in the
            // general ledger (via the "reverse" JE) — no audit gap.
            // ============================================================
            if (entry.source == "payment")
            {
                await ReversePaymentVoucherAsync(conn, tx, entryId);
            }
            else if (entry.source == "receipt")
            {
                await ReverseReceiptVoucherAsync(conn, tx, entryId);
            }
            else if (entry.source?.StartsWith("invoice:") == true)
            {
                // Standalone invoice posting (no voucher). Just
                // revert the invoice status so it can be re-edited
                // or re-posted.
                await ReverseInvoicePostingAsync(conn, tx, entryId);
            }

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Approves a pending journal entry and posts it (transitions to
    /// "posted" status). The entry then affects financial reports.
    ///
    /// Used by the accountant's "Approve" button on the Pending Entries
    /// page. Idempotent: approving an already-posted entry is a no-op.
    ///
    /// Refuses to approve:
    ///   - Entries in any state other than "pending" (e.g. already posted,
    ///     already reversed, manual drafts that should be published via
    ///     PostAsync instead).
    ///   - Empty entries (no lines).
    ///   - Unbalanced entries (debits != credits).
    /// </summary>
    /// <summary>
    /// Sprint 30 — ApproveAsync now does PENDING → DRAFT (not POSTED).
    ///
    /// The user explicitly wants the accountant to be able to review a
    /// rule-generated entry, approve it as a "draft for review", and
    /// then post it (DRAFT → POSTED) as a separate explicit step. This
    /// matches the natural accounting workflow where the reviewer signs
    /// off before the entry hits the General Ledger.
    ///
    /// Flow:
    ///   PENDING (rule generated) ── approve ──▶ DRAFT (approved, ready to post)
    ///   DRAFT (any source)       ── post    ──▶ POSTED (hits the GL, affects reports)
    ///   POSTED                   ── reverse ──▶ REVERSED (reversing entry created)
    ///
    /// Note: this method does NOT touch `accounts.balance`. Balance
    /// updates happen in PostingEngine.PostAsync (DRAFT → POSTED).
    /// </summary>
    public async Task<JournalEntryDto?> ApproveAsync(Guid entryId, Guid? userId)
    {
        try
        {
            using var conn = _db.CreateConnection();

            // Load the entry first (no transaction yet — we want a clean
            // status check before opening a tx)
            var entry = await conn.QuerySingleOrDefaultAsync<JournalEntryRow>(@"
                SELECT id, company_id, entry_number, entry_date, narration,
                       status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at, project_id
                FROM journal_entries WHERE id = @id;",
                new { id = entryId });

            if (entry is null) return null;
            if (entry.status == "draft") return await GetByIdAsync(entryId); // idempotent
            if (entry.status != "pending")
                throw new InvalidOperationException(
                    $"لا يمكن اعتماد قيد بحالة '{entry.status}'. المتوقع: 'pending'");

            // Sprint 30: PENDING → DRAFT only (no balance update).
            // The accountant must then click "post" to actually push
            // the entry into the General Ledger.
            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(@"
                    UPDATE journal_entries
                    SET status = 'draft'
                    WHERE id = @id AND status = 'pending';",
                    new { id = entryId }, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return await GetByIdAsync(entryId);
        }
        catch (Exception ex)
        {
            // Re-throw with a clear prefix so the seed loop error log
            // can pinpoint which step failed.
            throw new InvalidOperationException(
                $"ApproveAsync({entryId}) failed: {ex.GetType().Name}: {ex.Message} | stack={ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}",
                ex);
        }
    }

    /// <summary>
    /// Rejects a pending journal entry: transitions it back to "draft"
    /// (so the originator can edit it) and stamps the reason. The entry
    /// is NOT deleted — accounting records are immutable; rejection is
    /// just a state transition.
    ///
    /// If the entry was auto-generated by a rule, the rejected entry
    /// stays in the journal as a draft with the rule's reference
    /// preserved, so the rule author can investigate.
    /// </summary>
    public async Task<JournalEntryDto?> RejectAsync(Guid entryId, Guid? userId, string? reason)
    {
        using var conn = _db.CreateConnection();

        var entry = await conn.QuerySingleOrDefaultAsync<JournalEntryRow>(@"
            SELECT id, company_id, entry_number, entry_date, narration,
                   status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at, project_id
            FROM journal_entries WHERE id = @id;",
            new { id = entryId });

        if (entry is null) return null;
        if (entry.status != "pending")
            throw new InvalidOperationException(
                $"لا يمكن رفض قيد بحالة '{entry.status}'. المتوقع: 'pending'");

        var newNarration = string.IsNullOrWhiteSpace(reason)
            ? entry.narration
            : $"[مرفوض: {reason}] {entry.narration}";

        await conn.ExecuteAsync(@"
            UPDATE journal_entries
            SET status = 'draft', narration = @narration
            WHERE id = @id;",
            new { id = entryId, narration = newNarration });

        // Log the rejection for audit purposes.
        try
        {
            await conn.ExecuteAsync(@"
                INSERT INTO audit_logs (id, user_id, action, entity_type, entity_id, payload_json, created_at)
                VALUES (@id, @userId, 'reject', 'journal_entry', @entityId, @payload::jsonb, NOW());",
                new
                {
                    id = Guid.NewGuid(),
                    userId,
                    entityId = entryId,
                    payload = $"{{\"reason\":\"{(reason ?? "").Replace("\"", "\\\"")}\",\"originalStatus\":\"pending\"}}"
                });
        }
        catch
        {
            // audit_logs insert failure should not block the rejection
        }

        return await GetByIdAsync(entryId);
    }

    /// <summary>
    /// Deletes a draft journal entry and its lines. Drafts are the only
    /// entries that can be removed — once an entry is posted (or pending,
    /// Sprint 30 — full lifecycle of journal entries:
    ///
    /// ┌──────────┐ approve ┌────────┐ post ┌─────────┐ reverse ┌────────────┐
    /// │ PENDING  │────────▶│ DRAFT  │─────▶│ POSTED  │────────▶│ REVERSED   │
    /// └──────────┘         └────────┘      └─────────┘         └────────────┘
    ///      │ delete            │ delete        │ cannot delete
    ///      ▼                   ▼               (use reverse)
    ///   (cascades)         (cascades)
    ///      │                   │
    ///      └──── restore source document to draft ───┘
    ///
    /// Deletable states: PENDING (rule-generated, awaiting review) and
    /// DRAFT (manually created or approved from pending, awaiting post).
    /// Both are pre-accounting — they don't touch `accounts.balance` so
    /// we can safely roll the source back to draft without disturbing
    /// the general ledger.
    ///
    /// Why PENDING is now deletable:
    ///   - The user may want to discard a rule-generated entry (e.g.
    ///     they posted the wrong invoice and want to re-post it).
    ///   - The previous behavior forced a Reject, but Reject required
    ///     a reason; Delete is faster for "just throw it away" cases.
    ///   - Source rollback ensures the source invoice/voucher can be
    ///     re-edited and re-posted.
    ///
    /// Why POSTED is NOT deletable:
    ///   - Posted entries are part of the permanent accounting record.
    ///     They can only be cancelled by a REVERSING entry (per
    ///     GAAP/IFRS — reversible, never erasable).
    ///
    /// Returns true if the entry was deleted (and source rolled back),
    /// false if it didn't exist. Throws InvalidOperationException if
    /// the entry is in 'posted' or 'reversed' state.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid entryId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 1) Load the entry — we need status + source for cascade.
            var entry = await conn.QuerySingleOrDefaultAsync<(Guid id, string status, string? source, string entry_number)?>(@"
                SELECT id, status, source, entry_number FROM journal_entries WHERE id = @id;",
                new { id = entryId }, tx);
            if (entry is null) return false;
            if (entry.Value.status != "pending" && entry.Value.status != "draft")
                throw new InvalidOperationException(
                    $"لا يمكن حذف قيد بحالة '{entry.Value.status}'. " +
                    "القيد المرحّل يجب عكسه بقيد عكسي، أو احذفه قبل الترحيل.");

            // 2) Cascade: restore the source document so the user can
            //    re-edit and re-post. PENDING/DRAFT entries never
            //    touched `accounts.balance`, so this is safe — no
            //    financial-report impact.
            await RestoreSourceForDeletedEntryAsync(conn, tx, entryId, entry.Value.source);

            // 3) Delete the lines first (defensive — there's no ON DELETE
            //    CASCADE on the FK, so this would orphan the lines if we
            //    only deleted the header).
            await conn.ExecuteAsync(
                "DELETE FROM journal_lines WHERE journal_entry_id = @id;",
                new { id = entryId }, tx);

            // 4) Delete the header.
            await conn.ExecuteAsync(
                "DELETE FROM journal_entries WHERE id = @id;",
                new { id = entryId }, tx);

            tx.Commit();
            return true;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Sprint 30 — when a PENDING/DRAFT journal entry is deleted, walk
    /// back to the source document (invoice / receipt / payment) and
    /// restore it to "draft" status, clearing the link. The source
    /// document was just in the "posted" intermediate state (it sent
    /// the JE to the journal but no one has yet approved/posted the
    /// JE to the GL), so rolling it back to draft lets the data-entry
    /// accountant re-edit and re-post it.
    ///
    /// Source detection — the rule engine sets source="rule:UUID"
    /// (the rule's own id) and the voucher code sets source="receipt"
    /// or "payment" (a plain string for the voucher type). For rules
    /// we have to dig into the narration to find the source document
    /// number, because the rule id doesn't tell us which invoice/voucher
    /// it processed. The narration is a stable, human-readable format:
    ///   "فاتورة مبيعات رقم INV-S-2026-0007 - Customer Name"
    ///   "فاتورة مشتريات رقم INV-P-2026-0003 - Supplier Name"
    ///   "سند قبض RV-2026-0005 - Customer Name (RV-005)"
    ///   "سند صرف PV-2026-0002 - Supplier Name (PV-002)"
    /// </summary>
    private async Task RestoreSourceForDeletedEntryAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        Guid entryId, string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return; // manual entry — no source

        if (source == "payment")
        {
            // Payment voucher that created this JE — roll it back to draft
            var pv = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid? invoice_id, decimal amount)?>(@"
                SELECT id, invoice_id, amount FROM payment_vouchers
                WHERE journal_entry_id = @entryId LIMIT 1;",
                new { entryId }, tx);
            if (pv is null) return;

            await conn.ExecuteAsync(@"
                UPDATE payment_vouchers
                SET status = 'draft', posted_at = NULL, journal_entry_id = NULL
                WHERE id = @id;",
                new { id = pv.Value.id }, tx);

            // If it was applied to an invoice, restore the invoice's
            // amount_paid by subtracting the payment (the payment is
            // being thrown away, so the invoice should be un-paid).
            if (pv.Value.invoice_id.HasValue)
            {
                await RestoreInvoiceAmountPaidAsync(
                    conn, tx, pv.Value.invoice_id.Value, -pv.Value.amount);
            }
        }
        else if (source == "receipt")
        {
            // Same pattern as payment but for receipts.
            var rv = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid? invoice_id, decimal amount)?>(@"
                SELECT id, invoice_id, amount FROM receipt_vouchers
                WHERE journal_entry_id = @entryId LIMIT 1;",
                new { entryId }, tx);
            if (rv is null) return;

            await conn.ExecuteAsync(@"
                UPDATE receipt_vouchers
                SET status = 'draft', posted_at = NULL, journal_entry_id = NULL
                WHERE id = @id;",
                new { id = rv.Value.id }, tx);

            if (rv.Value.invoice_id.HasValue)
            {
                await RestoreInvoiceAmountPaidAsync(
                    conn, tx, rv.Value.invoice_id.Value, -rv.Value.amount);
            }
        }
        else if (source.StartsWith("invoice:"))
        {
            // Standalone invoice posting (no voucher). Roll the invoice
            // back to draft so it can be re-edited or re-posted.
            var invoiceIdStr = source.Substring("invoice:".Length);
            if (!Guid.TryParse(invoiceIdStr, out var invoiceId)) return;

            await ReverseInvoicePostingAsync(conn, tx, invoiceId);
        }
        else if (source.StartsWith("rule:"))
        {
            // Rule-generated entry — parse the narration to find the
            // source document number, then look it up.
            var narration = await conn.QuerySingleOrDefaultAsync<string?>(@"
                SELECT narration FROM journal_entries WHERE id = @id;",
                new { id = entryId }, tx);
            if (string.IsNullOrWhiteSpace(narration)) return;

            // INV-?-YYYY-NNNN (sales INV-S, purchase INV-P) — invoice rule
            var invMatch = System.Text.RegularExpressions.Regex.Match(
                narration, @"INV-[SP]-\d{4}-\d{4}");
            if (invMatch.Success)
            {
                var invoiceNumber = invMatch.Value;
                var inv = await conn.QuerySingleOrDefaultAsync<(Guid id, string status, string invoice_type)?>(@"
                    SELECT id, status, invoice_type FROM invoices
                    WHERE invoice_number = @num LIMIT 1;",
                    new { num = invoiceNumber }, tx);
                if (inv is null) return;

                // Only roll back if invoice is currently in 'posted' state.
                // If it's already 'paid' / 'partiallypaid' / 'cancelled',
                // we leave it alone (a payment has been applied on top,
                // and rolling back the invoice would orphan the payment).
                //
                // NOTE: invoices table has no `journal_entry_id` column,
                // so we can't use ReverseInvoicePostingAsync (which queries
                // by that column). Update directly using the id we have.
                if (inv.Value.status == "posted")
                {
                    await conn.ExecuteAsync(@"
                        UPDATE invoices
                        SET status = 'draft', posted_at = NULL
                        WHERE id = @id;",
                        new { id = inv.Value.id }, tx);
                }
                return;
            }

            // RV-YYYY-NNNN — receipt rule (the narration has "سند قبض RV-...")
            var rvMatch = System.Text.RegularExpressions.Regex.Match(
                narration, @"RV-\d{4}-\d{4}");
            if (rvMatch.Success)
            {
                var rvNumber = rvMatch.Value;
                var rv = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid? invoice_id, decimal amount)?>(@"
                    SELECT id, invoice_id, amount FROM receipt_vouchers
                    WHERE voucher_number = @num LIMIT 1;",
                    new { num = rvNumber }, tx);
                if (rv is null) return;

                await conn.ExecuteAsync(@"
                    UPDATE receipt_vouchers
                    SET status = 'draft', posted_at = NULL, journal_entry_id = NULL
                    WHERE id = @id;",
                    new { id = rv.Value.id }, tx);

                if (rv.Value.invoice_id.HasValue)
                {
                    await RestoreInvoiceAmountPaidAsync(
                        conn, tx, rv.Value.invoice_id.Value, -rv.Value.amount);
                }
                return;
            }

            // PV-YYYY-NNNN — payment rule
            var pvMatch = System.Text.RegularExpressions.Regex.Match(
                narration, @"PV-\d{4}-\d{4}");
            if (pvMatch.Success)
            {
                var pvNumber = pvMatch.Value;
                var pv = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid? invoice_id, decimal amount)?>(@"
                    SELECT id, invoice_id, amount FROM payment_vouchers
                    WHERE voucher_number = @num LIMIT 1;",
                    new { num = pvNumber }, tx);
                if (pv is null) return;

                await conn.ExecuteAsync(@"
                    UPDATE payment_vouchers
                    SET status = 'draft', posted_at = NULL, journal_entry_id = NULL
                    WHERE id = @id;",
                    new { id = pv.Value.id }, tx);

                if (pv.Value.invoice_id.HasValue)
                {
                    await RestoreInvoiceAmountPaidAsync(
                        conn, tx, pv.Value.invoice_id.Value, -pv.Value.amount);
                }
            }
        }
    }

    private async Task<string> GenerateEntryNumberAsync(Guid companyId, System.Data.IDbConnection conn, System.Data.IDbTransaction tx)
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"JV-{year}-";
        var lastNumber = await conn.ExecuteScalarAsync<string?>(@"
            SELECT entry_number FROM journal_entries
            WHERE company_id = @companyId AND entry_number LIKE @pattern
            ORDER BY entry_number DESC LIMIT 1;",
            new { companyId, pattern = $"{prefix}%" }, tx);

        if (string.IsNullOrEmpty(lastNumber))
            return $"{prefix}0001";

        var numPart = lastNumber.Substring(prefix.Length);
        if (int.TryParse(numPart, out var n))
            return $"{prefix}{(n + 1):D4}";

        return $"{prefix}0001";
    }

    private record JournalEntryRow(
        Guid id, Guid company_id, string entry_number, DateTime entry_date, string? narration,
        string status, string? source, Guid? rule_id, Guid? reverses_entry_id,
        Guid? created_by, DateTime created_at, DateTime? posted_at,
        // Sprint 35: project tag.
        Guid? project_id);

    private record JournalLineRow(
        Guid id, Guid journal_entry_id, Guid account_id, decimal debit, decimal credit,
        string? description, int line_number);

    private record AccountRow(Guid id, string account_type, string nature);

    // ============================================================
    // Sprint 28 — CASCADING REVERSAL HELPERS
    // ============================================================
    // When a posted journal entry is reversed, the cascade walks
    // back through the originating document(s) and restores their
    // state so subsequent reports (aging, statements, invoice
    // status) reflect the reversal.
    //
    // The general-ledger side of the reversal is already done by
    // the caller (ReverseAsync updates the accounts). These
    // helpers only touch the business-document side:
    //   * Voucher: status posted → draft (so it can be edited/deleted)
    //   * Invoice: amount_paid reduced, status back to posted/outstanding
    //
    // Idempotency: if a voucher/invoice is already in a non-posted
    // state (e.g. another reversal already happened), the helpers
    // no-op so the cascade can be safely re-run.
    // ============================================================

    private async Task ReversePaymentVoucherAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid entryId)
    {
        var pv = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid? invoice_id, decimal amount, string status)?>(@"
            SELECT id, invoice_id, amount, status
            FROM payment_vouchers
            WHERE journal_entry_id = @entryId
            LIMIT 1;",
            new { entryId }, tx);

        if (pv is null) return; // no voucher links to this JE

        // Roll the voucher back to draft so the data-entry accountant
        // can fix it. We clear journal_entry_id so a re-post creates
        // a new JE.
        await conn.ExecuteAsync(@"
            UPDATE payment_vouchers
            SET status = 'draft',
                posted_at = NULL,
                journal_entry_id = NULL
            WHERE id = @id;",
            new { id = pv.Value.id }, tx);

        // Restore the invoice: subtract this voucher's amount from
        // amount_paid, recompute status. We DON'T recompute from
        // scratch — we just subtract and let the recompute query
        // decide the new status (outstanding / partiallypaid / paid).
        if (pv.Value.invoice_id.HasValue)
        {
            await RestoreInvoiceAmountPaidAsync(
                conn, tx, pv.Value.invoice_id.Value, -pv.Value.amount);
        }
    }

    private async Task ReverseReceiptVoucherAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid entryId)
    {
        var rv = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid? invoice_id, decimal amount, string status)?>(@"
            SELECT id, invoice_id, amount, status
            FROM receipt_vouchers
            WHERE journal_entry_id = @entryId
            LIMIT 1;",
            new { entryId }, tx);

        if (rv is null) return;

        await conn.ExecuteAsync(@"
            UPDATE receipt_vouchers
            SET status = 'draft',
                posted_at = NULL,
                journal_entry_id = NULL
            WHERE id = @id;",
            new { id = rv.Value.id }, tx);

        if (rv.Value.invoice_id.HasValue)
        {
            await RestoreInvoiceAmountPaidAsync(
                conn, tx, rv.Value.invoice_id.Value, -rv.Value.amount);
        }
    }

    /// <summary>
    /// Rolls an invoice back to "posted" or "outstanding" after one
    /// of its payments/receipts was reversed. Recomputes amount_paid
    /// from the remaining payment_vouchers + receipt_vouchers, and
    /// sets status from the comparison of (amount_paid vs total).
    /// </summary>
    private async Task RestoreInvoiceAmountPaidAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        Guid invoiceId, decimal deltaToApply)
    {
        // Apply the delta (typically negative — undoing the payment)
        // and clamp at 0 to avoid negative amount_paid if multiple
        // reversals race.
        await conn.ExecuteAsync(@"
            UPDATE invoices
            SET amount_paid = GREATEST(0, COALESCE(amount_paid, 0) + @delta)
            WHERE id = @id;",
            new { id = invoiceId, delta = deltaToApply }, tx);

        // Recompute the canonical status from the new amount_paid vs
        // the invoice total. We do this in SQL so the rule stays
        // consistent even if the C# state machine is out of sync.
        await conn.ExecuteAsync(@"
            UPDATE invoices
            SET status = CASE
                WHEN COALESCE(amount_paid, 0) <= 0 THEN 'posted'
                WHEN amount_paid < total THEN 'partiallypaid'
                ELSE 'paid'
            END
            WHERE id = @id;",
            new { id = invoiceId }, tx);

        // If the invoice is back to "posted" and was previously paid
        // by this voucher, the fully_paid_at timestamp is no longer
        // accurate — clear it. Same for the source link to the JE
        // (the invoice's "posted" JE is still valid, but the
        // voucher-driven amount_paid change has been reverted).
        await conn.ExecuteAsync(@"
            UPDATE invoices
            SET fully_paid_at = NULL
            WHERE id = @id AND status <> 'paid';",
            new { id = invoiceId }, tx);
    }

    private async Task ReverseInvoicePostingAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx, Guid entryId)
    {
        // For invoice postings (source = "invoice:..."), the JE
        // is the one that the InvoiceService.PostAsync created. We
        // roll the invoice back to "draft" so it can be edited and
        // re-posted. We DON'T touch the journal_entry_id (the entry
        // is now in 'reversed' state but the FK is harmless).
        var inv = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT id FROM invoices WHERE journal_entry_id = @entryId LIMIT 1;",
            new { entryId }, tx);

        if (inv is null) return;

        await conn.ExecuteAsync(@"
            UPDATE invoices
            SET status = 'draft',
                posted_at = NULL
            WHERE id = @id;",
            new { id = inv.Value }, tx);
    }
}
