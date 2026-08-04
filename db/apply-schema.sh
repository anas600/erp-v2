#!/usr/bin/env bash
# ============================================================
# Apply the canonical schema to the database.
# ============================================================
# Usage:
#   ./db/apply-schema.sh                  # uses DATABASE_URL env var
#   ./db/apply-schema.sh <postgres-url>   # uses provided URL
#
# The script is idempotent: every operation is "IF NOT EXISTS"
# or wrapped in a NOT VALID + DO block. Safe to re-run.
#
# Outputs a summary at the end:
#   - Migration version in the DB (if FluentMigrator was used)
#   - List of tables that now exist
#   - Any errors (with their statement for debugging)
# ============================================================

set -euo pipefail

DB_URL="${1:-${DATABASE_URL:-}}"
SCHEMA_FILE="$(dirname "$0")/schema.sql"

if [ -z "$DB_URL" ]; then
    echo "❌ No DATABASE_URL set. Either:"
    echo "   export DATABASE_URL=postgres://user:pass@host:port/db"
    echo "   or: $0 postgres://user:pass@host:port/db"
    exit 1
fi

if [ ! -f "$SCHEMA_FILE" ]; then
    echo "❌ Schema file not found: $SCHEMA_FILE"
    exit 1
fi

if ! command -v psql &> /dev/null; then
    echo "❌ psql not installed. Install with:"
    echo "   macOS:  brew install postgresql"
    echo "   Ubuntu: sudo apt install postgresql-client"
    exit 1
fi

echo "🔧 Applying schema from $SCHEMA_FILE ..."
echo "   Target: $(echo "$DB_URL" | sed 's|://[^@]*@|://***@|')"  # hide creds
echo

# Run with ON_ERROR_STOP so a single bad statement halts the script.
# Errors are printed with line numbers for easy debugging.
if ! psql "$DB_URL" \
    --set ON_ERROR_STOP=on \
    --single-transaction \
    -v ON_ERROR_STOP=1 \
    -f "$SCHEMA_FILE" 2>&1 | tee /tmp/schema-apply.log; then
    echo
    echo "❌ Schema apply FAILED. See /tmp/schema-apply.log for details."
    exit 1
fi

echo
echo "✅ Schema applied successfully."
echo
echo "--- Tables in database ---"
psql "$DB_URL" -c "\dt" 2>&1 | head -30
echo
echo "--- Key column checks ---"
psql "$DB_URL" -c "
    SELECT column_name, data_type
    FROM information_schema.columns
    WHERE table_name = 'journal_entries'
      AND column_name IN ('reverses_entry_id', 'rule_id', 'source')
    ORDER BY column_name;
" 2>&1
echo
echo "--- FK check on reverses_entry_id ---"
psql "$DB_URL" -c "
    SELECT conname, contype, pg_get_constraintdef(oid) AS definition
    FROM pg_constraint
    WHERE conname = 'fk_journal_entries_reverses';
" 2>&1
