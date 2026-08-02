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
            SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, created_by, created_at, posted_at
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

    public async Task<JournalEntryDto?> GetByIdAsync(Guid id) => await _posting.GetByIdAsync(id);

    public async Task<JournalEntryDto> CreateDraftAsync(CreateJournalEntryRequest req, Guid? createdBy)
    {
        if (req.Lines.Count == 0)
            throw new InvalidOperationException("Entry must have at least one line");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var entryId = Guid.NewGuid();
            var entryNumber = await GenerateEntryNumberAsync(req.CompanyId, conn, tx);

            await conn.ExecuteAsync(@"
                INSERT INTO journal_entries (id, company_id, entry_number, entry_date, narration, status, source, created_by)
                VALUES (@id, @companyId, @entryNumber, @entryDate, @narration, 'draft', 'manual', @createdBy);",
                new
                {
                    id = entryId,
                    companyId = req.CompanyId,
                    entryNumber,
                    entryDate = req.EntryDate,
                    narration = req.Narration,
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
                SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, created_by, created_at, posted_at
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

            await conn.ExecuteAsync(@"
                INSERT INTO journal_entries (id, company_id, entry_number, entry_date, narration, status, source, created_by, posted_at)
                VALUES (@id, @companyId, @entryNumber, @entryDate, @narration, 'posted', @source, @createdBy, NOW());",
                new
                {
                    id = newEntryId,
                    companyId = entry.company_id,
                    entryNumber = newEntryNumber,
                    entryDate = DateTime.UtcNow.Date,
                    narration = $"عكس قيد رقم {entry.entry_number}",
                    source = $"reverse:{entry.id}",
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
        string status, string? source, Guid? rule_id, Guid? created_by,
        DateTime created_at, DateTime? posted_at);

    private record JournalLineRow(
        Guid id, Guid journal_entry_id, Guid account_id, decimal debit, decimal credit,
        string? description, int line_number);

    private record AccountRow(Guid id, string account_type, string nature);
}
