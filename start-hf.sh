#!/usr/bin/env bash
# ===========================================================
# HF Space entrypoint.
# Brings up the local Postgres cluster, runs migrations once,
# then hands control to supervisord.
# ===========================================================
set -euo pipefail

log() { echo "[start-hf] $*"; }

PGDATA=/var/lib/postgresql/15/main
PGCONF=/etc/postgresql/15/main/postgresql.conf
PGRUN=/var/run/postgresql

mkdir -p "$PGRUN"
chown -R postgres:postgres /var/lib/postgresql /var/run/postgresql
chmod 0775 "$PGRUN"

# Initialize the cluster if empty (first boot in the container).
if [ ! -s "$PGDATA/PG_VERSION" ]; then
  log "Initializing fresh PostgreSQL cluster..."
  su - postgres -c "/usr/lib/postgresql/15/bin/initdb -D $PGDATA --auth-host=md5 --auth-local=trust --encoding=UTF8" >/dev/null
fi

# Make sure the cluster accepts local password connections from the .NET backend.
if ! grep -q "host all all 127.0.0.1/32 md5" "$PGDATA/pg_hba.conf"; then
  echo "host all all 127.0.0.1/32 md5" >> "$PGDATA/pg_hba.conf"
fi
if ! grep -q "host all all ::1/128 md5" "$PGDATA/pg_hba.conf"; then
  echo "host all all ::1/128 md5" >> "$PGDATA/pg_hba.conf"
fi

# Ensure password is set for the `erp` user (created later when the DB is ready).
log "Starting PostgreSQL..."
su - postgres -c "/usr/lib/postgresql/15/bin/pg_ctl -D $PGDATA -l /tmp/pg.log start"

# Wait for the server to accept connections.
for i in $(seq 1 30); do
  if su - postgres -c "psql -d postgres -c 'SELECT 1' >/dev/null 2>&1"; then
    log "PostgreSQL is up."
    break
  fi
  sleep 1
done

# Create the erp user and database if missing (idempotent).
su - postgres -c "psql -d postgres -tAc \"SELECT 1 FROM pg_roles WHERE rolname='erp'\"" | grep -q 1 \
  || su - postgres -c "psql -d postgres -c \"CREATE ROLE erp LOGIN PASSWORD 'erp_secret' CREATEDB\""

su - postgres -c "psql -d postgres -tAc \"SELECT 1 FROM pg_database WHERE datname='erp'\"" | grep -q 1 \
  || su - postgres -c "createdb -O erp erp"

# Enable the extension the migrations need.
su - postgres -c "psql -d erp -c 'CREATE EXTENSION IF NOT EXISTS pgcrypto;'"

# If invoked as `start-hf.sh migrate`, run the .NET migrations and exit.
# This is the dedicated `migrate` program in supervisord.conf.
if [ "${1:-}" = "migrate" ]; then
  log "Running FluentMigrator migrations..."
  cd /opt/backend
  dotnet ErpV2.dll --migrate-only || {
    log "Migration command failed; falling back to running app once."
    timeout 8 dotnet ErpV2.dll || true
  }
  log "Migrations complete."
  exit 0
fi

# Normal startup path: hand off to supervisord, which keeps the
# postgres, backend, and frontend processes alive.
log "Launching supervisord (postgres + backend + frontend)..."
exec /usr/bin/supervisord -c /etc/supervisor/conf.d/supervisord.conf
