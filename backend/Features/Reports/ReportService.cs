using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Reports;

public class ReportService
{
    private readonly IDbConnectionFactory _db;

    public ReportService(IDbConnectionFactory db) => _db = db;

    public async Task<TrialBalanceReport> GetTrialBalanceAsync(Guid companyId, DateTime? asOf = null)
    {
        var asOfDate = asOf ?? DateTime.UtcNow;
        using var conn = _db.CreateConnection();

        var company = await conn.QuerySingleAsync<(string name)>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        var rows = await conn.QueryAsync<TrialBalanceRow>(@"
            SELECT code, name, account_type, nature, balance
            FROM accounts
            WHERE company_id = @companyId AND is_active = true
            ORDER BY code;",
            new { companyId });

        var lines = new List<TrialBalanceLine>();
        decimal totalDebit = 0, totalCredit = 0;

        foreach (var r in rows)
        {
            // Trial balance presentation: show positive balance on its nature side
            // If account is Asset/Expense (Debit nature) and has positive balance → Debit side
            // If account is Asset/Expense and has negative balance → Credit side (unusual)
            // Same logic reversed for Liability/Equity/Revenue
            decimal debitBal = 0, creditBal = 0;
            if (r.nature == "Debit")
            {
                if (r.balance >= 0) debitBal = r.balance;
                else creditBal = -r.balance;
            }
            else
            {
                if (r.balance >= 0) creditBal = r.balance;
                else debitBal = -r.balance;
            }

            if (debitBal > 0 || creditBal > 0)
            {
                lines.Add(new TrialBalanceLine(r.code, r.name, r.account_type, r.nature, debitBal, creditBal));
                totalDebit += debitBal;
                totalCredit += creditBal;
            }
        }

        return new TrialBalanceReport(
            companyId, company.name, asOfDate, lines, totalDebit, totalCredit,
            Math.Abs(totalDebit - totalCredit) < 0.01m
        );
    }

    public async Task<IncomeStatementReport> GetIncomeStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        using var conn = _db.CreateConnection();

        var company = await conn.QuerySingleAsync<(string name)>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        // Sum movements (debit - credit) for each account over the period
        var movements = await conn.QueryAsync<(string code, string name, string account_type, decimal net)>(@"
            SELECT a.code, a.name, a.account_type,
                   COALESCE(SUM(jl.debit - jl.credit), 0) AS net
            FROM accounts a
            LEFT JOIN journal_lines jl ON jl.account_id = a.id
            LEFT JOIN journal_entries je ON je.id = jl.journal_entry_id
                AND je.status = 'posted'
                AND je.entry_date BETWEEN @from AND @to
            WHERE a.company_id = @companyId AND a.account_type IN ('Revenue', 'Expense')
            GROUP BY a.code, a.name, a.account_type
            ORDER BY a.code;",
            new { companyId, from = fromDate, to = toDate });

        var revenues = new List<IncomeStatementLine>();
        var expenses = new List<IncomeStatementLine>();
        decimal totalRevenue = 0, totalExpense = 0;

        foreach (var m in movements)
        {
            // Revenue is naturally credit-balance; net debit-credit is negative
            // We display as positive amount on the income statement
            if (m.account_type == "Revenue")
            {
                var amount = Math.Abs(m.net);
                revenues.Add(new IncomeStatementLine(m.code, m.name, amount));
                totalRevenue += amount;
            }
            else // Expense
            {
                var amount = Math.Abs(m.net);
                expenses.Add(new IncomeStatementLine(m.code, m.name, amount));
                totalExpense += amount;
            }
        }

        return new IncomeStatementReport(
            companyId, company.name, fromDate, toDate,
            revenues, expenses, totalRevenue, totalExpense,
            totalRevenue - totalExpense
        );
    }

    public async Task<BalanceSheetReport> GetBalanceSheetAsync(Guid companyId, DateTime? asOf = null)
    {
        var asOfDate = asOf ?? DateTime.UtcNow;
        using var conn = _db.CreateConnection();

        var company = await conn.QuerySingleAsync<(string name)>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        var rows = await conn.QueryAsync<BalanceRow>(@"
            SELECT code, name, account_type, balance
            FROM accounts
            WHERE company_id = @companyId AND is_active = true
              AND account_type IN ('Asset', 'Liability', 'Equity')
            ORDER BY code;",
            new { companyId });

        var assets = new List<BalanceSheetLine>();
        var liabilities = new List<BalanceSheetLine>();
        var equity = new List<BalanceSheetLine>();
        decimal totalAssets = 0, totalLiabilities = 0, totalEquity = 0;

        // Get net income for current year to add to equity
        var yearStart = new DateTime(asOfDate.Year, 1, 1);
        var income = await GetIncomeStatementAsync(companyId, yearStart, asOfDate);
        decimal netIncome = income.NetIncome;

        foreach (var r in rows)
        {
            // For balance sheet, present the absolute balance on its natural side
            var amount = Math.Abs(r.balance);
            switch (r.account_type)
            {
                case "Asset":
                    assets.Add(new BalanceSheetLine(r.code, r.name, amount));
                    totalAssets += amount;
                    break;
                case "Liability":
                    liabilities.Add(new BalanceSheetLine(r.code, r.name, amount));
                    totalLiabilities += amount;
                    break;
                case "Equity":
                    equity.Add(new BalanceSheetLine(r.code, r.name, amount));
                    totalEquity += amount;
                    break;
            }
        }

        if (Math.Abs(netIncome) > 0.01m)
        {
            equity.Add(new BalanceSheetLine("NET", "صافي الدخل (السنة الحالية)", netIncome));
            totalEquity += netIncome;
        }

        return new BalanceSheetReport(
            companyId, company.name, asOfDate,
            assets, liabilities, equity,
            totalAssets, totalLiabilities, totalEquity,
            Math.Abs(totalAssets - (totalLiabilities + totalEquity)) < 0.01m
        );
    }

    private record TrialBalanceRow(string code, string name, string account_type, string nature, decimal balance);
    private record BalanceRow(string code, string name, string account_type, decimal balance);
}
