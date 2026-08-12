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

/// <summary>
/// Sprint 44 — Sub-ledger Schedule (كشف الحسابات التحليلية).
///
/// When the user opens an L3 control account in the General
/// Ledger (e.g. "1103 Accounts Receivable"), the GL shows
/// "no movements" because L3 is not postable — all postings go
/// to L4 sub-ledgers. This report is the natural alternative:
/// it lists every L4 sub-ledger under the L3 control with its
/// current balance, plus the L3 control's own (NET) balance for
/// reconciliation.
///
/// This is the standard "Schedule of Accounts Receivable" /
/// "Schedule of Accounts Payable" — the reconciliation that
/// auditors require to prove that the control account equals
/// the sum of its sub-ledgers.
///
/// Examples:
///   1103 (Accounts Receivable) → 1103-CUST-001 .. 1103-CUST-010
///   2101 (Accounts Payable)    → 2101-SUPP-001 .. 2101-SUPP-010
///   1101 (Cash on Hand)        → 1101-CASH-001, 1101-CASH-002
///   1102 (Bank)                → 1102-BANK-001, 1102-BANK-002
/// </summary>
public record SubLedgerScheduleLine(
    Guid AccountId,
    string AccountCode,       // e.g. "1103-CUST-001"
    string AccountName,       // e.g. "Sub-ledger: CUST-001"
    Guid? ContactId,          // null for cash/bank; set for AR/AP
    string? ContactCode,      // e.g. "CUST-001"
    string? ContactName,      // e.g. "وزارة الإسكان والتعمير"
    decimal Balance           // signed per account nature
);

public record SubLedgerScheduleReport(
    Guid CompanyId,
    string CompanyName,
    DateTime AsOfDate,
    Guid ParentAccountId,
    string ParentCode,        // e.g. "1103"
    string ParentName,        // e.g. "Accounts Receivable"
    string AccountType,       // Asset | Liability
    string Nature,            // Debit | Credit
    decimal ParentBalance,    // The L3 control's NET balance (sum of sub-ledgers)
    List<SubLedgerScheduleLine> Lines,
    int SubLedgerCount
);

/// <summary>
/// Sprint 44 — Contact Statement Line (كشف حساب العميل/المورد).
///
/// A single line on a contact's statement of account. Either
/// an invoice (increases AR/AP) or a voucher receipt/payment
/// (decreases AR/AP). The running balance is computed on the
/// backend so the frontend doesn't have to walk the list.
///
/// Convention:
///   - For a CUSTOMER: positive running = they owe us.
///   - For a SUPPLIER: positive running = we owe them.
/// The `direction` field shows the natural side (Dr for AR,
/// Cr for AP) so the reader can read top-to-bottom.
/// </summary>
public record ContactStatementLine(
    DateTime Date,
    string DocType,           // "فاتورة" | "سند قبض" | "سند صرف" | "مستخلص"
    string DocNumber,         // e.g. "INV-S-2026-0001" or "RV-2026-0001"
    string? Description,
    decimal Debit,            // increases AR (invoice) or decreases AP (payment)
    decimal Credit,           // decreases AR (receipt) or increases AP (invoice)
    decimal RunningBalance    // signed per the contact type
);

public record ContactStatementReport(
    Guid CompanyId,
    string CompanyName,
    Guid ContactId,
    string ContactCode,
    string ContactName,
    string ContactType,       // "customer" | "supplier"
    DateTime FromDate,
    DateTime ToDate,
    decimal OpeningBalance,
    decimal TotalDebit,
    decimal TotalCredit,
    decimal ClosingBalance,
    List<ContactStatementLine> Lines
);
