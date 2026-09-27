#!/bin/sh
set -eu

: "${PGHOST:?PGHOST is required}"
: "${PGDATABASE:?PGDATABASE is required}"
: "${PGUSER:?PGUSER is required}"
: "${PGPASSFILE:?PGPASSFILE is required}"
: "${PEERONQ_BACKUP_FILE:?PEERONQ_BACKUP_FILE is required}"

if [ "${PEERONQ_CONFIRM_RESTORE:-}" != "RESTORE_PEERONQ_CLOUD" ]; then
  echo "Restore confirmation missing. Set PEERONQ_CONFIRM_RESTORE=RESTORE_PEERONQ_CLOUD." >&2
  exit 2
fi
if [ ! -f "$PEERONQ_BACKUP_FILE" ]; then
  echo "Backup file does not exist." >&2
  exit 2
fi
case "$PEERONQ_BACKUP_FILE" in
  *.dump) ;;
  *) echo "Only verified custom-format .dump files are accepted." >&2; exit 2 ;;
esac

umask 077
pgpass_source="$PGPASSFILE"
pgpass_runtime="$(mktemp)"
cp "$pgpass_source" "$pgpass_runtime"
chmod 600 "$pgpass_runtime"
export PGPASSFILE="$pgpass_runtime"
trap 'rm -f "$pgpass_runtime"' EXIT HUP INT TERM

if [ -f "$PEERONQ_BACKUP_FILE.sha256" ]; then
  (cd "$(dirname "$PEERONQ_BACKUP_FILE")" && sha256sum -c "$(basename "$PEERONQ_BACKUP_FILE").sha256")
fi
pg_restore --list "$PEERONQ_BACKUP_FILE" >/dev/null
pg_restore --exit-on-error --clean --if-exists --no-owner --no-privileges --dbname="$PGDATABASE" "$PEERONQ_BACKUP_FILE"
echo "Restore completed for the explicitly selected database. Run application readiness and migration checks before reopening traffic."
