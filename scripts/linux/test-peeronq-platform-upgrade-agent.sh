#!/bin/sh
set -eu

[ "$(id -u)" = "0" ] || {
  printf 'Run this isolated agent test as root (normally inside a disposable Linux container).\n' >&2
  exit 77
}

script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
agent_source="$script_dir/peeronq-platform-upgrade-agent.sh"
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

install_root="$temporary/opt/peeronq"
platform_root="$temporary/var/lib/peeronq/platform-upgrade"
config_file="$temporary/etc/peeronq/platform-upgrade.conf"
env_file="$temporary/etc/peeronq/peeronq.env"
lock_file="$temporary/run/peeronq-platform-upgrade.lock"
log_root="$temporary/var/log/peeronq/platform-upgrade"
keyring="$temporary/etc/peeronq/platform-upgrade-trustedkeys.gpg"
rendered_agent="$temporary/agent.sh"
fingerprint=0123456789ABCDEF0123456789ABCDEF01234567

mkdir -p "$install_root/releases/1.2.3" "$platform_root/inbox" \
  "$platform_root/processing" "$platform_root/archive" "$platform_root/status" \
  "$(dirname "$config_file")" "$(dirname "$lock_file")" "$log_root" "$temporary/stubs"
ln -s "$install_root/releases/1.2.3" "$install_root/current"
chown 0:0 "$platform_root" "$platform_root/processing" "$platform_root/archive" "$log_root"
chmod 750 "$platform_root"
chmod 700 "$platform_root/processing" "$platform_root/archive" "$log_root"
chown 0:1654 "$platform_root/inbox" "$platform_root/status"
chmod 1730 "$platform_root/inbox"
chmod 750 "$platform_root/status"
printf 'test public keyring\n' > "$keyring"
cat > "$config_file" <<EOF
keyring=$keyring
signer_fingerprint=$fingerprint
EOF
printf 'test=true\n' > "$env_file"
chown 0:0 "$keyring" "$config_file" "$env_file"
chmod 400 "$keyring" "$config_file"
chmod 600 "$env_file"

awk \
  -v install_root="$install_root" \
  -v platform_root="$platform_root" \
  -v config_file="$config_file" \
  -v keyring="$keyring" \
  -v env_file="$env_file" \
  -v lock_file="$lock_file" \
  -v log_root="$log_root" '
    /^INSTALL_ROOT=/ { print "INSTALL_ROOT=\"" install_root "\""; next }
    /^PLATFORM_ROOT=/ { print "PLATFORM_ROOT=\"" platform_root "\""; next }
    /^INBOX_ROOT=/ { print "INBOX_ROOT=\"$PLATFORM_ROOT/inbox\""; next }
    /^PROCESSING_ROOT=/ { print "PROCESSING_ROOT=\"$PLATFORM_ROOT/processing\""; next }
    /^ARCHIVE_ROOT=/ { print "ARCHIVE_ROOT=\"$PLATFORM_ROOT/archive\""; next }
    /^STATUS_ROOT=/ { print "STATUS_ROOT=\"$PLATFORM_ROOT/status\""; next }
    /^CONFIG_FILE=/ { print "CONFIG_FILE=\"" config_file "\""; next }
    /^KEYRING_FILE=/ { print "KEYRING_FILE=\"" keyring "\""; next }
    /^ENV_FILE=/ { print "ENV_FILE=\"" env_file "\""; next }
    /^LOCK_FILE=/ { print "LOCK_FILE=\"" lock_file "\""; next }
    /^LOG_ROOT=/ { print "LOG_ROOT=\"" log_root "\""; next }
    { print }
  ' "$agent_source" > "$rendered_agent"
chmod 755 "$rendered_agent"

cat > "$temporary/stubs/gpgv" <<EOF
#!/bin/sh
signature=
printf '%s\n' "\$*" >> "$temporary/gpgv-arguments.log"
for argument in "\$@"; do
  case "\$argument" in *.asc) signature=\$argument ;; esac
done
[ -n "\$signature" ] || exit 2
grep -Fqx good-signature "\$signature" || exit 1
if [ -e "$temporary/hold-verifier" ]; then
  : > "$temporary/verifier-started"
  while [ -e "$temporary/hold-verifier" ]; do sleep 0.05; done
