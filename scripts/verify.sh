#!/usr/bin/env bash
# ===========================================================
# ERP-V2 smoke tests
# Verifies that the project is in a deployable state.
# Usage: bash scripts/verify.sh
# ===========================================================
set -e

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

echo "=========================================="
echo "ERP-V2 Smoke Tests"
echo "=========================================="
echo ""

# -------------------------
# 1. Backend dotnet build
# -------------------------
echo "[1/18] Building .NET backend..."
cd backend
if ! dotnet build --no-restore --nologo 2>&1 | tail -1; then
  echo "❌ Backend build FAILED"
  exit 1
fi
cd ..
echo "✅ Backend build OK"
echo ""

# -------------------------
# 2. Frontend npm install
# -------------------------
echo "[2/18] Checking frontend node_modules..."
if [ ! -d "frontend/node_modules" ]; then
  echo "❌ frontend/node_modules missing; run: cd frontend && npm install"
  exit 1
fi
echo "✅ Frontend node_modules OK"
echo ""

# -------------------------
# 3. Frontend tsc type-check
# -------------------------
echo "[3/18] TypeScript type-check..."
cd frontend
if ! npx tsc --noEmit 2>&1 | head -20; then
  echo "❌ TypeScript check FAILED"
  exit 1
fi
cd ..
echo "✅ TypeScript OK"
echo ""

# -------------------------
# 4. Required files exist
# -------------------------
echo "[4/18] Checking required files..."
REQUIRED=(
  "docker-compose.yml"
  ".env.example"
  "README.md"
  "AGENTS.md"
  "CONSTITUTION.md"
  "render.yaml"
  "backend/Program.cs"
  "backend/ErpV2.csproj"
  "backend/Dockerfile"
  "frontend/package.json"
  "frontend/Dockerfile"
  "frontend/next.config.js"
  "docs/architecture.md"
  "docs/user-guide.md"
  "docs/deployment.md"
  "docs/deploy-render.md"
)
for f in "${REQUIRED[@]}"; do
  if [ ! -f "$f" ]; then
    echo "❌ Missing: $f"
    exit 1
  fi
done
echo "✅ All required files present"
echo ""

# -------------------------
# 5. All endpoints registered
# -------------------------
echo "[5/18] Verifying endpoint registration..."
EXPECTED_ENDPOINTS=(
  "AuthEndpoints.Map"
  "CompanyEndpoints.Map"
  "AccountEndpoints.Map"
  "JournalEndpoints.Map"
  "RuleEndpoints.Map"
  "ReportEndpoints.Map"
  "InvoiceEndpoints.Map"
  "ProjectEndpoints.Map"
  "UserEndpoints.Map"
)
PROGRAM_CS="backend/Program.cs"
for ep in "${EXPECTED_ENDPOINTS[@]}"; do
  if ! grep -q "$ep" "$PROGRAM_CS"; then
    echo "❌ Missing endpoint registration: $ep"
    exit 1
  fi
done
echo "✅ All ${#EXPECTED_ENDPOINTS[@]} endpoint groups registered"
echo ""

# -------------------------
# 6. All migrations present
# -------------------------
echo "[6/18] Verifying migrations..."
EXPECTED_MIGRATIONS=(
  "001_InitialSchema.cs"
  "002_SeedData.cs"
  "003_InvoicingSchema.cs"
  "004_ProjectsSchema.cs"
)
for m in "${EXPECTED_MIGRATIONS[@]}"; do
  if [ ! -f "backend/Migrations/$m" ]; then
    echo "❌ Missing migration: $m"
    exit 1
  fi
done
echo "✅ All ${#EXPECTED_MIGRATIONS[@]} migrations present"
echo ""

# -------------------------
# 7. JSON validity of rule templates
# -------------------------
echo "[7/18] Validating rule templates JSON..."
if ! python3 scripts/check_rules.py; then
  echo "❌ Rule templates JSON check FAILED (expected 6 templates)"
  exit 1
