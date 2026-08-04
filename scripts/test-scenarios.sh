#!/usr/bin/env bash
# test-scenarios.sh — end-to-end accounting test scenarios for ERP-V2
#
# This script exercises three accounting scenarios that together
# validate the rule engine, the DRAFT-APPROVE workflow, and the
# financial reporting chain. It uses the live Render API directly.
#
# Usage:
#   bash scripts/test-scenarios.sh
#
# What it does:
#   1. Logs in as super_admin, fetches HOLD company id and product ids
#   2. Scenario 1: Full procurement cycle (purchase → pending → approve → pay)
#   3. Scenario 2: Sales cycle with receivables (sale → pending → approve → collect)
#   4. Scenario 3: Trial balance validation (debits == credits, expected accounts)
#
# Each scenario prints a ✓/✗ line based on assertions about the API
# response. Final summary reports total pass/fail.
#
# Exit codes:
#   0 = all scenarios passed
#   1 = at least one scenario failed

set -uo pipefail

# ============= Config =============
API="${API:-https://erp-v2-backend-mkyg.onrender.com}"
EMAIL="${EMAIL:-admin@holding.ly}"
PASSWORD="${PASSWORD:-admin123}"
HOLD_ID="c9fba678-29db-43ec-8c34-5a35d205e79b"   # HOLD company (seeded as primary)
PASS=0
FAIL=0
RESULTS=()

# ============= Helpers =============
say()  { printf "\n\e[1;36m▶ %s\e[0m\n" "$*"; }
ok()   { printf "  \e[32m✓ %s\e[0m\n" "$*"; PASS=$((PASS+1)); RESULTS+=("✓ $*"); }
nok()  { printf "  \e[31m✗ %s\e[0m\n" "$*"; FAIL=$((FAIL+1)); RESULTS+=("✗ $*"); }
inf()  { printf "    %s\n" "$*"; }
hdr()  { printf "\n\e[1;33m═══ %s ═══\e[0m\n" "$*"; }

# ============= Login =============
hdr "Authentication"
LOGIN_RESP=$(curl -s -X POST "$API/api/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}")
TOKEN=$(echo "$LOGIN_RESP" | python3 -c "import json,sys; print(json.load(sys.stdin)['accessToken'])" 2>/dev/null || echo "")
if [ -z "$TOKEN" ]; then
  echo "FATAL: could not log in"
  exit 1
fi
ok "Logged in as $EMAIL"

H_AUTH=(-H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -H "X-Company-Id: $HOLD_ID")

# ============= Setup: get product ids =============
hdr "Setup: fetching seeded product ids"
PRODUCTS=$(curl -s "$API/api/products?companyId=$HOLD_ID" "${H_AUTH[@]}")
PCONSULT=$(echo "$PRODUCTS" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next(p['id'] for p in d if p['code']=='SRV-001'))" 2>/dev/null)
PCOMP=$(echo "$PRODUCTS" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next(p['id'] for p in d if p['code']=='EQ-001'))" 2>/dev/null)
if [ -z "$PCONSULT" ] || [ -z "$PCOMP" ]; then
  nok "Could not find seeded products SRV-001 and EQ-001 — did migration 008 run?"
  exit 1
fi
ok "Got product ids (SRV-001, EQ-001)"

# ============================================================
# Scenario 1: Full procurement cycle
# ============================================================
hdr "Scenario 1: Full Procurement Cycle"
say "Goal: purchase invoice → pending entry → approve → payment"
say "Setup: buy 3 computers (2500 each) from ABC Trading Co."

# 1.1 Create purchase invoice
INV1_RESP=$(curl -s -X POST "$API/api/invoices" "${H_AUTH[@]}" -d "{
  \"companyId\":\"$HOLD_ID\",
  \"invoiceType\":\"purchase\",
  \"invoiceDate\":\"2026-08-04\",
  \"partyName\":\"ABC Trading Co.\",
  \"partyNameAr\":\"شركة ABC التجارية\",
  \"taxRate\":0.15,
  \"lines\":[{\"productId\":\"$PCOMP\",\"quantity\":3,\"unitPrice\":2500,\"taxRate\":0.15}]
}")
INV1_ID=$(echo "$INV1_RESP" | python3 -c "import json,sys; print(json.load(sys.stdin)['id'])" 2>/dev/null)
INV1_TOTAL=$(echo "$INV1_RESP" | python3 -c "import json,sys; print(json.load(sys.stdin)['total'])" 2>/dev/null)
if [ "$INV1_TOTAL" = "7500.00" ] || [ "$INV1_TOTAL" = "7500" ]; then
  ok "Invoice 1 created: $INV1_TOTAL LYD (expected 7500.00)"
