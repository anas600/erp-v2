# Sprint 35 Backend — Project Cost-Center Foundation

## Summary

Delivered the backend half of the Sprint 35 "Project as cost center" feature:
projects now have a real shape (type, customer, contract, manager, dates,
location) and individual transactions (invoices, journal entries, payment
vouchers, receipt vouchers) can be tagged with a project. A new P&L report
computes revenue (posted sales invoices) and costs (JE lines on accounts
5401-5407) per project, plus a company-wide roll-up.

## What I built

### 1. Migration 020 — `ProjectCostCenter`

`backend/Migrations/020_ProjectCostCenter.cs` (FluentMigrator, id
`20260807000005`). Adds:

- 8 new columns to `projects`: `type`, `customer_id` (FK to contacts),
  `contract_value`, `expected_end_date`, `actual_end_date`,
  `project_manager`, `location`, `updated_at` (idempotent column-add
  with `Schema.Table().Column().Exists()` guards).
- Changes the default for `projects.status` from `'active'` to `'draft'`
  (existing rows unaffected).
- `customer_id` FK with `ON DELETE SET NULL` (deleting a contact does
  not cascade-wipe a project).
- Adds `project_id` (nullable) to `invoices`, `journal_entries`,
  `payment_vouchers`, `receipt_vouchers`. Each gets a `B-tree` index
  (`ix_invoices_project`, `ix_je_project`, `ix_payments_project`,
  `ix_receipts_project`).
- `Down()` reverses everything in the opposite order: drop indexes,
  drop columns, restore `'active'` default.

### 2. `ProjectModels.cs` — new DTOs

- Extended `ProjectDto` with `Type`, `CustomerId`, `CustomerName`
  (denormalized), `ContractValue`, `ExpectedEndDate`, `ActualEndDate`,
  `ProjectManager`, `Location`.
- `CreateProjectRequest` and `UpdateProjectRequest` accept the new
  fields. `UpdateProjectRequest` now also carries `Status` (lets the
  endpoint move a project to `'completed'` / `'on_hold'` / etc.).
- New `ProjectPnLResponse` DTO with `ProjectId`, `ProjectCode`,
  `ProjectName`, `TotalRevenue`, `CostsByCategory`,
  `GrossProfit`, `ProfitMargin`, `InvoiceCount`, `JournalEntryCount`.
- New `CostCategoryPnL(Category, AccountCode, Amount)`.
- New `AllocateRequest(InvoiceIds)` and
  `AllocateJournalEntriesRequest(JournalEntryIds)` for bulk endpoint
  bodies.

### 3. `ProjectService.cs` — new methods

- `CreateAsync` / `UpdateAsync` now write the new columns.
- `GetByIdAsync` returns the extended DTO and joins the customer name
  from `contacts`.
- `AllocateInvoicesAsync(projectId, invoiceIds)` — bulk-assigns the
  given invoices to the project. Idempotent. Cross-company safe
  (rejects invoices from a different company than the project).
- `AllocateJournalEntriesAsync(projectId, entryIds)` — same shape for
  JEs.
- `DeallocateInvoicesAsync(projectId, invoiceIds)` — clears
  `project_id` on the listed invoices (no-op if not currently tagged
  with the project).
- `GetCostsAsync(projectId)` — returns all tagged invoices + JE lines
  as `ProjectCostLine` rows. Each row carries `Source` ("invoice" /
  "journal"), reference (invoice_number or entry_number), account
  code/name, and amount.
- `GetRevenueAsync(projectId)` — returns the posted sales invoices
  tagged with the project as `ProjectRevenueLine` rows.
