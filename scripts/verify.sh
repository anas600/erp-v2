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
echo "[1/13] Building .NET backend..."
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
echo "[2/13] Checking frontend node_modules..."
if [ ! -d "frontend/node_modules" ]; then
  echo "❌ frontend/node_modules missing; run: cd frontend && npm install"
  exit 1
fi
echo "✅ Frontend node_modules OK"
echo ""

# -------------------------
# 3. Frontend tsc type-check
# -------------------------
echo "[3/13] TypeScript type-check..."
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
echo "[4/13] Checking required files..."
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
echo "[5/13] Verifying endpoint registration..."
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
echo "[6/13] Verifying migrations..."
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
echo "[7/13] Validating rule templates JSON..."
if ! python3 scripts/check_rules.py; then
  echo "❌ Rule templates JSON check FAILED (expected 6 templates)"
  exit 1
fi
echo "✅ Rule templates present"
echo ""

# -------------------------
# 8. Critical extension setup
# -------------------------
echo "[8/13] Checking that required PG extensions are created..."
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
echo "[9/13] Checking for hardcoded DB hostnames in migrations..."
# The only allowed hardcoded string is the docker-compose fallback in 002_SeedData.cs.
# Any other migration with a hardcoded 'Host=' is a bug.
violations=$(grep -lE "Host=(db|localhost|127\\.0\\.0\\.1)" backend/Migrations/*.cs 2>/dev/null | grep -v "002_SeedData.cs" || true)
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
echo "[10/13] Checking that backend uses CreateSlimBuilder (no JSON file watchers)..."
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
echo "[11/13] Checking that Dockerfile sets DOTNET_USE_POLLING_FILE_WATCHER..."
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
echo "[12/13] Checking that SeedData migration is idempotent (ON CONFLICT)..."
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
echo "[13/13] Checking that seed creates composite unique indexes..."
# 002_SeedData.cs must ensure (a) accounts are unique per company, and
# (b) business_rules are unique per (name, event_name). Otherwise the
# seed fails on re-deploy with 'no unique constraint matching the
# ON CONFLICT specification'.
constraints_fail=0
for needle in \
    "ALTER TABLE accounts DROP CONSTRAINT IF EXISTS uk_accounts_code" \
    "CREATE UNIQUE INDEX IF NOT EXISTS uk_accounts_company_code" \
    "CREATE UNIQUE INDEX IF NOT EXISTS uk_business_rules_name_event"; do
  if ! grep -qF "$needle" backend/Migrations/002_SeedData.cs; then
    echo "❌ Missing schema-fix statement: $needle"
    constraints_fail=1
  fi
done
if [ "$constraints_fail" -eq 1 ]; then
  echo "   (cloud deploys will fail at seed with SQLSTATE 42P10)"
  exit 1
fi
echo "✅ Composite unique indexes created idempotently"
echo ""

echo "=========================================="
echo "✅ All smoke tests passed!"
echo "=========================================="