fi
printf '[GNUPG:] VALIDSIG AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA 20260824 0 0 4 0 1 10 00 $fingerprint\n' >&3
EOF
chmod 755 "$temporary/stubs/gpgv"

make_bundle() {
  version=$1
  bundle="$2/peeronq-server-$version.run"
  cat > "$bundle" <<EOF
#!/bin/sh
set -eu
INSTALLER_VERSION="$version"
printf '%s\n' "\$*" >> "$temporary/installer-arguments.log"
if [ -e "$temporary/hold-installer" ]; then
  : > "$temporary/installer-started"
  while [ -e "$temporary/hold-installer" ]; do sleep 0.05; done
fi
case " \$* " in
  *' --dry-run '*) exit 0 ;;
  *' --rollback '*)
    current=\$(readlink -f "$install_root/current")
    previous=\$(readlink -f "$install_root/previous")
    ln -sfn "\$previous" "$install_root/current.new"
    mv -Tf "$install_root/current.new" "$install_root/current"
    ln -sfn "\$current" "$install_root/previous.new"
    mv -Tf "$install_root/previous.new" "$install_root/previous"
    exit 0
    ;;
esac
mkdir -p "$install_root/releases/$version"
old=\$(readlink -f "$install_root/current")
ln -sfn "\$old" "$install_root/previous.new"
mv -Tf "$install_root/previous.new" "$install_root/previous"
ln -sfn "$install_root/releases/$version" "$install_root/current.new"
mv -Tf "$install_root/current.new" "$install_root/current"
exit 0
__PEERONQ_PAYLOAD_BELOW__
EOF
  chmod 640 "$bundle"
  sha=$(sha256sum "$bundle" | awk '{ print $1 }')
  size=$(stat -c '%s' "$bundle")
  printf '%s  %s\n' "$sha" "peeronq-server-$version.run" > "$bundle.sha256"
  printf 'good-signature\n' > "$bundle.asc"
  chmod 640 "$bundle.sha256" "$bundle.asc"
  BUNDLE_SHA=$sha
  BUNDLE_SIZE=$size
}