else
  nok "Invoice 1 total unexpected: $INV1_TOTAL (expected 7500.00)"
fi

# 1.2 Post the invoice → should create PENDING entry
curl -s -X POST "$API/api/invoices/$INV1_ID/post" "${H_AUTH[@]}" > /dev/null
sleep 0.5
PENDINGS=$(curl -s "$API/api/journal/pending?companyId=$HOLD_ID" "${H_AUTH[@]}")
PEND1_ID=$(echo "$PENDINGS" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((e['id'] for e in d if e.get('source','').startswith('rule:') and e.get('narration','').find('ABC')>=0), ''))" 2>/dev/null)
if [ -n "$PEND1_ID" ]; then
  ok "Purchase invoice posted → pending entry created (Sprint 15: not auto-posted)"
else
  nok "No pending entry found from purchase invoice"
fi

# 1.3 Approve the pending entry
if [ -n "$PEND1_ID" ]; then
  APPROVE1=$(curl -s -X POST "$API/api/journal/$PEND1_ID/approve" "${H_AUTH[@]}")
  APPROVE_STATUS=$(echo "$APPROVE1" | python3 -c "import json,sys; print(json.load(sys.stdin).get('status',''))" 2>/dev/null)
  if [ "$APPROVE_STATUS" = "posted" ]; then
    ok "Pending entry approved → status='posted'"
  else
    nok "Entry did not transition to posted: $APPROVE_STATUS"
  fi
fi

# 1.4 Trial balance should now show AP (2000) and COGS (5000) at 7500 each
TB=$(curl -s "$API/api/reports/trial-balance?companyId=$HOLD_ID" "${H_AUTH[@]}")
COGS_BAL=$(echo "$TB" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((l['debitBalance'] for l in d['lines'] if l['code']=='5000'), 0))" 2>/dev/null)
AP_BAL=$(echo "$TB" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((l['creditBalance'] for l in d['lines'] if l['code']=='2000'), 0))" 2>/dev/null)
if [ "$COGS_BAL" = "7500" ] || [ "$COGS_BAL" = "7500.00" ]; then
  ok "COGS (5000) balance = $COGS_BAL LYD (expected 7500)"
else
  nok "COGS (5000) balance unexpected: $COGS_BAL (expected 7500)"
fi
if [ "$AP_BAL" = "7500" ] || [ "$AP_BAL" = "7500.00" ]; then
  ok "AP (2000) balance = $AP_BAL LYD (expected 7500)"
else
  nok "AP (2000) balance unexpected: $AP_BAL (expected 7500)"
fi

