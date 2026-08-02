# Journal Feature — Entries and the Posting Engine

## Purpose
- Hold manual and rule-generated journal entries.
- Enforce the **Nature Logic** that guarantees every posted entry is balanced (`Σ debit = Σ credit`).
- Update account balances after a successful post.
- Provide reversal entries that restore balances to their pre-posted state.

## Ownership
- `JournalModels.cs` — `JournalEntryDto`, `JournalLineDto`, `CreateJournalEntryRequest`, `CreateJournalLineRequest`.
- `JournalService.cs` — entry creation, listing, posting, reversal; orchestrates the Posting Engine.
- `PostingEngine.cs` — **the heart of accounting correctness**. See "Local Contracts" below.
- `JournalEndpoints.cs` — `GET /api/journal`, `POST /api/journal`, `POST /api/journal/{id}/post`, `POST /api/journal/{id}/reverse`.

## Local Contracts

### Nature Logic (do not modify without a Constitution amendment)
The `PostingEngine.PostAsync` method applies this rule for every line of a posted entry:

1. Load the account and read its `nature` field (`Debit` or `Credit`).
2. If the line has `debit > 0` and the account's nature is `Debit`, the account balance increases.
3. If the line has `credit > 0` and the account's nature is `Debit`, the account balance decreases.
4. Mirror image for nature `Credit`.

`PostingEngine.ComputePlacement(accountNature, requestedNature, amount)` is the helper the Rules Engine uses to place rule-generated amounts. It returns the `(debit, credit)` pair so that contra-accounts (e.g. `1510 Accumulated Depreciation`, type `Asset`, nature `Credit`) are honored.

### Balance check
Before any account balance is updated, `PostAsync` verifies that the sum of `debit` equals the sum of `credit`. If not, the whole transaction is rolled back and an exception is raised with a clear Arabic message.

### Entry number generation
`JV-YYYY-NNNN` format, scoped per company, zero-padded to four digits. The next number is the previous max + 1, computed inside the same transaction to avoid races.

### Reversal
`ReverseAsync` creates a new entry that mirrors the original (swap debit/credit) and updates balances by subtracting the original impact. The original entry moves to status `reversed`; the new entry is `posted` with `source = "reverse:<original-id>"`.

## Work Guidance
- New entry shapes go here, not in `Rules/`. The Rules Engine produces `CreateJournalEntryRequest` and calls `JournalService.CreateDraftAsync` + `PostingEngine.PostAsync`.
- Do not introduce a separate "balance" column maintained outside the engine; the `accounts.balance` field is the single source of truth.
- Drafts (`status = 'draft'`) can be edited; posted entries are immutable. Reversal is the only way to undo a posted entry.
- Always pass `createdBy` from the JWT subject when creating an entry; never leave it null for a real user.
- The Arabic error message in the balance check is a user-facing string; keep it short and direct.

## Verification
- `POST /api/journal` with unbalanced lines returns `400` and the message `القيد غير متوازن...`.
- `POST /api/journal/{id}/post` updates the matching `accounts.balance` rows.
- Posting the same entry twice returns `400` with "Entry already posted".
- Reversing a posted entry brings both the entry status and the account balances back to their pre-posted state.

## Child DOX Index
- *(No child folders; this is a leaf. The Posting Engine lives next to the service for clarity.)*
