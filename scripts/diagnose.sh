#!/usr/bin/env bash
# Quick diagnostic for a deployed ERP-V2 instance.
# Usage: BASE_URL=https://erp-v2-backend.onrender.com bash scripts/diagnose.sh

set -e
BASE="${BASE_URL:-https://erp-v2-backend.onrender.com}"

echo "=========================================="
echo "ERP-V2 Deployment Diagnostic"
echo "=========================================="
echo ""

# 1. Health check
echo "[1/4] Health endpoint..."
HTTP_CODE=$(curl -s -o /tmp/health.json -w "%{http_code}" "$BASE/health" || echo "000")
if [ "$HTTP_CODE" = "200" ]; then
  echo "  ✅ /health returned 200"
  cat /tmp/health.json
  echo ""
else
  echo "  ❌ /health returned $HTTP_CODE"
fi
echo ""

# 2. Login attempt
echo "[2/4] Login attempt (admin@holding.ly)..."
LOGIN_RESPONSE=$(curl -s -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@holding.ly","password":"admin123"}' || echo "FAILED")
if echo "$LOGIN_RESPONSE" | grep -q "token"; then
  echo "  ✅ Login succeeded"
  TOKEN=$(echo "$LOGIN_RESPONSE" | grep -o '"token":"[^"]*"' | sed 's/"token":"//;s/"$//')
  echo "  Token (first 30 chars): ${TOKEN:0:30}..."

  # 3. Use the token
  echo ""
  echo "[3/4] /api/companies with token..."
  COMPANIES=$(curl -s "$BASE/api/companies" -H "Authorization: Bearer $TOKEN" || echo "FAILED")
  if echo "$COMPANIES" | grep -q "\[\]"; then
    echo "  ⚠️  /api/companies returned empty array"
    echo "  → DB is empty (seed didn't run or was rolled back)"
  elif echo "$COMPANIES" | grep -q "id"; then
    echo "  ✅ /api/companies returned data:"
    echo "$COMPANIES" | head -200
  else
    echo "  ❌ /api/companies returned unexpected response:"
    echo "$COMPANIES"
  fi

  # 4. Check accounts
  echo ""
  echo "[4/4] /api/accounts with token..."
  ACCOUNTS=$(curl -s "$BASE/api/accounts" -H "Authorization: Bearer $TOKEN" || echo "FAILED")
  if echo "$ACCOUNTS" | grep -q "\[\]"; then
    echo "  ⚠️  /api/accounts returned empty array"
  elif echo "$ACCOUNTS" | grep -q "id"; then
    echo "  ✅ /api/accounts returned data"
    echo "  Account count: $(echo "$ACCOUNTS" | grep -o '"id":' | wc -l)"
  else
    echo "  ❌ /api/accounts returned unexpected response:"
    echo "$ACCOUNTS"
  fi
else
  echo "  ❌ Login failed. Response:"
  echo "$LOGIN_RESPONSE" | head -200
  echo ""
  echo "  This usually means:"
  echo "  - DB is empty (no users to login with) → recreate DB"
  echo "  - Connection string is wrong → check env vars"
  echo "  - DB has wrong schema → recreate DB"
fi
echo ""
echo "=========================================="
