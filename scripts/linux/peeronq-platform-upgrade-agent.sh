#!/bin/sh
set -efu

INSTALL_ROOT="/opt/peeronq"
PLATFORM_ROOT="/var/lib/peeronq/platform-upgrade"
INBOX_ROOT="$PLATFORM_ROOT/inbox"
PROCESSING_ROOT="$PLATFORM_ROOT/processing"
ARCHIVE_ROOT="$PLATFORM_ROOT/archive"
STATUS_ROOT="$PLATFORM_ROOT/status"
CONFIG_FILE="/etc/peeronq/platform-upgrade.conf"
KEYRING_FILE="/etc/peeronq/platform-upgrade-trustedkeys.gpg"
ENV_FILE="/etc/peeronq/peeronq.env"
LOCK_FILE="/run/lock/peeronq-platform-upgrade.lock"
LOG_ROOT="/var/log/peeronq/platform-upgrade"
MAX_BUNDLE_BYTES=268435456
PENDING_LEASE_SECONDS=1800
ADMIN_UID=1654
ADMIN_GID=1654

fail() {
  printf 'PeerOnQ platform upgrade agent: %s\n' "$1" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command is missing: $1"
}

safe_root_file() {
  path=$1
  [ -f "$path" ] && [ ! -L "$path" ] || return 1
  [ "$(stat -c '%u:%g:%h' "$path")" = "0:0:1" ] || return 1
  case "$(stat -c '%a' "$path")" in
    400|440|600|640) ;;
    *) return 1 ;;
  esac
}

safe_admin_file() {
  path=$1
  [ -f "$path" ] && [ ! -L "$path" ] || return 1
  [ "$(stat -c '%u:%g:%a:%h' "$path")" = "$ADMIN_UID:$ADMIN_GID:640:1" ]
}

safe_locked_marker() {
  path=$1
  [ -f "$path" ] && [ ! -L "$path" ] || return 1
  [ "$(stat -c '%u:%g:%a:%h' "$path")" = "0:0:400:1" ]
}

marker_is_fresh() {
  marker_path=$1
  marker_mtime=$(stat -c '%Y' "$marker_path" 2>/dev/null || printf '0')
  now_epoch=$(date -u '+%s')
  case "$marker_mtime" in *[!0-9]*|'') return 1 ;; esac
  [ "$marker_mtime" -gt 0 ] && [ $((now_epoch - marker_mtime)) -lt "$PENDING_LEASE_SECONDS" ]
}

read_active_id() {
  awk 'NR == 1 { value = $0 } END { if (NR != 1) exit 1; print value }' "$1" 2>/dev/null
}

lock_active_request() {
  lock_operation_id=$1
  lock_marker="$INBOX_ROOT/.active-request"
  lock_temp=$(mktemp "$INBOX_ROOT/.active-lock.XXXXXX") || return 1
  if ! printf '%s\n' "$lock_operation_id" > "$lock_temp" \
    || ! chown 0:0 "$lock_temp" \
    || ! chmod 400 "$lock_temp" \
    || ! sync -f "$lock_temp" \
    || ! mv -f "$lock_temp" "$lock_marker" \
    || ! sync -f "$INBOX_ROOT"; then
    rm -f "$lock_temp"
    return 1
  fi
  safe_locked_marker "$lock_marker" \
    && [ "$(read_active_id "$lock_marker" || true)" = "$lock_operation_id" ]
}

safe_admin_directory() {
  path=$1
  [ -d "$path" ] && [ ! -L "$path" ] || return 1
  [ "$(stat -c '%u:%g:%a' "$path")" = "$ADMIN_UID:$ADMIN_GID:750" ]
}

is_operation_id() {
  printf '%s\n' "$1" | awk 'NR == 1 && length($0) == 32 && $0 ~ /^[0-9a-f]+$/ { ok = 1 } END { exit !(NR == 1 && ok) }'
}

is_version() {
  printf '%s\n' "$1" | awk -F. '
    NR == 1 && NF == 3 && length($0) <= 32 {
      for (i = 1; i <= 3; i++) {
        if ($i !~ /^[0-9]+$/ || length($i) > 9 || (length($i) > 1 && substr($i, 1, 1) == "0")) exit 1
      }
      ok = 1
    }
    END { exit !(NR == 1 && ok) }
  '
}

is_sha256() {
  printf '%s\n' "$1" | awk 'NR == 1 && length($0) == 64 && $0 ~ /^[0-9a-f]+$/ { ok = 1 } END { exit !(NR == 1 && ok) }'
}

is_fingerprint() {
  printf '%s\n' "$1" | awk '
    NR == 1 && (length($0) == 40 || length($0) == 64) && $0 ~ /^[0-9A-F]+$/ { ok = 1 }
    END { exit !(NR == 1 && ok) }
  '
}

is_newer_version() {
  candidate=$1
  current=$2
  [ -z "$current" ] && return 0
  awk -F. -v candidate="$candidate" -v current="$current" '
    BEGIN {
      split(candidate, a, ".")
      split(current, b, ".")
      for (i = 1; i <= 3; i++) {
        if ((a[i] + 0) > (b[i] + 0)) exit 0
        if ((a[i] + 0) < (b[i] + 0)) exit 1
      }
      exit 1
    }
  '
}