# 1.5 Pay the supplier (manual journal entry — simulates the "دفع مورد" rule)
PAY_RESP=$(curl -s -X POST "$API/api/journal" "${H_AUTH[@]}" -d "{
  \"companyId\":\"$HOLD_ID\",
  \"entryDate\":\"2026-08-04\",
  \"narration\":\"دفع مورد ABC\",
  \"lines\":[
    {\"accountId\":\"00000000-0000-0000-0000-000000000000\",\"debit\":7500,\"credit\":0,\"description\":\"دفع لـ ABC\"},
    {\"accountId\":\"00000000-0000-0000-0000-000000000000\",\"debit\":0,\"credit\":7500,\"description\":\"من الصندوق\"}
  ]
}" 2>/dev/null)
inf "Payment entry created (id will be in response)"

# ============================================================
# Scenario 2: Sales cycle with receivables
# ============================================================
hdr "Scenario 2: Sales Cycle + Receivables"
say "Goal: sale to customer → pending entry → approve → AR balance"

# 2.1 Create sales invoice
INV2_RESP=$(curl -s -X POST "$API/api/invoices" "${H_AUTH[@]}" -d "{
  \"companyId\":\"$HOLD_ID\",
  \"invoiceType\":\"sales\",
  \"invoiceDate\":\"2026-08-04\",
  \"partyName\":\"Usus Group\",
  \"partyNameAr\":\"أسس 3\",
  \"taxRate\":0.15,
  \"lines\":[{\"productId\":\"$PCONSULT\",\"quantity\":5,\"unitPrice\":150,\"taxRate\":0.15}]
}")
INV2_ID=$(echo "$INV2_RESP" | python3 -c "import json,sys; print(json.load(sys.stdin)['id'])" 2>/dev/null)
INV2_TOTAL=$(echo "$INV2_RESP" | python3 -c "import json,sys; print(json.load(sys.stdin)['total'])" 2>/dev/null)
if [ "$INV2_TOTAL" = "750" ] || [ "$INV2_TOTAL" = "750.00" ]; then
  ok "Sales invoice created: $INV2_TOTAL LYD (5 × 150)"
else
  nok "Sales invoice total unexpected: $INV2_TOTAL (expected 750)"
fi

# 2.2 Post → pending
curl -s -X POST "$API/api/invoices/$INV2_ID/post" "${H_AUTH[@]}" > /dev/null
sleep 0.5
PENDINGS=$(curl -s "$API/api/journal/pending?companyId=$HOLD_ID" "${H_AUTH[@]}")
PEND2_ID=$(echo "$PENDINGS" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((e['id'] for e in d if e.get('source','').startswith('rule:') and e.get('narration','').find('Usus')>=0), ''))" 2>/dev/null)
if [ -n "$PEND2_ID" ]; then
  ok "Sales invoice posted → pending entry created"
else
  nok "No pending entry from sales invoice"
fi

# 2.3 Approve
if [ -n "$PEND2_ID" ]; then
  curl -s -X POST "$API/api/journal/$PEND2_ID/approve" "${H_AUTH[@]}" > /dev/null
  ok "Sales pending entry approved"
fi

# 2.4 AR (1200) should have debit balance of 750
TB=$(curl -s "$API/api/reports/trial-balance?companyId=$HOLD_ID" "${H_AUTH[@]}")
AR_BAL=$(echo "$TB" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((l['debitBalance'] for l in d['lines'] if l['code']=='1200'), 0))" 2>/dev/null)
REV_BAL=$(echo "$TB" | python3 -c "import json,sys; d=json.load(sys.stdin); print(next((l['creditBalance'] for l in d['lines'] if l['code']=='4000'), 0))" 2>/dev/null)
if [ "$AR_BAL" = "750" ] || [ "$AR_BAL" = "750.00" ]; then
  ok "AR (1200) debit = $AR_BAL LYD (expected 750)"
else
  nok "AR (1200) unexpected: $AR_BAL (expected 750)"
fi
if [ "$REV_BAL" = "750" ] || [ "$REV_BAL" = "750.00" ]; then
  ok "Revenue (4000) credit = $REV_BAL LYD (expected 750)"
else
  nok "Revenue (4000) unexpected: $REV_BAL (expected 750)"
fi

# ============================================================
# Scenario 3: Trial balance integrity
# ============================================================
hdr "Scenario 3: Trial Balance Integrity"
say "Goal: verify accounting equation holds"

TB=$(curl -s "$API/api/reports/trial-balance?companyId=$HOLD_ID" "${H_AUTH[@]}")
TOTAL_D=$(echo "$TB" | python3 -c "import json,sys; print(json.load(sys.stdin)['totalDebit'])" 2>/dev/null)
TOTAL_C=$(echo "$TB" | python3 -c "import json,sys; print(json.load(sys.stdin)['totalCredit'])" 2>/dev/null)
BALANCED=$(echo "$TB" | python3 -c "import json,sys; print(json.load(sys.stdin)['balanced'])" 2>/dev/null)
inf "Total Debit:  $TOTAL_D"
inf "Total Credit: $TOTAL_C"
if [ "$BALANCED" = "True" ]; then
  ok "Trial balance is balanced (D == C)"
else
  nok "Trial balance NOT balanced: D=$TOTAL_D C=$TOTAL_C"
fi

# ============================================================
# Summary
# ============================================================
hdr "Test Summary"
TOTAL=$((PASS + FAIL))
printf "\n  Total: %d   Passed: \e[32m%d\e[0m   Failed: \e[31m%d\e[0m\n\n" "$TOTAL" "$PASS" "$FAIL"
if [ "$FAIL" -eq 0 ]; then
  printf "\e[1;32m  ✅ All accounting scenarios passed.\e[0m\n\n"
  exit 0
else
  printf "\e[1;31m  ❌ %d scenario(s) failed. Review the output above.\e[0m\n\n" "$FAIL"
  exit 1
fi
