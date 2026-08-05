namespace ErpV2.Features.Reports;

public record TrialBalanceLine(
    string Code,
    string Name,
    string AccountType,
    string Nature,
    decimal DebitBalance,
    decimal CreditBalance
);

public record TrialBalanceReport(
    Guid CompanyId,
    string CompanyName,
    DateTime AsOfDate,
    List<TrialBalanceLine> Lines,
    decimal TotalDebit,
    decimal TotalCredit,
    bool Balanced
);

public record IncomeStatementLine(
    string Code,
    string Name,
    decimal Amount
);

public record IncomeStatementReport(
    Guid CompanyId,
    string CompanyName,
    DateTime FromDate,
    DateTime ToDate,
    List<IncomeStatementLine> Revenues,
    List<IncomeStatementLine> Expenses,
    decimal TotalRevenue,
    decimal TotalExpense,
    decimal NetIncome
);

public record BalanceSheetLine(
    string Code,
    string Name,
    decimal Amount
);

public record BalanceSheetReport(
    Guid CompanyId,
    string CompanyName,
    DateTime AsOfDate,
    List<BalanceSheetLine> Assets,
    List<BalanceSheetLine> Liabilities,
    List<BalanceSheetLine> Equity,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal TotalEquity,
    bool Balanced
);

/// <summary>
/// One transaction in the General Ledger for a specific account.
/// We expose enough fields for the user to drill from the
/// trial balance into "what makes up this balance".
/// </summary>
public record GeneralLedgerEntry(
    Guid EntryId,
    string EntryNumber,
    DateTime EntryDate,
    string? Narration,
    string? Source,            // manual | rule:{id} | invoice | reverse:{id}
    string? Reference,         // entry_number of the source entry (e.g. reversed)
    decimal Debit,
    decimal Credit,
    decimal RunningBalance     // running balance after this transaction
);

/// <summary>
/// The full General Ledger report for one account. Includes the
/// opening balance (sum of all postings before the from-date) so
/// the running balance in the lines makes sense.
/// </summary>
public record GeneralLedgerReport(
    Guid CompanyId,
    string CompanyName,
    Guid AccountId,
    string AccountCode,
    string AccountName,
    string AccountNature,        // Debit | Credit — determines normal balance
    DateTime FromDate,
    DateTime ToDate,
    decimal OpeningBalance,      // balance at the start of the period
    decimal TotalDebit,          // debits in the period
    decimal TotalCredit,         // credits in the period
    decimal ClosingBalance,      // balance at the end of the period
    List<GeneralLedgerEntry> Entries
);

/// <summary>
/// Customer Aging line: per-customer outstanding balance bucketed
/// by age. Buckets are 0-30, 31-60, 61-90, 91+ days.
/// </summary>
public record CustomerAgingLine(
    Guid ContactId,
    string ContactCode,
    string ContactName,
    decimal[] Buckets,   // [0-30, 31-60, 61-90, 91+]
    decimal Total
);

public record CustomerAgingReport(
    Guid CompanyId,
    DateTime AsOfDate,
    List<CustomerAgingLine> Lines,
    decimal[] Totals,
    decimal GrandTotal
);

public record SupplierAgingLine(
    Guid ContactId,
    string ContactCode,
    string ContactName,
    decimal[] Buckets,
    decimal Total
);

public record SupplierAgingReport(
    Guid CompanyId,
    DateTime AsOfDate,
    List<SupplierAgingLine> Lines,
    decimal[] Totals,
    decimal GrandTotal
);