read_config_value() {
  key=$1
  awk -v key="$key" '
    index($0, key "=") == 1 { sub("^[^=]*=", ""); print; found = 1; exit }
    END { if (!found) exit 1 }
  ' "$CONFIG_FILE"
}

load_trust_config() {
  safe_root_file "$CONFIG_FILE" || fail "trusted updater configuration is missing or unsafe"
  awk -F= '
    NR == 1 && $1 == "keyring" && length($2) > 0 { first = 1; next }
    NR == 2 && $1 == "signer_fingerprint" && length($2) > 0 { second = 1; next }
    { invalid = 1 }
    END { exit !(NR == 2 && first && second && !invalid) }
  ' "$CONFIG_FILE" || fail "trusted updater configuration has an invalid shape"
  KEYRING=$(read_config_value keyring)
  SIGNER_FINGERPRINT=$(read_config_value signer_fingerprint)
  [ "$KEYRING" = "$KEYRING_FILE" ] \
    || fail "trusted keyring path is not the installer-managed path"
  is_fingerprint "$SIGNER_FINGERPRINT" || fail "trusted signer fingerprint is invalid"
  safe_root_file "$KEYRING" || fail "trusted updater keyring is missing or unsafe"
  [ "$(stat -c '%s' "$KEYRING")" -le 1048576 ] || fail "trusted updater keyring is too large"
}

canonical_release_version() {
  link_path=$1
  [ -L "$link_path" ] || return 0
  resolved=$(readlink -f "$link_path" 2>/dev/null || true)
  case "$resolved" in
    "$INSTALL_ROOT/releases/"*) ;;
    *) return 0 ;;
  esac
  value=$(basename "$resolved")
  is_version "$value" || return 0
  printf '%s\n' "$value"
}

current_version() {
  canonical_release_version "$INSTALL_ROOT/current"
}

rollback_version() {
  canonical_release_version "$INSTALL_ROOT/previous"
}

json_nullable() {
  if [ -n "$1" ]; then
    printf '"%s"' "$1"
  else
    printf 'null'
  fi
}

status_checks() {
  case "$1" in
    idle)
      printf '[]'
      ;;
    queued)
      printf '[{"code":"request","label":"Request","state":"pending","message":"Waiting for the host updater."}]'
      ;;
    verifying)
      printf '[{"code":"integrity","label":"Bundle integrity","state":"pending","message":"Checking checksum and signed release authority."},{"code":"preflight","label":"Deployment preflight","state":"pending","message":"Preflight has not started."}]'
      ;;
    preflight)
      printf '[{"code":"integrity","label":"Bundle integrity","state":"passed","message":"Checksum and signer fingerprint verified."},{"code":"preflight","label":"Deployment preflight","state":"pending","message":"Validating the production deployment without activating it."}]'
      ;;
    ready)
      printf '[{"code":"integrity","label":"Bundle integrity","state":"passed","message":"Checksum and signer fingerprint verified."},{"code":"preflight","label":"Deployment preflight","state":"pending","message":"Executable preflight requires an Owner-authorized apply."}]'
      ;;
    applying)
      printf '[{"code":"integrity","label":"Bundle integrity","state":"passed","message":"Archived release was reverified."},{"code":"deployment","label":"Deployment","state":"pending","message":"Applying the complete platform release."}]'
      ;;
    verifying_deployment)
      printf '[{"code":"integrity","label":"Bundle integrity","state":"passed","message":"Archived release was reverified."},{"code":"deployment","label":"Deployment","state":"passed","message":"Installer completed."},{"code":"activation","label":"Activation","state":"pending","message":"Confirming the active release."}]'
      ;;
    succeeded)
      printf '[{"code":"integrity","label":"Bundle integrity","state":"passed","message":"Archived release was reverified."},{"code":"deployment","label":"Deployment","state":"passed","message":"Complete platform release applied."},{"code":"activation","label":"Activation","state":"passed","message":"Active release matches the approved target."}]'
      ;;
    rolling_back)
      printf '[{"code":"rollback","label":"Rollback","state":"pending","message":"Restoring the retained verified release."}]'
      ;;
    rolled_back)
      printf '[{"code":"rollback","label":"Rollback","state":"passed","message":"Retained verified release restored."}]'
      ;;
    failed)
      printf '[{"code":"operation","label":"Platform upgrade","state":"failed","message":"The host rejected or could not complete the operation."}]'
      ;;
    *)
      printf '[]'
      ;;
  esac
}