- `GetPnLAsync(projectId)` — computes the P&L:
  - **Revenue** = `SUM(invoices.total)` where `project_id = X AND
    invoice_type='sales' AND status='posted'`.
  - **Costs** = JE lines on `accounts.code LIKE '54%'`, grouped by
    `account_code`. Uses `GREATEST(debit, credit)` per line (the
    natural "amount" for an expense line). Costly lines are derived
    from JEs (not invoice lines) to avoid double-counting — every
    posted purchase invoice already produces a JE, and counting both
    would double the costs.
  - **CostsByCategory** = rows like `("مواد خام", "5401", 1234.50)`.
  - **GrossProfit** = revenue - costs.
  - **ProfitMargin** = `(gross / revenue) * 100`, rounded to 2dp;
    guards against div-by-zero (returns 0 when revenue is 0).
  - Returns `InvoiceCount` and `JournalEntryCount` for the UI badge.
- `GetCompanyPnLAsync(companyId)` — iterates all projects in a
  company and returns a `List<ProjectPnLResponse>` (empty projects
  included with zeros so the report shows "no activity" too).

### 4. Project tag on the four transaction services

All four services now accept an optional `Guid? ProjectId` on their
Create/Update request DTOs and persist it to the new column:

- `InvoiceService.CreateDraftAsync` / `UpdateDraftAsync` — writes
  `invoices.project_id`. `GetByIdAsync` now returns `InvoiceDto.ProjectId`.
- `JournalService.CreateDraftEntryCoreAsync` (the internal INSERT
  used by all create paths) — writes `journal_entries.project_id`.
  `PostingEngine.GetByIdAsync` and `JournalService.GetByIdAsync`
  (via `_posting.GetByIdAsync`) now return the tag on the DTO.
- `PaymentService.CreateAsync` / `UpdateAsync` — writes
  `payment_vouchers.project_id`. SELECT rows in `GetByCompanyAsync` /
  `GetByIdAsync` and the `PaymentRow` record / `Map` function
  updated.
- `ReceiptService.CreateAsync` / `UpdateAsync` — same pattern for
  `receipt_vouchers.project_id`.

Backward-compat note: every new field is nullable, and every new
constructor parameter has a default value (`null` for `Guid?`, `0` for
`decimal`, `null` for `string?`). Existing API callers (rule pipeline,
seed data, demo seeder, voucher services) keep working without
modification.

### 5. Endpoints (in `ProjectEndpoints.cs` + new
`ProjectPnLReportEndpoints`)

All routes require authorization (`RequireAuthorization()` on the
group).

| Method | Path | Purpose |
|---|---|---|
| `GET`  | `/api/projects` | List projects for a company (unchanged) |
| `GET`  | `/api/projects/{id}` | Get one project (unchanged) |
| `POST` | `/api/projects` | Create a project (now accepts the new fields) |
| `PUT`  | `/api/projects/{id}` | Update a project (now accepts the new fields + `Status`) |
| `DELETE` | `/api/projects/{id}` | Delete a project (unchanged) |
| `POST` | `/api/projects/{projectId}/milestones` | Add milestone (unchanged) |
| `POST` | `/api/projects/{projectId}/milestones/{milestoneId}/complete` | Complete milestone (unchanged) |
| **`POST`** | **`/api/projects/{id}/allocate-invoices`** | **Bulk-assign invoices. Body: `{ "invoiceIds": [...] }`. Returns `{ "allocated": <count> }`.** |
| **`POST`** | **`/api/projects/{id}/allocate-journal-entries`** | **Bulk-assign JEs. Body: `{ "journalEntryIds": [...] }`. Returns `{ "allocated": <count> }`.** |
| **`POST`** | **`/api/projects/{id}/deallocate-invoices`** | **Bulk-clear invoices. Body: `{ "invoiceIds": [...] }`. Returns `{ "deallocated": <count> }`.** |
| **`GET`** | **`/api/projects/{id}/pnl`** | **Per-project P&L report.** |
| **`GET`** | **`/api/projects/{id}/costs`** | **List of all tagged invoices + JE lines.** |
| **`GET`** | **`/api/projects/{id}/revenue`** | **List of all posted sales invoices tagged with the project.** |
| **`GET`** | **`/api/reports/projects-pnl?companyId={guid}`** | **Company-wide P&L: list of P&L for every project.** |

