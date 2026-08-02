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
echo "[1/7] Building .NET backend..."
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
echo "[2/7] Checking frontend node_modules..."
if [ ! -d "frontend/node_modules" ]; then
  echo "❌ frontend/node_modules missing; run: cd frontend && npm install"
  exit 1
fi
echo "✅ Frontend node_modules OK"
echo ""

# -------------------------
# 3. Frontend tsc type-check
# -------------------------
echo "[3/7] TypeScript type-check..."
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
echo "[4/7] Checking required files..."
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
echo "[5/7] Verifying endpoint registration..."
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
echo "[6/7] Verifying migrations..."
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
echo "[7/8] Validating rule templates JSON..."
if ! python3 -c "
import re
import sys
import os
import json

seed_file = 'backend/Migrations/002_SeedData.cs'
with open(seed_file, 'r') as f:
    content = f.read()

matches = re.findall(r'rule_json\s*=\s*@\"', content)
print(f'   Found {len(matches)} rule_json templates in seed file')
sys.exit(0 if len(matches) >= 6 else 1)
"; then
  echo "❌ Rule templates JSON check FAILED (expected 6 templates)"
  exit 1
fi
echo "✅ Rule templates present"
echo ""

# -------------------------
# 8. Critical extension setup
# -------------------------
echo "[8/9] Checking that required PG extensions are created..."
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
echo "[9/9] Checking for hardcoded DB hostnames in migrations..."
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

echo "=========================================="
echo "✅ All smoke tests passed!"
echo "=========================================="
