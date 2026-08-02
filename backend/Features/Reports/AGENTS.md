# Reports Feature

## Purpose
- Produce the three financial reports the client cares about: trial balance, income statement, balance sheet.
- Reports are read-only and computed on demand from the posted journal entries and account balances.

## Ownership
- `ReportModels.cs` — DTOs for each report (`TrialBalanceReport`, `IncomeStatementReport`, `BalanceSheetReport`).
- `ReportService.cs` — query logic; uses Dapper directly.
- `ReportEndpoints.cs` — `GET /api/reports/trial-balance`, `GET /api/reports/income-statement`, `GET /api/reports/balance-sheet`.

## Local Contracts
- All reports are scoped to one company; the `companyId` query parameter is required.
- Trial balance presents each account on its natural side: `nature = Debit` accounts show positive balances on the debit column, `nature = Credit` on the credit column. Negative balances flip to the other side so the totals stay equal.
- Income statement window defaults to the current calendar year (`from = Jan 1`, `to = today`). Override with `from` and `to` query params (ISO 8601).
- Balance sheet uses the current calendar year-to-date income statement to add `Net Income (current year)` to equity.
- The `balanced` flag on trial balance and balance sheet is true when the two totals differ by less than `0.01`.

## Work Guidance
- Adding a new report:
  1. Add a DTO to `ReportModels.cs`.
  2. Add a service method that takes `(companyId, ...)` and runs one Dapper query.
  3. Map the endpoint in `ReportEndpoints.Map` and call the service.
  4. Add a page under `frontend/src/app/dashboard/reports/<name>/page.tsx`.
- Never cache reports at the service layer; the user expects fresh numbers after each post.
- Arabic labels live in the DTO; the frontend displays them as-is.

## Verification
- After seeding and posting a balanced entry, the trial balance shows `balanced: true` with equal totals.
- After posting a second entry that is also balanced, the totals increase but stay equal.
- The income statement shows revenue minus expense; if no posted entries exist, both are zero.

## Child DOX Index
- *(No child folders; this is a leaf.)*