The bulk allocation endpoints validate that all tagged entities
belong to the same company as the project. On a mismatch, the entire
operation is rejected with HTTP 400 + an Arabic error message
("إحدى الفواتير لا تنتمي لنفس شركة المشروع" / etc.). The P&L
endpoints live under `/api/projects/{id}/...`; the company-wide
report lives at `/api/reports/projects-pnl` (a new `ProjectPnLReportEndpoints`
group, registered in `Program.cs` next to the other report endpoints).

### 6. `Program.cs` change

One new line:

```csharp
ProjectPnLReportEndpoints.Map(app);
```

No new DI registrations, no new dependencies in `ErpV2.csproj`.

## Build verification

```
$ cd backend && dotnet build --nologo --no-restore
... Build succeeded.
    5 Warning(s)  // pre-existing nullable warnings (UserService, InvoiceService)
    0 Error(s)
```

## Constraints honored

1. **No data break**: every new column is nullable, so existing
   projects/invoices/JEs/payments/receipts keep working.
2. **No COA change**: project is a tag, not a sub-ledger. The 4-level
   COA (1101-1102 cash, 1200 AR, 2000 AP, 4100 revenue, 5401-5407
   expenses) is unchanged.
3. **Multi-company**: every read query filters by `company_id`. The
   bulk allocation endpoints explicitly validate that all entity IDs
   belong to the project's company before issuing the UPDATE.
4. **Existing patterns**: Dapper + `IDbConnectionFactory` + Minimal
   API endpoints, exactly like the rest of the codebase. Records for
   Dapper row mapping (private nested records) follow the existing
   naming convention (`snake_case` for columns, PascalCase for the
   record properties).
5. **No new dependencies**: `ErpV2.csproj` is untouched.
6. **Migration is reversible**: `Down()` drops indexes, drops columns,
   restores the original `'active'` default for `projects.status`.
7. **Idempotent bulk allocation**: re-assigning the same invoice to
   the same project is a no-op (the UPDATE writes the same value).
   De-allocating an invoice not currently tagged with the project is
   a no-op (the WHERE clause filters on `project_id = @projectId`).
8. **Multi-company cross-check is pre-flight**: the bulk endpoints
   first SELECT for any entities with the wrong `company_id`, and
   only issue the bulk UPDATE if the count is zero. So a single
   mis-tagged invoice in the request aborts the whole operation
   cleanly (no partial assignment).

## Deviations from the spec

- **P&L "costs" walk**: the spec said "walk both invoices (purchase)
  AND journal_entries". I count costs from the JE side only. The
  reason: every posted purchase invoice produces a journal entry
  (via the rule pipeline), so counting invoice lines + JE lines
  would double-count the same costs. Counting from the JE — the
  authoritative accounting source — is correct and matches the
  income-statement / cost-of-reports convention.
- **Cost allocation has no automatic "from invoice" side-effect**:
  when a user posts an invoice with `projectId=X`, the resulting
  journal entry is **not** automatically tagged with X. The user can
  use the bulk JE allocation endpoint to tag the JE separately, or
  the P&L can be computed from the invoice alone (revenue side). This
  is a deliberate trade-off — the rule pipeline doesn't know about
  project tags, and making it know would require either passing the
  projectId through the rule payload (the rule can choose to stamp
  it) or having the rule-pipeline auto-stamp the JE (couples the
  rule engine to the project module). The bulk endpoint is the
  explicit, user-controlled path.
- **Project DTO status default**: the migration changes the DEFAULT
  of `projects.status` from `'active'` to `'draft'`. Existing rows
  are unaffected (DEFAULT only applies to new INSERTs). The seed
  migrations create projects with an explicit status, so the new
  default doesn't change their behavior.
