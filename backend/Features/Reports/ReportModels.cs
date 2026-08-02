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