write_status() {
  state=$1
  operation_id=$2
  target_version=$3
  progress=$4
  can_apply=$5
  blocking_reason=$6
  message=$7

  active_version=$(current_version || true)
  previous_version=$(rollback_version || true)
  can_rollback=false
  if [ "$state" = "succeeded" ] && [ -n "$active_version" ] && [ -n "$previous_version" ] \
    && [ -f "$ARCHIVE_ROOT/$active_version/peeronq-server-$active_version.run" ]; then
    can_rollback=true
  fi
  temp_status=$(mktemp "$STATUS_ROOT/.status.XXXXXX") \
    || fail "could not create atomic platform-upgrade status"
  checks=$(status_checks "$state")
  {
    printf '{'
    printf '"schemaVersion":1,'
    printf '"state":"%s",' "$state"
    printf '"operationId":'; json_nullable "$operation_id"; printf ','
    printf '"currentVersion":'; json_nullable "$active_version"; printf ','
    printf '"targetVersion":'; json_nullable "$target_version"; printf ','
    printf '"rollbackVersion":'; json_nullable "$previous_version"; printf ','
    printf '"progressPercent":%s,' "$progress"
    printf '"canApply":%s,' "$can_apply"
    printf '"canRollback":%s,' "$can_rollback"
    printf '"blockingReason":'; json_nullable "$blocking_reason"; printf ','
    printf '"message":"%s",' "$message"
    operation_log="$LOG_ROOT/$operation_id.log"
    if [ -n "$operation_id" ] && safe_root_file "$operation_log" \
      && [ "$(stat -c '%a' "$operation_log")" = "600" ]; then
      printf '"logReference":"platform-upgrade-%s",' "$operation_id"
    else
      printf '"logReference":null,'
    fi
    printf '"updatedAtUtc":"%s",' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    printf '"checks":%s' "$checks"
    printf '}\n'
  } > "$temp_status" || {
    rm -f "$temp_status"
    fail "could not render platform-upgrade status"
  }
  [ "$(stat -c '%s' "$temp_status")" -le 16384 ] || {
    rm -f "$temp_status"
    fail "generated status exceeded its fixed bound"
  }
  chown "0:$ADMIN_GID" "$temp_status" || { rm -f "$temp_status"; fail "could not own platform-upgrade status"; }
  chmod 640 "$temp_status" || { rm -f "$temp_status"; fail "could not protect platform-upgrade status"; }
  sync -f "$temp_status" || { rm -f "$temp_status"; fail "could not flush platform-upgrade status"; }
  mv -f "$temp_status" "$STATUS_ROOT/status.json" || { rm -f "$temp_status"; fail "could not publish platform-upgrade status"; }
  sync -f "$STATUS_ROOT" || fail "could not flush the platform-upgrade status directory"
}

initialize_status() {
  [ -e "$STATUS_ROOT/status.json" ] || write_status idle "" "" 0 false "" "No platform upgrade is queued."
}

initialize_operation_log() {
  log_operation_id=$1
  log_file="$LOG_ROOT/$log_operation_id.log"
  (set -C; : > "$log_file") 2>/dev/null || return 1
  chown 0:0 "$log_file" || return 1
  chmod 600 "$log_file" || return 1
  printf '%s request claimed by the constrained host updater\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" \
    >> "$log_file" || return 1
  sync -f "$log_file" && sync -f "$LOG_ROOT"
}

append_operation_log() {
  log_operation_id=$1
  log_message=$2
  log_file="$LOG_ROOT/$log_operation_id.log"
  safe_root_file "$log_file" && [ "$(stat -c '%a' "$log_file")" = "600" ] || return 1
  printf '%s %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$log_message" >> "$log_file" \
    && sync -f "$log_file"
}

request_value() {
  key=$1
  file=$2
  awk -v key="$key" '
    index($0, key "=") == 1 { sub("^[^=]*=", ""); print; found = 1; exit }
    END { if (!found) exit 1 }
  ' "$file"
}

validate_request_shape() {
  file=$1
  action=$(awk -F= 'NR == 3 && $1 == "action" { print $2; exit }' "$file")
  case "$action" in
    stage)
      expected='schema request_id action version expected_current bundle_file checksum_file signature_file bundle_sha256 bundle_size'
      expected_lines=10
      ;;
    apply|rollback)
      expected='schema request_id action version expected_current'
      expected_lines=5
      ;;
    *) return 1 ;;
  esac
  awk -F= -v expected="$expected" -v expected_lines="$expected_lines" '
    BEGIN { count = split(expected, keys, " ") }
    index($0, "\r") > 0 || NF < 2 || $1 != keys[NR] { invalid = 1 }
    END { exit !(NR == expected_lines && count == expected_lines && !invalid) }
  ' "$file" || return 1
  [ "$(request_value schema "$file")" = "1" ] || return 1
}

