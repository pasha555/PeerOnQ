#!/bin/sh
set -eu

: "${PEERONQ_REDIS_CLOUD_PASSWORD:?PEERONQ_REDIS_CLOUD_PASSWORD is required}"
: "${PEERONQ_REDIS_PRESENCE_PASSWORD:?PEERONQ_REDIS_PRESENCE_PASSWORD is required}"
: "${PEERONQ_REDIS_PRESENCE_TOKEN_READER_PASSWORD:?PEERONQ_REDIS_PRESENCE_TOKEN_READER_PASSWORD is required}"
: "${PEERONQ_REDIS_ADMIN_PASSWORD:?PEERONQ_REDIS_ADMIN_PASSWORD is required}"
: "${PEERONQ_REDIS_DOWNLOADS_PASSWORD:?PEERONQ_REDIS_DOWNLOADS_PASSWORD is required}"
: "${PEERONQ_REDIS_SIGNALING_PASSWORD:?PEERONQ_REDIS_SIGNALING_PASSWORD is required}"
: "${PEERONQ_REDIS_HEALTH_PASSWORD:?PEERONQ_REDIS_HEALTH_PASSWORD is required}"

hash_password() {
  printf '%s' "$1" | sha256sum | cut -d ' ' -f 1
}

umask 077
acl_temp=/data/users.acl.tmp
acl_file=/data/users.acl
cloud_hash="$(hash_password "$PEERONQ_REDIS_CLOUD_PASSWORD")"
presence_hash="$(hash_password "$PEERONQ_REDIS_PRESENCE_PASSWORD")"
presence_token_reader_hash="$(hash_password "$PEERONQ_REDIS_PRESENCE_TOKEN_READER_PASSWORD")"
admin_hash="$(hash_password "$PEERONQ_REDIS_ADMIN_PASSWORD")"
downloads_hash="$(hash_password "$PEERONQ_REDIS_DOWNLOADS_PASSWORD")"
signaling_hash="$(hash_password "$PEERONQ_REDIS_SIGNALING_PASSWORD")"
health_hash="$(hash_password "$PEERONQ_REDIS_HEALTH_PASSWORD")"

cat > "$acl_temp" <<EOF
user default off
user peeronq_cloud on #$cloud_hash ~peeronq:cloud:v1:* &__Booksleeve_MasterChanged +@all -@admin -@dangerous
user peeronq_presence on #$presence_hash ~peeronq:cloud:v1:presence* ~peeronq:cloud:v1:operation-lease:presence-expiration &* +@all -@admin -@dangerous
user peeronq_presence_token_reader on #$presence_token_reader_hash resetkeys resetchannels %R~peeronq:cloud:v1:device-token:* +ping +echo +get
user peeronq_admin on #$admin_hash %RW~peeronq:{admin}:* %RW~peeronq:cloud:v1:operation-lease:admin-infrastructure-snapshots %R~peeronq:cloud:v1:presence* &__Booksleeve_MasterChanged +@all -@admin -@dangerous
user peeronq_downloads on #$downloads_hash resetkeys resetchannels +@connection +info
user peeronq_signaling on #$signaling_hash resetkeys resetchannels ~peeronq:{signaling}:* &peeronq:{signaling}:* &__Booksleeve_MasterChanged +@all -@admin -@dangerous
user peeronq_health on #$health_hash resetkeys resetchannels +ping
EOF
mv "$acl_temp" "$acl_file"

exec redis-server \
  --appendonly yes \
  --aclfile "$acl_file" \
  --maxmemory 512mb \
  --maxmemory-policy noeviction
