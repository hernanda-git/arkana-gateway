#!/bin/sh
# One-shot database initializer for a database created from scratch.
#
# Why this exists: the migration history in src/Arkana.Infrastructure/Migrations cannot
# reproduce the production schema on an empty database — migrations were rewritten after they
# had been applied, and at least one table (OAuthPendingFlows) has no creating migration left in
# the repository, so `dotnet ef database update` on a fresh database aborts partway and the
# gateway crash-loops. Instead, a fresh database is created from a schema dump of the running
# production database (baseline-schema.sql) and its migration history is stamped
# (baseline-migrations.txt) so EF applies only migrations written after this baseline.
#
# Idempotent: if a migration history already exists — a real database, production, staging, a
# restored dump — the script exits immediately and changes nothing. Safe to leave wired into the
# compose file for every environment.
set -eu

BASELINE_DIR="${BASELINE_DIR:-/baseline}"
SCHEMA_FILE="$BASELINE_DIR/baseline-schema.sql"
MIGRATIONS_FILE="$BASELINE_DIR/baseline-migrations.txt"

log() { printf '%s\n' "[db-baseline] $*"; }

wait_for_postgres() {
  i=0
  while [ "$i" -lt 60 ]; do
    if pg_isready -q; then return 0; fi
    i=$((i + 1))
    sleep 2
  done
  log "postgres did not become ready in time"
  return 1
}

log "starting against ${PGHOST:-postgres}:${PGPORT:-5432}/${PGDATABASE:-arkana}"
wait_for_postgres

# Already initialised? A database with a migration history is left completely alone.
history_present=$(psql -tAc "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL" | tr -d '[:space:]')
if [ "$history_present" = "t" ]; then
  rows=$(psql -tAc 'SELECT count(*) FROM public."__EFMigrationsHistory"' | tr -d '[:space:]')
  log "migration history already present ($rows row(s)) — nothing to do"
  exit 0
fi

# Any table at all means this is not an empty database: refuse to touch it rather than
# half-apply a baseline over somebody's schema.
table_count=$(psql -tAc "SELECT count(*) FROM information_schema.tables WHERE table_schema='public'" | tr -d '[:space:]')
case "$table_count" in
  ''|*[!0-9]*)
    log "cannot query the database (connection/authentication problem) — refusing to baseline"
    exit 1
    ;;
esac
if [ "$table_count" != "0" ]; then
  log "database has $table_count table(s) in schema public but no migration history; refusing to baseline (inspect it, or drop and recreate the database)"
  exit 1
fi

log "empty database detected — applying schema baseline"
psql -q -v ON_ERROR_STOP=1 -f "$SCHEMA_FILE"

if [ -s "$BASELINE_DIR/baseline-seed.sql" ]; then
  log "applying reference rows (baseline-seed.sql)"
  psql -q -v ON_ERROR_STOP=1 -f "$BASELINE_DIR/baseline-seed.sql"
fi

if [ ! -s "$MIGRATIONS_FILE" ]; then
  log "baseline-migrations.txt is missing or empty"
  exit 1
fi

log "stamping baseline migration history"
{
  printf 'INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES\n'
  first=1
  while IFS= read -r id; do
    case "$id" in ''|'#'*) continue ;; esac
    clean=$(printf '%s' "$id" | tr -cd 'A-Za-z0-9_.-')
    if [ "$first" -eq 1 ]; then first=0; else printf ',\n'; fi
    printf "('%s', '10.0.0')" "$clean"
  done < "$MIGRATIONS_FILE"
  printf '\nON CONFLICT ("MigrationId") DO NOTHING;\n'
} > /tmp/baseline-migrations.sql

psql -q -v ON_ERROR_STOP=1 -f /tmp/baseline-migrations.sql

rows=$(psql -tAc 'SELECT count(*) FROM public."__EFMigrationsHistory"' | tr -d '[:space:]')
tables=$(psql -tAc "SELECT count(*) FROM information_schema.tables WHERE table_schema='public'" | tr -d '[:space:]')
log "baseline applied: $tables table(s), $rows migration row(s) stamped"
