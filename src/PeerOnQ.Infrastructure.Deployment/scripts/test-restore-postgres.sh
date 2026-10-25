#!/bin/sh
set -eu

: "${PGHOST:?PGHOST is required}"
: "${PGUSER:?PGUSER is required}"
: "${PGPASSFILE:?PGPASSFILE is required}"
: "${PEERONQ_BACKUP_FILE:?PEERONQ_BACKUP_FILE is required}"

if [ ! -f "$PEERONQ_BACKUP_FILE" ]; then
  echo "Backup file does not exist." >&2
  exit 2
fi

test_database="peeronq_restore_test_$(date -u +%Y%m%d%H%M%S)_$$"
case "$test_database" in
  *[!a-z0-9_]*) echo "Generated test database name is invalid." >&2; exit 2 ;;
esac

umask 077
pgpass_source="$PGPASSFILE"
pgpass_runtime="$(mktemp)"
cp "$pgpass_source" "$pgpass_runtime"
chmod 600 "$pgpass_runtime"
export PGPASSFILE="$pgpass_runtime"

cleanup() {
  psql --dbname=postgres --set=ON_ERROR_STOP=1 --command="SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$test_database' AND pid <> pg_backend_pid();" >/dev/null 2>&1 || true
  dropdb --if-exists "$test_database" >/dev/null 2>&1 || true
  rm -f "$pgpass_runtime"
}
trap cleanup EXIT HUP INT TERM

createdb "$test_database"
pg_restore --exit-on-error --no-owner --no-privileges --dbname="$test_database" "$PEERONQ_BACKUP_FILE"
table_count="$(psql --dbname="$test_database" --tuples-only --no-align --set=ON_ERROR_STOP=1 --command="SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';")"
case "$table_count" in
  ''|*[!0-9]*) echo "Restore verification returned an invalid table count." >&2; exit 1 ;;
  0) echo "Restore verification failed: restored database has no public tables." >&2; exit 1 ;;
esac

echo "Restore test passed in isolated database $test_database with $table_count public tables."
