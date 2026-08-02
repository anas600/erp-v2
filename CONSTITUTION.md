# ERP-V2 CONSTITUTION

This document is **immutable**. It defines the non-negotiable contracts of the system.
Any change here requires explicit approval from the project owner and a version bump.

> **Supremacy rule:** If any other document (`AGENTS.md`, `docs/`, code comments) contradicts this file, **this file wins**. Update the conflicting doc to align.

---

## Article 1 — Mission
Deliver a multi-company accounting web application that a non-technical client can install on their own VPS, run with a single `docker compose up`, and administer through a browser UI without touching code.

## Article 2 — Scope
**In scope:**
- Holding company with multiple subsidiary companies (Multi-Company).
- User, role, and permission management.
- Chart of accounts (tree with type + nature).
- Manual journal entries and rule-generated entries.
- Trial balance, income statement, balance sheet.
- Configurable business rules engine for posting behavior.

**Out of scope (for the MVP):**
- Multi-tenant SaaS (one Holding per deployment only).
- Payroll, inventory, fixed-asset depreciation schedules beyond the example template.
- Mobile apps, offline mode, real-time collaboration.
- External API integrations (banks, payment gateways, tax authorities).
- AI/ML features, forecasting, dashboards beyond the basic stats.

## Article 3 — Multi-Company (not Multi-Tenant)
- The system supports **exactly one Holding** per deployment. The Holding is a row in the `companies` table with `is_holding = true` and `parent_id IS NULL`.
- Subsidiaries are rows in the same table with `parent_id` pointing to the Holding.
- Every company-scoped table carries `company_id`. There is **no `tenant_id` column anywhere**.
- Data isolation is enforced by filtering on `company_id` at the service layer.
- Future addition of a second Holding would require a Constitution amendment and a schema migration.

## Article 4 — Data Ownership
- **User** owns: profile, password hash, role memberships.
- **Company** owns: code, name, currency, parent reference.
- **Account** owns: code, name, type, nature, balance (computed from posted entries).
- **JournalEntry** owns: header metadata; balances are immutable after posting.
- **BusinessRule** owns: JSON definition; the engine never mutates rule content at runtime.
- **AuditLog** (when present) is append-only; never updated, never deleted by application code.

## Article 5 — Security Boundaries
- Authentication is JWT-based; access token lifetime ≤ 24h.
- Passwords are stored using **BCrypt** with cost factor 12.
- The JWT carries `user_id`, `email`, `is_super_admin`, `company_ids[]`, `active_company_id`, `roles[]`, `permissions[]`.
- The active company is resolved from the token first, then from the `X-Company-Id` header as a fallback.
- No endpoint may trust a `company_id` from the request body or query string; only token or header.
- CORS allows only origins listed in the `CORS_ORIGINS` env var.
- Secrets (`JWT_KEY`, `POSTGRES_PASSWORD`) come from env vars; never commit them.

## Article 6 — Development Process
- Vertical slices: each feature lives under `backend/Features/<Name>/` with `Models`, `Service`, `Endpoints`.
- Dapper for queries, FluentMigrator for schema; **no Entity Framework**.
- Migrations are append-only and versioned by date prefix.
- The Rules Engine is the **only** way to introduce new posting behavior; the Posting Engine itself is immutable.
- DOX (`AGENTS.md` tree) is the documentation contract; any change to a folder requires updating its `AGENTS.md`.

## Article 7 — Amendment Process
- Changes to this Constitution require:
  1. Written proposal in a `docs/governance/DEC-NNN-amendment-<topic>.md` file.
  2. Explicit approval from the project owner.
  3. A version bump at the top of this file.
- Until all three are satisfied, the proposed change is **not in force**.

---

**Version:** 1.0
**Effective date:** 2026-08-02
**Status:** ACTIVE
