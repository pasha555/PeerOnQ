#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
bootstrap="$script_dir/bootstrap-peeronq-production.sh"
installer="$script_dir/peeronq-server-installer.sh"
renewal="$script_dir/renew-peeronq-tls.sh"
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

sh -n "$bootstrap"
sh -n "$installer"
sh -n "$renewal"

awk '
  {
    remaining = $0
    while (match(remaining, /\$\{PEERONQ_[A-Z0-9_]+:\?/)) {
      print substr(remaining, RSTART + 2, RLENGTH - 4)
      remaining = substr(remaining, RSTART + RLENGTH)
    }
  }
' \
  "$repo_root/src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml" \
  "$repo_root/src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml" \
  "$repo_root/src/PeerOnQ.Infrastructure.Deployment/docker-compose.production.yml" \
  | sort -u > "$temporary/required"

awk '
  $0 == "cat > \"$ENV_FILE\" <<EOF" { in_environment = 1; next }
  in_environment && $0 == "EOF" { exit }
  in_environment && match($0, /^PEERONQ_[A-Z0-9_]+=/) {
    print substr($0, RSTART, RLENGTH - 1)
  }
' "$bootstrap" | sort -u > "$temporary/generated"

missing=$(comm -23 "$temporary/required" "$temporary/generated")
[ -z "$missing" ] || {
  printf 'Bootstrap omits production-required Compose variables:\n%s\n' "$missing" >&2
  exit 1
}

grep -Fq 'customer_token=$(random_base64 48)' "$bootstrap"
grep -Fq 'PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY=$customer_token' "$bootstrap"
grep -Fq 'PEERONQ_CUSTOMER_MAIL_PROVIDER=$CUSTOMER_MAIL_PROVIDER' "$bootstrap"
grep -Fq 'PEERONQ_TURN_EXTERNAL_IP=$PUBLIC_IP' "$bootstrap"
if grep -Fq 'PEERONQ_TURN_EXTERNAL_IP=$PUBLIC_IP/$LOCAL_IP' "$bootstrap"; then
  printf 'Bootstrap still supplies the host LAN address as a coturn Docker-interface mapping.\n' >&2
  exit 1
fi
grep -Fq -- '--customer-smtp-host' "$bootstrap"
grep -Fq -- '--customer-smtp-host' "$installer"
grep -Fq 'actual_hash=$(wget --quiet -O - "http://127.0.0.1:8080/downloads/PeerOnQ-Windows-x64.msi?v=$2"' "$installer"
grep -Fq 'https://$PEERONQ_WEB_HOST/downloads/embedded-windows-version.txt' "$installer"
grep -Fq 'Public Windows client response is cacheable.' "$installer"
if grep -Fq 'destination=/tmp/peeronq-windows-x64.msi' "$installer"; then
  printf 'Windows client publication verification still depends on bounded container temporary storage.\n' >&2
  exit 1
fi
grep -Fq 'run_compose_step "quiesce previous application release without removing data" "$old_current"' "$installer"
grep -Fq 'down --remove-orphans || deployment_failed=true' "$installer"
grep -Fq 'PEERONQ_ADMIN_HOST PEERONQ_GRAFANA_HOST PEERONQ_PROMETHEUS_HOST' "$installer"
grep -Fq -- '--keep-until-expiring --expand "$@"' "$installer"
grep -Fq 'PEERONQ_ADMIN_HOST PEERONQ_GRAFANA_HOST PEERONQ_PROMETHEUS_HOST' "$renewal"
for acme_script in "$installer" "$renewal"; do
  grep -Fq 'run_standalone_acme_with_proxy_handoff' "$acme_script"
  grep -Fq 'stop -t 30 proxy' "$acme_script"
  grep -Fq 'start proxy' "$acme_script"
done
grep -Fq 'checkend 2592000' "$renewal"
grep -Fq 'atomic_install_root_file "$renewal_source" "$TLS_RENEWAL_COMMAND" 755' "$installer"
grep -Fq 'refresh_tls_renewal_command "$target"' "$installer"
grep -Fq 'validate_tls_import_source' "$installer"
grep -Fq 'checkend 86400' "$installer"
grep -Fq 'certificate_covers_required_hosts "$cert"' "$installer"
grep -Fq -- '-verify_hostname "$name" "$cert"' "$installer"
for tls_script in "$bootstrap" "$installer" "$renewal"; do
  if grep -Fq -- '-checkhost' "$tls_script"; then
    printf 'TLS hostname validation still relies on the non-failing x509 -checkhost status: %s\n' \
      "$tls_script" >&2
    exit 1
  fi
  grep -Fq -- '-verify_hostname' "$tls_script"
done
grep -Fq 'cmp -s "$public_key_dir/cert.der" "$public_key_dir/key.der"' "$installer"
grep -Fq 'begin_tls_import_transaction' "$installer"
grep -Fq 'retire_tls_renewal_management' "$installer"
grep -Fq 'rollback_tls_import "$old_current"' "$installer"
grep -Fq 'up -d --no-deps --force-recreate --wait --wait-timeout 120 proxy turn' "$installer"
grep -Fq 'write_tls_renewal_units \' "$bootstrap"
grep -Fq '"$ENV_FILE"' "$bootstrap"
tls_gate_line=$(grep -n '^ensure_required_tls_hosts$' "$installer" | tail -n 1 | cut -d: -f1)
quiesce_line=$(grep -n 'quiesce previous application release without removing data' "$installer" | tail -n 1 | cut -d: -f1)
[ -n "$tls_gate_line" ] && [ -n "$quiesce_line" ] && [ "$tls_gate_line" -lt "$quiesce_line" ] || {
  printf 'TLS host coverage must be repaired before the active release is stopped.\n' >&2
  exit 1
}
if grep -F 'quiesce previous application release without removing data' "$installer" | grep -Eq -- '--volumes|-v([[:space:]]|$)'; then
  printf 'Upgrade quiesce must preserve all existing volumes.\n' >&2
  exit 1
fi

renewal_refresh_line=$(grep -n 'refresh_tls_renewal_command "$target"' "$installer" | tail -n 1 | cut -d: -f1)
website_commit_line=$(grep -n 'transition_website_overlay commit "$target"' "$installer" | tail -n 1 | cut -d: -f1)
[ -n "$renewal_refresh_line" ] && [ -n "$website_commit_line" ] \
  && [ "$renewal_refresh_line" -lt "$website_commit_line" ] || {
  printf 'The managed TLS renewal command must refresh before the website overlay is committed.\n' >&2
  exit 1
}

tls_import_begin_line=$(grep -nF '  begin_tls_import_transaction \' "$installer" | tail -n 1 | cut -d: -f1)
tls_activation_line=$(grep -n 'activate imported TLS certificate' "$installer" | tail -n 1 | cut -d: -f1)
public_verification_line=$(grep -n '^  verify_public_control_plane "$target"' "$installer" | tail -n 1 | cut -d: -f1)
tls_retirement_line=$(grep -n '^  retire_tls_renewal_management' "$installer" | tail -n 1 | cut -d: -f1)
[ -n "$tls_import_begin_line" ] && [ "$tls_import_begin_line" -lt "$tls_gate_line" ] \
  || { printf 'In-place TLS import must publish before the required-host gate.\n' >&2; exit 1; }
[ -n "$tls_activation_line" ] && [ -n "$public_verification_line" ] \
  && [ "$tls_activation_line" -lt "$public_verification_line" ] \
  || { printf 'Imported TLS must activate before public verification.\n' >&2; exit 1; }
[ -n "$tls_retirement_line" ] && [ "$tls_retirement_line" -gt "$public_verification_line" ] \
  || { printf 'HTTP-01 management must retire only after public verification.\n' >&2; exit 1; }
[ "$tls_retirement_line" -lt "$website_commit_line" ] \
  || { printf 'HTTP-01 retirement must remain rollback-safe before website commit.\n' >&2; exit 1; }

tls_unit_renderer="$temporary/tls-renewal-unit-renderer.sh"
cat > "$tls_unit_renderer" <<'EOF'
#!/bin/sh
set -eu
EOF
awk '
  /^write_tls_renewal_units\(\)/ { copy = 1 }
  copy && /^random_hex\(\)/ { exit }
  copy { print }
' "$bootstrap" >> "$tls_unit_renderer"
cat >> "$tls_unit_renderer" <<'EOF'
runtime=$1
custom_environment=/etc/peeronq-custom/config.env
write_tls_renewal_units "$runtime/renew.service" "$runtime/renew.timer" "$custom_environment"
grep -Fxq \
  "ExecStart=/usr/local/sbin/peeronq-renew-tls --env-file $custom_environment" \
  "$runtime/renew.service"
EOF
chmod 755 "$tls_unit_renderer"
sh "$tls_unit_renderer" "$temporary"

tls_binding_unit="$temporary/tls-renewal-binding.sh"
cat > "$tls_binding_unit" <<'EOF'
#!/bin/sh
set -eu
fail() {
  return 1
}
EOF
awk '
  /^validate_managed_tls_binding\(\)/ { copy = 1 }
  copy && /^required_tls_hosts\(\)/ { exit }
  copy { print }
' "$renewal" >> "$tls_binding_unit"
cat >> "$tls_binding_unit" <<'EOF'
ENV_FILE=/etc/peeronq-custom/config.env
validate_managed_tls_binding \
  /etc/peeronq-custom/tls \
  /etc/peeronq-custom/tls/privkey.pem
if validate_managed_tls_binding \
  /etc/peeronq/tls \
  /etc/peeronq/tls/privkey.pem; then
  printf 'Renewal preflight accepted a TLS directory unrelated to its custom environment.\n' >&2
  exit 1
fi
EOF
chmod 755 "$tls_binding_unit"
sh "$tls_binding_unit"

tls_refresh_unit="$temporary/tls-renewal-refresh.sh"
cat > "$tls_refresh_unit" <<'EOF'
#!/bin/sh
set -eu

safe_root_runtime_file() {
  [ -f "$1" ] && [ ! -L "$1" ]
}

atomic_install_root_file() {
  cp "$1" "$2"
  chmod "$3" "$2"
}

same_file_contents() {
  cmp -s "$1" "$2"
}

ensure_root_directory() {
  [ ! -L "$1" ] || return 1
  mkdir -p "$1"
  chmod "$2" "$1"
}

validate_spaceship_credentials_dir() {
  [ -n "$1" ] && [ -d "$1" ]
}

systemctl() {
  return 0
}
EOF
awk '
  /^write_tls_renewal_units\(\)/ { copy = 1 }
  copy && /^install_platform_upgrade_agent\(\)/ { exit }
  copy { print }
' "$installer" >> "$tls_refresh_unit"
cat >> "$tls_refresh_unit" <<'EOF'
runtime=$1
renewal_source=$2
release_path="$runtime/release"
TLS_RENEWAL_COMMAND="$runtime/peeronq-renew-tls"
TLS_RENEWAL_SERVICE="$runtime/peeronq-tls-renew.service"
TLS_RENEWAL_TIMER="$runtime/peeronq-tls-renew.timer"
TLS_DNS_HOOK="$runtime/libexec/peeronq-spaceship-dns-hook.py"
SPACESHIP_CREDENTIALS_DIR=""
ENV_FILE=/etc/peeronq/peeronq.env
mkdir -p "$release_path/scripts/linux"
cp "$renewal_source" "$release_path/scripts/linux/renew-peeronq-tls.sh"
printf 'outdated\n' > "$TLS_RENEWAL_COMMAND"
printf 'ExecStart=%s --env-file %s\n' "$TLS_RENEWAL_COMMAND" "$ENV_FILE" > "$TLS_RENEWAL_SERVICE"
: > "$TLS_RENEWAL_TIMER"

refresh_tls_renewal_command "$release_path"
cmp -s "$renewal_source" "$TLS_RENEWAL_COMMAND"

rm -f "$TLS_RENEWAL_COMMAND" "$TLS_RENEWAL_SERVICE" "$TLS_RENEWAL_TIMER"
refresh_tls_renewal_command "$release_path"
[ ! -e "$TLS_RENEWAL_COMMAND" ]

: > "$TLS_RENEWAL_SERVICE"
if refresh_tls_renewal_command "$release_path"; then
  printf 'A partial TLS renewal installation unexpectedly passed the safety gate.\n' >&2
  exit 1
fi

# A server upgraded from managed HTTP-01 can have a complete renewal timer but no libexec
# directory yet. Enabling Spaceship DNS-01 must create the hook parent instead of rolling back an
# otherwise healthy platform upgrade.
printf 'outdated\n' > "$TLS_RENEWAL_COMMAND"
printf 'ExecStart=%s --env-file %s\n' "$TLS_RENEWAL_COMMAND" "$ENV_FILE" > "$TLS_RENEWAL_SERVICE"
: > "$TLS_RENEWAL_TIMER"
SPACESHIP_CREDENTIALS_DIR="$runtime/acme-spaceship"
mkdir -p "$SPACESHIP_CREDENTIALS_DIR"
printf 'dns hook\n' > "$release_path/scripts/linux/peeronq-spaceship-dns-hook.py"
rm -rf "$(dirname "$TLS_DNS_HOOK")"

refresh_tls_renewal_command "$release_path"
cmp -s "$release_path/scripts/linux/peeronq-spaceship-dns-hook.py" "$TLS_DNS_HOOK"
EOF
chmod 755 "$tls_refresh_unit"
sh "$tls_refresh_unit" "$temporary/tls-refresh-runtime" "$renewal"

acme_handoff_unit="$temporary/acme-proxy-handoff.sh"
cat > "$acme_handoff_unit" <<'EOF'
#!/bin/sh
set -eu
runtime=$1
log="$runtime/handoff.log"
mkdir -p "$runtime"
: > "$log"
CERTBOT_IMAGE=certbot:test
DOCKER_STATUS=0

current_release() {
  printf '/active\n'
}

compose() {
  printf 'compose:%s\n' "$*" >> "$log"
  case "${2-}" in
    ps) printf 'proxy\n' ;;
  esac
}

docker() {
  printf 'docker:%s\n' "$*" >> "$log"
  return "$DOCKER_STATUS"
}
EOF
awk '
  /^run_standalone_acme_with_proxy_handoff\(\)/ { copy = 1 }
  copy && /^ensure_required_tls_hosts\(\)/ { exit }
  copy { print }
' "$installer" >> "$acme_handoff_unit"
cat >> "$acme_handoff_unit" <<'EOF'
assert_handoff() {
  [ "$(grep -Fc 'compose:/active stop -t 30 proxy' "$log")" = "1" ]
  [ "$(grep -Fc 'compose:/active start proxy' "$log")" = "1" ]
  [ "$(grep -Fc 'docker:run --rm -p 80:80' "$log")" = "1" ]
  stop_line=$(grep -n 'compose:/active stop -t 30 proxy' "$log" | cut -d: -f1)
  docker_line=$(grep -n 'docker:run --rm -p 80:80' "$log" | cut -d: -f1)
  start_line=$(grep -n 'compose:/active start proxy' "$log" | cut -d: -f1)
  [ "$stop_line" -lt "$docker_line" ] && [ "$docker_line" -lt "$start_line" ]
}

run_standalone_acme_with_proxy_handoff /etc/letsencrypt certonly -d portal.peeronq.com
assert_handoff

: > "$log"
DOCKER_STATUS=7
if run_standalone_acme_with_proxy_handoff /etc/letsencrypt certonly -d portal.peeronq.com; then
  printf 'Failed ACME issuance unexpectedly passed the proxy handoff gate.\n' >&2
  exit 1
fi
assert_handoff
EOF
chmod 755 "$acme_handoff_unit"
sh "$acme_handoff_unit" "$temporary/acme-runtime"

tls_pair_unit="$temporary/tls-import-pair-validation.sh"
cat > "$tls_pair_unit" <<'EOF'
#!/bin/sh
set -eu

fail() {
  return 1
}

read_environment_value() {
  case "$1" in
    PEERONQ_WEB_HOST) printf 'peeronq.com\n' ;;
    PEERONQ_WEB_WWW_HOST) printf 'www.peeronq.com\n' ;;
    PEERONQ_API_HOST) printf 'api.peeronq.com\n' ;;
    PEERONQ_PORTAL_HOST) printf 'portal.peeronq.com\n' ;;
    PEERONQ_ADMIN_HOST) printf 'admin.peeronq.com\n' ;;
    PEERONQ_GRAFANA_HOST) printf 'grafana.peeronq.com\n' ;;
    PEERONQ_PROMETHEUS_HOST) printf 'prometheus.peeronq.com\n' ;;
    PEERONQ_DOWNLOAD_HOST) printf 'download.peeronq.com\n' ;;
    PEERONQ_UPDATE_HOST) printf 'updates.peeronq.com\n' ;;
    PEERONQ_PRESENCE_HOST) printf 'presence.peeronq.com\n' ;;
    PEERONQ_SIGNAL_HOST) printf 'signal.peeronq.com\n' ;;
    PEERONQ_TURN_PUBLIC_HOST) printf 'turn.peeronq.com\n' ;;
    *) return 1 ;;
  esac
}
EOF
awk '
  /^required_tls_hosts\(\)/ { copy = 1 }
  copy && /^run_standalone_acme_with_proxy_handoff\(\)/ { exit }
  copy { print }