fi
echo "✅ Rule templates present"
echo ""

# -------------------------
# 8. Critical extension setup
# -------------------------
echo "[8/18] Checking that required PG extensions are created..."
if ! grep -q "uuid-ossp" backend/Migrations/001_InitialSchema.cs; then
  echo "❌ InitialSchema does not CREATE EXTENSION \"uuid-ossp\""
  echo "   (FluentMigrator's SystemMethods.NewGuid emits uuid_generate_v4() which needs this)"
  exit 1
fi
echo "✅ uuid-ossp extension setup OK"
echo ""

# -------------------------
# 9. No hardcoded connection strings in migrations
# -------------------------
echo "[9/18] Checking for hardcoded DB hostnames in migrations..."
# The only allowed hardcoded string is the docker-compose fallback in 002_SeedData.cs.
# Any other migration with a hardcoded 'Host=' is a bug.
violations=$(grep -lE "Host=(db|localhost|127\\.0\\.0\\.1)" backend/Migrations/*.cs 2>/dev/null | grep -v -E "(002|005)_.*\\.cs$" || true)
if [ -n "$violations" ]; then
  echo "❌ Hardcoded DB hostname found in: $violations"
  echo "   Use Environment.GetEnvironmentVariable(\"ConnectionStrings__Default\") instead."
  exit 1
fi
echo "✅ No hardcoded DB hostnames in migrations (except 002 fallback)"
echo ""

# -------------------------
# 10. Production-safe config (CreateSlimBuilder)
# -------------------------
echo "[10/18] Checking that backend uses CreateSlimBuilder (no JSON file watchers)..."
# The old code used CreateBuilder() + Sources.Clear() which was too late:
# the file watchers are created during CreateBuilder() itself, before any
# user code runs. CreateSlimBuilder() skips the JSON config sources entirely,
# so no file watchers are ever created. This is the only way to be safe in
# a container with the 128-instance inotify limit.
if ! grep -q "WebApplication.CreateSlimBuilder" backend/Program.cs; then
  echo "❌ Program.cs does not use WebApplication.CreateSlimBuilder"
  echo "   (CreateBuilder + Sources.Clear() is too late; file watchers"
  echo "    are created before user code runs)"
  exit 1
fi
echo "✅ Backend uses CreateSlimBuilder (no JSON file watchers)"
echo ""

# -------------------------
# 11. Polling file watcher env var in Dockerfile
# -------------------------
echo "[11/18] Checking that Dockerfile sets DOTNET_USE_POLLING_FILE_WATCHER..."
if ! grep -q "DOTNET_USE_POLLING_FILE_WATCHER" backend/Dockerfile; then
  echo "❌ backend/Dockerfile does not set DOTNET_USE_POLLING_FILE_WATCHER"
  echo "   (containers hit the inotify instance limit at startup)"
  exit 1
fi
echo "✅ Dockerfile uses polling file watcher (container-safe)"
echo ""

# -------------------------
# 12. Seed migration is idempotent
# -------------------------
echo "[12/18] Checking that SeedData migration is idempotent (ON CONFLICT)..."
# At minimum, the bulk INSERTs in 002_SeedData must be idempotent.
# We check the major tables: companies, users, accounts, business_rules.
idempotent_fail=0
for keyword in "ON CONFLICT (code) DO NOTHING" "ON CONFLICT (email) DO NOTHING" "ON CONFLICT (company_id, code) DO NOTHING" "ON CONFLICT (name, event_name) DO NOTHING"; do
  if ! grep -qF "$keyword" backend/Migrations/002_SeedData.cs; then
    echo "❌ Missing ON CONFLICT pattern: $keyword"
    idempotent_fail=1
  fi
done
if [ "$idempotent_fail" -eq 1 ]; then
  echo "   (deploys on cloud will re-run this migration; inserts must be idempotent)"
  exit 1
fi
echo "✅ SeedData is idempotent (safe to re-run on cloud deploys)"
echo ""

# -------------------------
# 13. Multi-company unique constraints
# -------------------------
echo "[13/18] Checking that seed creates composite unique indexes..."
# 002_SeedData.cs must ensure (a) accounts are unique per company, and
# (b) business_rules are unique per (name, event_name). Otherwise the
# seed fails on re-deploy with 'no unique constraint matching the
# ON CONFLICT specification'.
#
# The schema fix must use DROP INDEX (not DROP CONSTRAINT) because
# uk_accounts_code was created as a unique index by FluentMigrator's
# Create.Index().Unique(), not as an ALTER TABLE constraint.
constraints_fail=0
for needle in \
    "DROP INDEX IF EXISTS uk_accounts_code" \
    "CREATE UNIQUE INDEX IF NOT EXISTS uk_accounts_company_code" \
    "CREATE UNIQUE INDEX IF NOT EXISTS uk_business_rules_name_event"; do
  if ! grep -qF "$needle" backend/Migrations/002_SeedData.cs; then
    echo "❌ Missing schema-fix statement: $needle"
    constraints_fail=1
  fi
done
# Also catch the historical bug: using DROP CONSTRAINT on an index.
if grep -qF "ALTER TABLE accounts DROP CONSTRAINT IF EXISTS uk_accounts_code" backend/Migrations/002_SeedData.cs; then
  echo "❌ DROP CONSTRAINT is wrong — uk_accounts_code is an INDEX, not a constraint"
  constraints_fail=1
fi
if [ "$constraints_fail" -eq 1 ]; then
  echo "   (cloud deploys will fail at seed with SQLSTATE 42P10 or 23505)"
  exit 1
fi
echo "✅ Composite unique indexes created idempotently"
echo ""

# -------------------------
# 14. Schema fix runs in separate connection (autocommit)
# -------------------------
echo "[14/18] Checking that schema fix runs OUTSIDE the seed transaction..."
# If the DDL is inside the same transaction as the seed, a failure
# of the seed (which is common on re-deploys) will roll back the DDL
# too, and we're back to the bug. The DDL must be in its own
# connection (no explicit transaction) so it commits before the seed
# even starts.
if ! sed -n '/public override void Up/,/using var conn = new Npgsql.NpgsqlConnection/p' backend/Migrations/002_SeedData.cs | grep -q "schemaConn.Open()"; then
  echo "❌ Schema fix must be in its own connection (schemaConn.Open())"
  echo "   so it commits independently of the seed transaction"
  exit 1
fi
echo "✅ Schema fix runs in autocommit (survives seed failures)"
echo ""

# -------------------------
# 15. Regex route constraint registered (SlimBuilder fix)
# -------------------------
echo "[15/18] Checking that regex route constraint is registered..."
# CreateSlimBuilder() does NOT register RegexInlineRouteConstraint by
# default. Swashbuckle (Swagger) uses regex constraints in its route
# templates, so without explicit registration, the app crashes at
# startup with "RegexErrorStubRouteConstraint: ... isn't registered".
# We must call SetParameterPolicy<RegexInlineRouteConstraint>("regex")
# during DI setup. This check catches a regression to CreateBuilder +
# Sources.Clear (the old broken approach) AND catches a regression to
# CreateSlimBuilder without the regex fix.
if ! grep -q "SetParameterPolicy<.*RegexInlineRouteConstraint>" backend/Program.cs; then
  echo "❌ Regex route constraint is not registered"
  echo "   (Swashbuckle's SwaggerMiddleware will fail with:"
  echo "    'A route parameter uses the regex constraint, which isn't registered')"
  exit 1
fi
if ! grep -q "WebApplication.CreateSlimBuilder" backend/Program.cs; then
  echo "❌ SlimBuilder is not in use; file watchers will be created"
  echo "   (see test 10/15 above)"
  exit 1
fi
echo "✅ Regex route constraint registered (SlimBuilder compatible)"
echo ""

# -------------------------
# 16. Render blueprint has connection string binding
# -------------------------
echo "[16/18] Checking that render.yaml wires the connection string..."
# Without this, the backend starts with an empty connection string
# and the migration runner throws:
#   "Format of the initialization string does not conform to
#    specification starting at index 0."
if ! grep -q "fromDatabase" render.yaml; then
  echo "❌ render.yaml has no fromDatabase reference for ConnectionStrings__Default"
  echo "   (backend will start with empty connection string)"
  exit 1
fi
if ! grep -q "name: erp-v2-db" render.yaml; then
  echo "❌ render.yaml does not reference the erp-v2-db resource"
  exit 1
fi
echo "✅ render.yaml binds ConnectionStrings__Default to the database"
echo ""

# -------------------------
# 17a. Frontend uses relative API paths (no NEXT_PUBLIC_API_URL)
# -------------------------
echo "[17/18] Checking that frontend uses relative /api paths..."
# Hardcoding NEXT_PUBLIC_API_URL with fromService is fragile:
#   - fromService only supports host/hostport/port/connectionString
#     (not url), so the user's Blueprint fails to apply.
#   - Render may suffix the backend URL (-86pf, etc.), so any
#     hardcoded value goes stale on re-creation.
# The robust approach: use relative paths (/api/...) and let
# Next.js's rewrite forward to the backend at request time.
if grep -E "process\.env\.NEXT_PUBLIC_API_URL|NEXT_PUBLIC_API_URL\s*=" frontend/src/lib/api.ts > /dev/null; then
  echo "❌ frontend/src/lib/api.ts still uses NEXT_PUBLIC_API_URL as a value"
  echo "   (use relative '/api' so Next.js rewrites forward to the backend)"
  exit 1
fi
if ! grep -q 'baseURL: "/api"' frontend/src/lib/api.ts; then
  echo "❌ frontend baseURL is not '/api'"
  echo "   (Next.js rewrites need a relative path to forward)"
  exit 1
fi
if grep -q "fromService" render.yaml && grep -A 2 "fromService" render.yaml | grep -q "property: url"; then
  echo "❌ render.yaml still uses fromService with property: url (invalid)"
  echo "   Render Blueprint rejects this; deploys will fail"
  exit 1
fi
echo "✅ Frontend uses relative /api paths (no fromService url)"
echo ""

# -------------------------
# 17. Program.cs normalizes postgresql:// URLs
# -------------------------
echo "[18/18] Checking that Program.cs normalizes postgresql:// URLs..."
# Render's fromDatabase returns a postgresql:// URL. Npgsql requires
# key=value format. Without normalization, the migration runner throws
# 'Format of the initialization string does not conform to
#  specification starting at index 0' on every fresh deploy.
if ! grep -q "NormalizeConnectionString" backend/Program.cs; then
  echo "❌ Program.cs does not normalize postgresql:// URLs"
  echo "   (Npgsql will reject the URL format from Render's fromDatabase)"
  exit 1
fi
if ! grep -q "postgresql://" backend/Program.cs; then
  echo "❌ Program.cs doesn't reference postgresql:// URL format"
  echo "   (normalization code missing)"
  exit 1
fi
# Confirm we use a manual URL parser (System.Uri) and NOT a naive
# pass-through to NpgsqlConnectionStringBuilder, which cannot parse
# URL format and would throw ArgumentException on the first deploy.
if grep -q "new Npgsql.NpgsqlConnectionStringBuilder(raw)" backend/Program.cs; then
  echo "❌ Normalization passes the URL directly to NpgsqlConnectionStringBuilder"
  echo "   That class only accepts key=value format. The URL must be parsed"
  echo "   manually with System.Uri first."
  exit 1
fi
if ! grep -q "new System.Uri\|new Uri(raw)\|new Uri(raw" backend/Program.cs; then
  echo "❌ Normalization is not using System.Uri to parse the URL"
  exit 1
fi
echo "✅ Program.cs normalizes postgresql:// URLs to Npgsql format"
echo ""

echo "=========================================="
echo "✅ All smoke tests passed!"
echo "=========================================="
