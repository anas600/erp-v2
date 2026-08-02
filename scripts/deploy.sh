#!/usr/bin/env bash
# Trigger Render deploys for ERP-V2 services.
#
# Usage:
#   1. Get the deploy hook URLs from Render dashboard:
#      - erp-v2-backend  → Settings → Deploy Hook
#      - erp-v2-frontend → Settings → Deploy Hook
#   2. Either:
#      a. Set as env vars:
#         export BACKEND_DEPLOY_HOOK="https://api.render.com/deploy/srv-xxx?key=yyy"
#         export FRONTEND_DEPLOY_HOOK="https://api.render.com/deploy/srv-zzz?key=www"
#         bash scripts/deploy.sh
#      b. Or pass as args:
#         bash scripts/deploy.sh <backend_hook> <frontend_hook>
#
# This script:
#   1. Triggers both deploys (in parallel, in background)
#   2. Waits for both to complete
#   3. Reports success/failure
#   4. Does NOT wait for the new container to be healthy
#      (Render sends a separate webhook for that)
#
# Note: deploy hooks bypass autoDeploy setting. Use them to control
# when Render burns free-tier hours.

set -euo pipefail

BACKEND_HOOK="${1:-${BACKEND_DEPLOY_HOOK:-}}"
FRONTEND_HOOK="${2:-${FRONTEND_DEPLOY_HOOK:-}}"

if [ -z "$BACKEND_HOOK" ] || [ -z "$FRONTEND_HOOK" ]; then
  echo "❌ Usage:"
  echo "   bash scripts/deploy.sh <backend_hook_url> <frontend_hook_url>"
  echo ""
  echo "   Or set env vars: BACKEND_DEPLOY_HOOK and FRONTEND_DEPLOY_HOOK"
  exit 1
fi

trigger_deploy() {
  local name="$1"
  local url="$2"
  echo "▶ Triggering $name deploy..."
  local response
  response=$(curl -s -X POST "$url" -w "\n%{http_code}")
  local code=$(echo "$response" | tail -n1)
  local body=$(echo "$response" | head -n -1)
  if [ "$code" = "200" ] || [ "$code" = "201" ]; then
    local deploy_id=$(echo "$body" | grep -o '"id":"[^"]*"' | head -1 | sed 's/"id":"//;s/"$//')
    echo "  ✅ $name deploy started (id: $deploy_id)"
    echo "$deploy_id"
  else
    echo "  ❌ $name deploy failed (HTTP $code): $body"
    return 1
  fi
}

echo "=========================================="
echo "ERP-V2 Deploy Script"
echo "=========================================="
echo ""

# Trigger both deploys and capture their IDs
BACKEND_ID=$(trigger_deploy "backend"  "$BACKEND_HOOK"  | tail -1)
FRONTEND_ID=$(trigger_deploy "frontend" "$FRONTEND_HOOK" | tail -1)

echo ""
echo "📋 Deploys in progress:"
echo "   backend:  $BACKEND_ID"
echo "   frontend: $FRONTEND_ID"
echo ""
echo "⏳ Render will build and deploy in 2-5 minutes."
echo "   Watch progress in: https://dashboard.render.com/"
echo ""
echo "✅ Health checks (after deploy completes):"
echo "   curl https://erp-v2-backend.onrender.com/health"
echo "   curl https://erp-v2-frontend.onrender.com/"