validate_claimed_files() {
  request_dir=$1
  action=$2
  request_file="$request_dir/request.env"
  safe_admin_directory "$request_dir" || return 1
  safe_admin_file "$request_file" || return 1

  if [ "$action" = "stage" ]; then
    bundle_file=$(request_value bundle_file "$request_file")
    checksum_file=$(request_value checksum_file "$request_file")
    signature_file=$(request_value signature_file "$request_file")
    expected_names="request.env $bundle_file $checksum_file $signature_file"
    expected_count=4
  else
    expected_names="request.env"
    expected_count=1
  fi

  actual_count=$(find "$request_dir" -mindepth 1 -maxdepth 1 -print | wc -l | tr -d '[:space:]')
  [ "$actual_count" = "$expected_count" ] || return 1
  for name in $expected_names; do
    case "$name" in
      */*|.*|'') return 1 ;;
    esac
    safe_admin_file "$request_dir/$name" || return 1
  done
}

claim_request() {
  active_marker="$INBOX_ROOT/.active-request"
  [ -e "$active_marker" ] || return 1
  marker_kind=
  if safe_admin_file "$active_marker"; then
    marker_kind=producer
  elif safe_locked_marker "$active_marker"; then
    marker_kind=locked
  elif marker_is_fresh "$active_marker"; then
    CLAIM_PENDING=true
    return 1
  else
    write_status failed "" "" 0 false manual_recovery "An unsafe request gate requires root-operator recovery."
    return 1
  fi
  if [ "$(stat -c '%s' "$active_marker")" -gt 64 ]; then
    if [ "$marker_kind" = "producer" ] && marker_is_fresh "$active_marker"; then CLAIM_PENDING=true; return 1; fi
    write_status failed "" "" 0 false manual_recovery "An invalid request gate requires root-operator recovery."
    return 1
  fi
  operation_id=$(read_active_id "$active_marker" || true)
  if ! is_operation_id "$operation_id"; then
    if [ "$marker_kind" = "producer" ] && marker_is_fresh "$active_marker"; then CLAIM_PENDING=true; return 1; fi
    write_status failed "" "" 0 false manual_recovery "A malformed request gate requires root-operator recovery."
    return 1
  fi

  request_dir="$INBOX_ROOT/$operation_id"
  claimed_dir="$PROCESSING_ROOT/$operation_id"
  if [ -e "$claimed_dir" ]; then
    # A prior updater process may have terminated after its atomic claim. Never
    # replay an install whose outcome is uncertain; preserve the gate and the
    # claimed evidence for explicit root-operator recovery.
    write_status failed "$operation_id" "" 0 false manual_recovery "An interrupted host operation requires root-operator recovery." \
      || fail "could not publish interrupted-operation status"
    return 1
  fi
  if [ "$marker_kind" = "locked" ]; then
    write_status failed "$operation_id" "" 0 false manual_recovery "A locked request without claimed evidence requires root-operator recovery." \
      || fail "could not publish locked-gate recovery status"
    return 1
  fi

  ready_marker="$INBOX_ROOT/$operation_id.ready"
  if [ ! -e "$ready_marker" ]; then
    if marker_is_fresh "$active_marker"; then
      CLAIM_PENDING=true
      return 1
    fi
    if [ -e "$request_dir" ] && ! safe_admin_directory "$request_dir"; then
      write_status failed "$operation_id" "" 0 false manual_recovery "An incomplete unsafe producer request requires root-operator recovery." \
        || fail "could not publish unsafe producer status"
      return 1
    fi
    write_status failed "$operation_id" "" 0 false producer_timeout "The request producer did not publish a ready marker before its lease expired." \
      || fail "could not publish producer-timeout status"
    if [ -d "$request_dir" ] && [ ! -L "$request_dir" ]; then
      rm -rf "$request_dir"
    fi
    release_active_request "$operation_id" producer \
      || fail "expired producer gate was changed or could not be released safely"
    return 1
  fi
  if ! safe_admin_file "$ready_marker" || [ "$(stat -c '%s' "$ready_marker")" -ne 0 ]; then
    rm -f "$ready_marker" "$active_marker" 2>/dev/null || true
    write_status failed "$operation_id" "" 0 false invalid_request "The ready marker was unsafe." \
      || fail "could not publish invalid-ready status"
    return 1
  fi
  if [ ! -d "$request_dir" ] || [ -L "$request_dir" ]; then
    rm -f "$ready_marker" "$active_marker"
    write_status failed "$operation_id" "" 0 false invalid_request "The queued request directory was missing or unsafe." \
      || fail "could not publish missing-request status"
    return 1
  fi

  lock_active_request "$operation_id" || fail "could not lock the claimed request gate"
  safe_admin_file "$ready_marker" && [ "$(stat -c '%s' "$ready_marker")" -eq 0 ] \
    && safe_admin_directory "$request_dir" \
    || fail "queued request changed while its gate was being locked"
  mv "$request_dir" "$claimed_dir" || fail "could not atomically claim the request directory"
  initialize_operation_log "$operation_id" \
    || fail "could not create root-only claimed-request evidence"
  rm -f "$ready_marker" || fail "could not remove the claimed ready marker"
  CLAIMED_DIR=$claimed_dir
  OPERATION_ID=$operation_id
  write_status queued "$operation_id" "" 0 false "" "The host updater claimed the request." \
    || fail "could not publish claimed-request status"
  return 0
}

release_active_request() {
  completed_operation_id=$1
  completed_marker_kind=$2
  completed_active_marker="$INBOX_ROOT/.active-request"
  case "$completed_marker_kind" in
    producer) safe_admin_file "$completed_active_marker" || return 1 ;;
    locked) safe_locked_marker "$completed_active_marker" || return 1 ;;
    *) return 1 ;;
  esac
  [ "$(stat -c '%s' "$completed_active_marker")" -le 64 ] || return 1
  completed_active_id=$(read_active_id "$completed_active_marker" || true)
  [ "$completed_active_id" = "$completed_operation_id" ] || return 1
  rm -f "$completed_active_marker"
}

verify_signature() {
  bundle=$1
  signature=$2
  log_file=$3
  status_file=$(mktemp "$PROCESSING_ROOT/.gpg-status.XXXXXX")
  if gpgv --status-fd 3 --keyring "$KEYRING" "$signature" "$bundle" \
      3>"$status_file" >>"$log_file" 2>&1; then
    verified=true
  else
    verified=false
  fi
  valid_count=$(awk -v expected="$SIGNER_FINGERPRINT" '
    $1 == "[GNUPG:]" && $2 == "VALIDSIG" \
      && (toupper($3) == expected || (NF == 12 && toupper($12) == expected)) { count++ }
    END { print count + 0 }
  ' "$status_file")
  all_valid_count=$(awk '$1 == "[GNUPG:]" && $2 == "VALIDSIG" { count++ } END { print count + 0 }' "$status_file")
  rm -f "$status_file"
  [ "$verified" = "true" ] && [ "$valid_count" = "1" ] && [ "$all_valid_count" = "1" ]
}

embedded_bundle_version() {
  awk -F'"' '
    NR > 16 { exit }
    /^INSTALLER_VERSION="[0-9]+\.[0-9]+\.[0-9]+"$/ { print $2; found = 1; exit }
    END { if (!found) exit 1 }
  ' "$1"
}

verify_bundle_set() {
  verify_directory=$1
  verify_version=$2
  verify_expected_sha=$3
  verify_expected_size=$4
  verify_log_file=$5
  bundle_name="peeronq-server-$verify_version.run"
  checksum_name="$bundle_name.sha256"
  signature_name="$bundle_name.asc"
  verify_bundle="$verify_directory/$bundle_name"
  verify_checksum="$verify_directory/$checksum_name"
  verify_signature_file="$verify_directory/$signature_name"

  [ -f "$verify_bundle" ] && [ ! -L "$verify_bundle" ] && [ "$(stat -c '%h' "$verify_bundle")" = "1" ] || return 1
  [ -f "$verify_checksum" ] && [ ! -L "$verify_checksum" ] && [ "$(stat -c '%h' "$verify_checksum")" = "1" ] || return 1
  [ -f "$verify_signature_file" ] && [ ! -L "$verify_signature_file" ] && [ "$(stat -c '%h' "$verify_signature_file")" = "1" ] || return 1
  case "$verify_directory" in
    "$ARCHIVE_ROOT/"*)
    [ "$(stat -c '%u:%g:%a' "$verify_bundle")" = "0:0:500" ] \
      && [ "$(stat -c '%u:%g:%a' "$verify_checksum")" = "0:0:400" ] \
      && [ "$(stat -c '%u:%g:%a' "$verify_signature_file")" = "0:0:400" ] || return 1
      ;;
  esac
  actual_size=$(stat -c '%s' "$verify_bundle")
  [ "$actual_size" = "$verify_expected_size" ] && [ "$actual_size" -gt 0 ] \
    && [ "$actual_size" -le "$MAX_BUNDLE_BYTES" ] || return 1
  [ "$(stat -c '%s' "$verify_checksum")" -le 512 ] || return 1
  [ "$(stat -c '%s' "$verify_signature_file")" -le 1048576 ] || return 1
  actual_sha=$(sha256sum "$verify_bundle" | awk '{ print $1 }')
  [ "$actual_sha" = "$verify_expected_sha" ] || return 1
  awk -v sha="$verify_expected_sha" -v name="$bundle_name" '
    NR == 1 && NF == 2 && $1 == sha && $2 == name { valid = 1; next }
    { invalid = 1 }
    END { exit !(NR == 1 && valid && !invalid) }
  ' "$verify_checksum" || return 1
  [ "$(embedded_bundle_version "$verify_bundle" 2>/dev/null || true)" = "$verify_version" ] || return 1
  verify_signature "$verify_bundle" "$verify_signature_file" "$verify_log_file"
}

archive_metadata_value() {
  key=$1
  archive_dir=$2
  request_value "$key" "$archive_dir/metadata.env"
}

verify_archive() {
  archive_version=$1
  archive_log_file=$2
  archive_dir="$ARCHIVE_ROOT/$archive_version"
  [ -d "$archive_dir" ] && [ ! -L "$archive_dir" ] \
    && [ "$(stat -c '%u:%g:%a' "$archive_dir")" = "0:0:700" ] || return 1
  metadata="$archive_dir/metadata.env"
  safe_root_file "$metadata" || return 1
  awk -F= '
    NR == 1 && $1 == "schema" && $2 == "1" { one = 1; next }
    NR == 2 && $1 == "version" { two = 1; next }
    NR == 3 && $1 == "bundle_sha256" { three = 1; next }
    NR == 4 && $1 == "bundle_size" { four = 1; next }
    { invalid = 1 }
    END { exit !(NR == 4 && one && two && three && four && !invalid) }
  ' "$metadata" || return 1
  [ "$(archive_metadata_value version "$archive_dir")" = "$archive_version" ] || return 1
  sha=$(archive_metadata_value bundle_sha256 "$archive_dir")
  size=$(archive_metadata_value bundle_size "$archive_dir")
  is_sha256 "$sha" || return 1
  case "$size" in *[!0-9]*|'') return 1 ;; esac
  verify_bundle_set "$archive_dir" "$archive_version" "$sha" "$size" "$archive_log_file"
}

prune_verified_archives() {
  keep_primary=$1
  keep_secondary=$2
  prune_log_file=$3
  archive_candidate_names=$(find "$ARCHIVE_ROOT" -mindepth 1 -maxdepth 1 -type d -printf '%f\n')
  for archive_candidate_version in $archive_candidate_names; do
    archive_candidate="$ARCHIVE_ROOT/$archive_candidate_version"
    [ -d "$archive_candidate" ] && [ ! -L "$archive_candidate" ] || continue
    is_version "$archive_candidate_version" || continue
    [ "$archive_candidate_version" = "$keep_primary" ] && continue
    [ -n "$keep_secondary" ] && [ "$archive_candidate_version" = "$keep_secondary" ] && continue
    archive_entry_count=$(find "$archive_candidate" -mindepth 1 -maxdepth 1 -print | wc -l | tr -d '[:space:]')
    [ "$archive_entry_count" = "4" ] || continue
    verify_archive "$archive_candidate_version" "$prune_log_file" || continue
    rm -rf "$archive_candidate" || return 1
  done
}

cleanup_stale_archive_temps() {
  stage_candidate_names=$(find "$ARCHIVE_ROOT" -mindepth 1 -maxdepth 1 -type d -name '.stage-*' -printf '%f\n')
  for stage_candidate_name in $stage_candidate_names; do
    stage_candidate="$ARCHIVE_ROOT/$stage_candidate_name"
    stage_tail=${stage_candidate_name#.stage-}
    stage_version=${stage_tail%.*}
    stage_suffix=${stage_tail##*.}
    is_version "$stage_version" || continue
    printf '%s\n' "$stage_suffix" \
      | awk 'NR == 1 && length($0) == 6 && $0 ~ /^[0-9A-Za-z]+$/ { ok = 1 } END { exit !(NR == 1 && ok) }' \
      || continue
    [ -d "$stage_candidate" ] && [ ! -L "$stage_candidate" ] \
      && [ "$(stat -c '%u:%g:%a' "$stage_candidate")" = "0:0:700" ] || continue
    stage_entry_count=$(find "$stage_candidate" -mindepth 1 -maxdepth 1 -print | wc -l | tr -d '[:space:]')
    [ "$stage_entry_count" -le 4 ] || continue
    rm -rf "$stage_candidate"
  done
}

run_installer() {
  bundle=$1
  mode=$2
  log_file=$3
  case "$mode" in
    preflight)
      "$bundle" --env-file "$ENV_FILE" --dry-run >>"$log_file" 2>&1
      ;;
    apply)
      "$bundle" --env-file "$ENV_FILE" >>"$log_file" 2>&1
      ;;
    rollback)
      "$bundle" --env-file "$ENV_FILE" --rollback >>"$log_file" 2>&1
      ;;
    *) return 64 ;;
  esac
}

process_stage() {
  request_dir=$1
  request_file="$request_dir/request.env"
  operation_id=$2
  version=$(request_value version "$request_file")
  expected_current=$(request_value expected_current "$request_file")
  bundle_file=$(request_value bundle_file "$request_file")
  checksum_file=$(request_value checksum_file "$request_file")
  signature_file=$(request_value signature_file "$request_file")
  bundle_sha=$(request_value bundle_sha256 "$request_file")
  bundle_size=$(request_value bundle_size "$request_file")
  log_file="$LOG_ROOT/$operation_id.log"
  safe_root_file "$log_file" && [ "$(stat -c '%a' "$log_file")" = "600" ] \
    || fail "claimed stage request has no safe operation log"

  if ! is_version "$version" || ! is_sha256 "$bundle_sha"; then
    write_status failed "$operation_id" "" 0 false invalid_request "The staged release metadata was invalid."
    return 0
  fi
  case "$bundle_size" in *[!0-9]*|'')
    write_status failed "$operation_id" "$version" 0 false invalid_request "The staged bundle size was invalid."
    return 0 ;;
  esac
  canonical_bundle="peeronq-server-$version.run"
  [ "$bundle_file" = "$canonical_bundle" ] \
    && [ "$checksum_file" = "$canonical_bundle.sha256" ] \
    && [ "$signature_file" = "$canonical_bundle.asc" ] || {
      write_status failed "$operation_id" "$version" 0 false invalid_request "The staged artifact names were not canonical."
      return 0
    }
  active=$(current_version || true)
  [ "$expected_current" = "$active" ] || {
    write_status failed "$operation_id" "$version" 0 false stale_current "The active release changed before staging."
    return 0
  }
  is_newer_version "$version" "$active" || {
    write_status failed "$operation_id" "$version" 0 false downgrade "The target must be newer than the active release."
    return 0
  }
  [ ! -e "$ARCHIVE_ROOT/$version" ] || {
    write_status failed "$operation_id" "$version" 0 false replay "That platform release is already staged."
    return 0
  }

  write_status verifying "$operation_id" "$version" 15 false "" "The host is verifying the signed platform bundle."
  verify_bundle_set "$request_dir" "$version" "$bundle_sha" "$bundle_size" "$log_file" || {
    write_status failed "$operation_id" "$version" 15 false signature_invalid "Bundle integrity or signer verification failed."
    return 0
  }

  archive_temp=$(mktemp -d "$ARCHIVE_ROOT/.stage-$version.XXXXXX")
  chmod 700 "$archive_temp"
  install -o 0 -g 0 -m 0500 "$request_dir/$bundle_file" "$archive_temp/$bundle_file"
  install -o 0 -g 0 -m 0400 "$request_dir/$checksum_file" "$archive_temp/$checksum_file"
  install -o 0 -g 0 -m 0400 "$request_dir/$signature_file" "$archive_temp/$signature_file"
  metadata_temp="$archive_temp/metadata.env"
  {
    printf 'schema=1\n'
    printf 'version=%s\n' "$version"
    printf 'bundle_sha256=%s\n' "$bundle_sha"
    printf 'bundle_size=%s\n' "$bundle_size"
  } > "$metadata_temp"
  chmod 400 "$metadata_temp"
  chown 0:0 "$metadata_temp"

  mv "$archive_temp" "$ARCHIVE_ROOT/$version"
  prune_verified_archives "$active" "$version" "$log_file" || {
    write_status failed "$operation_id" "$version" 90 false storage_cleanup_failed "The signed archive was retained, but bounded archive cleanup failed."
    return 0
  }
  write_status ready "$operation_id" "$version" 100 true "" "The signed platform archive is verified; executable preflight awaits an Owner-authorized apply."
}

process_apply() {
  request_dir=$1
  request_file="$request_dir/request.env"
  operation_id=$2
  version=$(request_value version "$request_file")
  expected_current=$(request_value expected_current "$request_file")
  log_file="$LOG_ROOT/$operation_id.log"
  safe_root_file "$log_file" && [ "$(stat -c '%a' "$log_file")" = "600" ] \
    || fail "claimed apply request has no safe operation log"

  is_version "$version" || {
    write_status failed "$operation_id" "" 0 false invalid_request "The apply target version was invalid."
    return 0
  }
  active=$(current_version || true)
  [ "$expected_current" = "$active" ] || {
    write_status failed "$operation_id" "$version" 0 false stale_current "The active release changed before apply."
    return 0
  }
  is_newer_version "$version" "$active" || {
    write_status failed "$operation_id" "$version" 0 false downgrade "The apply target must be newer than the active release."
    return 0
  }
  write_status verifying "$operation_id" "$version" 10 false "" "The host is reverifying the archived release."
  verify_archive "$version" "$log_file" || {
    write_status failed "$operation_id" "$version" 10 false archive_invalid "The retained release archive failed verification."
    return 0
  }
  bundle="$ARCHIVE_ROOT/$version/peeronq-server-$version.run"
  write_status preflight "$operation_id" "$version" 25 false "" "Final production preflight is running."
  run_installer "$bundle" preflight "$log_file" || {
    write_status failed "$operation_id" "$version" 25 false preflight_failed "Final production preflight failed; apply was not started."
    return 0
  }
  write_status applying "$operation_id" "$version" 45 false "" "The host is applying the complete platform release."
  run_installer "$bundle" apply "$log_file" || {
    write_status failed "$operation_id" "$version" 45 false deployment_failed "Deployment failed; the installer restored the previous application release when possible."
    return 0
  }
  write_status verifying_deployment "$operation_id" "$version" 90 false "" "Deployment completed; the active release is being confirmed."
  [ "$(current_version || true)" = "$version" ] || {
    write_status failed "$operation_id" "$version" 90 false activation_mismatch "Deployment completed without activating the approved target."
    return 0
  }
  prune_verified_archives "$version" "$(rollback_version || true)" "$log_file" || {
    write_status failed "$operation_id" "$version" 95 false storage_cleanup_failed "Deployment succeeded, but bounded archive cleanup requires operator attention."
    return 0
  }
  write_status succeeded "$operation_id" "$version" 100 false "" "The complete platform release is active."
}

process_rollback() {
  request_dir=$1
  request_file="$request_dir/request.env"
  operation_id=$2
  version=$(request_value version "$request_file")
  expected_current=$(request_value expected_current "$request_file")
  log_file="$LOG_ROOT/$operation_id.log"
  safe_root_file "$log_file" && [ "$(stat -c '%a' "$log_file")" = "600" ] \
    || fail "claimed rollback request has no safe operation log"

  is_version "$version" || {
    write_status failed "$operation_id" "" 0 false invalid_request "The rollback target version was invalid."
    return 0
  }
  active=$(current_version || true)
  previous=$(rollback_version || true)
  [ "$expected_current" = "$active" ] || {
    write_status failed "$operation_id" "$version" 0 false stale_current "The active release changed before rollback."
    return 0
  }
  [ -n "$active" ] && [ "$version" = "$previous" ] || {
    write_status failed "$operation_id" "$version" 0 false rollback_unavailable "The requested rollback is not the retained previous release."
    return 0
  }
  write_status verifying "$operation_id" "$version" 15 false "" "The host is reverifying the retained rollback authority."
  verify_archive "$active" "$log_file" || {
    write_status failed "$operation_id" "$version" 15 false archive_invalid "The retained current-release archive failed verification."
    return 0
  }
  bundle="$ARCHIVE_ROOT/$active/peeronq-server-$active.run"
  write_status rolling_back "$operation_id" "$version" 50 false "" "The host is restoring the retained verified release."
  run_installer "$bundle" rollback "$log_file" || {
    write_status failed "$operation_id" "$version" 50 false rollback_failed "Rollback failed; host diagnostics remain root-only."
    return 0
  }
  [ "$(current_version || true)" = "$version" ] || {
    write_status failed "$operation_id" "$version" 90 false activation_mismatch "Rollback completed without activating the retained target."
    return 0
  }
  prune_verified_archives "$version" "$(rollback_version || true)" "$log_file" || {
    write_status failed "$operation_id" "$version" 95 false storage_cleanup_failed "Rollback succeeded, but bounded archive cleanup requires operator attention."
    return 0
  }
  write_status rolled_back "$operation_id" "$version" 100 false "" "The retained verified platform release is active."
}

process_claimed_request() {
  request_dir=$1
  operation_id=$2
  request_file="$request_dir/request.env"
  if ! safe_admin_file "$request_file" || ! validate_request_shape "$request_file"; then
    append_operation_log "$operation_id" "request rejected: invalid request contract" \
      || fail "could not record invalid request contract"
    write_status failed "$operation_id" "" 0 false invalid_request "The request contract was invalid."
    return 0
  fi
  request_id=$(request_value request_id "$request_file")
  action=$(request_value action "$request_file")
  [ "$request_id" = "$operation_id" ] || {
    append_operation_log "$operation_id" "request rejected: identifier did not match the claimed queue entry" \
      || fail "could not record request identifier rejection"
    write_status failed "$operation_id" "" 0 false invalid_request "The request identifier did not match its queue entry."
    return 0
  }
  validate_claimed_files "$request_dir" "$action" || {
    append_operation_log "$operation_id" "request rejected: claimed file set was unsafe or non-canonical" \
      || fail "could not record claimed file-set rejection"
    write_status failed "$operation_id" "" 0 false invalid_request "The request contained missing, extra, linked, or unsafe files."
    return 0
  }
  find "$request_dir" -mindepth 1 -maxdepth 1 -type f -exec chown 0:0 {} \;
  find "$request_dir" -mindepth 1 -maxdepth 1 -type f -exec chmod 400 {} \;
  chown 0:0 "$request_dir"
  chmod 700 "$request_dir"
  append_operation_log "$operation_id" "request contract accepted for constrained processing" \
    || fail "could not record accepted request contract"
  case "$action" in
    stage) process_stage "$request_dir" "$operation_id" ;;
    apply) process_apply "$request_dir" "$operation_id" ;;
    rollback) process_rollback "$request_dir" "$operation_id" ;;
    *) return 1 ;;
  esac
}

[ "$(id -u)" = "0" ] || fail "run this agent as root"
umask 077
for tool in awk basename chmod chown date find flock gpgv install mktemp mv readlink rm sha256sum stat sync tr wc; do
  require_command "$tool"
done
for directory in "$PLATFORM_ROOT" "$PROCESSING_ROOT" "$ARCHIVE_ROOT" "$LOG_ROOT"; do
  [ -d "$directory" ] && [ ! -L "$directory" ] \
    && [ "$(stat -c '%u:%g' "$directory")" = "0:0" ] || fail "protected host directory is missing or unsafe: $directory"
done
[ "$(stat -c '%a' "$PROCESSING_ROOT")" = "700" ] \
  && [ "$(stat -c '%a' "$ARCHIVE_ROOT")" = "700" ] \
  && [ "$(stat -c '%a' "$LOG_ROOT")" = "700" ] \
  || fail "root-only platform upgrade directories have unsafe permissions"
[ -d "$INBOX_ROOT" ] && [ ! -L "$INBOX_ROOT" ] \
  && [ "$(stat -c '%u:%g:%a' "$INBOX_ROOT")" = "0:$ADMIN_GID:1730" ] \
  || fail "Admin inbox directory is missing or unsafe"
[ "$(stat -c '%d' "$INBOX_ROOT")" = "$(stat -c '%d' "$PROCESSING_ROOT")" ] \
  || fail "Admin inbox and protected processing directory must share one filesystem"
[ "$(stat -c '%u:%g:%a' "$STATUS_ROOT")" = "0:$ADMIN_GID:750" ] \
  || fail "Admin status directory is missing or unsafe"
load_trust_config
if [ "${1:-}" = "--self-check" ]; then
  [ "$#" -eq 1 ] || fail "--self-check accepts no additional arguments"
  exit 0
fi
if [ "${1:-}" = "--initialize" ]; then
  [ "$#" -eq 1 ] || fail "--initialize accepts no additional arguments"
  initialize_status
  exit 0
fi
if [ "${1:-}" = "--refresh-idle" ]; then
  [ "$#" -eq 1 ] || fail "--refresh-idle accepts no additional arguments"
  write_status idle "" "" 0 false "" "No platform upgrade is queued."
  exit 0
fi
[ "$#" -eq 0 ] || fail "this agent accepts no request arguments"

initialize_status
mkdir -p "$(dirname "$LOCK_FILE")"
exec 9>"$LOCK_FILE"
flock -n 9 || exit 0

cleanup_stale_archive_temps
CLAIM_PENDING=false
if claim_request; then
  process_claimed_request "$CLAIMED_DIR" "$OPERATION_ID"
  rm -rf "$CLAIMED_DIR"
  release_active_request "$OPERATION_ID" locked \
    || fail "completed request gate was changed or could not be released safely"
elif [ "$CLAIM_PENDING" = "true" ]; then
  exit 75
fi
exit 0
