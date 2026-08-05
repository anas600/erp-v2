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
    ///
    /// Each DELETE is its own statement. We don't use a single
    /// transaction because the four DELETEs are independent
    /// (each table is its own boundary) and a partial cleanup
    /// (lines deleted, entries not) is recoverable: the user
    /// just runs the endpoint again. A single tx with multiple
    /// statements had reliability issues on Render free tier
    /// (intermittent 500s with empty bodies), so we go with
    /// the simpler, more robust approach here.
    /// </summary>
    public async Task<CleanupResult> CleanupAllTransactionsAsync()
    {
        using var conn = _db.CreateConnection();

        // Order matters: child rows first, then parents.
        // (lines before their parents, so the FK constraint
        // is satisfied.)

        var journalLinesDeleted = await conn.ExecuteAsync(
            "DELETE FROM journal_lines;");

        var journalEntriesDeleted = await conn.ExecuteAsync(
            "DELETE FROM journal_entries;");

        var invoiceLinesDeleted = await conn.ExecuteAsync(
            "DELETE FROM invoice_lines;");

        var invoicesDeleted = await conn.ExecuteAsync(
            "DELETE FROM invoices;");

        // Reset every account balance to zero. We do NOT
        // touch the chart of accounts itself — just the
        // running totals. After the cleanup, opening
        // balance for every account is 0.00.
        var accountsReset = await conn.ExecuteAsync(
            "UPDATE accounts SET balance = 0;");

        return new CleanupResult(
            JournalLinesDeleted: journalLinesDeleted,
            JournalEntriesDeleted: journalEntriesDeleted,
            InvoiceLinesDeleted: invoiceLinesDeleted,
            InvoicesDeleted: invoicesDeleted,
            AccountsReset: accountsReset,
            CleanedAt: DateTime.UtcNow
        );
    }

    /// <summary>
    /// Sprint 26 — full demo reset. Compared to CleanupAllTransactionsAsync,
    /// this goes further:
    ///   - Wipes receipt_vouchers and payment_vouchers (so the new
    ///     atomic settlement flow can re-run cleanly).
    ///   - Wipes intercompany_pairs (Sprint 24) so a re-seed doesn't
    ///     reference a deleted sister invoice.
    ///   - Wipes account_contact_links and any L4 sub-ledger accounts
    ///     the user created — so seed can re-create them.
    ///   - Re-opens all fiscal periods (Sprint 25) so the seeder
    ///     can post invoices into 2026.
    ///
    /// The operation is wrapped in a single transaction so a failure
    /// rolls back cleanly (no half-cleaned state).
    /// </summary>
    public async Task<CleanupDataResult> CleanupDataAsync()
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 1) journal_lines and journal_entries (lines first — FK)
            await conn.ExecuteAsync("TRUNCATE TABLE journal_lines CASCADE;", tx);
            await conn.ExecuteAsync("TRUNCATE TABLE journal_entries CASCADE;", tx);

            // 2) receipt_vouchers and payment_vouchers
            await conn.ExecuteAsync("TRUNCATE TABLE receipt_vouchers CASCADE;", tx);
            await conn.ExecuteAsync("TRUNCATE TABLE payment_vouchers CASCADE;", tx);

            // 3) invoice_lines and invoices
            await conn.ExecuteAsync("TRUNCATE TABLE invoice_lines CASCADE;", tx);
            await conn.ExecuteAsync("TRUNCATE TABLE invoices CASCADE;", tx);

            // 4) intercompany_pairs (Sprint 24 — they reference deleted invoices)
            await conn.ExecuteAsync("TRUNCATE TABLE intercompany_pairs CASCADE;", tx);

            // 5) account_contact_links and the L4 sub-ledger accounts
            //    we created in previous demos. (TRUNCATE ... CASCADE on the
            //    link table would also wipe the L4 accounts, but we do it
            //    explicitly so the count is informative.)
            await conn.ExecuteAsync("DELETE FROM account_contact_links;", tx);
            await conn.ExecuteAsync("DELETE FROM accounts WHERE level = 4;", tx);

            // 6) Reset every account balance to 0 (including the L3
            //    control accounts that survived the level=4 delete).
            await conn.ExecuteAsync("UPDATE accounts SET balance = 0;", tx);

            // 7) Re-open all fiscal periods (Sprint 25 — they were
            //    left open by default; this is a belt-and-suspenders
            //    in case the user closed one).
            await conn.ExecuteAsync("UPDATE fiscal_periods SET is_closed = false;", tx);

            tx.Commit();

            // After commit, count what's left so the result is informative.
            var l3Accounts = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM accounts WHERE level = 3;");
            var l4Accounts = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM accounts WHERE level = 4;");

            return new CleanupDataResult(
                CleanedAt: DateTime.UtcNow,
                RemainingL3Accounts: l3Accounts,
                RemainingL4Accounts: l4Accounts,
                Message: "تم تنظيف جميع البيانات. الحسابات الهيكلية (L1/L2/L3) محفوظة.");
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

public record CleanupDataResult(
    DateTime CleanedAt,
    long RemainingL3Accounts,
    long RemainingL4Accounts,
    string Message
);