make_archive() {
  archive_version=$1
  archive_dir="$platform_root/archive/$archive_version"
  mkdir "$archive_dir"
  chmod 700 "$archive_dir"
  make_bundle "$archive_version" "$archive_dir"
  {
    printf 'schema=1\n'
    printf 'version=%s\n' "$archive_version"
    printf 'bundle_sha256=%s\n' "$BUNDLE_SHA"
    printf 'bundle_size=%s\n' "$BUNDLE_SIZE"
  } > "$archive_dir/metadata.env"
  chown 0:0 "$archive_dir"/*
  chmod 500 "$archive_dir/peeronq-server-$archive_version.run"
  chmod 400 "$archive_dir/peeronq-server-$archive_version.run.sha256" \
    "$archive_dir/peeronq-server-$archive_version.run.asc" "$archive_dir/metadata.env"
}

publish_active() {
  operation_id=$1
  active_temp="$platform_root/inbox/.active-request.tmp"
  printf '%s\n' "$operation_id" > "$active_temp"
  chown 1654:1654 "$active_temp"
  chmod 640 "$active_temp"
  mv "$active_temp" "$platform_root/inbox/.active-request"
}

publish_ready() {
  operation_id=$1
  ready_temp="$platform_root/inbox/$operation_id/.ready.tmp"
  : > "$ready_temp"
  chown 1654:1654 "$ready_temp"
  chmod 640 "$ready_temp"
  mv "$ready_temp" "$platform_root/inbox/$operation_id.ready"
}

prepare_request_dir() {
  operation_id=$1
  request_dir="$platform_root/inbox/$operation_id"
  mkdir "$request_dir"
  chown 1654:1654 "$request_dir"
  chmod 750 "$request_dir"
}

finalize_request_files() {
  request_dir=$1
  chown 1654:1654 "$request_dir"/*
  chmod 640 "$request_dir"/*
}

run_agent() {
  PATH="$temporary/stubs:$PATH" "$rendered_agent"
}

wait_for_installer() {
  attempts=0
  while [ ! -e "$temporary/installer-started" ]; do
    attempts=$((attempts + 1))
    [ "$attempts" -le 200 ] || return 1
    sleep 0.05
  done
}

make_archive 1.2.3
make_archive 1.1.0
mkdir "$platform_root/archive/0.9.0"
chmod 755 "$platform_root/archive/0.9.0"
printf 'preserve unsafe archive\n' > "$platform_root/archive/0.9.0/sentinel"
mkdir "$temporary/archive-symlink-target"
printf 'do not follow\n' > "$temporary/archive-symlink-target/sentinel"
ln -s "$temporary/archive-symlink-target" "$platform_root/archive/0.8.0"
mkdir "$platform_root/archive/.stage-1.0.0.ABC123"
chmod 700 "$platform_root/archive/.stage-1.0.0.ABC123"
printf 'partial\n' > "$platform_root/archive/.stage-1.0.0.ABC123/partial"
mkdir "$platform_root/archive/.stage-1.0.1.DEF456"
chmod 755 "$platform_root/archive/.stage-1.0.1.DEF456"
printf 'unsafe\n' > "$platform_root/archive/.stage-1.0.1.DEF456/partial"

PATH="$temporary/stubs:$PATH" "$rendered_agent" --initialize
grep -Fq '"state":"idle"' "$platform_root/status/status.json"

# An early PathChanged event must not consume an incomplete active marker.
: > "$platform_root/inbox/.active-request"
chown 1654:1654 "$platform_root/inbox/.active-request"
chmod 640 "$platform_root/inbox/.active-request"
if run_agent; then
  printf 'Fresh incomplete active marker unexpectedly became terminal.\n' >&2
  exit 1
else
  [ "$?" -eq 75 ]
fi
[ -f "$platform_root/inbox/.active-request" ]
grep -Fq '"state":"idle"' "$platform_root/status/status.json"
[ ! -e "$platform_root/archive/.stage-1.0.0.ABC123" ]
[ -f "$platform_root/archive/.stage-1.0.1.DEF456/partial" ]
rm -f "$platform_root/inbox/.active-request"

stage_id=11111111111111111111111111111111
prepare_request_dir "$stage_id"
stage_dir="$platform_root/inbox/$stage_id"
make_bundle 1.2.4 "$stage_dir"
cat > "$stage_dir/request.env" <<EOF
schema=1
request_id=$stage_id
action=stage
version=1.2.4
expected_current=1.2.3
bundle_file=peeronq-server-1.2.4.run
checksum_file=peeronq-server-1.2.4.run.sha256
signature_file=peeronq-server-1.2.4.run.asc
bundle_sha256=$BUNDLE_SHA
bundle_size=$BUNDLE_SIZE
EOF
finalize_request_files "$stage_dir"
publish_active "$stage_id"
if run_agent; then
  printf 'Incomplete producer request unexpectedly completed.\n' >&2
  exit 1
else
  [ "$?" -eq 75 ]
fi
[ -d "$stage_dir" ]
[ -f "$platform_root/inbox/.active-request" ]
publish_ready "$stage_id"
: > "$temporary/hold-verifier"
run_agent &
stage_agent_pid=$!
attempts=0
while [ ! -e "$temporary/verifier-started" ]; do
  attempts=$((attempts + 1))
  [ "$attempts" -le 200 ] || exit 1
  sleep 0.05
done
[ -f "$platform_root/inbox/.active-request" ]
[ "$(stat -c '%u:%g:%a:%h' "$platform_root/inbox/.active-request")" = "0:0:400:1" ]
[ -d "$platform_root/processing/$stage_id" ]
grep -Fq '"state":"verifying"' "$platform_root/status/status.json"
grep -Fq '"canRollback":false' "$platform_root/status/status.json"
rm -f "$temporary/hold-verifier"
wait "$stage_agent_pid"
rm -f "$temporary/verifier-started"
[ ! -e "$stage_dir" ]
[ ! -e "$platform_root/processing/$stage_id" ]
[ ! -e "$platform_root/inbox/.active-request" ]
[ "$(stat -c '%u:%g:%a:%h' "$log_root/$stage_id.log")" = "0:0:600:1" ]
grep -Fq 'request claimed by the constrained host updater' "$log_root/$stage_id.log"
[ -f "$platform_root/archive/1.2.4/peeronq-server-1.2.4.run" ] || {
  printf 'Target archive missing after stage:\n' >&2
  sed 's/^/  /' "$log_root/$stage_id.log" >&2
  find "$platform_root/archive" -mindepth 1 -maxdepth 1 -printf '  %f\n' >&2
  find "$platform_root/archive/1.2.3" -mindepth 1 -maxdepth 2 -printf '  CURRENT %P\n' >&2
  grep -R 'candidate=' "$temporary" 2>/dev/null >&2 || true
  exit 1
}
[ -d "$platform_root/archive/1.2.3" ] || {
  printf 'Current rollback archive was unexpectedly pruned:\n' >&2
  find "$platform_root/archive" -mindepth 1 -maxdepth 1 -printf '  %f\n' >&2
  exit 1
}
[ ! -e "$platform_root/archive/1.1.0" ] || {
  printf 'Verified archive prune did not remove history. gpgv calls:\n' >&2
  sed 's/^/  STATUS /' "$platform_root/status/status.json" >&2
  find "$log_root" -maxdepth 1 -type f -printf '  LOG %f size=%s\n' >&2
  sed 's/^/  /' "$temporary/gpgv-arguments.log" >&2
  sed 's/^/  /' "$log_root/$stage_id.log" >&2
  find "$platform_root/archive/1.1.0" -mindepth 1 -maxdepth 1 -printf '  %f %u:%g %m %n\n' >&2
  exit 1
}
[ -f "$platform_root/archive/0.9.0/sentinel" ]
[ -L "$platform_root/archive/0.8.0" ]
[ -f "$temporary/archive-symlink-target/sentinel" ]
grep -Fq '"state":"ready"' "$platform_root/status/status.json"
grep -Fq '"canApply":true' "$platform_root/status/status.json"
[ ! -e "$temporary/installer-arguments.log" ]

apply_id=22222222222222222222222222222222
prepare_request_dir "$apply_id"
apply_dir="$platform_root/inbox/$apply_id"
cat > "$apply_dir/request.env" <<EOF
schema=1
request_id=$apply_id
action=apply
version=1.2.4
expected_current=1.2.3
EOF
finalize_request_files "$apply_dir"
publish_active "$apply_id"
publish_ready "$apply_id"
run_agent
[ "$(basename "$(readlink -f "$install_root/current")")" = "1.2.4" ]
grep -Fq '"state":"succeeded"' "$platform_root/status/status.json"
grep -Fq '"schemaVersion":1' "$platform_root/status/status.json"
grep -Fq '"canRollback":true' "$platform_root/status/status.json"
grep -Fqx -- "--env-file $env_file --dry-run" "$temporary/installer-arguments.log"
grep -Fqx -- "--env-file $env_file" "$temporary/installer-arguments.log"

rollback_id=33333333333333333333333333333333
prepare_request_dir "$rollback_id"
rollback_dir="$platform_root/inbox/$rollback_id"
cat > "$rollback_dir/request.env" <<EOF
schema=1
request_id=$rollback_id
action=rollback
version=1.2.3
expected_current=1.2.4
EOF
finalize_request_files "$rollback_dir"
publish_active "$rollback_id"
publish_ready "$rollback_id"
: > "$temporary/hold-installer"
run_agent &
rollback_agent_pid=$!
wait_for_installer
[ -f "$platform_root/inbox/.active-request" ]
grep -Fq '"state":"rolling_back"' "$platform_root/status/status.json"
grep -Fq '"canRollback":false' "$platform_root/status/status.json"
rm -f "$temporary/hold-installer"
wait "$rollback_agent_pid"
rm -f "$temporary/installer-started"
[ "$(basename "$(readlink -f "$install_root/current")")" = "1.2.3" ]
grep -Fq '"state":"rolled_back"' "$platform_root/status/status.json" || {
  printf 'Unexpected rollback status:\n' >&2
  sed 's/^/  /' "$platform_root/status/status.json" >&2
  exit 1
}
grep -Fq '"canRollback":false' "$platform_root/status/status.json"
grep -Fqx -- "--env-file $env_file --rollback" "$temporary/installer-arguments.log" || {
  printf 'Unexpected installer argument log:\n' >&2
  sed 's/^/  /' "$temporary/installer-arguments.log" >&2
  exit 1
}

# A ready request with an extra file is terminally rejected before signature execution.
bad_id=44444444444444444444444444444444
prepare_request_dir "$bad_id"
bad_dir="$platform_root/inbox/$bad_id"
make_bundle 1.2.5 "$bad_dir"
cat > "$bad_dir/request.env" <<EOF
schema=1
request_id=$bad_id
action=stage
version=1.2.5
expected_current=1.2.3
bundle_file=peeronq-server-1.2.5.run
checksum_file=peeronq-server-1.2.5.run.sha256
signature_file=peeronq-server-1.2.5.run.asc
bundle_sha256=$BUNDLE_SHA
bundle_size=$BUNDLE_SIZE
EOF
printf 'unexpected\n' > "$bad_dir/extra"
finalize_request_files "$bad_dir"
publish_active "$bad_id"
publish_ready "$bad_id"
run_agent
grep -Fq '"state":"failed"' "$platform_root/status/status.json"
grep -Fq '"blockingReason":"invalid_request"' "$platform_root/status/status.json"
[ ! -e "$platform_root/archive/1.2.5" ]
[ ! -e "$platform_root/processing/$bad_id" ]
[ ! -e "$platform_root/inbox/.active-request" ]
[ "$(stat -c '%u:%g:%a:%h' "$log_root/$bad_id.log")" = "0:0:600:1" ]
grep -Fq 'request rejected: claimed file set was unsafe or non-canonical' "$log_root/$bad_id.log"

# Signature failure cannot reach preflight or create a retained archive.
signature_id=55555555555555555555555555555555
prepare_request_dir "$signature_id"
signature_dir="$platform_root/inbox/$signature_id"
make_bundle 1.2.5 "$signature_dir"
printf 'bad-signature\n' > "$signature_dir/peeronq-server-1.2.5.run.asc"
cat > "$signature_dir/request.env" <<EOF
schema=1
request_id=$signature_id
action=stage
version=1.2.5
expected_current=1.2.3
bundle_file=peeronq-server-1.2.5.run
checksum_file=peeronq-server-1.2.5.run.sha256
signature_file=peeronq-server-1.2.5.run.asc
bundle_sha256=$BUNDLE_SHA
bundle_size=$BUNDLE_SIZE
EOF
finalize_request_files "$signature_dir"
publish_active "$signature_id"
publish_ready "$signature_id"
run_agent
grep -Fq '"blockingReason":"signature_invalid"' "$platform_root/status/status.json"
[ ! -e "$platform_root/archive/1.2.5" ]
[ ! -e "$platform_root/processing/$signature_id" ]
[ ! -e "$platform_root/inbox/.active-request" ]

# A crash after claim is fail-safe: never replay an unknown installer outcome,
# retain the root-owned evidence, and keep the queue gate closed for an operator.
stale_id=66666666666666666666666666666666
mkdir "$platform_root/processing/$stale_id"
chown 0:0 "$platform_root/processing/$stale_id"
chmod 700 "$platform_root/processing/$stale_id"
publish_active "$stale_id"
chown 0:0 "$platform_root/inbox/.active-request"
chmod 400 "$platform_root/inbox/.active-request"
run_agent
[ -d "$platform_root/processing/$stale_id" ]
[ -f "$platform_root/inbox/.active-request" ]
grep -Fq '"state":"failed"' "$platform_root/status/status.json"
grep -Fq '"blockingReason":"manual_recovery"' "$platform_root/status/status.json"
rm -rf "$platform_root/processing/$stale_id"
rm -f "$platform_root/inbox/.active-request"

# An unexpected atomic status publication failure must never execute or discard
# the claimed request. A service restart converts it to fail-safe manual recovery.
status_failure_id=88888888888888888888888888888888
prepare_request_dir "$status_failure_id"
status_failure_dir="$platform_root/inbox/$status_failure_id"
cat > "$status_failure_dir/request.env" <<EOF
schema=1
request_id=$status_failure_id
action=apply
version=1.2.5
expected_current=1.2.3
EOF
finalize_request_files "$status_failure_dir"
publish_active "$status_failure_id"
publish_ready "$status_failure_id"
cat > "$temporary/stubs/mv" <<'EOF'
#!/bin/sh
for argument in "$@"; do
  case "$argument" in */status/status.json) exit 70 ;; esac
