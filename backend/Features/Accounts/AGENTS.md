# Accounts Feature — Chart of Accounts

## Purpose
- Manage the chart of accounts: a tree of accounts where each account has a **type** and a **nature**.
- The type and nature are inputs to the **Posting Engine** (see `Journal/AGENTS.md`).

## Ownership
- `AccountModels.cs` — `AccountDto`, `CreateAccountRequest`, `UpdateAccountRequest`, plus the `AccountType` and `AccountNature` enums.
- `AccountService.cs` — CRUD, tree fetch (`GetTreeAsync` returns a flat list; the frontend builds the tree).
- `AccountEndpoints.cs` — `GET/POST/PUT /api/accounts`, all require `companyId`.

## Local Contracts
- `accountType` is one of: `Asset`, `Liability`, `Equity`, `Revenue`, `Expense`.
- `nature` is one of: `Debit`, `Credit`.
- `(companyId, code)` is unique. Two companies may share codes because they live in different company scopes.
- The default nature follows the type:
  - `Asset` → `Debit`
  - `Liability` → `Credit`
  - `Equity` → `Credit`
  - `Revenue` → `Credit`
  - `Expense` → `Debit`
- Contra-accounts (e.g. Accumulated Depreciation) override the default by setting `nature` to the opposite side.
- `balance` is computed by the Posting Engine; application code must not write to it directly.

## Work Guidance
- Adding a new account: pick the type first, then the nature. Validate the pair in the service.
- The seed migration already creates the standard tree (1xxx Assets, 2xxx Liabilities, 3xxx Equity, 4xxx Revenue, 5xxx Expenses). Extend it rather than replacing it.
- Deactivating an account is preferred over deletion; existing journal lines still reference it.
- Use `parentId` to build sub-ledgers (e.g. `1100-01` for "Bank - Bank of Libya"). The tree is materialized only in the UI.

## Verification
- Creating an account with `accountType = Asset` and `nature = Credit` is allowed (this is the contra-asset case).
- Creating an account with `accountType = Liability` and `nature = Debit` is allowed (contra-liability case).
- Re-creating the same `(companyId, code)` pair returns a DB-level error surfaced as `400`.
- `GET /api/accounts?companyId=...` returns the full set including the seed data.

## Child DOX Index
- *(No child folders; this is a leaf.)*