' "$installer" >> "$tls_pair_unit"
cat >> "$tls_pair_unit" <<'EOF'
runtime=$1
mkdir -p "$runtime"
openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
  -subj /CN=peeronq.com \
  -addext 'subjectAltName=DNS:peeronq.com,DNS:*.peeronq.com' \
  -keyout "$runtime/valid.key" -out "$runtime/valid.pem" >/dev/null 2>&1
validate_tls_pair_material "$runtime/valid.pem" "$runtime/valid.key" \
  || { printf 'TLS import rejected a valid wildcard and apex certificate pair.\n' >&2; exit 1; }

openssl genpkey -algorithm RSA -out "$runtime/wrong.key" >/dev/null 2>&1
if validate_tls_pair_material "$runtime/valid.pem" "$runtime/wrong.key"; then
  printf 'TLS import accepted a mismatched private key.\n' >&2
  exit 1
fi

openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
  -subj /CN=peeronq.com \
  -addext 'subjectAltName=DNS:peeronq.com,DNS:www.peeronq.com' \
  -keyout "$runtime/incomplete.key" -out "$runtime/incomplete.pem" >/dev/null 2>&1
if validate_tls_pair_material "$runtime/incomplete.pem" "$runtime/incomplete.key"; then
  printf 'TLS import accepted a certificate missing required SANs.\n' >&2
  exit 1
