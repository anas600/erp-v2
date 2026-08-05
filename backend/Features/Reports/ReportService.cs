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

        var company = await conn.QuerySingleAsync<string>(
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
            companyId, company, asOfDate, lines, totalDebit, totalCredit,
            Math.Abs(totalDebit - totalCredit) < 0.01m
        );
    }

    public async Task<IncomeStatementReport> GetIncomeStatementAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        using var conn = _db.CreateConnection();

        var company = await conn.QuerySingleAsync<string>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        // Sum movements (debit - credit) for each account over the period
        var movements = await conn.QueryAsync<IncomeMovementRow>(@"
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
            companyId, company, fromDate, toDate,
            revenues, expenses, totalRevenue, totalExpense,
            totalRevenue - totalExpense
        );
    }

    public async Task<BalanceSheetReport> GetBalanceSheetAsync(Guid companyId, DateTime? asOf = null)
    {
        var asOfDate = asOf ?? DateTime.UtcNow;
        using var conn = _db.CreateConnection();

        var company = await conn.QuerySingleAsync<string>(
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
            companyId, company, asOfDate,
            assets, liabilities, equity,
            totalAssets, totalLiabilities, totalEquity,
            Math.Abs(totalAssets - (totalLiabilities + totalEquity)) < 0.01m
        );
    }

    private record TrialBalanceRow(string code, string name, string account_type, string nature, decimal balance);
    private record BalanceRow(string code, string name, string account_type, decimal balance);
    private record IncomeMovementRow(string code, string name, string account_type, decimal net);

    /// <summary>
    /// General Ledger (دفتر الأستاذ) for one account, in a date range.
    ///
    /// Returns every POSTED journal line that touched the account
    /// between from..to (inclusive), plus a running balance. The
    /// opening balance is the sum of all postings BEFORE the from
    /// date so the running balance at the top of the period is
    /// correct.
    ///
    /// Drafts and pending entries are excluded — they don't affect
    /// the books yet. Reversed entries are also excluded (their
    /// reversing counterpart already undoes them).
    ///
    /// Sign convention: the running balance is in the account's
    /// natural sign — positive for debit-nature accounts means a
    /// debit balance, negative means a credit balance. The
    /// frontend is expected to display the natural balance
    /// without an explicit "DR/CR" suffix.
    /// </summary>
    public async Task<GeneralLedgerReport?> GetGeneralLedgerAsync(
        Guid companyId, Guid accountId, DateTime from, DateTime to)
    {
        using var conn = _db.CreateConnection();

        // Look up the account (and its nature) and the company name.
        var account = await conn.QuerySingleOrDefaultAsync<(string code, string name, string nature)>(@"
            SELECT code, name, nature FROM accounts
            WHERE id = @id AND company_id = @companyId;",
            new { id = accountId, companyId });

        if (account.code is null) return null;

        var company = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        // Opening balance: sum of (debit - credit) for the account
        // BEFORE the from date, for posted non-reversed entries.
        // For credit-nature accounts, multiply by -1 to get the
        // natural balance sign.
        var openingRaw = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(jl.debit - jl.credit), 0)
            FROM journal_lines jl
            JOIN journal_entries je ON je.id = jl.journal_entry_id
            WHERE je.company_id = @companyId
              AND je.status = 'posted'
              AND jl.account_id = @accountId
              AND je.entry_date < @from;",
            new { companyId, accountId, from });
        var isDebitNature = account.nature?.Equals("Debit", StringComparison.OrdinalIgnoreCase) ?? true;
        var openingBalance = isDebitNature ? (openingRaw ?? 0) : -(openingRaw ?? 0);

        // Period transactions
        var lines = (await conn.QueryAsync<GeneralLedgerLineRow>(@"
            SELECT je.id AS entry_id, je.entry_number, je.entry_date,
                   je.narration, je.source, je.entry_number AS reference,
                   jl.debit, jl.credit
            FROM journal_lines jl
            JOIN journal_entries je ON je.id = jl.journal_entry_id
            WHERE je.company_id = @companyId
              AND je.status = 'posted'
              AND jl.account_id = @accountId
              AND je.entry_date BETWEEN @from AND @to
            ORDER BY je.entry_date ASC, je.created_at ASC;",
            new { companyId, accountId, from, to })).ToList();

        // Compute running balance as we walk the lines
        var entries = new List<GeneralLedgerEntry>();
        decimal running = openingBalance;
        decimal totalDebit = 0, totalCredit = 0;
        foreach (var l in lines)
        {
            totalDebit += l.debit;
            totalCredit += l.credit;
            // Running balance in natural sign:
            //   raw delta = debit - credit
            //   For debit-nature accounts: +debit, -credit → running += raw
            //   For credit-nature accounts: opposite
            var raw = l.debit - l.credit;
            running += isDebitNature ? raw : -raw;

            entries.Add(new GeneralLedgerEntry(
                l.entry_id, l.entry_number, l.entry_date,
                l.narration, l.source, l.reference,
                l.debit, l.credit, running));
        }

        return new GeneralLedgerReport(
            companyId, company ?? "", accountId, account.code, account.name,
            account.nature ?? "Debit",
            from, to, openingBalance, totalDebit, totalCredit, running, entries);
    }

    /// <summary>
    /// Customer Aging Report (أعمار المدينين).
    ///
    /// For each customer (contact type='customer') in the company,
    /// show their unpaid balance bucketed by age:
    ///   - 0-30 days  : due recently
    ///   - 31-60 days : 1-2 months late
    ///   - 61-90 days : 1 quarter late
    ///   - 91+ days   : severely overdue
    ///
    /// Implementation:
    ///   - For each posted sales invoice, calculate age as
    ///     asOfDate - invoice_date.
    ///   - Sum the (total - amount_paid) per customer per bucket.
    ///     Since we don't yet track partial payments explicitly,
    ///     we treat the full invoice total as outstanding.
    ///   - Sub-ledger detail accounts (level 4) are included in
    ///     the customer rollup via account_contact_links.
    /// </summary>
    public async Task<CustomerAgingReport> GetCustomerAgingAsync(Guid companyId, DateTime asOfDate)
    {
        using var conn = _db.CreateConnection();

        // For now: each posted sales invoice is "outstanding"
        // at its full total. We bucket by invoice age.
        // Future enhancement: subtract receipts linked to invoices.
        // FIX 2026-08-05: cast i.invoice_date to date too — Postgres rejects
        // `date - timestamp` (type mismatch). date - date returns integer days.
        var rows = await conn.QueryAsync<CustomerAgingRow>(@"
            SELECT
                c.id AS contact_id,
                c.code AS contact_code,
                c.name AS contact_name,
                i.invoice_date,
                i.total AS outstanding,
                (@asOfDate::date - i.invoice_date::date)::int AS days_overdue
            FROM invoices i
            JOIN contacts c ON c.id = i.contact_id
            WHERE i.company_id = @companyId
              AND c.type = 'customer'
              AND i.status = 'posted'
              AND i.invoice_date <= @asOfDate
            ORDER BY c.name, i.invoice_date;",
            new { companyId, asOfDate });

        // Bucket the invoices
        var customerMap = new Dictionary<Guid, CustomerAgingLine>();
        foreach (var r in rows)
        {
            if (!customerMap.TryGetValue(r.contact_id, out var line))
            {
                line = new CustomerAgingLine(
                    r.contact_id, r.contact_code, r.contact_name,
                    new decimal[4], // buckets
                    0m                // total
                );
                customerMap[r.contact_id] = line;
            }

            // Sum the existing bucketed amounts (immutable record)
            var buckets = line.Buckets.ToArray();
            int idx = r.days_overdue switch
            {
                <= 30 => 0,
                <= 60 => 1,
                <= 90 => 2,
                _ => 3
            };
            buckets[idx] += r.outstanding;
            customerMap[r.contact_id] = line with { Buckets = buckets, Total = line.Total + r.outstanding };
        }

        var lines = customerMap.Values.OrderByDescending(l => l.Total).ToList();
        var totals = new decimal[4];
        decimal grandTotal = 0;
        foreach (var l in lines)
        {
            for (int i = 0; i < 4; i++) totals[i] += l.Buckets[i];
            grandTotal += l.Total;
        }

        return new CustomerAgingReport(companyId, asOfDate, lines, totals, grandTotal);
    }

    /// <summary>
    /// Supplier Aging Report (أعمار الدائنين).
    /// Symmetric to customer aging: outstanding posted purchase
    /// invoices, bucketed by age.
    /// </summary>
    public async Task<SupplierAgingReport> GetSupplierAgingAsync(Guid companyId, DateTime asOfDate)
    {
        using var conn = _db.CreateConnection();
        // FIX 2026-08-05: cast i.invoice_date to date too — same date-timestamp
        // mismatch fix as GetCustomerAgingAsync.
        var rows = await conn.QueryAsync<SupplierAgingRow>(@"
            SELECT
                c.id AS contact_id,
                c.code AS contact_code,
                c.name AS contact_name,
                i.invoice_date,
                i.total AS outstanding,
                (@asOfDate::date - i.invoice_date::date)::int AS days_overdue
            FROM invoices i
            JOIN contacts c ON c.id = i.contact_id
            WHERE i.company_id = @companyId
              AND c.type = 'supplier'
              AND i.status = 'posted'
              AND i.invoice_date <= @asOfDate
            ORDER BY c.name, i.invoice_date;",
            new { companyId, asOfDate });

        var supplierMap = new Dictionary<Guid, SupplierAgingLine>();
        foreach (var r in rows)
        {
            if (!supplierMap.TryGetValue(r.contact_id, out var line))
            {
                line = new SupplierAgingLine(
                    r.contact_id, r.contact_code, r.contact_name,
                    new decimal[4], 0m);
                supplierMap[r.contact_id] = line;
            }
            var buckets = line.Buckets.ToArray();
            int idx = r.days_overdue switch
            {
                <= 30 => 0,
                <= 60 => 1,
                <= 90 => 2,
                _ => 3
            };
            buckets[idx] += r.outstanding;
            supplierMap[r.contact_id] = line with { Buckets = buckets, Total = line.Total + r.outstanding };
        }

        var lines = supplierMap.Values.OrderByDescending(l => l.Total).ToList();
        var totals = new decimal[4];
        decimal grandTotal = 0;
        foreach (var l in lines)
        {
            for (int i = 0; i < 4; i++) totals[i] += l.Buckets[i];
            grandTotal += l.Total;
        }

        return new SupplierAgingReport(companyId, asOfDate, lines, totals, grandTotal);
    }

    private record GeneralLedgerLineRow(
        Guid entry_id, string entry_number, DateTime entry_date,
        string? narration, string? source, string? reference,
        decimal debit, decimal credit);

    private record CustomerAgingRow(
        Guid contact_id, string contact_code, string contact_name,
        DateTime invoice_date, decimal outstanding, int days_overdue);

    private record SupplierAgingRow(
        Guid contact_id, string contact_code, string contact_name,
        DateTime invoice_date, decimal outstanding, int days_overdue);
}
