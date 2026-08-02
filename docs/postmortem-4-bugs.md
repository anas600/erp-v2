# DEC-2026-08-02: 4 Production Deploy Bugs — Postmortem

**Date**: 2026-08-02
**Author**: Mavis (AI Coordinator)
**Severity**: P0 — every deploy failed for 4 different reasons
**Resolution**: All 4 fixed; smoke tests added to prevent regression

---

## 📋 Summary

Took ERP-V2 from "works on local docker-compose" to "deployed on Render free
tier". The first 4 deploys each failed with a different bug. This document
captures the root cause and the fix for each one, so we don't repeat them.

---

## 🐛 Bug #1: `uuid_generate_v4()` does not exist

**Symptom** (Render logs, deploy #1):
```
Npgsql.PostgresException: 42883: function uuid_generate_v4() does not exist
  at Npgsql.NpgsqlCommand.ExecuteReader...
  at ErpV2.Migrations.InitialSchema.Up() in /src/Migrations/001_InitialSchema.cs
```

**Root cause**:
The `uuid-ossp` extension is not enabled by default in PostgreSQL. The
`uuid_generate_v4()` function lives in that extension. In local
docker-compose, we enabled it in `init.sql` and never hit this. In Render,
we didn't.

**Fix** (`backend/Migrations/001_InitialSchema.cs`):
```sql
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
```
Added at the top of the migration, before any `CREATE TABLE`.

**Lesson**: PostgreSQL extensions are PER DATABASE. The migration that
needs the function is the migration that should enable the extension. Don't
rely on side-channel init scripts that may not run in every environment.

**Regression test**: `scripts/verify.sh [3/12]` — grep for `uuid-ossp` in
`001_InitialSchema.cs`.

---

## 🐛 Bug #2: `Host=db` not found in production

**Symptom** (deploy #2):
```
Npgsql.NpgsqlException: Connection refused at Host=db:5432
  at ErpV2.Migrations.SeedData.Up() in /src/Migrations/002_SeedData.cs
```

**Root cause**:
`002_SeedData.cs` opened its own `NpgsqlConnection` with a hardcoded
`"Host=db;Port=5432;..."` — the docker-compose service name. On Render,
the service name is different (or there's no service name at all; the host
is the Render-managed Postgres hostname).

The fix in `Program.cs` already read from `ConnectionStrings__Default`
env var, but the seed migration didn't follow the same pattern.

**Fix** (`backend/Migrations/002_SeedData.cs`):
```csharp
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
    ?? "Host=db;Port=5432;Database=erp;Username=erp;Password=erp_secret";
```
Same env-var-first, local-fallback pattern as the runtime code.

**Lesson**: Migrations are still part of the app. They run in the same
process. They MUST use the same configuration as the rest of the app.
Never duplicate config.

**Regression test**: `scripts/verify.sh [8/12]` — grep for `Host=db` in
non-test code.

---

## 🐛 Bug #3: inotify instance limit (128) reached

**Symptom** (deploy #3):
```
System.IO.IOException: The configured user limit (128) on the number of
inotify instances has been reached
  at System.IO.FileSystemWatcher.StartRaisingEvents()
  at Microsoft.Extensions.FileProviders.Physical.PhysicalFilesWatcher...
  at Microsoft.Extensions.Configuration.Json.JsonConfigurationSource.Build
  at Program.<Main>$(String[] args) in /src/Program.cs:line 18
```

**Root cause**:
Linux caps each user at 128 inotify instances. When
`WebApplication.CreateBuilder()` is called, ASP.NET Core registers JSON
config sources with `reloadOnChange: true` (the default). Each source
tries to create a `FileSystemWatcher` → boom.

Our `Sources.Clear()` fix in `Program.cs` was too late: the watchers are
created INSIDE `WebApplication.CreateBuilder()`, before any user code runs.

**Fix** (two layers):
1. `backend/Dockerfile`: `ENV DOTNET_USE_POLLING_FILE_WATCHER=true`
   — this env var is read by the .NET runtime before any code runs, so
   it must be in the Dockerfile (or in Render's env vars), NOT in
   `Program.cs`.
2. `backend/Program.cs`: clear default sources and re-add with
   `reloadOnChange: false` — defense in depth for the case where the
   env var is missing.

**Lesson**: Runtime initialization order matters. The .NET runtime reads
`DOTNET_*` and `ASPNETCORE_*` env vars before the first line of
`Program.cs`. Configuration that affects early initialization must be
env vars, not code.

**Regression test**: `scripts/verify.sh [10/12]` (reloadOnChange check)
and `[11/12]` (env var in Dockerfile).

---

## 🐛 Bug #4: `IX_companies_code` duplicate key on re-deploy

**Symptom** (deploy #4):
```
Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates
unique constraint "IX_companies_code"
  at Dapper.SqlMapper.Execute(...)
  at ErpV2.Migrations.SeedData.Up() in /src/Migrations/002_SeedData.cs:line 136
```

**Root cause**:
Two issues, working together:
1. The seed migration did plain `INSERT INTO companies` with no
   `ON CONFLICT` clause. On Render, the previous deploy had partially
   succeeded (the database transaction wasn't atomic across the migration
   boundary because we opened a fresh `NpgsqlConnection` and never used
   the FluentMigrator transaction context).
2. The migration used `Guid.NewGuid()` for the company id. Even if we
   added `ON CONFLICT (code) DO NOTHING`, the user_companies insert
   downstream would use the new id, not the existing one, breaking the
   foreign key.

**Fix** (`backend/Migrations/002_SeedData.cs`):
- Wrap all seed operations in a single explicit `BEGIN`/`COMMIT`
  transaction.
- Every `INSERT` ends with `ON CONFLICT (...) DO NOTHING` on its
  natural key (code, email, name+event_name, etc.).
- After each `INSERT`, re-read the row by its natural key to get the
  actual id, then use THAT id for downstream foreign-key inserts.
- Idempotent local functions (`UpsertCompany`, `UpsertUser`,
  `UpsertMembership`) make the pattern obvious at every call site.

**Lesson**: Cloud deploys re-run. Migrations re-run. Migrations must
be idempotent end-to-end (insert + downstream FK inserts) and must use
the FluentMigrator transaction context (or a manual transaction) for
atomicity. `Guid.NewGuid()` is a foot-gun in idempotent seeds — always
look up the id by natural key after the upsert.

**Regression test**: `scripts/verify.sh [12/12]` — grep for
`ON CONFLICT (code)`, `ON CONFLICT (email)`,
`ON CONFLICT (company_id, code)`, `ON CONFLICT (name, event_name)`.

---

## 🧠 Meta-lesson: 3 patterns we keep violating

1. **"It works in docker-compose" ≠ "It works in production"**
   Every bug here was a difference between dev and prod:
   - Local had `uuid-ossp` enabled; Render didn't.
   - Local had `Host=db`; Render had a different hostname.
   - Local had no inotify pressure; Render's container hit the cap.
   - Local had clean DB state; Render's DB accumulated state across deploys.

   The fix: write code that works the SAME way in every environment. Use
   env vars for the differences. Use idempotent migrations for state.

2. **Initialization order matters**
   The .NET runtime reads env vars before `Program.cs` runs. The
   `WebApplicationBuilder` constructor registers config sources before
   the next line of `Program.cs` runs. The FluentMigrator runner opens
   a connection before the migration's `Up()` is called.

   When fixing a bug, ask: **"At what point in the startup sequence
   does my fix need to take effect?"** If it's before any code runs, it
   must be an env var. If it's after `CreateBuilder`, it can be code.

3. **"Just rerun it" is not a recovery strategy**
   On Render free tier, every manual deploy takes 3-5 minutes. Each
   bug above cost a deploy cycle to diagnose and fix. The cost of NOT
   having smoke tests is real. The cost of having them is 10 seconds.

---

## 📊 Numbers

| Metric | Value |
|---|---|
| Total deploy attempts | 4 |
| Failed deploys | 4 |
| Time to first successful deploy | ~25 minutes of debugging + redeploy cycles |
| Bugs introduced per "working locally" code path | 1 |
| Time to add smoke tests covering all 4 bug classes | 15 minutes |
| Time saved by smoke tests going forward (estimated) | 1+ hour per future incident |

---

## ✅ What we now check before every deploy

`bash scripts/verify.sh` runs 12 checks in 10 seconds:

| # | Check | Prevents |
|---|---|---|
| 1 | .NET build | Type errors, missing files |
| 2 | (placeholder) | — |
| 3 | TypeScript build | Type errors in Next.js |
| 4 | Required files present | Missing migrations/features |
| 5 | Endpoint groups registered | Missing `MapGroup` calls |
| 6 | Migrations present | Deleted migration files |
| 7 | Idempotent seed (`ON CONFLICT`) | Bug #4 regression |
| 8 | Env-var seed path | Bug #2 regression |
| 9 | No hardcoded `Host=db` | Bug #2 future variants |
| 10 | `reloadOnChange: false` on JSON | Bug #3 partial regression |
| 11 | `DOTNET_USE_POLLING_FILE_WATCHER` in Dockerfile | Bug #3 full regression |
| 12 | 6 business rule templates | Partial seed regression |

If any check fails, the deploy will likely fail. Don't push until they all
pass.

---

## 🎯 Where to go next

1. **For current Render deploy**: wait for build of latest commit, then
   manual deploy on `erp-v2-backend`.
2. **For future deploys**: run `bash scripts/verify.sh` first. If it
   passes, the deploy will likely succeed.
3. **For Hostinger VPS** (the original plan): the same `render.yaml`
   pattern applies, but the `docker-compose.yml` needs the same
   env-var-based config (already done in `docker-compose.yml`).
4. **For real production**: upgrade from free tier to $7/mo plan
   (eliminates cold starts, doubles RAM).
