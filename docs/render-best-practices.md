# Render.com Deployment — Lessons Learned

> Hard-won wisdom from taking ERP-V2 to production on Render free tier.
> Every bug here cost at least one deploy cycle. Read this BEFORE your first
> deploy, not after your fourth.

---

## 🪦 Postmortem: the 4 deploy bugs we hit

| # | Symptom | Root cause | Fix |
|---|---|---|---|
| 1 | `function uuid_generate_v4() does not exist` | `uuid-ossp` extension not enabled | `CREATE EXTENSION IF NOT EXISTS "uuid-ossp";` in migration |
| 2 | `connection to host=db:5432 refused` at startup | `Host=db` hardcoded in `002_SeedData.cs` from local docker-compose | Read connection string from `ConnectionStrings__Default` env var with docker-compose fallback |
| 3 | `inotify instance limit (128) reached` at startup | `FileSystemWatcher` enabled by default in `appsettings.json` config | `DOTNET_USE_POLLING_FILE_WATCHER=true` in Dockerfile ENV |
| 4 | `duplicate key IX_companies_code` on re-deploy | Seed INSERTs not idempotent + `Guid.NewGuid()` mismatched between runs | All INSERTs use `ON CONFLICT DO NOTHING` + re-read IDs by natural key |

The pattern: each bug is a **gap between "works in dev" and "works in a cloud container that gets restarted and re-deployed"**. Render's free tier made every gap visible because the same container was created and destroyed multiple times during debugging.

---

## 📐 The 5 Render invariants

These are non-negotiable. If you violate any of them, you WILL hit bugs.

### 1. **Every migration must be idempotent**
A cloud deploy can be retried by Render, by you, or by the platform. The same
migration will run more than once. **Every `INSERT` in a seed migration must
end with `ON CONFLICT DO NOTHING`**, and you must **re-read the row's id
afterwards** (don't reuse the locally-generated `Guid.NewGuid()` — it might
not match the existing row).

```csharp
// ❌ BAD: fails the second time
var id = Guid.NewGuid();
conn.Execute("INSERT INTO companies (id, code, name) VALUES (@id, 'HOLD', 'Holding');", new { id });

// ✅ GOOD: works every time
var id = conn.ExecuteScalar<Guid?>("SELECT id FROM companies WHERE code = 'HOLD';", transaction: tx) ?? Guid.NewGuid();
conn.Execute(@"
    INSERT INTO companies (id, code, name) VALUES (@id, 'HOLD', 'Holding')
    ON CONFLICT (code) DO NOTHING;",
    new { id }, tx);
```

### 2. **Every config value must come from env var or be a local fallback**
Hardcoding `Host=db`, `Server=localhost`, or any hostname in code means the
code only works in ONE environment. The 12-factor rule: code is the same in
dev, staging, prod. Only env vars differ.

```csharp
// ❌ BAD: only works in docker-compose
var conn = new NpgsqlConnection("Host=db;Port=5432;...");

// ✅ GOOD: env var first, local fallback
var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
    ?? "Host=db;Port=5432;...";
var conn = new NpgsqlConnection(cs);
```

### 3. **No inotify in containers**
Linux caps each user at 128 inotify instances. Containers often hit this on
startup when `WebApplication.CreateBuilder()` adds JSON config sources that
auto-enable file watchers. **Use polling instead** — it's slightly less
efficient but never hits the kernel cap.

```dockerfile
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
```

This env var is read by the .NET runtime **before any application code runs**,
so it must be set in the Dockerfile (or in Render's env vars), NOT in
`Program.cs`.

### 4. **No secrets in source — ever**
Even in a demo. Set `Jwt__Key` via Render's `generateValue: true` so each
deployment gets a fresh key. Never commit `.env` with real values to git.
Never bake a password into a Docker image.

### 5. **Health checks must be cheap and reliable**
Render uses `/health` to know when to route traffic. If the endpoint hits
the database, it can be slow under load. If it doesn't, it can lie (app is
up but DB is down). **The `/health` endpoint should verify both the app is
running AND the database is reachable**.

```csharp
// ✅ GOOD: health check that includes DB
app.MapGet("/health", async (NpgsqlDataSource db) => {
    try {
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync();
        return Results.Ok(new { status = "healthy", db = "ok" });
    } catch (Exception ex) {
        return Results.Problem("DB unreachable: " + ex.Message);
    }
});
```

---

## 🌐 Render-specific quirks

