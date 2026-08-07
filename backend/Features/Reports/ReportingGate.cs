using Dapper;

namespace ErpV2.Features.Reports;

/// <summary>
/// Sprint 32 — the reporting gate. Every financial report (IS, BS,
/// CF, aging, project P&L) MUST pass through this gate before
/// rendering. The rule is simple: the trial balance must balance.
///
/// If TB doesn't balance, reports return an Arabic error explaining
/// the situation. This is the accountant's safety net: the books
/// must balance before any "official" financial statement can be
/// produced.
///
/// Why:
///   - A = L + E is the fundamental accounting equation.
///   - If TB doesn't balance (debit total ≠ credit total), then
///     every downstream report is mathematically unreliable.
///   - The accountant can fix the unbalance BEFORE showing clients
///     or filing reports.
/// </summary>
public class ReportingGate
{
    private readonly ErpV2.Common.IDbConnectionFactory _db;

    public ReportingGate(ErpV2.Common.IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// Checks if the trial balance balances for the given company
    /// as of the given date. Returns the difference and whether it's
    /// within tolerance (0.01 LYD for rounding).
    /// </summary>
    public async Task<TrialBalanceCheck> CheckBalanceAsync(Guid companyId, DateTime asOf)
    {
        using var conn = _db.CreateConnection();

        // Sum ALL movements (debit - credit) grouped by L3.
        // The total of all (debit - credit) should be ZERO.
        // (Each posted JE is internally balanced, so the sum of all
        // movements across all accounts must also be zero.)
        var totalDebit = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(jl.debit), 0)
            FROM journal_lines jl
            JOIN journal_entries je ON je.id = jl.journal_entry_id
            JOIN accounts a ON a.id = jl.account_id
            WHERE a.company_id = @companyId
              AND a.level = 3
              AND je.status = 'posted'
              AND je.entry_date <= @asOf;",
            new { companyId, asOf }) ?? 0m;

        var totalCredit = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(jl.credit), 0)
            FROM journal_lines jl
            JOIN journal_entries je ON je.id = jl.journal_entry_id
            JOIN accounts a ON a.id = jl.account_id
            WHERE a.company_id = @companyId
              AND a.level = 3
              AND je.status = 'posted'
              AND je.entry_date <= @asOf;",
            new { companyId, asOf }) ?? 0m;

        var difference = totalDebit - totalCredit;
        var isBalanced = Math.Abs(difference) < 0.01m;

        return new TrialBalanceCheck(totalDebit, totalCredit, difference, isBalanced);
    }
}

public record TrialBalanceCheck(decimal TotalDebit, decimal TotalCredit, decimal Difference, bool IsBalanced);