done
exec /usr/bin/mv "$@"
EOF
chmod 755 "$temporary/stubs/mv"
if run_agent; then
  printf 'Status publication failure unexpectedly completed.\n' >&2
  exit 1
fi
[ -d "$platform_root/processing/$status_failure_id" ]
[ "$(stat -c '%u:%g:%a:%h' "$platform_root/inbox/.active-request")" = "0:0:400:1" ]
rm -f "$temporary/stubs/mv"
run_agent
[ -d "$platform_root/processing/$status_failure_id" ]
[ -f "$platform_root/inbox/.active-request" ]
grep -Fq '"blockingReason":"manual_recovery"' "$platform_root/status/status.json"
rm -rf "$platform_root/processing/$status_failure_id"
rm -f "$platform_root/inbox/.active-request"

# If terminal payload cleanup fails, the locked gate and root evidence remain;
# the service restart must fail closed instead of admitting another action.
cleanup_failure_id=99999999999999999999999999999999
prepare_request_dir "$cleanup_failure_id"
cleanup_failure_dir="$platform_root/inbox/$cleanup_failure_id"
printf 'invalid-contract\n' > "$cleanup_failure_dir/request.env"
finalize_request_files "$cleanup_failure_dir"
publish_active "$cleanup_failure_id"
publish_ready "$cleanup_failure_id"
cat > "$temporary/stubs/rm" <<EOF
#!/bin/sh
for argument in "\$@"; do
  [ "\$argument" != "$platform_root/processing/$cleanup_failure_id" ] || exit 70