| Quirk | What it means | How to handle |
|---|---|---|
| **Free tier sleeps after 15 min idle** | First request after sleep takes 30-60s | Acceptable for demo; upgrade for production |
| **Cold start = full app restart** | All in-memory state is lost; seed migrations run again | Migrations must be idempotent (see #1 above) |
| **Container is destroyed between deploys** | No filesystem persistence; use Postgres | All app state lives in the DB |
| **512 MB RAM on free tier** | .NET + Next.js + Postgres = tight | Disable unused services, log to stdout only |
| **Build context is the service rootDir** | `dockerfilePath` and `dockerContext` are relative | Point `dockerContext: .` to project root for multi-service repos |
| **Build is cached by Render** | Same Dockerfile = faster rebuild | Cached layers speed up iteration |
| **Health check is on port 8080 by default** | We use `ASPNETCORE_URLS=http://+:8080` | Must match the internal port Render expects |
| **`autoDeploy: true` redeploys on every push** | Costs free-tier hours | Set `autoDeploy: false` to deploy manually |

---

## 🧪 Pre-deploy checklist (run `bash scripts/verify.sh` first)

| # | Check | Why |
|---|---|---|
| 1 | `dotnet build` | Catches type errors before commit |
| 2 | `npm run build` | Catches TypeScript errors before commit |
| 3 | All required files present | Catches missing migrations or features |
| 4 | Endpoints registered | Catches missing `MapGroup` calls |
| 5 | Migrations present | Catches deleted `001_InitialSchema.cs` |
| 6 | `ON CONFLICT` on seed inserts | Catches non-idempotent migrations |
| 7 | Env-var seed path | Catches hardcoded hostnames |
| 8 | No hardcoded `Host=db` | Catches leaking docker-compose hosts into prod code |
| 9 | `reloadOnChange: false` on JSON | Catches inotify-prone config |
| 10 | `DOTNET_USE_POLLING_FILE_WATCHER` in Dockerfile | Catches missing container env var |
| 11 | All 6 business rule templates | Catches partial seed migration |

**If any test fails, the deploy WILL fail in production. Do not skip.**

---

## 🆘 Recovery playbook

When a deploy fails, work the stack from the bottom up.

### Step 1: check the build phase
Look for `dotnet publish` errors. This is the cheapest place to fail.

### Step 2: check the container startup
Look for the line `at Program.<Main>$(String[] args) in /src/Program.cs:line 18`.
That means `WebApplication.CreateBuilder()` failed before your code ran.
The cause is usually inotify, missing env var, or unparseable config.

### Step 3: check migrations
Look for `FluentMigrator.Runner.MigrationRunner[*]` log lines. If you see
`Beginning Transaction` followed by `Rolling back transaction`, find the
SQL state code (e.g. `23505` = unique violation, `42P01` = table not found,
`42703` = column not found) and search the codebase for that constraint.

### Step 4: check the HTTP server
If the app starts and migrates but Render says "no open ports", check
`ASPNETCORE_URLS`. The default `http://localhost:5000` won't work in a
container. Use `http://+:8080` so Render's port scan finds it.

### Step 5: check the health endpoint
Hit `https://<service>.onrender.com/health` from a browser. If it 200s,
the app is running. If it 502/503, the app is still starting or the
process exited.

### Step 6: nuke and rebuild
If you can't figure out the state, the fastest path is:
1. Delete the failing service on Render
2. Delete the Postgres database on Render
3. Re-create both from the same `render.yaml`
4. Run a fresh manual deploy

This is what free tier is for. Don't waste time debugging stale state.

---

## 💡 Patterns that pay off

### 1. **Configuration via env vars, not appsettings.json**
```json
// ❌ appsettings.Production.json (gets baked into image)
{ "ConnectionStrings": { "Default": "Host=..." } }

// ✅ Environment variable on Render
envVars:
  - key: ConnectionStrings__Default
    fromDatabase:
      name: erp-v2-db
      property: connectionString
```

### 2. **Migrations as code, not as a separate process**
Run FluentMigrator / EF Migrations / Flyway on app startup, in `Program.cs`,
BEFORE the HTTP server starts. This way the schema and the app are always
in sync, and a bad migration is caught at deploy time, not at first request.

### 3. **Single transaction per migration**
Even seed migrations. If you have many INSERTs, wrap them all in one
`BEGIN`/`COMMIT` so a failure mid-way rolls back the whole seed. AND use
`ON CONFLICT DO NOTHING` so re-running is safe.

### 4. **Test migrations against a fresh DB locally**
```bash
docker run -d --name pg-test -e POSTGRES_PASSWORD=test -p 5433:5432 postgres:15
ConnectionStrings__Default="Host=localhost;Port=5433;Database=erp;..." dotnet run
docker stop pg-test && docker rm pg-test
```

### 5. **Smoke test before push**
`bash scripts/verify.sh` takes 10 seconds and catches 11 categories of bugs.
Run it BEFORE `git push`, not after.

---

## 📋 Render free tier limits (as of 2026-08)

| Resource | Limit | Implication |
|---|---|---|
| **RAM per service** | 512 MB | Tight for .NET + Next.js; consider $7/mo plan for production |
| **CPU** | 0.1 vCPU | Slow builds (~3-5 min for .NET publish) |
| **Bandwidth** | 100 GB/month | Enough for ~50k page views |
| **Build minutes** | 500/month | 30 deploys/month is safe |
| **Postgres storage** | 1 GB | Enough for ~100k journal entries |
| **Postgres connections** | 100 | Use a connection pool with max=20 |
| **Sleep after idle** | 15 min | First request takes 30-60s |
| **Custom domains** | Unlimited | Use them — they look more professional |

For a real production demo, upgrade to the $7/mo Web Service plan. The cold
starts alone are not acceptable for a paying customer.

---

## 🎓 TL;DR

1. **Migrations must be idempotent** (cloud = re-runs)
2. **Config from env vars only** (one codebase, many environments)
3. **No inotify in containers** (`DOTNET_USE_POLLING_FILE_WATCHER=true`)
4. **No secrets in source** (`generateValue: true` in render.yaml)
5. **Health check is cheap but real** (200 OK means app + DB work)

When in doubt, read the logs. Render shows you everything: build output,
container stdout, health check results. The answer is almost always in
the logs.
