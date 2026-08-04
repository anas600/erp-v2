using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Admin;

/// <summary>
/// Admin / Maintenance operations for the ERP.
///
/// These endpoints are deliberately SEPARATE from the regular
/// journal/invoice endpoints because they bypass the normal
/// "immutability of posted entries" rules. GAAP/IFRS say you
/// can never delete a posted entry. That's true in production.
/// But when the system is in active development or a demo, you
/// need a way to wipe the slate clean without dropping tables
/// or losing the chart of accounts / products / contacts / rules.
///
/// The full reset is the "demo reset" — it clears all
/// transactions but keeps the setup data (companies, accounts,
/// products, contacts, projects, business rules, users).
///
/// Access is gated on the super_admin claim: the JWT issued by
/// AuthService includes `is_super_admin` (a hard-coded flag in
/// the seed admin user; the system does not allow non-super
/// users to obtain this flag). See Program.cs auth setup.
/// </summary>
public class AdminService
{
    private readonly IDbConnectionFactory _db;

    public AdminService(IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// Wipes all transaction data but preserves setup data:
    ///   - Deletes every journal_line, then every journal_entry
    ///     (in that order — no FK would cascade, so we have to do
    ///     it ourselves; the schema's ON DELETE CASCADE on
    ///     journal_lines is the safety net).
    ///   - Deletes every invoice_line, then every invoice.
    ///   - Resets every accounts.balance back to 0. This is the
    ///     key step: account balances are derived from posted
    ///     entries (PostingEngine updates them on Post, reverses
    ///     on Reverse). If we delete entries without resetting
    ///     balances, the trial balance would be wrong.
    ///
    /// Returns a count of rows deleted per table.
    /// </summary>
    public async Task<CleanupResult> CleanupAllTransactionsAsync()
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // Order matters: child rows first, then parents.
            // If any step fails, the tx rolls back and we leave
            // the system in a consistent state.

            var journalLinesDeleted = await conn.ExecuteAsync(
                "DELETE FROM journal_lines;", tx);

            var journalEntriesDeleted = await conn.ExecuteAsync(
                "DELETE FROM journal_entries;", tx);

            var invoiceLinesDeleted = await conn.ExecuteAsync(
                "DELETE FROM invoice_lines;", tx);

            var invoicesDeleted = await conn.ExecuteAsync(
                "DELETE FROM invoices;", tx);

            // Reset every account balance to zero. We do NOT
            // touch the chart of accounts itself — just the
            // running totals. After the cleanup, opening
            // balance for every account is 0.00.
            var accountsReset = await conn.ExecuteAsync(
                "UPDATE accounts SET balance = 0;", tx);

            // Also clear the FluentMigrator VersionInfo so a
            // fresh migration run from scratch doesn't think
            // the DB is up-to-date. Wait, no — we DON'T want
            // to do that. The migrations should still be
            // marked as applied; we just want a clean data
            // state. The migrations modify schema, not data,
            // and re-running them on a schema that already
            // has the new columns would be a no-op.
            //
            // Leave VersionInfo alone.

            tx.Commit();

            return new CleanupResult(
                JournalLinesDeleted: journalLinesDeleted,
                JournalEntriesDeleted: journalEntriesDeleted,
                InvoiceLinesDeleted: invoiceLinesDeleted,
                InvoicesDeleted: invoicesDeleted,
                AccountsReset: accountsReset,
                CleanedAt: DateTime.UtcNow
            );
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

public record CleanupResult(
    int JournalLinesDeleted,
    int JournalEntriesDeleted,
    int InvoiceLinesDeleted,
    int InvoicesDeleted,
    int AccountsReset,
    DateTime CleanedAt
);
