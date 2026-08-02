# Migrations — Schema and Seed

## Purpose
- Own every change to the database schema, in order, with idempotent scripts.
- Provide the seed data that makes a fresh deployment immediately useful for the demo.

## Ownership
- `001_InitialSchema.cs` — creates the core tables: `users`, `roles`, `permissions`, `role_permissions`, `companies`, `user_companies`, `accounts`, `journal_entries`, `journal_lines`, `business_rules`, `audit_logs`. Enables the `pgcrypto` extension for `gen_random_uuid()`.
- `002_SeedData.cs` — inserts 6 roles, 12 permissions, 1 holding + 2 subsidiaries, 3 users (with hashed passwords), 17 accounts per company, and 6 rule templates.
- `MigrationRunner.cs` — empty marker class so FluentMigrator can locate the assembly via `typeof(MigrationRunner).Assembly`.

## Local Contracts
- Migration order is enforced by the `[Migration(<numeric>)]` attribute; the numeric prefix is a sortable timestamp (`YYYYMMDDhhmmss`).
- Every migration must implement `Up()` and a no-op or symmetric `Down()`.
- `Up()` must be safe to run twice; the seed migration uses `ON CONFLICT DO NOTHING` to achieve that.
- The connection string in the seed migration must match the one in `appsettings.json` and `docker-compose.yml`; the values are duplicated intentionally because FluentMigrator runs before DI is built.
- Adding a new column to an existing table: write a new `NNN_<Topic>.cs` migration; do not edit a committed migration.

## Work Guidance
- To add a new table: extend `001_InitialSchema.cs` for a fresh deployment, or add a new `003_<Topic>.cs` migration for an existing deployment. Prefer the new migration in real projects.
- To add a new role, permission, or rule template: append to `002_SeedData.cs` inside the relevant section.
- The seed passwords are deliberately weak (`admin123`, `acc123`, `eng123`) for demo use only. They are bcrypt-hashed at seed time, not stored in plain text.
- The `audit_logs` table is created but no application code writes to it yet. When adding audit logging, write a service that appends to this table.

## Verification
- After a clean `docker compose down -v && docker compose up -d --build`, all tables exist and the seed users can log in.
- The `business_rules` table has 6 rows, all with `is_template = true`.
- The `accounts` table has 17 rows per company (3 companies × 17 = 51 rows).

## Child DOX Index
- *(No child folders; this is a leaf.)*