fi
EOF
chmod 755 "$tls_pair_unit"
if command -v openssl >/dev/null 2>&1; then
  sh "$tls_pair_unit" "$temporary/tls-pair-runtime"
fi

tls_import_transaction_unit="$temporary/tls-import-transaction.sh"
cat > "$tls_import_transaction_unit" <<'EOF'
#!/bin/sh
set -eu

safe_root_runtime_file() {
  [ -f "$1" ] && [ ! -L "$1" ]
}

atomic_install_root_file() {
  cp "$1" "$2"
  chmod "$3" "$2"
}

validate_tls_pair_material() {
  [ -s "$1" ] && [ -s "$2" ]
}

read_environment_value() {
  [ "$1" = PEERONQ_TLS_CERT_DIR ] || return 1
  printf '%s\n' "$TLS_DIRECTORY"
}

chown() {
  return 0
}

compose() {
  printf 'compose:%s\n' "$*" >> "$ACTION_LOG"
  return "$COMPOSE_STATUS"
}

systemctl() {
  command_name=$1
  shift
  printf 'systemctl:%s %s\n' "$command_name" "$*" >> "$ACTION_LOG"
  case "$command_name" in
    is-enabled)
      [ "$TIMER_ENABLED" = true ]
      ;;
    is-active)
      unit_name=""
      for argument in "$@"; do
        case "$argument" in --*) ;; *) unit_name=$argument ;; esac
      done
      case "$unit_name" in
        peeronq-tls-renew.timer) [ "$TIMER_ACTIVE" = true ] ;;
        peeronq-tls-renew.service) [ "$SERVICE_ACTIVE" = true ] ;;
        *) return 1 ;;
      esac
      ;;
    stop)
      for unit_name in "$@"; do
        case "$unit_name" in
          peeronq-tls-renew.timer) TIMER_ACTIVE=false ;;
          peeronq-tls-renew.service) SERVICE_ACTIVE=false ;;
        esac
      done
      ;;
    disable)
      TIMER_ENABLED=false
      for argument in "$@"; do
        [ "$argument" != --now ] || TIMER_ACTIVE=false
      done
      ;;
    enable) TIMER_ENABLED=true ;;
    start)
      case "${1-}" in
        peeronq-tls-renew.timer) TIMER_ACTIVE=true ;;
        peeronq-tls-renew.service) SERVICE_ACTIVE=true ;;
      esac
      ;;
    daemon-reload) ;;
    *) return 1 ;;
  esac
}
EOF
awk '
  /^atomic_publish_tls_pair\(\)/ { copy = 1 }
  copy && /^prepare_managed_secret_file\(\)/ { exit }
  copy { print }
