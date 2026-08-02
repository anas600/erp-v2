# Architecture Overview

## Goals
- One-company-per-deployment simplicity (no multi-tenant SaaS).
- Client self-hosts on a single VPS (Hostinger VPS 2: 2 vCPU, ~2 GB RAM).
- 10–20 users; Arabic-only UI.
- A **Business Rules Engine** that lets non-developers add posting behavior without code changes.
- A **Posting Engine** that guarantees `Σ debit = Σ credit` for every posted entry.

## High-Level Diagram

```
                    ┌───────────────────────────┐
                    │   Client Browser (RTL)    │
                    │   Tajawal font, Arabic    │
                    └─────────────┬─────────────┘
                                  │ HTTPS
                                  ▼
                    ┌───────────────────────────┐
                    │  Next.js 15 (port 3000)   │
                    │  App Router · TypeScript  │
                    │  Auth Context · API       │
                    └─────────────┬─────────────┘
                                  │ /api/* → rewrite
                                  ▼
                    ┌───────────────────────────┐
                    │  .NET 8 Backend (5000)    │
                    │  Minimal API · Dapper     │
                    │  Posting Engine           │
                    │  Rules Evaluator          │
                    └─────────────┬─────────────┘
                                  │ Npgsql
                                  ▼
                    ┌───────────────────────────┐
                    │  PostgreSQL 15 / 17       │
                    │  34+ tables · JSONB       │
                    └───────────────────────────┘
```

## Module Map

| Module | Backend folder | Frontend page | Purpose |
|--------|---------------|---------------|---------|
| Auth | `backend/Features/Auth/` | `auth/login/` | Login + JWT + company switcher |
| Companies | `backend/Features/Companies/` | `dashboard/companies/` | Holding + subsidiaries CRUD |
| Accounts | `backend/Features/Accounts/` | `dashboard/accounts/` | Chart of accounts |
| Journal | `backend/Features/Journal/` | `dashboard/journal/` | Manual entries + Posting Engine |
| Rules | `backend/Features/Rules/` | `dashboard/rules/` | Business rules engine |
| Reports | `backend/Features/Reports/` | `dashboard/reports/` | Trial balance + Income + Balance sheet |

## Posting Engine — Nature Logic

Every account has a `type` (Asset, Liability, Equity, Revenue, Expense) and a `nature` (Debit, Credit). The Posting Engine uses the nature to decide where each line lands:

```
For each line in the entry:
  Load the account.
  delta = (line.debit - line.credit)   if nature == 'Debit'
        = (line.credit - line.debit)   if nature == 'Credit'
  accounts.balance += delta

Before committing:
  if Σ(line.debit) != Σ(line.credit):
    raise "القيد غير متوازن" and roll back
```

This single rule enforces the fundamental accounting identity `A = L + E` at the storage layer. Contra-accounts (e.g. `1510 Accumulated Depreciation`, an Asset with Credit nature) work because the engine reads the nature, not the type.

## Business Rules Engine

A rule is a JSON document with this shape:

```json
{
  "conditions": { "all": [ { "field": "invoice.total", "op": ">", "value": 0 } ] },
  "actions": [
    {
      "type": "PostJournalEntry",
      "narration": "فاتورة مشتريات رقم {invoice.number}",
      "lines": [
        { "accountCode": "5000", "nature": "debit",  "amountFormula": "invoice.total - invoice.tax" },
        { "accountCode": "2000", "nature": "credit", "amountFormula": "invoice.total" }
      ]
    }
  ]
}
```

The Rules Evaluator loads rules that match an event name, checks conditions, and turns `PostJournalEntry` actions into real entries via the Posting Engine. New behavior ships as new rows in the `business_rules` table — no code change, no redeploy.

## Data Isolation

Every company-scoped table has a `company_id` column. The JWT carries `company_ids[]` and `active_company_id`. Every protected endpoint resolves the active company from the token (or the `X-Company-Id` header as a fallback) and filters on it. There is **no `tenant_id` column anywhere** because this is Multi-Company, not Multi-Tenant (see `CONSTITUTION.md` Article 3).

## Why a Monolith?

A 10–20 user Arabic ERP on a single VPS does not need microservices, Kafka, or a separate frontend/backend deployment. One Docker Compose stack with three services (`db`, `backend`, `frontend`) is enough and is dramatically easier to operate.
