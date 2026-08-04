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
            SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at
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
    /// Lists all PENDING entries for a company — the ones that need the
    /// accountant's review. Used by the "Pending Entries" page (Sprint 15).
    /// Ordered oldest-first so the accountant drains the queue in arrival
    /// order (FIFO).
    /// </summary>
    public async Task<List<JournalEntryDto>> GetPendingAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var entries = (await conn.QueryAsync<JournalEntryRow>(@"
            SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at
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

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var entryId = Guid.NewGuid();
            var entryNumber = await GenerateEntryNumberAsync(req.CompanyId, conn, tx);

            // Source resolution order:
            //   1. Caller-provided source (e.g. "rule:{ruleId}" from the
            //      rules engine, or "invoice" from InvoiceService)
            //   2. Default to "manual" so existing callers keep working
            var source = string.IsNullOrWhiteSpace(req.Source) ? "manual" : req.Source;

            await conn.ExecuteAsync(@"
                INSERT INTO journal_entries (id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by)
                VALUES (@id, @companyId, @entryNumber, @entryDate, @narration, @status, @source, @ruleId, @reversesEntryId, @createdBy);",
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
                    createdBy
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
                    INSERT INTO journal_lines (id, journal_entry_id, account_id, debit, credit, description, line_number)
                    VALUES (@id, @entryId, @accountId, @debit, @credit, @description, @lineNumber);",
                    new
                    {
                        id = Guid.NewGuid(),
                        entryId,
                        accountId = line.AccountId,
                        debit = line.Debit,
                        credit = line.Credit,
                        description = line.Description,
                        lineNumber = lineNum++
                    }, tx);
            }

            tx.Commit();
            return (await _posting.GetByIdAsync(entryId))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<JournalEntryDto> PostAsync(Guid entryId) => await _posting.PostAsync(entryId);

    public async Task<bool> ReverseAsync(Guid entryId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var entry = await conn.QuerySingleOrDefaultAsync<JournalEntryRow>(@"
                SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at
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
    public async Task<JournalEntryDto?> ApproveAsync(Guid entryId, Guid? userId)
    {
        using var conn = _db.CreateConnection();

        // Load the entry first (no transaction yet — we want a clean
        // status check before opening a tx)
        var entry = await conn.QuerySingleOrDefaultAsync<JournalEntryRow>(@"
            SELECT id, company_id, entry_number, entry_date, narration,
                   status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at
            FROM journal_entries WHERE id = @id;",
            new { id = entryId });

        if (entry is null) return null;
        if (entry.status == "posted") return await GetByIdAsync(entryId); // idempotent
        if (entry.status != "pending")
            throw new InvalidOperationException(
                $"لا يمكن اعتماد قيد بحالة '{entry.status}'. المتوقع: 'pending'");

        // Delegate the actual transition to PostingEngine — it handles
        // balance validation, account-balance updates, and the
        // UPDATE journal_entries SET status = 'posted'.
        return await _posting.PostAsync(entryId);
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
                   status, source, rule_id, reverses_entry_id, created_by, created_at, posted_at
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
    /// or reversed) it becomes part of the permanent accounting record.
    ///
    /// Why only drafts?
    ///   - Posted entries must never be deleted (GAAP/IFRS — reversible,
    ///     never erasable).
    ///   - Pending entries are awaiting review; the accountant should
    ///     Approve or Reject, not delete.
    ///   - Reversed entries are already cancelled by a reversing entry;
    ///     deleting them would break the audit trail.
    ///
    /// Returns true if the entry was deleted, false if it didn't exist.
    /// Throws InvalidOperationException if the entry exists but is not
    /// in draft state.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid entryId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 1) Status check first — fail fast before doing any DELETE.
            var status = await conn.QuerySingleOrDefaultAsync<string?>(@"
                SELECT status FROM journal_entries WHERE id = @id;",
                new { id = entryId }, tx);
            if (status is null) return false;
            if (status != "draft")
                throw new InvalidOperationException(
                    $"لا يمكن حذف قيد بحالة '{status}'. الحذف مسموح فقط للمسودات.");

            // 2) Delete the lines first (defensive — there's no ON DELETE
            //    CASCADE on the FK, so this would orphan the lines if we
            //    only deleted the header).
            await conn.ExecuteAsync(
                "DELETE FROM journal_lines WHERE journal_entry_id = @id;",
                new { id = entryId }, tx);

            // 3) Delete the header.
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
        Guid? created_by, DateTime created_at, DateTime? posted_at);

    private record JournalLineRow(
        Guid id, Guid journal_entry_id, Guid account_id, decimal debit, decimal credit,
        string? description, int line_number);

    private record AccountRow(Guid id, string account_type, string nature);
}