' "$installer" >> "$tls_import_transaction_unit"
cat >> "$tls_import_transaction_unit" <<'EOF'
runtime=$1
TLS_DIRECTORY="$runtime/managed-tls"
temporary="$runtime/installer-transaction"
ACTION_LOG="$runtime/actions.log"
ENV_FILE=/etc/peeronq/peeronq.env
TLS_RENEWAL_COMMAND="$runtime/peeronq-renew-tls"
TLS_RENEWAL_SERVICE="$runtime/peeronq-tls-renew.service"
TLS_RENEWAL_TIMER="$runtime/peeronq-tls-renew.timer"
TLS_CERT_SOURCE="$runtime/import-fullchain.pem"
TLS_KEY_SOURCE="$runtime/import-privkey.pem"
TLS_IMPORT_ACTIVE=false
TLS_IMPORT_RENEWAL_PRESENT=false
TLS_IMPORT_RENEWAL_WAS_ENABLED=false
TLS_IMPORT_RENEWAL_WAS_ACTIVE=false
TLS_IMPORT_RENEWAL_SERVICE_WAS_ACTIVE=false
TLS_IMPORT_TRANSACTION_DIR=""
TIMER_ENABLED=true
TIMER_ACTIVE=true
SERVICE_ACTIVE=false
COMPOSE_STATUS=0
mkdir -p "$TLS_DIRECTORY" "$temporary" "$runtime/release"
printf 'old certificate\n' > "$TLS_DIRECTORY/fullchain.pem"
printf 'old private key\n' > "$TLS_DIRECTORY/privkey.pem"
printf 'new certificate\n' > "$TLS_CERT_SOURCE"
printf 'new private key\n' > "$TLS_KEY_SOURCE"
printf '#!/bin/sh\n' > "$TLS_RENEWAL_COMMAND"
printf 'ExecStart=%s --env-file %s\n' "$TLS_RENEWAL_COMMAND" "$ENV_FILE" > "$TLS_RENEWAL_SERVICE"
printf '[Timer]\n' > "$TLS_RENEWAL_TIMER"

