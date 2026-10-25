#!/bin/sh
set -eu

: "${PEERONQ_REDIS_CLOUD_PASSWORD:?cloud ACL password is required}"
: "${PEERONQ_REDIS_PRESENCE_PASSWORD:?presence ACL password is required}"
: "${PEERONQ_REDIS_PRESENCE_TOKEN_READER_PASSWORD:?token-reader ACL password is required}"
: "${PEERONQ_REDIS_ADMIN_PASSWORD:?admin ACL password is required}"
: "${PEERONQ_REDIS_SIGNALING_PASSWORD:?signaling ACL password is required}"

token_key=peeronq:cloud:v1:device-token:acl-acceptance
presence_key=peeronq:cloud:v1:presence:acl-acceptance
presence_lease_key=peeronq:cloud:v1:operation-lease:presence-expiration
admin_lease_key=peeronq:cloud:v1:operation-lease:admin-infrastructure-snapshots
signaling_key=peeronq:{signaling}:acl-acceptance
signaling_channel=peeronq:{signaling}:route:acl-acceptance

cloud() {
  redis-cli --user peeronq_cloud --pass "$PEERONQ_REDIS_CLOUD_PASSWORD" --no-auth-warning "$@"
}

presence() {
  redis-cli --user peeronq_presence --pass "$PEERONQ_REDIS_PRESENCE_PASSWORD" --no-auth-warning "$@"
}

reader() {
  redis-cli --user peeronq_presence_token_reader \
    --pass "$PEERONQ_REDIS_PRESENCE_TOKEN_READER_PASSWORD" --no-auth-warning "$@"
}

admin() {
  redis-cli --user peeronq_admin --pass "$PEERONQ_REDIS_ADMIN_PASSWORD" --no-auth-warning "$@"
}

signaling() {
  redis-cli --user peeronq_signaling --pass "$PEERONQ_REDIS_SIGNALING_PASSWORD" --no-auth-warning "$@"
}

expect_denied() {
  output="$($@ 2>&1 || true)"
  printf '%s' "$output" | grep -q NOPERM
}

cleanup() {
  cloud DEL "$token_key" >/dev/null 2>&1 || true
  presence DEL "$presence_key" >/dev/null 2>&1 || true
  presence DEL "$presence_lease_key" >/dev/null 2>&1 || true
  cloud DEL "$admin_lease_key" >/dev/null 2>&1 || true
  signaling DEL "$signaling_key" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

test "$(cloud SET "$token_key" acl-probe)" = OK
test "$(reader PING)" = PONG
test "$(reader GET "$token_key")" = acl-probe
expect_denied reader SET "$token_key" blocked
expect_denied reader DEL "$token_key"
expect_denied reader SCAN 0
expect_denied presence GET "$token_key"

test "$(presence SET "$presence_key" presence-probe)" = OK
test "$(presence SET "$presence_lease_key" presence-lease-probe)" = OK
test "$(admin GET "$presence_key")" = presence-probe
expect_denied admin SET "$presence_key" blocked
expect_denied admin DEL "$presence_key"
test "$(admin SET "$admin_lease_key" admin-lease-probe)" = OK
test "$(admin EVAL "return redis.call('PEXPIRE', KEYS[1], ARGV[1])" 1 "$admin_lease_key" 60000)" = 1
test "$(admin EVAL "return redis.call('DEL', KEYS[1])" 1 "$admin_lease_key")" = 1
expect_denied admin SET peeronq:cloud:v1:operation-lease:retention blocked

test "$(signaling SET "$signaling_key" signaling-probe)" = OK
test "$(signaling GET "$signaling_key")" = signaling-probe
test "$(signaling EVAL "return redis.call('SET', KEYS[1], ARGV[1])" 1 "$signaling_key" scripted-probe)" = OK
signaling PUBLISH "$signaling_channel" probe >/dev/null
expect_denied signaling GET "$token_key"
expect_denied signaling PUBLISH peeronq:cloud:v1:route blocked
expect_denied cloud GET "$signaling_key"

printf '%s\n' \
  'reader_ping_get=PASS' \
  'reader_set_del_scan=DENIED' \
  'normal_presence_token_get=DENIED' \
  'presence_operation_lease=PASS' \
  'admin_presence_read=PASS' \
  'admin_presence_write=DENIED' \
  'admin_infrastructure_lease=PASS' \
  'admin_other_operation_lease=DENIED' \
  'signaling_key_script_channel=PASS' \
  'signaling_cross_namespace=DENIED'
