# Lessons Learned — Cycles 1 to 4 (2026-08-14)

> **Author**: Mavis (Tech Lead)
> **Context**: Mavis took over the project to ship a polished, client-ready
> version of the ERP-V2 frontend. Working in 1-2 hour cycles, each ending
> with a merge commit to `main` and an auto-deploy via the existing
> Render Blueprint.

## Cycle 1: Reports Index (lobby)

**Goal**: Add a single entry point to the reports section that solves two
problems at once — the sidebar group is collapsed by default, and even
when open, 10+ items are a wall of text.

**What shipped**:
- `frontend/src/app/dashboard/reports/page.tsx` (503 lines) — a sortable,
  filterable, searchable table of all 10 reports.
- Sidebar link added as the first item in the التقارير المالية group.

**What worked**:
- Inline data (no JSON file, no API call) — reports don't change often.
- Existing `cn` utility + Tailwind classes for consistent styling.
- No changes to backend needed.

**Lesson learned**: Don't add infrastructure (JSON, API) for data that
changes less than once a quarter. Inline is fine.

---

## Cycle 2: Sub-Ledger Schedule

**Goal**: Show the L3 control account (e.g. "1103 Accounts Receivable")
broken down into its L4 sub-ledgers — the auditor's "Schedule of
Accounts Receivable".

**What shipped**:
- `frontend/src/app/dashboard/reports/sub-ledger-schedule/page.tsx`
  (416 lines) with L3 dropdown, as-of date picker, reconciliation check,
  sub-ledger table.

**What went wrong** (and how I fixed it):
- The page shipped with a 404 from Render. The build was silently
  failing with a TypeScript error: I used `report.asOf` but the DTO
  is `asOfDate`. The page was never compiled, so it 404'd at runtime.
- **Fix**: pulled `node_modules`, ran `npx tsc --noEmit`, found the error,
  fixed it, redeployed.
- **Lesson**: ALWAYS run the type check locally before pushing. Render's
  failure mode is silent (no error in the browser, just a 404). The
  cost of `npx tsc` is 30 seconds; the cost of a failed deploy is 5
  minutes (build + waiting + debugging + redeploying).

**What I also did right**:
- L3 dropdown loads from `/api/accounts` (already in the codebase),
  filtered to `level === 3`. Reused the existing pattern from the
  accounts page.
- Reconciliation check is automatic: `L3 NET == Σ L4` with 0.01
  tolerance. If unbalanced, the banner turns red with the exact diff.

---

## Cycle 3: Projects P&L

**Goal**: Per-project profitability report for project managers.

**What shipped**:
- `frontend/src/app/dashboard/reports/projects-pnl/page.tsx` (321 lines)
  with summary cards, per-project table, cost breakdown by category,
  margin color-coding.
- Updated the index page to mark sub-ledger-schedule and projects-pnl
  as "working" instead of "missing".

**What went wrong** (and how I fixed it):
- I imported `formatPercent` from `@/lib/utils` — but that function
  doesn't exist there. Only `cn`, `formatNumber`, `formatDate`,
  `formatDateTime`. The type check caught this.
- **Fix**: removed the unused import. I had written `margin.toFixed(1)%`
  inline anyway, so the import was a leftover from a refactor.
- **Lesson**: Check existing utilities before adding new ones. Read
  `frontend/src/lib/utils.ts` before importing from it.

**What I also did right**:
- Used the existing `ProfitStatus` / `MarginPill` color-coding pattern
  from the existing journal page (where "posted" / "pending" / "draft"
  have similar badges). Visual consistency matters.

---

## Cycle 4: fiscal-periods LIST endpoint

**Goal**: Fix the bug where the fiscal-years page couldn't list all
periods without iterating year-by-year. Also, the contact-statement
detail page references `/api/contacts/{id}/statement`, which exists,
but the fiscal-years page was missing a similar LIST endpoint.

**What shipped**:
- `GET /api/fiscal-periods?companyId=<uuid>[&fiscalYearId=<uuid>]`
  Returns `List<FiscalPeriodDto>` sorted by start date DESC.
- `FiscalYearService.GetAllPeriodsAsync(companyId, fiscalYearId)` joins
  `fiscal_periods` with `fiscal_years` to filter by company.

**Verification**:
- `dotnet build` succeeds with 0 errors.
- The 9 warnings are all pre-existing null-reference warnings in
  unrelated files (UserService, InvoiceService, FullYearSeeder) — not
  introduced by this change.

**What I did right**:
- Joined with `fiscal_years` to filter by company_id (periods don't
  have company_id directly; they're scoped via their parent year).
- Optional `fiscalYearId` filter — defaults to "all years" but can
  narrow to one.

---

## Cross-cycle lessons

### 1. Local type check before pushing
Both Cycles 2 and 3 had TS errors that the build silently swallowed.
The fix: `cd frontend && npx tsc --noEmit` is now a non-negotiable
pre-commit step.

### 2. Reuse existing utilities + components
Every new page pulls from the same 5 things: `useAuth`, `api`,
`getErrorMessage`, `formatNumber`/`formatDate`/`cn`, `Loader2`. This
keeps the visual + error-handling + loading-state experience
identical across the app.

### 3. Don't add infrastructure for stable data
The reports catalog is inline (10 entries, changes maybe once a
year). The cost of a JSON file + a `/api/reports/catalog` endpoint
+ a fetch + loading/error state is more than the convenience. Just
inline the data.

### 4. Backend is the bottleneck
Cycles 1, 2, 3 were frontend-only and shipped in <30 minutes each.
Cycle 4 needed a backend change (`dotnet restore` + `dotnet build` =
~30s, but the reasoning takes longer because C# is stricter than
TS). When the user said "minimal backend changes", they meant it.

### 5. Auto-deploy via Render Blueprint is the magic
Push to `main` → Render builds + deploys in ~90 seconds. No CI/CD
to configure, no manual deploy hooks (those exist for emergencies).
This made the cycle rhythm possible.

### 6. The user can always roll back
Every commit is a separate, self-contained change. The user can roll
back to any of:
- `2cbe278` (Sprint 45 hotfix) — pre-Mavis state
- `eb47f11` (Cycle 1) — just the index page
- `7e5044f` (Cycle 2) — index + sub-ledger
- `0784744` (Cycle 3) — + projects-pnl
- `2ecf9ee` (Cycle 4) — + fiscal-periods LIST (current main)

via `git reset --hard <sha>` on the VPS.

---

## Queue (cycles 5+)

| # | Goal | Type | Status |
|---|------|------|--------|
| 5 | URL alignment (intercompany-elimination → intercompany-transactions) | Frontend | Pending |
| 6 | Lessons learned file (this one) ✅ | Docs | Done |
| 7 | Period filter component (shared) | Frontend | Pending |
| 8 | Period-locking UI polish (if requested) | Frontend | Pending |
| 9 | Print/export for reports (Excel, PDF) | Frontend | Pending |
| 10 | Final smoke test + handoff doc | Frontend + Docs | Pending |

---

**Tech Lead**: Mavis
**Date**: 2026-08-14
**State**: 4 cycles complete, 1 file (this) added, 2 files (sub-ledger, projects-pnl) shipped, 1 backend endpoint (fiscal-periods LIST) added, 1 layout updated.