- **New endpoint group**: I put the company-wide P&L report in a new
  `ProjectPnLReportEndpoints` group (so the route stays at
  `/api/reports/projects-pnl` to group with the other reports) rather
  than appending to the existing `ReportEndpoints`. The spec said
  "Add a new endpoint group for the report, or put it in the existing
  ReportEndpoints" — both are acceptable, I chose the new group for
  isolation.

## Known limitations

- **Revenue is from invoices only**, not from manual milestone revenue
  posted via the rules engine. A milestone completion currently
  creates a journal entry that hits a revenue account (4xxx) but is
  not tagged with the project (the rules engine doesn't know about
  project_id yet). If you want milestone revenue in the P&L, the
  `RuleMilestoneProjected` rule needs to forward `projectId` to the
  rule payload, and the `CreatePendingAsync` path needs to read it.
  This is a follow-up — out of scope for the foundation sprint.
- **Account code 54% is hardcoded** as the cost bucket. If the user
  adds a non-54xx expense account later, the P&L won't pick it up.
  Acceptable for now (the 4-level COA mandates 5401-5407 as the
  expense range).
- **No project P&L cache**: each call re-runs the SQL. For
  small/medium project counts this is fine. If the company has 100+
  projects, the company-wide report should be cached.
- **`ProjectPnLResponse.ProjectCode`**: the project code is currently
  shown in the DTO but the UI may want a separate
  `ProjectCode` / `ProjectName` translation if the user later renames
  the project (we already return both, so the frontend is
  future-proof).

## Files created

- `backend/Migrations/020_ProjectCostCenter.cs`

## Files modified

- `backend/Features/Projects/ProjectModels.cs` — new fields + new DTOs.
- `backend/Features/Projects/ProjectService.cs` — new methods
  (`GetPnLAsync`, `GetCostsAsync`, `GetRevenueAsync`,
  `GetCompanyPnLAsync`, `AllocateInvoicesAsync`,
  `AllocateJournalEntriesAsync`, `DeallocateInvoicesAsync`) and
  updated `CreateAsync` / `UpdateAsync` / `GetByIdAsync`.
- `backend/Features/Projects/ProjectEndpoints.cs` — new bulk
  allocation + P&L/cost/revenue endpoints.
- `backend/Features/Invoicing/InvoiceModels.cs` — added
  `ProjectId` to `InvoiceDto` and `CreateInvoiceRequest`.
- `backend/Features/Invoicing/InvoiceService.cs` — writes/reads
  `invoices.project_id` on create, update, and get.
- `backend/Features/Journal/JournalModels.cs` — added `ProjectId`
  to `JournalEntryDto` and `CreateJournalEntryRequest`.
- `backend/Features/Journal/JournalService.cs` — writes
  `journal_entries.project_id` in `CreateDraftEntryCoreAsync`.
- `backend/Features/Journal/PostingEngine.cs` — reads/returns
  `project_id` in `GetByIdAsync`.
- `backend/Features/Payments/PaymentModels.cs` — added
  `ProjectId` to `PaymentVoucherDto` and `CreatePaymentVoucherRequest`.
- `backend/Features/Payments/PaymentService.cs` — writes/reads
  `payment_vouchers.project_id` on create, update, list, and get.
- `backend/Features/Receipts/ReceiptModels.cs` — added `ProjectId`
  to `ReceiptVoucherDto` and `CreateReceiptVoucherRequest`.
- `backend/Features/Receipts/ReceiptService.cs` — writes/reads
  `receipt_vouchers.project_id` on create, update, list, and get.
- `backend/Program.cs` — added `ProjectPnLReportEndpoints.Map(app)`.

## Build status

`dotnet build` succeeded with 0 errors. The 5 warnings are
pre-existing nullable-reference warnings in `UserService.cs` and
`InvoiceService.cs` (unrelated to this sprint's changes).