done
exec /usr/bin/rm "\$@"
EOF
chmod 755 "$temporary/stubs/rm"
if run_agent; then
  printf 'Claimed payload cleanup failure unexpectedly completed.\n' >&2
  exit 1
fi
[ -d "$platform_root/processing/$cleanup_failure_id" ]
[ "$(stat -c '%u:%g:%a:%h' "$platform_root/inbox/.active-request")" = "0:0:400:1" ]
[ "$(stat -c '%u:%g:%a:%h' "$log_root/$cleanup_failure_id.log")" = "0:0:600:1" ]
rm -f "$temporary/stubs/rm"
run_agent
grep -Fq '"blockingReason":"manual_recovery"' "$platform_root/status/status.json"
[ -d "$platform_root/processing/$cleanup_failure_id" ]
[ -f "$platform_root/inbox/.active-request" ]
rm -rf "$platform_root/processing/$cleanup_failure_id"
rm -f "$platform_root/inbox/.active-request"

# A producer that never publishes ready is retried only within a bounded lease,
# then its safe partial request and exact gate are cleared without root execution.
producer_id=77777777777777777777777777777777
prepare_request_dir "$producer_id"
publish_active "$producer_id"
touch -d '1 hour ago' "$platform_root/inbox/.active-request"
run_agent
[ ! -e "$platform_root/inbox/$producer_id" ]
[ ! -e "$platform_root/inbox/.active-request" ]
grep -Fq '"blockingReason":"producer_timeout"' "$platform_root/status/status.json"
grep -Fq '"logReference":null' "$platform_root/status/status.json"

[ "$(stat -c '%u:%g:%a' "$platform_root/status/status.json")" = "0:1654:640" ]
[ "$(stat -c '%s' "$platform_root/status/status.json")" -le 16384 ]
! grep -Fq "$log_root" "$platform_root/status/status.json"

printf 'PeerOnQ constrained platform upgrade agent tests passed.\n'
