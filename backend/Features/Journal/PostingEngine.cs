using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Journal;

/// <summary>
/// Posting Engine — the heart of the accounting logic.
///
/// Implements the "Nature Logic":
///   - Every account has a TYPE (Asset, Liability, Equity, Revenue, Expense)
///   - Every account has a NATURE (Debit, Credit)
///   - When a journal line is recorded as "debit" but the account is naturally
///     a Credit-balance account (e.g. contra-asset like Accumulated Depreciation),
///     the amount is recorded as the OPPOSITE side.
///   - This ensures that **A = L + E** (accounting equation) is always satisfied:
///     total debits MUST equal total credits in every posted entry.
///
///  Examples:
///    - Debit 1000 to "Cash" (Asset, Debit nature)  → Debit increases the account
///    - Debit 500 to "Accumulated Depreciation" (Asset, Credit nature) → records as Credit
/// </summary>
public class PostingEngine
{
    private readonly IDbConnectionFactory _db;

    public PostingEngine(IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// Posts a draft journal entry. This is the only method that mutates account balances.
    ///
    /// Steps (all inside a single DB transaction; full rollback on any failure):
    ///   1. Load the entry and its lines.
    ///   2. Verify total debit == total credit (the "القيد غير متوازن" guard).
    ///   3. For each line, apply the Nature Logic to compute the net change to the account.
    ///   4. Update each account's balance.
    ///   5. Mark the entry as `posted` and stamp `posted_at`.
    ///
    /// Throws <see cref="InvalidOperationException"/> with an Arabic message on any failure.
    /// </summary>
    /// <param name="entryId">The id of the draft entry to post.</param>
    /// <returns>The posted entry with all lines and the new status.</returns>
    public async Task<JournalEntryDto> PostAsync(Guid entryId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        try
        {
            // Load entry
            var entry = await conn.QuerySingleOrDefaultAsync<JournalEntryRow>(@"
                SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, created_by, created_at, posted_at
                FROM journal_entries WHERE id = @id;",
                new { id = entryId }, tx);

            if (entry is null) throw new InvalidOperationException("Entry not found");
            if (entry.status == "posted") throw new InvalidOperationException("Entry already posted");
            if (entry.status == "reversed") throw new InvalidOperationException("Entry is reversed");

            // Load lines
            var lines = (await conn.QueryAsync<JournalLineRow>(@"
                SELECT id, journal_entry_id, account_id, debit, credit, description, line_number
                FROM journal_lines WHERE journal_entry_id = @id ORDER BY line_number;",
                new { id = entryId }, tx)).ToList();

            if (lines.Count == 0) throw new InvalidOperationException("Entry has no lines");

            // Validate balance: total debit == total credit
            var totalDebit = lines.Sum(l => l.debit);
            var totalCredit = lines.Sum(l => l.credit);
            if (totalDebit != totalCredit)
                throw new InvalidOperationException(
                    $"القيد غير متوازن: إجمالي المدين = {totalDebit:N2}, إجمالي الدائن = {totalCredit:N2}");

            // Update account balances based on Nature Logic.
            // This is the single place where the accounting equation is enforced.
            foreach (var line in lines)
            {
                var account = await conn.QuerySingleOrDefaultAsync<AccountRow>(@"
                    SELECT id, account_type, nature FROM accounts WHERE id = @id;",
                    new { id = line.account_id }, tx);

                if (account is null)
                    throw new InvalidOperationException($"Account {line.account_id} not found");

                // Nature Logic:
                //   Debit-nature accounts (Asset, Expense by default):
                //     line.debit  -> balance increases
                //     line.credit -> balance decreases
                //   Credit-nature accounts (Liability, Equity, Revenue by default):
                //     line.debit  -> balance decreases
                //     line.credit -> balance increases
                //   Contra accounts (e.g. Accumulated Depreciation) override the default
                //   by setting their stored `nature` to the opposite side.
                var netChange = account.nature == "Debit"
                    ? line.debit - line.credit      // Debit-nature: +debit, -credit
                    : line.credit - line.debit;     // Credit-nature: +credit, -debit

                await conn.ExecuteAsync(@"
                    UPDATE accounts SET balance = balance + @netChange WHERE id = @id;",
                    new { netChange, id = account.id }, tx);
            }

            // Mark as posted
            await conn.ExecuteAsync(@"
                UPDATE journal_entries SET status = 'posted', posted_at = NOW() WHERE id = @id;",
                new { id = entryId }, tx);

            tx.Commit();

            return await GetByIdAsync(entryId) ?? throw new InvalidOperationException("Failed to load posted entry");
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Computes the correct (debit, credit) pair for a given amount, honoring the account's nature.
    ///
    /// The Rules Engine uses this when generating entries from JSON rules: the rule says
    /// "debit 1000 to account 1000", and this method decides whether the line should be
    /// `(debit=1000, credit=0)` or `(debit=0, credit=1000)` based on whether the account
    /// is naturally a Debit-balance or Credit-balance account.
    ///
    /// Rule:
    ///   - If account nature matches the requested side, the amount goes on the requested side.
    ///   - Otherwise, the amount is mirrored to the opposite side.
    /// </summary>
    /// <param name="accountNature">The account's stored nature, "Debit" or "Credit".</param>
    /// <param name="requestedNature">The side the rule wants to place the amount on, "Debit" or "Credit".</param>
    /// <param name="amount">The amount to place. Always non-negative.</param>
    /// <returns>A tuple (debit, credit) where exactly one is non-zero.</returns>
    public (decimal debit, decimal credit) ComputePlacement(string accountNature, string requestedNature, decimal amount)
    {
        // If requested nature matches account nature → straightforward
        // If opposite → reverse placement
        if (accountNature == requestedNature)
        {
            return requestedNature == "Debit" ? (amount, 0) : (0, amount);
        }
        else
        {
            return requestedNature == "Debit" ? (0, amount) : (amount, 0);
        }
    }

    public async Task<JournalEntryDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var entry = await conn.QuerySingleOrDefaultAsync<JournalEntryRow>(@"
            SELECT id, company_id, entry_number, entry_date, narration, status, source, rule_id, created_by, created_at, posted_at
            FROM journal_entries WHERE id = @id;",
            new { id });
        if (entry is null) return null;

        var lines = (await conn.QueryAsync<JournalLineWithAccountRow>(@"
            SELECT jl.id, jl.journal_entry_id, jl.account_id, a.code AS account_code, a.name AS account_name,
                   jl.debit, jl.credit, jl.description, jl.line_number
            FROM journal_lines jl
            JOIN accounts a ON a.id = jl.account_id
            WHERE jl.journal_entry_id = @id
            ORDER BY jl.line_number;",
            new { id })).ToList();

        return new JournalEntryDto(
            entry.id, entry.company_id, entry.entry_number, entry.entry_date, entry.narration,
            entry.status, entry.source, entry.rule_id, entry.created_by, entry.created_at, entry.posted_at,
            lines.Select(l => new JournalLineDto(
                l.id, l.account_id, l.account_code, l.account_name,
                l.debit, l.credit, l.description, l.line_number)).ToList()
        );
    }

    private record JournalEntryRow(
        Guid id, Guid company_id, string entry_number, DateTime entry_date, string? narration,
        string status, string? source, Guid? rule_id, Guid? created_by,
        DateTime created_at, DateTime? posted_at);

    private record JournalLineRow(
        Guid id, Guid journal_entry_id, Guid account_id, decimal debit, decimal credit,
        string? description, int line_number);

    private record JournalLineWithAccountRow(
        Guid id, Guid journal_entry_id, Guid account_id, string account_code, string account_name,
        decimal debit, decimal credit, string? description, int line_number);

    private record AccountRow(Guid id, string account_type, string nature);
}