begin_tls_import_transaction
grep -Fxq 'new certificate' "$TLS_DIRECTORY/fullchain.pem"
grep -Fxq 'new private key' "$TLS_DIRECTORY/privkey.pem"
[ "$TIMER_ACTIVE" = false ] && [ "$SERVICE_ACTIVE" = false ]

retire_tls_renewal_management
[ ! -e "$TLS_RENEWAL_COMMAND" ] && [ ! -e "$TLS_RENEWAL_SERVICE" ] && [ ! -e "$TLS_RENEWAL_TIMER" ]
rollback_tls_import "$runtime/release"
grep -Fxq 'old certificate' "$TLS_DIRECTORY/fullchain.pem"
grep -Fxq 'old private key' "$TLS_DIRECTORY/privkey.pem"
[ -f "$TLS_RENEWAL_COMMAND" ] && [ -f "$TLS_RENEWAL_SERVICE" ] && [ -f "$TLS_RENEWAL_TIMER" ]
[ "$TIMER_ENABLED" = true ] && [ "$TIMER_ACTIVE" = true ] && [ "$TLS_IMPORT_ACTIVE" = false ]
grep -Fq 'compose:'"$runtime"'/release up -d --no-deps --force-recreate --wait --wait-timeout 120 proxy turn' "$ACTION_LOG"

partial="$runtime/partial"
TLS_DIRECTORY="$partial/managed-tls"
temporary="$partial/installer-transaction"
TLS_RENEWAL_COMMAND="$partial/peeronq-renew-tls"
TLS_RENEWAL_SERVICE="$partial/peeronq-tls-renew.service"
TLS_RENEWAL_TIMER="$partial/peeronq-tls-renew.timer"
TLS_CERT_SOURCE="$partial/import-fullchain.pem"
TLS_KEY_SOURCE="$partial/import-privkey.pem"
TLS_IMPORT_ACTIVE=false
TLS_IMPORT_RENEWAL_PRESENT=false
TLS_IMPORT_TRANSACTION_DIR=""
mkdir -p "$TLS_DIRECTORY" "$temporary"
printf 'old certificate\n' > "$TLS_DIRECTORY/fullchain.pem"
printf 'old private key\n' > "$TLS_DIRECTORY/privkey.pem"
printf 'new certificate\n' > "$TLS_CERT_SOURCE"
printf 'new private key\n' > "$TLS_KEY_SOURCE"
printf '#!/bin/sh\n' > "$TLS_RENEWAL_COMMAND"
if begin_tls_import_transaction; then
  printf 'TLS import accepted a partial renewal installation.\n' >&2
  exit 1
fi
grep -Fxq 'old certificate' "$TLS_DIRECTORY/fullchain.pem"
[ "$TLS_IMPORT_ACTIVE" = false ]
EOF
chmod 755 "$tls_import_transaction_unit"
sh "$tls_import_transaction_unit" "$temporary/tls-import-runtime"

migration_unit="$temporary/customer-mail-migration.sh"
awk '
  /^ensure_environment_schema\(\)/ { exit }
  { print }
