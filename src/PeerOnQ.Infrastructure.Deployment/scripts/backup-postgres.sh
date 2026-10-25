#!/bin/sh
set -eu

: "${PGHOST:?PGHOST is required}"
: "${PGDATABASE:?PGDATABASE is required}"
: "${PGUSER:?PGUSER is required}"
: "${PGPASSFILE:?PGPASSFILE is required}"

backup_dir="${PEERONQ_BACKUP_DIR:-/backups}"
textfile_dir="${PEERONQ_TEXTFILE_DIR:-/metrics}"
retention_days="${PEERONQ_BACKUP_RETENTION_DAYS:-14}"

case "$backup_dir" in
  /|""|.) echo "Refusing unsafe backup directory." >&2; exit 2 ;;
esac
case "$retention_days" in
  *[!0-9]*|"") echo "Backup retention must be a positive number of days." >&2; exit 2 ;;
esac

umask 077
pgpass_source="$PGPASSFILE"
pgpass_runtime="$(mktemp)"
cp "$pgpass_source" "$pgpass_runtime"
chmod 600 "$pgpass_runtime"
export PGPASSFILE="$pgpass_runtime"
mkdir -p "$backup_dir" "$textfile_dir"
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="$backup_dir/peeronq-cloud-$timestamp.dump"
partial="$target.partial"
metric_partial="$textfile_dir/peeronq_backup.prom.partial"
trap 'rm -f "$partial" "$metric_partial" "$pgpass_runtime"' EXIT HUP INT TERM

pg_dump --format=custom --compress=9 --no-owner --no-privileges --file="$partial"
pg_restore --list "$partial" >/dev/null
mv "$partial" "$target"
sha256sum "$target" > "$target.sha256"

printf '# HELP peeronq_backup_last_success_timestamp_seconds Unix timestamp of the last verified PostgreSQL backup.\n' > "$metric_partial"
printf '# TYPE peeronq_backup_last_success_timestamp_seconds gauge\n' >> "$metric_partial"
printf 'peeronq_backup_last_success_timestamp_seconds %s\n' "$(date -u +%s)" >> "$metric_partial"
chmod 644 "$metric_partial"
mv "$metric_partial" "$textfile_dir/peeronq_backup.prom"

find "$backup_dir" -maxdepth 1 -type f \( -name 'peeronq-cloud-*.dump' -o -name 'peeronq-cloud-*.dump.sha256' \) -mtime "+$retention_days" -delete
echo "Backup completed: $(basename "$target")"
