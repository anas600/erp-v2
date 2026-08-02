# ERP-V2 DOX — Project Root

## Purpose
- Own project-wide engineering rules and the top-level DOX index.
- Govern a multi-company accounting system delivered to a client who runs it on their own VPS.
- Make every meaningful folder understandable from its closest `AGENTS.md` plus this root file.

## Project
- Product name: **ERP-V2**.
- Form factor: Web application for a Holding Company with subsidiaries.
- Deployment: Self-hosted on a client-owned VPS (Hostinger VPS 2).
- Locales: **Arabic (RTL) only** — no bilingual requirement.
- Scale target: **10 to 20 users**, single company, low traffic.
- Currency: **LYD** (Libyan Dinar) as base currency.
- Demo data: seeded (1 holding + 2 subsidiaries + chart of accounts + 3 users + 6 rule templates).

## Stack (do not change without updating `CONSTITUTION.md`)
- Frontend: **Next.js 15** (App Router) + **TypeScript 5.6** + **React 19** + **Tailwind CSS 3.4**.
- Backend: **.NET 8 LTS** (Minimal API) + **Dapper 2.1** + **FluentMigrator 5** + **BCrypt.Net-Next 4** + **System.IdentityModel.Tokens.Jwt 7**.
- Database: **PostgreSQL 15 or 17** — version is selected by `POSTGRES_VERSION` env var in `.env`.
- Container: Docker Compose with three services (`db`, `backend`, `frontend`).
- Reverse proxy / hosting: provided by Hostinger VPS; app binds to ports `3000` (frontend), `5000` (backend), `5432` (db).

## Root Ownership
- `docker-compose.yml` owns the runtime topology (service names, ports, env wiring).
- `.env.example` owns the configuration surface; never hardcode values in `docker-compose.yml`.
- `README.md` is the operator-facing entry point (deployment, troubleshooting, login credentials).
- `CONSTITUTION.md` is the immutable design contract. It overrides any `AGENTS.md` if they conflict.
- `docs/` owns human-readable guides (architecture, user guide, deployment).
- `backend/` and `frontend/` are the two main product trees; each has its own `AGENTS.md`.

## Project-Wide Contracts
- **Multi-Company, not Multi-Tenant.** Every company-scoped table carries `company_id`; never introduce `tenant_id`.
- **Nature Logic is sacred.** `PostingEngine` (in `backend/Features/Journal/`) decides debit/credit placement from each account's stored `nature`. No code path may bypass it.
- **Rules Engine is data, not code.** New posting behavior ships as a JSON rule in the `business_rules` table, not as a new C# branch.
- **JWT carries `company_ids[]` and `active_company_id`.** Every protected endpoint resolves the active company from the token or the `X-Company-Id` header, never from query strings.
- **Arabic is the only UI language.** UI strings are written in Arabic; code identifiers stay in English; technical docs may be bilingual.
- **No secrets in source.** Passwords, JWT keys, and DB credentials come from env vars only.
- **No `tenant_id`, no `multi-tenancy`, no Kafka, no outbox, no microservices.** The architecture is a single-tenant, single-process, single-DB monolith by design.
- **Migrations are idempotent.** Each FluentMigrator migration must be safe to re-run; use `IF EXISTS` / `IF NOT EXISTS` guards where possible.

## Permissions
Allowed without asking:
- Read any tracked file.
- Edit any file under `docs/`, `backend/`, `frontend/`, or the root config files.
- Add a new file under any tracked folder.
- Run `dotnet` or `npm` commands locally to verify code compiles.

Ask before:
- Changing `CONSTITUTION.md` (requires project owner sign-off).
- Modifying `docker-compose.yml` service names, ports, or image versions.
- Adding a new external dependency (NuGet package, npm package) — record the reason.
- Deleting any tracked file.
- Creating commits, branches, or pushes.
- Changing the database schema in a way that breaks seed data.

## DOX Workflow
- `AGENTS.md` files are binding contracts for their subtrees.
- Before editing, read this file plus every `AGENTS.md` on the path to each target. The closest contract controls local details without weakening parent rules.
- Keep work understandable from the applicable DOX chain. Put project-wide rules here and concrete ownership, workflows, inputs, outputs, side effects, and verification in child docs.
- Create a child `AGENTS.md` only for a durable boundary with distinct ownership or workflow.
- Child docs use the section order: **Purpose, Ownership, Local Contracts, Work Guidance, Verification, Child DOX Index**.
- After every meaningful change, re-check the affected paths, update the closest owning docs and indexes, remove stale guidance, and run relevant verification.
- Keep DOX concise, current, operational, and free of diary entries or duplicated parent guidance.

## Child DOX Index
- `backend/AGENTS.md` — .NET 8 backend root (DI, pipeline, migrations runner).
- `backend/Common/AGENTS.md` — shared cross-cutting services (DB factory, JWT, password hashing).
- `backend/Features/AGENTS.md` — feature-module registry and conventions.
- `backend/Features/Auth/AGENTS.md` — login, JWT issuance, company switching.
- `backend/Features/Companies/AGENTS.md` — Holding + subsidiaries CRUD.
- `backend/Features/Accounts/AGENTS.md` — chart of accounts CRUD.
- `backend/Features/Journal/AGENTS.md` — journal entries and the **Posting Engine**.
- `backend/Features/Rules/AGENTS.md` — business rules engine and evaluator.
- `backend/Features/Reports/AGENTS.md` — trial balance, income statement, balance sheet.
- `backend/Migrations/AGENTS.md` — FluentMigrator schema and seed migrations.
- `frontend/AGENTS.md` — Next.js 15 frontend root.
- `frontend/src/AGENTS.md` — frontend source tree.
- `frontend/src/app/AGENTS.md` — App Router routes.
- `frontend/src/lib/AGENTS.md` — API client, auth context, formatting utilities.
- `docs/AGENTS.md` — human-readable guides (architecture, user guide, deployment).

## Intentionally Unindexed
`bin/`, `obj/`, `node_modules/`, `.next/`, `.vs/`, `*.user`, `*.suo`, `out/`, `.env*.local`, `*.log`, `.DS_Store`, `Dockerfile` (covered by owning `AGENTS.md`), `package-lock.json` (auto-generated).