' "$installer" > "$migration_unit"
cat >> "$migration_unit" <<'EOF'
ENV_FILE=$1
CUSTOMER_SMTP_HOST=$2
CUSTOMER_SMTP_PORT=$3
CUSTOMER_SMTP_HOST_PROVIDED=$4
CUSTOMER_SMTP_PORT_PROVIDED=$5
CUSTOMER_MAIL_DISABLE_REQUESTED=${6-false}
ensure_customer_mail_configuration
EOF
chmod 755 "$migration_unit"

mkdir "$temporary/stubs"
cat > "$temporary/stubs/chown" <<'EOF'
#!/bin/sh
exit 0
EOF
cat > "$temporary/stubs/getent" <<'EOF'
#!/bin/sh
[ "${1-}" = 'ahosts' ] || exit 1
[ "${2-}" != 'smtp.unresolvable.invalid' ] || exit 1
printf '192.0.2.1 STREAM %s\n' "${2-}"
EOF
chmod 755 "$temporary/stubs/chown" "$temporary/stubs/getent"

legacy_environment="$temporary/legacy.env"
: > "$legacy_environment"
PATH="$temporary/stubs:$PATH" sh "$migration_unit" \
  "$legacy_environment" '' 587 false false
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_MAIL_PROVIDER=Disabled' "$legacy_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_REGISTRATION_MODE=Closed' "$legacy_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION=false' "$legacy_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_SMTP_HOST=' "$legacy_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_SMTP_PORT=587' "$legacy_environment")" = "1" ]

smtp_environment="$temporary/smtp.env"
: > "$smtp_environment"
PATH="$temporary/stubs:$PATH" sh "$migration_unit" \
  "$smtp_environment" smtp.example.com 587 true false
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_MAIL_PROVIDER=Smtp' "$smtp_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION=true' "$smtp_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_SMTP_HOST=smtp.example.com' "$smtp_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_SMTP_PORT=587' "$smtp_environment")" = "1" ]

if PATH="$temporary/stubs:$PATH" sh "$migration_unit" \
  "$smtp_environment" other.example.com 587 true false \
  > "$temporary/conflict.out" 2>&1; then
  printf 'SMTP migration unexpectedly replaced an existing host.\n' >&2
  exit 1
fi
grep -Fq 'is already configured' "$temporary/conflict.out"
grep -Fq 'PEERONQ_CUSTOMER_SMTP_HOST=smtp.example.com' "$smtp_environment"

unresolved_environment="$temporary/unresolved.env"
: > "$unresolved_environment"
if PATH="$temporary/stubs:$PATH" sh "$migration_unit" \
  "$unresolved_environment" smtp.unresolvable.invalid 587 true false \
  > "$temporary/unresolved.out" 2>&1; then
  printf 'Unresolved SMTP host unexpectedly passed migration.\n' >&2
  exit 1
fi
grep -Fq 'customer SMTP host does not resolve from this server' "$temporary/unresolved.out"

existing_smtp_environment="$temporary/existing-smtp.env"
printf '%s\n' \
  'PEERONQ_CUSTOMER_MAIL_PROVIDER=Smtp' \
  'PEERONQ_CUSTOMER_SMTP_HOST=smtp.example.com' \
  > "$existing_smtp_environment"
PATH="$temporary/stubs:$PATH" sh "$migration_unit" \
  "$existing_smtp_environment" '' 587 false false
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_MAIL_PROVIDER=Smtp' "$existing_smtp_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_SMTP_HOST=smtp.example.com' "$existing_smtp_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_CUSTOMER_SMTP_PORT=587' "$existing_smtp_environment")" = "1" ]

port_only_environment="$temporary/port-only.env"
: > "$port_only_environment"
if PATH="$temporary/stubs:$PATH" sh "$migration_unit" \
  "$port_only_environment" '' 2525 false true \
  > "$temporary/port-only.out" 2>&1; then
  printf 'SMTP port without a host unexpectedly passed migration.\n' >&2
  exit 1
fi
grep -Fq -- '--customer-smtp-port requires --customer-smtp-host' "$temporary/port-only.out"

forced_disabled_environment="$temporary/forced-disabled.env"
printf '%s\n' \
  'PEERONQ_CUSTOMER_MAIL_PROVIDER=Smtp' \
  'PEERONQ_CUSTOMER_REGISTRATION_MODE=Closed' \
  'PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION=true' \
  'PEERONQ_CUSTOMER_SMTP_HOST=smtp.unresolvable.invalid' \
  'PEERONQ_CUSTOMER_SMTP_PORT=2525' \
  'PEERONQ_CUSTOMER_SMTP_USERNAME=smtp-user' \
  'PEERONQ_CUSTOMER_SMTP_PASSWORD=smtp-credential' \
  > "$forced_disabled_environment"
PATH="$temporary/stubs:$PATH" sh "$migration_unit" \
  "$forced_disabled_environment" '' 587 false false true
for expected in \
  PEERONQ_CUSTOMER_MAIL_PROVIDER=Disabled \
  PEERONQ_CUSTOMER_REGISTRATION_MODE=Closed \
  PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION=false \
  PEERONQ_CUSTOMER_SMTP_HOST= \
  PEERONQ_CUSTOMER_SMTP_PORT=587 \
  PEERONQ_CUSTOMER_SMTP_USERNAME= \
  PEERONQ_CUSTOMER_SMTP_PASSWORD=
