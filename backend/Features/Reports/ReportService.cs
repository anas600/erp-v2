using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Reports;

public class ReportService
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<ReportService> _log;

    public ReportService(IDbConnectionFactory db, ILogger<ReportService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Trial balance. `level` filter:
    ///   3 = L3 only (control / general accounts) — DEFAULT for the
    ///       classic trial balance view
    ///   4 = L3 + L4 sub-ledgers (expanded view for sub-ledger rollup)
    /// </summary>
    public async Task<TrialBalanceReport> GetTrialBalanceAsync(Guid companyId, DateTime? asOf = null, int level = 3)
    {
        var asOfDate = asOf ?? DateTime.UtcNow;
        using var conn = _db.CreateConnection();

        var company = await conn.QuerySingleAsync<string>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        var rows = await conn.QueryAsync<TrialBalanceRowEx>(@"
            SELECT id, code, name, account_type, nature, balance, level, parent_id
            FROM accounts
            WHERE company_id = @companyId AND is_active = true
              AND level <= @level
            ORDER BY code;",
            new { companyId, level });

        // Sprint 26 hotfix — same NET-control logic as GetBalanceSheetAsync.
        // A control account (L3) like 1200 carries the GROSS AR
        // (invoices post to it). Sub-ledgers (L4) carry the
        // SETTLEMENT detail (receipts post to them). Without
        // netting, summing both double-counts the receipt amount.
        //
        // The trial balance is a "true" view, so we use the
        // balance-sheet rule: control is shown with its NET
        // (control − Σ sub-ledger), labelled "غير مخصص", and the
        // sub-ledgers are listed separately. Total still equals the
        // original control balance.
        var subLedgerParentIds = rows
            .Where(r => r.level == 4 && r.parent_id.HasValue)
            .Select(r => r.parent_id!.Value)
            .ToHashSet();

        var lines = new List<TrialBalanceLine>();
        decimal totalDebit = 0, totalCredit = 0;

        void AddLine(string code, string name, string type, string nature, decimal balance)
        {
            if (Math.Abs(balance) < 0.01m) return;
            // Sprint 42 — use account_type to determine which side
            // of the T-account the line goes on. The convention
            // (matches the rebuild-balances endpoint):
            //   - Asset / Expense: positive balance → Dr, negative → Cr
            //   - Liability / Equity / Revenue: positive balance → Cr, negative → Dr
            // The "nature" field is kept on the line for the UI but
            // is no longer used for side determination — that broke
            // contra-assets (e.g. 1202 Accum Dep) which are
            // account_type=Asset but nature=Credit.
            decimal debitBal = 0, creditBal = 0;
            var isDebitNormal = type == "Asset" || type == "Expense";
            if (isDebitNormal)
            {
                if (balance >= 0) debitBal = balance;
                else creditBal = -balance;
            }
            else
            {
                if (balance >= 0) creditBal = balance;
                else debitBal = -balance;
            }
            lines.Add(new TrialBalanceLine(code, name, type, nature, debitBal, creditBal));
            totalDebit += debitBal;
            totalCredit += creditBal;
        }

        // L3 controls (with NET adjustment for those that have sub-ledgers)
        //
        // Sprint 42 fix — r.balance is ALREADY the NET (rebuild wrote
        // it as sum of L4 sub-ledgers with natural signs). So we just
        // use r.balance directly — adding subSum again would double-count.
        // Sign convention is already encoded in the stored value:
        //   - Asset/Expense: positive = Dr, negative = Cr
        //   - Liability/Equity/Revenue: positive = Cr, negative = Dr
        foreach (var r in rows.Where(r => r.level == 3))
        {
            decimal bal = r.balance;
            string name = r.name;
            if (subLedgerParentIds.Contains(r.id))
            {
                // r.balance is the NET — use it as-is.
                if (Math.Abs(bal) < 0.01m) bal = 0;
                name = $"{r.name} (غير مخصص)";
            }
            AddLine(r.code, name, r.account_type, r.nature, bal);
        }

        // L4 sub-ledgers (Sprint 33 fix — display only, do NOT add to totals)
        //
        // For accounts that have sub-ledgers, the L3 NET line above
        // already carries the correct group total (L3 + ΣL4 in natural
        // signs). If we added the L4 amounts separately we'd double-
        // count for AR/AP and STILL be wrong for Cash/Bank.
        //
        // Example: AR with 10550 in L3 control and 5800 in 5 L4 sub-
        // ledgers (all negative because receipts are credits):
        //   L3 NET = 10550 + (-5800) = 4750
        //   L4 sum = -5800
        //   L3 NET + L4 sum = 10550 (the gross, not the net)
        //
        // So the right approach: L3 NET contributes to total, L4 is
        // for display only. (Same as the BalanceSheet treatment.)
        //
        // For sub-ledgers that DON'T belong to a control (orphan
        // sub-ledgers from old data) we add them to totals. Detect
        // this by checking if the L4's parent is in subLedgerParentIds.
        foreach (var r in rows.Where(r => r.level == 4))
        {
            // Skip L4 sub-ledgers whose parent is in the L3 NET group
            // (their balance is already represented in the parent L3 NET line).
            if (r.parent_id.HasValue && subLedgerParentIds.Contains(r.parent_id.Value))
            {
                // Display-only: render the line but don't add to totals
                if (Math.Abs(r.balance) < 0.01m) continue;
                decimal d = 0, c = 0;
                // Sprint 42 — use account_type for side (see AddLine).
                var isDebitNormal = r.account_type == "Asset" || r.account_type == "Expense";
                if (isDebitNormal)
                {
                    if (r.balance >= 0) d = r.balance;
                    else c = -r.balance;
                }
                else
                {
                    if (r.balance >= 0) c = r.balance;
                    else d = -r.balance;
                }
                lines.Add(new TrialBalanceLine(r.code, r.name, r.account_type, r.nature, d, c));
                continue;
            }
            // Orphan sub-ledger (parent not in our results) — add to totals
            AddLine(r.code, r.name, r.account_type, r.nature, r.balance);
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

        _log.LogInformation("IS: company={CompanyId} from={From} to={To}", companyId, fromDate, toDate);

        // Sprint 40 — RESTRUCTURE to inner join (not left join) so the
        // date filter on journal_entries actually excludes out-of-range
        // postings. The previous LEFT JOIN placed the date filter in
        // the ON clause, which kept accounts-with-no-matching-JE
        // visible but did NOT filter the matched rows by date when
        // those rows already satisfied the LEFT JOIN's id match.
        //
        // Wait — that's not how SQL works. The ON filter SHOULD
        // exclude out-of-range JEs. But the symptom is that 2020-01-01
        // to 2020-12-31 still shows the full year. So something is
        // bypassing the filter.
        //
        // Hypothesis: PostgreSQL's BETWEEN on TIMESTAMP requires the
        // comparison type to match. Dapper passes DateTime, Npgsql
        // sends it as 'timestamp' which compares correctly. BUT the
        // entry_date column is timestamp without time zone, and
        // the parameter is being sent as DateTime (Kind=Utc). The
        // mismatch might silently coerce the parameter to NULL.
        //
        // Fix: send the parameter as Date only (cast to date), which
        // matches the column semantics and avoids any tz shenanigans.
        var movements = await conn.QueryAsync<IncomeMovementRow>(@"
            SELECT a.code, a.name, a.account_type,
                   COALESCE(SUM(jl.debit - jl.credit), 0) AS net
            FROM accounts a
            INNER JOIN journal_lines jl ON jl.account_id = a.id
            INNER JOIN journal_entries je ON je.id = jl.journal_entry_id
                AND je.status = 'posted'
                AND je.entry_date::date BETWEEN @fromDate AND @toDate
                AND (je.source IS NULL OR je.source <> 'year-end-closing')
            WHERE a.company_id = @companyId AND a.account_type IN ('Revenue', 'Expense')
            GROUP BY a.code, a.name, a.account_type
            ORDER BY a.code;",
            new { companyId, fromDate = fromDate.Date, toDate = toDate.Date });

        foreach (var m in movements)
            _log.LogInformation("IS movement: {Code} {Type} {Net}", m.code, m.account_type, m.net);

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

        // Fetch all balance-sheet accounts including their level + parent.
        // We need `level` to detect sub-ledgers (L4) and `parent_id` to
        // find their control account (L3) — see the de-dup logic below.
        var rows = await conn.QueryAsync<BalanceRowEx>(@"
            SELECT id, code, name, account_type, nature, balance, level, parent_id
            FROM accounts
            WHERE company_id = @companyId AND is_active = true
              AND account_type IN ('Asset', 'Liability', 'Equity')
            ORDER BY code;",
            new { companyId });

        // ============================================================
        // Sprint 26 hotfix — sub-ledger double-counting.
        //
        // The current posting flow is asymmetric:
        //   * Invoices: Dr 1200 (control) / Cr 4000 (revenue).
        //     Postings go to the CONTROL account, not the sub-ledger.
        //   * Receipts/payments: Dr cash / Cr 1200-CUST-001 (sub-ledger).
        //     Postings go to the SUB-LEDGER, not the control.
        //
        // This means the control carries the GROSS AR (invoices),
        // and the sub-ledgers carry the SETTLEMENT detail (receipts).
        // They are NOT the same number.
        //
        // Showing both summed would over-count by the receipt amount.
        // Showing only the control hides the contact-level detail.
        //
        // Fix: present the control with its NET balance
        // (control_balance − Σ sub_ledger_balances) labelled as
        // "غير مخصص" (unallocated). The sub-ledgers are shown
        // individually underneath, carrying the settlement detail.
        //
        // Total = NET control + Σ sub-ledgers = control_balance
        // (no double counting, by construction).
        // ============================================================
        var rowsById = rows.ToDictionary(r => r.id);
        var subLedgerParentIds = rows
            .Where(r => r.level == 4 && r.parent_id.HasValue)
            .Select(r => r.parent_id!.Value)
            .ToHashSet();

        var assets = new List<BalanceSheetLine>();
        var liabilities = new List<BalanceSheetLine>();
        var equity = new List<BalanceSheetLine>();
        decimal totalAssets = 0, totalLiabilities = 0, totalEquity = 0;

        // Get net income for current year to add to equity
        var yearStart = new DateTime(asOfDate.Year, 1, 1);
        var income = await GetIncomeStatementAsync(companyId, yearStart, asOfDate);
        decimal netIncome = income.NetIncome;

        // Process controls first (L3) — show NET balance when they
        // have sub-ledger children, full balance otherwise.
        //
        // The NET formula (Sprint 42 fix): just use r.balance.
        //
        // The rebuild-balances endpoint sets L3 control = sum of its
        // L4 sub-ledger balances (with natural signs for the account
        // type). So r.balance is ALREADY the correct NET — we must
        // NOT add subSum again or we'd double-count.
        //
        // Sign convention (matches the rebuild):
        //   - Asset / Expense accounts: positive balance = Dr magnitude,
        //     negative balance = Cr (contra-asset like 1202)
        //   - Liability / Equity: positive balance = Cr magnitude,
        //     negative = Dr (unusual)
        // The stored value already has the correct sign — we use it
        // as-is for the total (no extra negation by nature).
        foreach (var r in rows.Where(r => r.level == 3))
        {
            decimal amount;
            string displayName = r.name;
            string displayCode = r.code;
            if (subLedgerParentIds.Contains(r.id))
            {
                // r.balance is already the NET (rebuild wrote it
                // as sum of L4 sub-ledgers with natural signs).
                amount = r.balance;
                if (Math.Abs(amount) < 0.01m) amount = 0;
                displayCode = r.code;
                displayName = $"{r.name} (غير مخصص)";
            }
            else
            {
                amount = r.balance;
                if (Math.Abs(amount) < 0.01m) continue; // skip zero rows
            }

            switch (r.account_type)
            {
                case "Asset":
                    // Stored sign already encodes the side: positive
                    // for normal Dr balance, negative for contra-asset
                    // (e.g. 1202 Accum Dep at -30,000 reduces total).
                    var assetAmount = amount;
                    assets.Add(new BalanceSheetLine(displayCode, displayName, assetAmount));
                    totalAssets += assetAmount;
                    break;
                case "Liability":
                    liabilities.Add(new BalanceSheetLine(displayCode, displayName, Math.Abs(amount)));
                    totalLiabilities += Math.Abs(amount);
                    break;
                case "Equity":
                    equity.Add(new BalanceSheetLine(displayCode, displayName, Math.Abs(amount)));
                    totalEquity += Math.Abs(amount);
                    break;
            }
        }

        // Add L4 sub-ledgers as separate lines (Sprint 33 fix).
        //
        // The previous code omitted L4 sub-ledgers entirely, assuming
        // the L3 control NET already includes their effect. That's
        // true for AR/AP but WRONG for Cash/Bank (where the L3
        // control carries no activity). Now we always list each L4
        // sub-ledger separately so the contact-level detail and the
        // cash/bank L4s are both visible on the balance sheet.
        //
        // IMPORTANT: L4 sub-ledgers are DISPLAY-ONLY. They do NOT
        // contribute to the total. The L3 NET line above already
        // carries the correct group total (rebuild wrote L3 = sum
        // of L4 sub-ledgers with natural signs).
        foreach (var r in rows.Where(r => r.level == 4))
        {
            if (Math.Abs(r.balance) < 0.01m) continue; // skip zero rows
            var amount = Math.Abs(r.balance);
            switch (r.account_type)
            {
                case "Asset":
                    assets.Add(new BalanceSheetLine(r.code, r.name, amount));
                    // No total addition — L3 NET already covers it
                    break;
                case "Liability":
                    liabilities.Add(new BalanceSheetLine(r.code, r.name, amount));
                    break;
                case "Equity":
                    equity.Add(new BalanceSheetLine(r.code, r.name, amount));
                    break;
            }
        }

        // Sprint 40 — only add the year-to-date NET line if the
        // books have NOT been year-end-closed. Once the closing
        // entry posts, the net income lives in 3201 (Retained
        // Earnings) and 3202 (Current Year P&L) is cleared — the
        // "صافي الدخل" line would then double-count. We detect the
        // close by checking whether 3202 carries a non-zero balance
        // (the closing credits it; the next period starts at 0).
        var isYearEndClosed = false;
        foreach (var r in rows.Where(r => r.level == 3))
        {
            if (r.code == "3202" && Math.Abs(r.balance) > 0.01m)
            {
                isYearEndClosed = true;
                break;
            }
        }

        if (Math.Abs(netIncome) > 0.01m && !isYearEndClosed)
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
    private record TrialBalanceRowEx(Guid id, string code, string name, string account_type, string nature, decimal balance, int level, Guid? parent_id);
    private record BalanceRow(string code, string name, string account_type, decimal balance);
    private record BalanceRowEx(Guid id, string code, string name, string account_type, string? nature, decimal balance, int level, Guid? parent_id);
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

        // FIX 2026-08-05 (2 bugs):
        //   1. Postgres rejects `date - timestamp` (type mismatch).
        //      Cast both sides to date — `date - date = integer days`.
        //   2. `invoices` has no `contact_id` FK; party is stored as
        //      text (`party_name`). When the user picks a contact from
        //      the catalog, the frontend sets `partyName = c.name`.
        //      Match on (company_id, name, type). Invoices with a
        //      manually-typed party name (no matching contact) are
        //      silently excluded from aging — acceptable for now.
        //
        // FIX 2026-08-05 (Sprint 25 — aging correctness):
        //   `outstanding` is now `total - amount_paid`, not `total`. Before
        //   Sprint 25, every posted invoice was treated as 100% outstanding
        //   even after partial payments. This was the bug the accountant
        //   flagged: "aging overstates the receivable". The bucket key
        //   is still the invoice age (so the buckets are stable across
        //   partial payments), but the amount is the true outstanding.
        // Sprint 30 — aging only includes invoices whose linked JE
        // is POSTED. PENDING/DRAFT JEs (and missing JEs) mean the
        // accountant hasn't approved the entry yet, so there's no
        // financial impact yet. The user explicitly required this —
        // an invoice posted to the journal but not yet approved
        // should NOT show in aging (it's a workflow stage, not a
        // receivable).
        //
        // Join strategy: invoices link to JEs through different paths
        // depending on what created the JE. Sprint 43 fix — broaden
        // the source pattern to cover ALL the seeder-generated sources
        // actually in production:
        //   - 'rule:<UUID>' — rule engine (default path)
        //   - 'invoice:sales' / 'invoice:purchase' — FullYearSeeder direct post
        //   - 'project:billing' — seeder's billing source (used by seeder)
        //   - 'billing' — BillingService source (used when user creates
        //     a billing voucher via the UI) — Sprint 44 fix
        //   - 'manual' — admin manual entry with invoice number in narration
        //   - 'invoice:<UUID>' — future voucher-created invoice journals
        //
        // The original Sprint 41 JOIN was `je.source = 'invoice:' ||
        // i.id::text` (UUID format) which never matched the seeder
        // sources like 'invoice:sales' — that's why the aging report
        // was empty even though CUST-001 had 7 outstanding invoices
        // visible on the contact detail page. The accountant flagged
        // this on 2026-08-12.
        //
        // Sprint 44 fix (hotfix 2): added 'billing' (exact match) for
        // the BillingService path. The user noticed the aging total
        // for CUST-005 stayed at 142K after paying a 25K billing
        // because the billings were never in the aging report in
        // the first place. Now billings are included.
        //
        // We use narration parsing because the actual JE source values
        // vary by event type — the narration always contains the
        // invoice number as the reliable join key.
        var rows = await conn.QueryAsync<CustomerAgingRow>(@"
            SELECT
                c.id AS contact_id,
                c.code AS contact_code,
                c.name AS contact_name,
                i.invoice_date,
                (i.total - i.amount_paid) AS outstanding,
                (@asOfDate::date - i.invoice_date::date)::int AS days_overdue
            FROM invoices i
            JOIN contacts c
              ON c.company_id = i.company_id
             AND c.name = i.party_name
             AND c.type = 'customer'
            LEFT JOIN journal_entries je
              ON je.company_id = i.company_id
             AND je.status = 'posted'
             AND (
                  je.source LIKE 'rule:%'
               OR je.source LIKE 'invoice:%'
               OR je.source LIKE 'project:%'
               OR je.source = 'billing'
               OR je.source = 'manual'
             )
             AND je.narration LIKE '%' || i.invoice_number || '%'
            WHERE i.company_id = @companyId
              AND i.invoice_type = 'sales'
              AND i.status IN ('posted', 'partiallypaid')
              AND (i.total - i.amount_paid) > 0
              AND i.invoice_date <= @asOfDate
              AND je.id IS NOT NULL
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
        // FIX 2026-08-05: same as GetCustomerAgingAsync —
        //   1. date - timestamp → date - date
        //   2. JOIN on party_name (text) instead of non-existent contact_id FK
        //   3. Filter by invoice_type='purchase' (not 'sales')
        //
        // FIX 2026-08-05 (Sprint 25 — aging correctness):
        //   outstanding = total - amount_paid. The bucket key stays the
        //   invoice age; only the amount is the true outstanding.
        // Sprint 30 — see customer aging for the same logic. We
        // exclude invoices whose linked JE is not POSTED (still
        // PENDING or DRAFT), so aging only reflects real financial
        // impact.
        //
        // Sprint 43 — broaden source pattern to cover all seeder sources
        // (see customer aging comment for the full list).
        // Sprint 44 hotfix 2 — also include 'billing' source (the
        // exact string used by BillingService for UI-created billings).
        var rows = await conn.QueryAsync<SupplierAgingRow>(@"
            SELECT
                c.id AS contact_id,
                c.code AS contact_code,
                c.name AS contact_name,
                i.invoice_date,
                (i.total - i.amount_paid) AS outstanding,
                (@asOfDate::date - i.invoice_date::date)::int AS days_overdue
            FROM invoices i
            JOIN contacts c
              ON c.company_id = i.company_id
             AND c.name = i.party_name
             AND c.type = 'supplier'
            LEFT JOIN journal_entries je
              ON je.company_id = i.company_id
             AND je.status = 'posted'
             AND (
                  je.source LIKE 'rule:%'
               OR je.source LIKE 'invoice:%'
               OR je.source LIKE 'project:%'
               OR je.source = 'billing'
               OR je.source = 'manual'
             )
             AND je.narration LIKE '%' || i.invoice_number || '%'
            WHERE i.company_id = @companyId
              AND i.invoice_type = 'purchase'
              AND i.status IN ('posted', 'partiallypaid')
              AND (i.total - i.amount_paid) > 0
              AND i.invoice_date <= @asOfDate
              AND je.id IS NOT NULL
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

    /// <summary>
    /// Sprint 44 — Sub-ledger Schedule (كشف الحسابات التحليلية).
    ///
    /// Returns every L4 sub-ledger under an L3 control account with
    /// its current balance. The reader can verify the L3 control
    /// equals the sum of its L4 sub-ledgers — the standard
    /// reconciliation any auditor will demand.
    ///
    /// Implementation:
    ///   1. Look up the parent (L3) account. Reject if not L3.
    ///   2. Find every L4 child of that parent in the same company.
    ///   3. Pull each child's balance from `accounts.balance`
    ///      (already maintained by rebuild-balances, single source
    ///      of truth). The balance is in the account's natural sign.
    ///   4. Also pull the contact info (when the sub-ledger is
    ///      "1103-CUST-XXX" or "2101-SUPP-XXX") by joining on code
    ///      suffix.
    ///
    /// Note: we use the `accounts.balance` column, not the SUM of
    /// journal_lines. Reason: rebuild-balances (Sprint 41) is the
    /// single source of truth — both the L4 sub-ledger AND the L3
    /// NET use the same value, so they reconcile by construction.
    /// Re-deriving from journal_lines here would be redundant and
    /// could drift if a future change touches one path but not the
    /// other.
    /// </summary>
    public async Task<SubLedgerScheduleReport?> GetSubLedgerScheduleAsync(
        Guid companyId, Guid parentAccountId)
    {
        using var conn = _db.CreateConnection();

        // 1. Look up the parent. We need code, name, level, balance.
        var parent = await conn.QuerySingleOrDefaultAsync<(string code, string name, int level, string account_type, string nature, decimal balance)>(@"
            SELECT code, name, level, account_type, nature, balance
            FROM accounts
            WHERE id = @id AND company_id = @companyId;",
            new { id = parentAccountId, companyId });

        if (parent.code is null) return null;

        if (parent.level != 3)
        {
            // Only L3 controls have sub-ledgers. L1/L2 are pure
            // rollups, L4 is the leaf. For L4 we return null
            // (the frontend should already know not to call this).
            return null;
        }

        var company = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        // 2. Find all L4 children of this parent. The `parent_id`
        //    column is set when the sub-ledger is created (Sprint 26).
        //    We also pull the contact info via the code suffix:
        //      1103-CUST-001 → contacts.code = 'CUST-001'
        //      1103-CASH-001 → no contact (cash on hand)
        //      1102-BANK-001 → no contact (bank account)
        //    We use LIKE 'CUST-%' / 'SUPP-%' / 'CASH-%' / 'BANK-%'
        //    to pick the right contact type.
        var parentPrefix = parent.code; // e.g. "1103"
        var rows = await conn.QueryAsync<SubLedgerRow>(@"
            SELECT
                a.id          AS account_id,
                a.code        AS account_code,
                a.name        AS account_name,
                a.balance     AS balance,
                c.id          AS contact_id,
                c.code        AS contact_code,
                c.name        AS contact_name
            FROM accounts a
            LEFT JOIN contacts c
              ON c.company_id = a.company_id
             AND (
                  (a.code LIKE @custPattern AND c.code = SUBSTRING(a.code FROM LENGTH(@parentPrefix) + 2))
               OR (a.code LIKE @suppPattern AND c.code = SUBSTRING(a.code FROM LENGTH(@parentPrefix) + 2))
             )
            WHERE a.company_id = @companyId
              AND a.parent_id = @parentId
              AND a.level = 4
            ORDER BY a.code;",
            new
            {
                companyId,
                parentId = parentAccountId,
                parentPrefix,
                custPattern = parentPrefix + "-CUST-%",
                suppPattern = parentPrefix + "-SUPP-%"
            });

        var lines = rows.Select(r => new SubLedgerScheduleLine(
            r.account_id, r.account_code, r.account_name,
            r.contact_id, r.contact_code, r.contact_name,
            r.balance
        )).ToList();

        return new SubLedgerScheduleReport(
            companyId, company ?? "", DateTime.UtcNow,
            parentAccountId, parent.code, parent.name,
            parent.account_type, parent.nature, parent.balance,
            lines, lines.Count
        );
    }

    /// <summary>
    /// Sprint 44 — Contact Statement of Account (كشف حساب عميل/مورد).
    ///
    /// Returns every invoice + voucher for a single contact within
    /// a date range, with a running balance. This is the drill-down
    /// view behind the Aging reports: "why does CUST-001 have 95K
    /// outstanding?" → answer is here, line by line.
    ///
    /// The data is pulled from TWO sources (union) and ordered by date:
    ///   1. Invoices (sales or purchase depending on contact type)
    ///   2. Vouchers (receipt for customer, payment for supplier)
    ///
    /// Sign convention (the natural sign for the contact type):
    ///   - For CUSTOMERS: invoice = +Debit (they owe us more),
    ///                     receipt = -Debit (they paid us, so owe less)
    ///   - For SUPPLIERS: invoice = +Credit (we owe them more),
    ///                     payment = -Credit (we paid them, so owe less)
    /// We use the simple (debit - credit) raw delta and flip the
    /// sign for the supplier case.
    /// </summary>
    public async Task<ContactStatementReport?> GetContactStatementAsync(
        Guid companyId, Guid contactId, DateTime from, DateTime to)
    {
        using var conn = _db.CreateConnection();

        // 1. Look up the contact. We need its company, type, code, name.
        var contact = await conn.QuerySingleOrDefaultAsync<(Guid company_id, string code, string name, string type)>(@"
            SELECT company_id, code, name, type FROM contacts
            WHERE id = @id;",
            new { id = contactId });

        if (contact.company_id == Guid.Empty) return null;
        if (contact.company_id != companyId) return null; // cross-company guard

        var isCustomer = contact.type == "customer";
        var invoiceType = isCustomer ? "sales" : "purchase";
        var invoiceDocType = isCustomer ? "فاتورة" : "فاتورة مشتريات";
        var voucherTable = isCustomer ? "receipt_vouchers" : "payment_vouchers";
        var voucherDocType = isCustomer ? "سند قبض" : "سند صرف";

        var company = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT name FROM companies WHERE id = @id;",
            new { id = companyId });

        // 2. Opening balance = sum of (invoice outstanding) - sum of
        //    (vouchers) BEFORE the from date. For customers, invoices
        //    add to what they owe us, receipts reduce it. For suppliers,
        //    purchase invoices add to what we owe them, payments reduce.
        //    Both cases: opening = invoices_outstanding - vouchers_paid
        var openingInvoices = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(total - amount_paid), 0)
            FROM invoices
            WHERE company_id = @companyId
              AND party_name = @partyName
              AND invoice_type = @invoiceType
              AND status IN ('posted', 'partiallypaid', 'paid')
              AND invoice_date < @from;",
            new { companyId, partyName = contact.name, invoiceType, from });
        var openingVouchers = await conn.ExecuteScalarAsync<decimal?>(@"
            SELECT COALESCE(SUM(amount), 0)
            FROM receipt_vouchers
            WHERE company_id = @companyId
              AND contact_id = @contactId
              AND voucher_date < @from
              AND status NOT IN ('draft', 'voided')
            UNION ALL
            SELECT COALESCE(SUM(amount), 0)
            FROM payment_vouchers
            WHERE company_id = @companyId
              AND contact_id = @contactId
              AND voucher_date < @from
              AND status NOT IN ('draft', 'voided');",
            new { companyId, contactId, from });
        // The UNION ALL above returns two rows for a contact. Sum them.
        var opening = (openingInvoices ?? 0) - (openingVouchers ?? 0);

        // 3. Period invoices
        //    NOTE: the invoices table has a `notes` column, not
        //    `description`. We use notes as the description.
        var invoiceLines = new List<(DateTime date, string docType, string docNumber, string? desc, decimal dr, decimal cr)>();
        var invRows = await conn.QueryAsync<(DateTime invoice_date, string invoice_number, string? notes, decimal total, decimal amount_paid, string status)>(@"
            SELECT invoice_date, invoice_number, notes, total, amount_paid, status
            FROM invoices
            WHERE company_id = @companyId
              AND party_name = @partyName
              AND invoice_type = @invoiceType
              AND invoice_date BETWEEN @from AND @to
            ORDER BY invoice_date;",
            new { companyId, partyName = contact.name, invoiceType, from, to });

        foreach (var inv in invRows)
        {
            var outstanding = inv.total - inv.amount_paid;
            // Skip fully-paid invoices in the period — they don't
            // affect the running balance. We show them as 0/0 for
            // readability (the user can still see the invoice happened).
            // Actually no — skip them entirely for the running balance
            // (they would distort the view with 0/0 lines that confuse
            // the reader). If the user wants to see paid invoices,
            // they can extend the report later.
            if (outstanding <= 0 && inv.status == "paid") continue;
            var dr = isCustomer ? outstanding : 0m;
            var cr = isCustomer ? 0m : outstanding;
            invoiceLines.Add((inv.invoice_date, invoiceDocType, inv.invoice_number, inv.notes, dr, cr));
        }

        // 4. Period vouchers
        var voucherLines = new List<(DateTime date, string docType, string docNumber, string? desc, decimal dr, decimal cr)>();
        if (isCustomer)
        {
            var rows = await conn.QueryAsync<(DateTime voucher_date, string voucher_number, string? payment_method, decimal amount, string status)>(@"
                SELECT voucher_date, voucher_number, payment_method, amount, status
                FROM receipt_vouchers
                WHERE company_id = @companyId
                  AND contact_id = @contactId
                  AND voucher_date BETWEEN @from AND @to
                ORDER BY voucher_date;",
                new { companyId, contactId, from, to });
            foreach (var v in rows)
            {
                if (v.status == "draft" || v.status == "voided") continue;
                voucherLines.Add((v.voucher_date, voucherDocType, v.voucher_number, v.payment_method, 0m, v.amount));
            }
        }
        else
        {
            var rows = await conn.QueryAsync<(DateTime voucher_date, string voucher_number, string? payment_method, decimal amount, string status)>(@"
                SELECT voucher_date, voucher_number, payment_method, amount, status
                FROM payment_vouchers
                WHERE company_id = @companyId
                  AND contact_id = @contactId
                  AND voucher_date BETWEEN @from AND @to
                ORDER BY voucher_date;",
                new { companyId, contactId, from, to });
            foreach (var v in rows)
            {
                if (v.status == "draft" || v.status == "voided") continue;
                voucherLines.Add((v.voucher_date, voucherDocType, v.voucher_number, v.payment_method, v.amount, 0m));
            }
        }

        // 5. Merge and order. Invoices before vouchers on the same day
        //    (readers expect "what they were billed for" first, then
        //    "how they paid it").
        var lines = invoiceLines.Concat(voucherLines)
            .OrderBy(l => l.date)
            .ThenBy(l => l.docType)
            .ToList();

        var entries = new List<ContactStatementLine>();
        decimal running = opening;
        decimal totalDr = 0m, totalCr = 0m;
        foreach (var l in lines)
        {
            running += (l.dr - l.cr);
            totalDr += l.dr;
            totalCr += l.cr;
            entries.Add(new ContactStatementLine(
                l.date, l.docType, l.docNumber, l.desc, l.dr, l.cr, running));
        }

        return new ContactStatementReport(
            companyId, company ?? "", contactId, contact.code, contact.name,
            contact.type, from, to, opening, totalDr, totalCr, running, entries);
    }

    private record SubLedgerRow(
        Guid account_id, string account_code, string account_name,
        decimal balance, Guid? contact_id, string? contact_code, string? contact_name);

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
