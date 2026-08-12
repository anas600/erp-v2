#!/bin/bash
# Sprint 41 — Auto-verify the deployed ERP system
# Calls /api/admin/verify which hits all report endpoints and
# returns PASS/FAIL per check. Designed to run from a cron.

set -e
BACKEND="${ERP_BACKEND:-https://erp-v2-backend-mkyg.onrender.com}"
COMPANY_ID="${ERP_COMPANY:-c9fba678-29db-43ec-8c34-5a35d205e79b}"
USER="${ERP_USER:-admin@holding.ly}"
PASS="${ERP_PASS:-admin123}"

echo "=== ERP Verification $(date -u +%Y-%m-%dT%H:%M:%SZ) ==="

# 1. Login
TOKEN=$(curl -s -X POST "$BACKEND/api/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$USER\",\"password\":\"$PASS\"}" \
  | python3 -c "import sys,json; print(json.load(sys.stdin).get('accessToken',''))")

if [ -z "$TOKEN" ]; then
  echo "❌ Login failed"
  exit 1
fi
echo "✅ Login OK"

# 2. Hit /api/admin/verify
RESULT=$(curl -s -H "Authorization: Bearer $TOKEN" \
  "$BACKEND/api/admin/verify?companyId=$COMPANY_ID")

echo "$RESULT" | python3 -c "
import sys, json
d = json.load(sys.stdin)
s = d.get('summary', {})
print(f\"  Total: {s.get('total')}, Passed: {s.get('passed')}, Failed: {s.get('failed')}, Overall: {s.get('overall')}\")
for c in d.get('checks', []):
  icon = '✅' if c.get('status') == 'PASS' else '❌'
  print(f\"  {icon} {c.get('report')}: {c.get('status')} (HTTP {c.get('http', '-')})\")
"

# 3. Hit /api/admin/journals-summary
echo ""
echo "=== Journal Entries Summary ==="
curl -s -H "Authorization: Bearer $TOKEN" \
  "$BACKEND/api/admin/journals-summary?companyId=$COMPANY_ID" \
  | python3 -c "
import sys, json
d = json.load(sys.stdin)
print(f\"  Total: {d.get('totalEntries')}\")
print(f\"  By status: {d.get('byStatus')}\")
print(f\"  By source: {d.get('bySource')}\")
"

echo ""
echo "=== Done ==="
