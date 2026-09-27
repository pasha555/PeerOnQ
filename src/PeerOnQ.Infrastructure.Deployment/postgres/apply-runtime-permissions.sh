#!/bin/sh
set -eu

: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required}"
: "${RETENTION_POLICY_VERSION:?RETENTION_POLICY_VERSION is required}"
: "${AUDIT_RETENTION_DAYS:?AUDIT_RETENTION_DAYS is required}"
: "${AUDIT_RETENTION_ENABLED:?AUDIT_RETENTION_ENABLED is required}"
: "${AUDIT_LEGAL_HOLD:?AUDIT_LEGAL_HOLD is required}"
: "${ALERT_RETENTION_DAYS:?ALERT_RETENTION_DAYS is required}"
: "${ALERT_RETENTION_ENABLED:?ALERT_RETENTION_ENABLED is required}"
: "${ALERT_LEGAL_HOLD:?ALERT_LEGAL_HOLD is required}"

case "$RETENTION_POLICY_VERSION" in
  *[!A-Za-z0-9._-]*|'') echo "Invalid retention policy version" >&2; exit 64 ;;
esac
[ "${#RETENTION_POLICY_VERSION}" -le 64 ] || { echo "Retention policy version is too long" >&2; exit 64; }

for value in "$AUDIT_RETENTION_DAYS" "$ALERT_RETENTION_DAYS"; do
  case "$value" in *[!0-9]*|'') echo "Retention days must be unsigned integers" >&2; exit 64 ;; esac
done
[ "$AUDIT_RETENTION_DAYS" -ge 365 ] || { echo "Audit retention must be at least 365 days" >&2; exit 64; }
[ "$ALERT_RETENTION_DAYS" -ge 7 ] || { echo "Alert retention must be at least 7 days" >&2; exit 64; }

for value in "$AUDIT_RETENTION_ENABLED" "$AUDIT_LEGAL_HOLD" \
  "$ALERT_RETENTION_ENABLED" "$ALERT_LEGAL_HOLD"; do
  case "$value" in true|false) ;; *) echo "Retention flags must be true or false" >&2; exit 64 ;; esac
done

export PGPASSWORD="$POSTGRES_PASSWORD"
exec psql --host=postgres --port=5432 --username="$POSTGRES_USER" --dbname=peeronq_cloud \
  --set=ON_ERROR_STOP=1 --single-transaction \
  --set=retention_policy_version="$RETENTION_POLICY_VERSION" \
  --set=audit_retention_days="$AUDIT_RETENTION_DAYS" \
  --set=audit_retention_enabled="$AUDIT_RETENTION_ENABLED" \
  --set=audit_legal_hold="$AUDIT_LEGAL_HOLD" \
  --set=alert_retention_days="$ALERT_RETENTION_DAYS" \
  --set=alert_retention_enabled="$ALERT_RETENTION_ENABLED" \
  --set=alert_legal_hold="$ALERT_LEGAL_HOLD" \
  --file=/permissions/apply-runtime-permissions.sql