do
  [ "$(grep -Fxc "$expected" "$forced_disabled_environment")" = "1" ] || {
    printf 'Explicit customer-mail disable did not canonicalize: %s\n' "$expected" >&2
    exit 1
  }
done

host_migration_unit="$temporary/public-host-migration.sh"
awk '
  /^ensure_environment_schema\(\)/ { exit }
  { print }
' "$installer" > "$host_migration_unit"
cat >> "$host_migration_unit" <<'EOF'
ENV_FILE=$1
ensure_public_host_configuration
EOF
chmod 755 "$host_migration_unit"

legacy_host_environment="$temporary/legacy-hosts.env"
printf 'PEERONQ_WEB_HOST=peeronq.com\n' > "$legacy_host_environment"
PATH="$temporary/stubs:$PATH" sh "$host_migration_unit" "$legacy_host_environment"
for expected in \
  PEERONQ_WEB_WWW_HOST=www.peeronq.com \
  PEERONQ_API_HOST=api.peeronq.com \
  PEERONQ_PORTAL_HOST=portal.peeronq.com \
  PEERONQ_ADMIN_HOST=admin.peeronq.com \
  PEERONQ_GRAFANA_HOST=grafana.peeronq.com \
  PEERONQ_PROMETHEUS_HOST=prometheus.peeronq.com \
  PEERONQ_DOWNLOAD_HOST=download.peeronq.com \
  PEERONQ_UPDATE_HOST=updates.peeronq.com \
  PEERONQ_PRESENCE_HOST=presence.peeronq.com \
  PEERONQ_SIGNAL_HOST=signal.peeronq.com \
  PEERONQ_TURN_PUBLIC_HOST=turn.peeronq.com \
  PEERONQ_TURN_REALM=turn.peeronq.com \
  PEERONQ_RELEASE_ARTIFACT_HOST=updates.peeronq.com
do
  [ "$(grep -Fxc "$expected" "$legacy_host_environment")" = "1" ] || {
    printf 'Legacy public-host migration omitted: %s\n' "$expected" >&2
    exit 1
  }
done

custom_host_environment="$temporary/custom-hosts.env"
printf '%s\n' \
  'PEERONQ_WEB_HOST=peeronq.com' \
  'PEERONQ_PORTAL_HOST=accounts.peeronq.com' \
  > "$custom_host_environment"
PATH="$temporary/stubs:$PATH" sh "$host_migration_unit" "$custom_host_environment"
[ "$(grep -Fxc 'PEERONQ_PORTAL_HOST=accounts.peeronq.com' "$custom_host_environment")" = "1" ]
[ "$(grep -Fc 'PEERONQ_PORTAL_HOST=' "$custom_host_environment")" = "1" ]

turn_migration_unit="$temporary/turn-external-ip-migration.sh"
awk '
  /^ensure_environment_schema\(\)/ { exit }
  { print }
' "$installer" > "$turn_migration_unit"
cat >> "$turn_migration_unit" <<'EOF'
ENV_FILE=$1
ensure_turn_external_ip_configuration
EOF
chmod 755 "$turn_migration_unit"

legacy_turn_environment="$temporary/legacy-turn.env"
printf '%s\n' \
  'PEERONQ_TURN_EXTERNAL_IP=31.171.38.28/10.1.10.1' \
  > "$legacy_turn_environment"
PATH="$temporary/stubs:$PATH" sh "$turn_migration_unit" "$legacy_turn_environment"
[ "$(grep -Fxc 'PEERONQ_TURN_EXTERNAL_IP=31.171.38.28' "$legacy_turn_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_ADMIN_ALLOWED_CIDR=10.1.10.0/24' "$legacy_turn_environment")" = "1" ]

current_turn_environment="$temporary/current-turn.env"
printf '%s\n' \
  'PEERONQ_TURN_EXTERNAL_IP=31.171.38.28' \
  'PEERONQ_ADMIN_ALLOWED_CIDR=10.1.10.0/24' \
  > "$current_turn_environment"
PATH="$temporary/stubs:$PATH" sh "$turn_migration_unit" "$current_turn_environment"
[ "$(grep -Fxc 'PEERONQ_TURN_EXTERNAL_IP=31.171.38.28' "$current_turn_environment")" = "1" ]
[ "$(grep -Fxc 'PEERONQ_ADMIN_ALLOWED_CIDR=10.1.10.0/24' "$current_turn_environment")" = "1" ]

missing_admin_environment="$temporary/missing-turn-admin.env"
printf 'PEERONQ_TURN_EXTERNAL_IP=31.171.38.28\n' > "$missing_admin_environment"
if PATH="$temporary/stubs:$PATH" sh "$turn_migration_unit" "$missing_admin_environment" \
  > "$temporary/missing-turn-admin.out" 2>&1; then
  printf 'Public-only TURN migration unexpectedly accepted a missing Admin CIDR.\n' >&2
  exit 1
fi
grep -Fq 'PEERONQ_ADMIN_ALLOWED_CIDR is required' "$temporary/missing-turn-admin.out"

retry_unit="$temporary/compose-retry.sh"
cat > "$retry_unit" <<'EOF'
#!/bin/sh
set -eu
temporary=$1
mkdir -p "$temporary"
LOG_ROOT=$temporary
install_log="$temporary/install.log"
attempt_file="$temporary/attempts"
delay_file="$temporary/delays"
: > "$install_log"
: > "$attempt_file"
: > "$delay_file"
compose() {
  attempts=$(wc -l < "$attempt_file" | tr -d '[:space:]')
  attempts=$((attempts + 1))
  printf '%s\n' "$attempts" >> "$attempt_file"
  [ "$attempts" -ge "${COMPOSE_SUCCEED_AT:-3}" ]
}
sleep() {
  printf '%s\n' "$1" >> "$delay_file"
}
EOF
awk '
  /^run_compose_step\(\)/ { copy = 1 }
  copy && /^capture_failure_diagnostics\(\)/ { exit }
  copy { print }
' "$installer" >> "$retry_unit"
cat >> "$retry_unit" <<'EOF'
run_compose_step_with_retry "start prerequisites" /release 3 up -d
[ "$(wc -l < "$attempt_file" | tr -d '[:space:]')" = "3" ]
[ "$(tr '\n' ' ' < "$delay_file")" = "2 4 " ]

: > "$attempt_file"
: > "$delay_file"
COMPOSE_SUCCEED_AT=99
export COMPOSE_SUCCEED_AT
if run_compose_step_with_retry "persistent failure" /release 2 up -d; then
  printf 'Persistent Compose failure unexpectedly passed retry gate.\n' >&2
  exit 1
fi
[ "$(wc -l < "$attempt_file" | tr -d '[:space:]')" = "2" ]
[ "$(tr '\n' ' ' < "$delay_file")" = "2 " ]
EOF
chmod 755 "$retry_unit"
sh "$retry_unit" "$temporary/retry-runtime" >/dev/null

# Execute the installer's actual nested proxy shell command, not a copied header parser.
# In particular, CR stripping must survive both the host shell and container sh -ec.
publication_unit="$temporary/publication.sh"
awk '
  /^verify_embedded_windows_client\(\)/ { copy = 1 }
  copy && /^transition_website_overlay\(\)/ { exit }
  copy { print }
' "$installer" > "$publication_unit"
publication_root="$temporary/publication"
mkdir -p "$publication_root/bin" "$publication_root/release/scripts/linux" \
  "$publication_root/release/artifacts/peeronq/public/downloads"
cp "$script_dir/verify-embedded-windows-client.sh" "$publication_root/release/scripts/linux/"
publication_downloads="$publication_root/release/artifacts/peeronq/public/downloads"
printf 'fixture client bytes\n' > "$publication_downloads/PeerOnQ-Windows-x64.msi"
publication_hash=$(sha256sum "$publication_downloads/PeerOnQ-Windows-x64.msi" | awk '{ print $1 }')
printf '%s  PeerOnQ-Windows-x64.msi\n' "$publication_hash" > "$publication_downloads/SHA256SUMS.txt"
printf '0.1.0\n' > "$publication_downloads/embedded-windows-version.txt"
printf 'unsigned-pilot\n' > "$publication_downloads/embedded-windows-release-type.txt"
printf '%s\n' 'This x64 MSI is unsigned and is authorized only for controlled PeerOnQ pilot testing.' \
  > "$publication_downloads/UNSIGNED-PILOT-NOTICE.txt"
cat > "$publication_root/bin/curl" <<'EOF'
#!/bin/sh
case "$*" in
  *--head*) printf 'HTTP/2 200\r\n%s\r\n\r\n' "$TEST_CACHE_HEADER" ;;
  *embedded-windows-version.txt*) printf '0.1.0\n' ;;
  *PeerOnQ-Windows-x64.msi*) printf 'fixture client bytes\n' ;;
  *) printf 'fixture base page\n' ;;
esac
EOF
cat > "$publication_root/bin/nginx" <<'EOF'
#!/bin/sh
printf '%s\n' 'location = /downloads/PeerOnQ-Windows-x64.msi {' \
  'proxy_buffering off;' 'proxy_max_temp_file_size 0;' '}'
EOF
chmod 755 "$publication_root/bin/curl" "$publication_root/bin/nginx"
cat >> "$publication_unit" <<'EOF'
set -eu
publication_root=$1
PATH="$publication_root/bin:$PATH"
export PATH PEERONQ_WEB_HOST=peeronq.invalid
run_compose_step() {
  case "$1" in
    "verify embedded Windows client publication") return 0 ;; # Separate web-origin gate.
    "verify Windows client proxy streaming")
      shift 5 # step, release, exec, -T, proxy; execute the exact sh -ec arguments.
      "$@" ;;
    *) return 1 ;;
  esac
}
for valid_header in 'Cache-Control: no-store, max-age=0' 'cache-control: no-store'; do
  export TEST_CACHE_HEADER="$valid_header"
  verify_embedded_windows_client "$publication_root/release"
done
for invalid_header in 'Cache-Control: public, max-age=3600' 'X-Other: no-store' ''; do
  export TEST_CACHE_HEADER="$invalid_header"
  if verify_embedded_windows_client "$publication_root/release" > "$publication_root/rejected.log" 2>&1; then
    printf 'Installer accepted a missing or cacheable download policy.\n' >&2
    exit 1
  fi
  grep -Fq 'Public Windows client response is cacheable.' "$publication_root/rejected.log"
done
EOF
sh "$publication_unit" "$publication_root"

printf 'Bootstrap/production Compose environment contract tests passed.\n'
