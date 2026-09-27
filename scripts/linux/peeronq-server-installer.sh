#!/bin/sh
set -eu

INSTALLER_VERSION="__VERSION__"
PAYLOAD_SHA256="__PAYLOAD_SHA256__"
INSTALL_ROOT="/opt/peeronq"
ENV_FILE="/etc/peeronq/peeronq.env"
LOCK_FILE="/run/lock/peeronq-server-install.lock"
LOG_ROOT="/var/log/peeronq/installer"
PLATFORM_UPGRADE_ROOT="/var/lib/peeronq/platform-upgrade"
PLATFORM_UPGRADE_CONFIG="/etc/peeronq/platform-upgrade.conf"
PLATFORM_UPGRADE_KEYRING="/etc/peeronq/platform-upgrade-trustedkeys.gpg"
PLATFORM_UPGRADE_AGENT="/usr/local/libexec/peeronq-platform-upgrade-agent"
PLATFORM_UPGRADE_SERVICE="/etc/systemd/system/peeronq-platform-upgrade-agent.service"
PLATFORM_UPGRADE_PATH_UNIT="/etc/systemd/system/peeronq-platform-upgrade-agent.path"
TLS_RENEWAL_COMMAND="/usr/local/sbin/peeronq-renew-tls"
TLS_RENEWAL_SERVICE="/etc/systemd/system/peeronq-tls-renew.service"
TLS_RENEWAL_TIMER="/etc/systemd/system/peeronq-tls-renew.timer"
TLS_DNS_HOOK="/usr/local/libexec/peeronq-spaceship-dns-hook.py"
SPACESHIP_CREDENTIALS_DIR=""
CERTBOT_IMAGE="certbot/certbot:v5.7.0@sha256:34ee91d2f43008eb78a007d22f23ed4b2eaa9a454cb27ca2c042b49527a695b4"
PAYLOAD_MARKER="__PEERONQ_PAYLOAD_BELOW__"

usage() {
  cat <<EOF
Usage: peeronq-server-${INSTALLER_VERSION}.run [--env-file PATH] [--dry-run]
       [--disable-customer-mail | --customer-smtp-host HOST [--customer-smtp-port PORT]]
       [--tls-cert PATH --tls-key PATH]
       peeronq-server-${INSTALLER_VERSION}.run --bootstrap --acme-email EMAIL [options]
       peeronq-server-${INSTALLER_VERSION}.run --bootstrap --tls-cert PATH --tls-key PATH [options]
       peeronq-server-${INSTALLER_VERSION}.run --status [--env-file PATH]
       peeronq-server-${INSTALLER_VERSION}.run --rollback [--env-file PATH]

Installs or upgrades the PeerOnQ Linux server stack. This is not a Linux
desktop client. Verify the detached release signature before running as root.

Bootstrap options:
  --base-domain DOMAIN  (default: peeronq.com)
  --public-ip IPV4      (default: 31.171.38.28)
  --local-ip IPV4       (default: detected from the server route)
  --admin-allowed-cidr CIDR (default: the server LAN IP's /24)
  --region REGION       (default: az-1)
  --admin-email EMAIL   (default: admin@peeronq.com)
  --customer-registration-mode Closed|InvitationOnly|Open (preserves existing mode on upgrade)
  --enable-customer-mfa | --disable-customer-mfa (default: disabled; Admin MFA unchanged)
  --customer-smtp-host HOST (optional; enables customer email delivery)
  --customer-smtp-port PORT (default: 587 when SMTP is enabled)
  --platform-upgrade-keyring PATH
  --platform-upgrade-signer-fingerprint HEX
                        Enable Admin-staged whole-platform upgrades with an
                        operator-verified OpenPGP keyring and exact signer fingerprint.

Optional customer email options:
  --disable-customer-mail
                        Explicitly disable customer email and clear stale SMTP values.
  --customer-smtp-host HOST
  --customer-smtp-port PORT
                        Enable SMTP during bootstrap or fill missing SMTP settings
                        during an upgrade. Existing non-empty values are not replaced.
                        Without these options, existing customer mail policy is preserved.

TLS import options:
  --tls-cert PATH --tls-key PATH
                        Import a root-owned trusted SAN/wildcard certificate and
                        matching private key during bootstrap or an in-place upgrade.
                         A dry run validates the pair without changing the active files.
  --spaceship-dns-credentials DIR
                         Root-only directory containing api-key and api-secret.
                         Enables unattended DNS-01 for peeronq.com.
EOF
}

fail() {
  printf 'PeerOnQ installer: %s\n' "$1" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command is missing: $1"
}

validate_spaceship_credentials_dir() {
  credentials_dir=$1
  [ -d "$credentials_dir" ] && [ ! -L "$credentials_dir" ] || return 1
  [ "$(readlink -f "$credentials_dir")" = "$credentials_dir" ] || return 1
  [ "$(stat -c '%u:%a' "$credentials_dir")" = "0:700" ] || return 1
  for credential_name in api-key api-secret; do
    credential_file="$credentials_dir/$credential_name"
    [ -f "$credential_file" ] && [ ! -L "$credential_file" ] || return 1
    case "$(stat -c '%u:%h:%a' "$credential_file")" in 0:1:400|0:1:600) ;; *) return 1 ;; esac
    value=$(tr -d '\r\n' < "$credential_file")
    [ -n "$value" ] && [ "$(printf '%s' "$value" | wc -c)" -ge 16 ] && [ "$(printf '%s' "$value" | wc -c)" -le 512 ] || return 1
    case "$value" in *[!A-Za-z0-9_-]*) return 1 ;; esac
    [ "$(wc -l < "$credential_file")" -le 1 ] || return 1
  done
}

install_spaceship_credentials_dir() {
  source_dir=$1
  destination_dir=$2
  validate_spaceship_credentials_dir "$source_dir" || return 1
  mkdir -p "$destination_dir" || return 1
  chown 0:0 "$destination_dir" && chmod 700 "$destination_dir" || return 1
  for credential_name in api-key api-secret; do
    cp "$source_dir/$credential_name" "$destination_dir/$credential_name" || return 1
    chown 0:0 "$destination_dir/$credential_name" && chmod 400 "$destination_dir/$credential_name" || return 1
  done
}

validate_installer_version() {
  printf '%s\n' "$INSTALLER_VERSION" | awk '
    NR == 1 && $0 ~ /^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$/ {
      valid = 1
      next
    }
    { valid = 0 }
    END { exit !(NR == 1 && valid) }
  ' || fail "the embedded release version is invalid"
}

canonical_existing_file() {
  [ -f "$1" ] || fail "environment file does not exist: $1"
  [ ! -L "$1" ] || fail "environment file must not be a symbolic link"
  readlink -f "$1"
}

check_environment_file() {
  ENV_FILE=$(canonical_existing_file "$ENV_FILE")
  owner=$(stat -c '%u' "$ENV_FILE")
  mode=$(stat -c '%a' "$ENV_FILE")
  [ "$owner" = "0" ] || fail "environment file must be owned by root"
  case "$mode" in
    600|400) ;;
    *) fail "environment file permissions must be 0600 or 0400" ;;
  esac
}

read_environment_value() {
  awk -v key="$1" '
    index($0, key "=") == 1 { sub("^[^=]*=", ""); print; found = 1; exit }
    END { if (!found) exit 1 }
  ' "$ENV_FILE"
}

environment_has_key() {
  grep -E "^$1=" "$ENV_FILE" >/dev/null 2>&1
}

append_environment_values() {
  updated=$(mktemp "${ENV_FILE}.update.XXXXXX")
  cp "$ENV_FILE" "$updated"
  changed=false
  while [ "$#" -gt 0 ]; do
    [ "$#" -ge 2 ] || fail "internal environment migration argument mismatch"
    key=$1
    value=$2
    if ! environment_has_key "$key"; then
      printf '%s=%s\n' "$key" "$value" >> "$updated"
      changed=true
    fi
    shift 2
  done
  if [ "$changed" = "false" ]; then
    rm -f "$updated"
    return 0
  fi
  chown 0:0 "$updated"
  chmod 600 "$updated"
  mv -f "$updated" "$ENV_FILE"
}

set_environment_value() {
  key=$1
  value=$2
  updated=$(mktemp "${ENV_FILE}.update.XXXXXX")
  awk -v key="$key" -v value="$value" '
    index($0, key "=") == 1 {
      if (replaced) exit 2
      print key "=" value
      replaced = 1
      next
    }
    { print }
    END { if (!replaced) print key "=" value }
  ' "$ENV_FILE" > "$updated" || {
    rm -f "$updated"
    fail "environment file contains a duplicate key: $key"
  }
  chown 0:0 "$updated"
  chmod 600 "$updated"
  mv -f "$updated" "$ENV_FILE"
}

validate_customer_smtp_host() {
  value=$1
  case "$value" in
    *[!A-Za-z0-9._:-]*|'') fail "customer SMTP host is invalid" ;;
  esac
  [ "${#value}" -le 253 ] || fail "customer SMTP host must not exceed 253 characters"
  getent ahosts "$value" >/dev/null 2>&1 \
    || fail "customer SMTP host does not resolve from this server: $value"
}

validate_customer_smtp_port() {
  value=$1
  case "$value" in *[!0-9]*|'') fail "customer SMTP port is invalid" ;; esac
  [ "${#value}" -le 5 ] && [ "$value" -ge 1 ] && [ "$value" -le 65535 ] \
    || fail "customer SMTP port must be between 1 and 65535"
}

validate_environment_host() {
  value=$1
  label=$2
  case "$value" in
    *[!A-Za-z0-9.-]*|''|.*|*..*|*.-*|*-.*|*.) fail "$label is not a valid bounded DNS host" ;;
  esac
  [ "${#value}" -le 253 ] || fail "$label must not exceed 253 characters"
  case "$value" in *.*) ;; *) fail "$label must be a fully qualified DNS host" ;; esac
}

validate_environment_ipv4() {
  value=$1
  label=$2
  printf '%s\n' "$value" | awk -F. '
    NF != 4 { exit 1 }
    {
      for (i = 1; i <= 4; i++) {
        if ($i !~ /^[0-9]+$/ || $i < 0 || $i > 255) exit 1
      }
    }
  ' || fail "$label must be a valid IPv4 address"
}

ensure_environment_host() {
  key=$1
  fallback=$2
  current=$(read_environment_value "$key" 2>/dev/null || true)
  if [ -z "$current" ]; then
    set_environment_value "$key" "$fallback"
    current=$fallback
  fi
  validate_environment_host "$current" "$key"
}

ensure_public_host_configuration() {
  base_host=$(read_environment_value PEERONQ_WEB_HOST 2>/dev/null || true)
  [ -n "$base_host" ] \
    || fail "PEERONQ_WEB_HOST is required before legacy public hosts can be migrated"
  validate_environment_host "$base_host" PEERONQ_WEB_HOST

  ensure_environment_host PEERONQ_WEB_WWW_HOST "www.$base_host"
  ensure_environment_host PEERONQ_API_HOST "api.$base_host"
  ensure_environment_host PEERONQ_PORTAL_HOST "portal.$base_host"
  ensure_environment_host PEERONQ_ADMIN_HOST "admin.$base_host"
  ensure_environment_host PEERONQ_GRAFANA_HOST "grafana.$base_host"
  ensure_environment_host PEERONQ_PROMETHEUS_HOST "prometheus.$base_host"
  ensure_environment_host PEERONQ_DOWNLOAD_HOST "download.$base_host"
  ensure_environment_host PEERONQ_UPDATE_HOST "updates.$base_host"
  ensure_environment_host PEERONQ_PRESENCE_HOST "presence.$base_host"
  ensure_environment_host PEERONQ_SIGNAL_HOST "signal.$base_host"
  ensure_environment_host PEERONQ_TURN_PUBLIC_HOST "turn.$base_host"
  ensure_environment_host PEERONQ_TURN_REALM "turn.$base_host"
  ensure_environment_host PEERONQ_RELEASE_ARTIFACT_HOST "updates.$base_host"
}

ensure_turn_external_ip_configuration() {
  external_ip=$(read_environment_value PEERONQ_TURN_EXTERNAL_IP) \
    || fail "PEERONQ_TURN_EXTERNAL_IP is missing"
  [ -n "$external_ip" ] || fail "PEERONQ_TURN_EXTERNAL_IP must not be empty"

  case "$external_ip" in
    */*)
      public_ip=${external_ip%%/*}
      local_ip=${external_ip#*/}
      case "$local_ip" in */*) fail "PEERONQ_TURN_EXTERNAL_IP contains more than one mapping separator" ;; esac
      validate_environment_ipv4 "$public_ip" "PEERONQ_TURN_EXTERNAL_IP public address"
      validate_environment_ipv4 "$local_ip" "PEERONQ_TURN_EXTERNAL_IP legacy private address"

      if ! environment_has_key PEERONQ_ADMIN_ALLOWED_CIDR; then
        admin_cidr=$(printf '%s\n' "$local_ip" | awk -F. \
          '{ print $1 "." $2 "." $3 ".0/24" }')
        append_environment_values PEERONQ_ADMIN_ALLOWED_CIDR "$admin_cidr"
      fi

      # Coturn runs on a Docker bridge, so the host LAN address is not one of its relay
      # interfaces and cannot be used as the private side of an external-ip mapping. The
      # single-address form applies the public address to its sole IPv4 relay interface.
      set_environment_value PEERONQ_TURN_EXTERNAL_IP "$public_ip"
      ;;
    *)
      validate_environment_ipv4 "$external_ip" "PEERONQ_TURN_EXTERNAL_IP"
      environment_has_key PEERONQ_ADMIN_ALLOWED_CIDR \
        || fail "PEERONQ_ADMIN_ALLOWED_CIDR is required when PEERONQ_TURN_EXTERNAL_IP has no legacy private address"
      ;;
  esac
}

ensure_customer_policy_configuration() {
  customer_registration_mode=$(read_environment_value PEERONQ_CUSTOMER_REGISTRATION_MODE 2>/dev/null || true)
  if environment_has_key PEERONQ_CUSTOMER_REGISTRATION_MODE && [ -z "$customer_registration_mode" ]; then fail "PEERONQ_CUSTOMER_REGISTRATION_MODE must not be empty"; fi
  case "$customer_registration_mode" in Closed|InvitationOnly|Open|'') ;; *) fail "PEERONQ_CUSTOMER_REGISTRATION_MODE must be Closed, InvitationOnly, or Open" ;; esac
  if [ -n "${CUSTOMER_REGISTRATION_MODE:-}" ]; then customer_registration_mode=$CUSTOMER_REGISTRATION_MODE; fi
  customer_registration_mode=${customer_registration_mode:-Closed}
  case "$customer_registration_mode" in Closed|InvitationOnly|Open) ;; *) fail "--customer-registration-mode must be Closed, InvitationOnly, or Open" ;; esac
  customer_mfa_enabled=$(read_environment_value PEERONQ_CUSTOMER_MFA_ENABLED 2>/dev/null || true)
  case "$customer_mfa_enabled" in true|false|'') ;; *) fail "PEERONQ_CUSTOMER_MFA_ENABLED must be true or false" ;; esac
  if [ -n "${CUSTOMER_MFA_ENABLED:-}" ]; then customer_mfa_enabled=$CUSTOMER_MFA_ENABLED; fi
  case "${customer_mfa_enabled:-false}" in true|false) ;; *) fail "customer MFA value must be true or false" ;; esac
}

commit_customer_policy_configuration() {
  set_environment_value PEERONQ_CUSTOMER_REGISTRATION_MODE "$customer_registration_mode"
  set_environment_value PEERONQ_CUSTOMER_MFA_ENABLED "${customer_mfa_enabled:-false}"
}

ensure_customer_mail_configuration() {
  ensure_customer_policy_configuration
  if [ "${CUSTOMER_MAIL_DISABLE_REQUESTED:-false}" = "true" ]; then
    [ "$customer_registration_mode" = Closed ] || fail "Disabling customer mail requires explicit Closed registration; existing registration mode was preserved"
    set_environment_value PEERONQ_CUSTOMER_MAIL_PROVIDER Disabled
    set_environment_value PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION false
    set_environment_value PEERONQ_CUSTOMER_SMTP_HOST ""
    set_environment_value PEERONQ_CUSTOMER_SMTP_PORT 587
    set_environment_value PEERONQ_CUSTOMER_SMTP_USERNAME ""
    set_environment_value PEERONQ_CUSTOMER_SMTP_PASSWORD ""
    commit_customer_policy_configuration
    return 0
  fi

  customer_smtp_host=$(read_environment_value PEERONQ_CUSTOMER_SMTP_HOST 2>/dev/null || true)
  if [ "${CUSTOMER_SMTP_HOST_PROVIDED:-false}" = "true" ]; then
    validate_customer_smtp_host "$CUSTOMER_SMTP_HOST"
    if [ -n "$customer_smtp_host" ] && [ "$customer_smtp_host" != "$CUSTOMER_SMTP_HOST" ]; then
      fail "PEERONQ_CUSTOMER_SMTP_HOST is already configured; edit the protected environment explicitly to replace it"
    fi
    if [ "$customer_smtp_host" != "$CUSTOMER_SMTP_HOST" ]; then
      set_environment_value PEERONQ_CUSTOMER_SMTP_HOST "$CUSTOMER_SMTP_HOST"
      customer_smtp_host=$CUSTOMER_SMTP_HOST
    fi
  fi

  customer_mail_provider=$(read_environment_value PEERONQ_CUSTOMER_MAIL_PROVIDER 2>/dev/null || true)
  if [ "${CUSTOMER_SMTP_HOST_PROVIDED:-false}" = "true" ]; then
    customer_mail_provider=Smtp
    set_environment_value PEERONQ_CUSTOMER_MAIL_PROVIDER "$customer_mail_provider"
    set_environment_value PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION true
  elif [ -z "$customer_mail_provider" ]; then
    if [ -n "$customer_smtp_host" ]; then customer_mail_provider=Smtp; else customer_mail_provider=Disabled; fi
    set_environment_value PEERONQ_CUSTOMER_MAIL_PROVIDER "$customer_mail_provider"
  fi

  case "$customer_mail_provider" in
    [Dd][Ii][Ss][Aa][Bb][Ll][Ee][Dd])
      [ "${CUSTOMER_SMTP_PORT_PROVIDED:-false}" = "false" ] \
        || fail "--customer-smtp-port requires --customer-smtp-host when customer mail is disabled"
      set_environment_value PEERONQ_CUSTOMER_MAIL_PROVIDER Disabled
      [ "$customer_registration_mode" = Closed ] || fail "Customer registration requires SMTP; configure SMTP or explicitly select Closed"
      set_environment_value PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION false
      append_environment_values \
        PEERONQ_CUSTOMER_SMTP_HOST "" \
        PEERONQ_CUSTOMER_SMTP_PORT 587 \
        PEERONQ_CUSTOMER_SMTP_USERNAME "" \
        PEERONQ_CUSTOMER_SMTP_PASSWORD ""
      commit_customer_policy_configuration
      return 0
      ;;
    [Ss][Mm][Tt][Pp])
      set_environment_value PEERONQ_CUSTOMER_MAIL_PROVIDER Smtp
      ;;
    *)
      fail "PEERONQ_CUSTOMER_MAIL_PROVIDER must be Disabled or Smtp"
      ;;
  esac

  [ -n "$customer_smtp_host" ] \
    || fail "PEERONQ_CUSTOMER_SMTP_HOST is required when customer mail provider is Smtp"
  validate_customer_smtp_host "$customer_smtp_host"
  customer_smtp_port=$(read_environment_value PEERONQ_CUSTOMER_SMTP_PORT 2>/dev/null || true)
  if [ "${CUSTOMER_SMTP_PORT_PROVIDED:-false}" = "true" ]; then
    validate_customer_smtp_port "$CUSTOMER_SMTP_PORT"
    if [ -n "$customer_smtp_port" ] && [ "$customer_smtp_port" != "$CUSTOMER_SMTP_PORT" ]; then
      fail "PEERONQ_CUSTOMER_SMTP_PORT is already configured; edit the protected environment explicitly to replace it"
    fi
    if [ "$customer_smtp_port" != "$CUSTOMER_SMTP_PORT" ]; then
      set_environment_value PEERONQ_CUSTOMER_SMTP_PORT "$CUSTOMER_SMTP_PORT"
      customer_smtp_port=$CUSTOMER_SMTP_PORT
    fi
  fi
  if [ -z "$customer_smtp_port" ]; then
    customer_smtp_port=587
    set_environment_value PEERONQ_CUSTOMER_SMTP_PORT "$customer_smtp_port"
  fi
  validate_customer_smtp_port "$customer_smtp_port"
  append_environment_values \
    PEERONQ_CUSTOMER_REGISTRATION_MODE Closed \
    PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION true \
    PEERONQ_CUSTOMER_SMTP_USERNAME "" \
    PEERONQ_CUSTOMER_SMTP_PASSWORD ""
  customer_verify=$(read_environment_value PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION)
  case "$customer_verify" in true|false) ;; *) fail "PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION must be true or false" ;; esac
  [ "$customer_registration_mode" = Closed ] || [ "$customer_verify" = true ] || fail "Public registration requires email verification"
  commit_customer_policy_configuration
}

ensure_environment_schema() {
  ensure_public_host_configuration
  ensure_turn_external_ip_configuration

  signaling_redis=$(read_environment_value PEERONQ_REDIS_SIGNALING_PASSWORD 2>/dev/null || true)
  if [ -z "$signaling_redis" ]; then
    if environment_has_key PEERONQ_REDIS_SIGNALING_PASSWORD; then
      fail "PEERONQ_REDIS_SIGNALING_PASSWORD must not be empty"
    fi
    signaling_redis=$(openssl rand -hex 32)
    append_environment_values PEERONQ_REDIS_SIGNALING_PASSWORD "$signaling_redis"
  fi

  signaling_instance=$(read_environment_value PEERONQ_SIGNALING_INSTANCE_ID 2>/dev/null || true)
  if [ -z "$signaling_instance" ]; then
    if environment_has_key PEERONQ_SIGNALING_INSTANCE_ID; then
      fail "PEERONQ_SIGNALING_INSTANCE_ID must not be empty"
    fi
    signaling_instance=$(read_environment_value PEERONQ_SIGNAL_SERVER_ID 2>/dev/null || true)
    [ -n "$signaling_instance" ] || fail "PEERONQ_SIGNAL_SERVER_ID is required to migrate the Signaling instance ID"
    append_environment_values PEERONQ_SIGNALING_INSTANCE_ID "$signaling_instance"
  fi

  ensure_customer_mail_configuration

  customer_token=$(read_environment_value PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY 2>/dev/null || true)
  if [ -z "$customer_token" ]; then
    if environment_has_key PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY; then
      fail "PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY must not be empty"
    fi
    customer_token=$(openssl rand -base64 48 | tr -d '\r\n')
    append_environment_values PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY "$customer_token"
  fi
  [ "${#customer_token}" -ge 48 ] \
    || fail "PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY must contain at least 48 characters"
  website_key=$(read_environment_value PEERONQ_WEBSITE_SIGNING_KEY_ID 2>/dev/null || true)
  website_public=$(read_environment_value PEERONQ_WEBSITE_SIGNING_PUBLIC_KEY_SPKI_BASE64 2>/dev/null || true)
  if [ -z "$website_key" ] && [ -z "$website_public" ]; then
    website_signing_dir="$(dirname "$ENV_FILE")/pilot-website-signing"
    ensure_root_directory "$website_signing_dir" 700
    private_key="$website_signing_dir/private-key.pem"
    public_key="$website_signing_dir/public-key.pem"
    if [ ! -e "$private_key" ] && [ ! -e "$public_key" ]; then
      openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 \
        -out "$private_key" >/dev/null 2>&1
      openssl pkey -in "$private_key" -pubout -out "$public_key" >/dev/null 2>&1
      chmod 400 "$private_key"
      chmod 644 "$public_key"
    else
      [ -f "$private_key" ] && [ ! -L "$private_key" ] \
        && [ -f "$public_key" ] && [ ! -L "$public_key" ] \
        || fail "interrupted website signing migration contains unsafe key paths"
      [ "$(stat -c '%u' "$private_key")" = "0" ] && [ "$(stat -c '%u' "$public_key")" = "0" ] \
        || fail "interrupted website signing migration key files must be owned by root"
      private_public_hash=$(openssl pkey -in "$private_key" -pubout -outform DER 2>/dev/null | sha256sum | awk '{ print $1 }')
      public_hash=$(openssl pkey -pubin -in "$public_key" -outform DER 2>/dev/null | sha256sum | awk '{ print $1 }')
      [ -n "$private_public_hash" ] && [ "$private_public_hash" = "$public_hash" ] \
        || fail "interrupted website signing migration key pair is invalid"
      chmod 400 "$private_key"
      chmod 644 "$public_key"
    fi
    website_public=$(openssl pkey -pubin -in "$public_key" -outform DER 2>/dev/null | base64 | tr -d '\r\n')
    append_environment_values \
      PEERONQ_WEBSITE_SIGNING_KEY_ID peeronq-website-pilot-1 \
      PEERONQ_WEBSITE_SIGNING_PUBLIC_KEY_SPKI_BASE64 "$website_public" \
      PEERONQ_WEBSITE_PUBLICATION_ENABLED true \
      PEERONQ_WEBSITE_MAXIMUM_ARCHIVE_BYTES 100663296
    if [ "${SCHEMA_DRY_RUN:-false}" != "true" ]; then
      printf 'A separate website signing key was created at %s. Copy it offline and remove the server private-key copy before production.\n' "$private_key"
    fi
  elif [ -z "$website_key" ] || [ -z "$website_public" ]; then
    fail "website signing key ID and public key must both be configured"
  fi
  append_environment_values \
    PEERONQ_WEBSITE_PUBLICATION_ENABLED true \
    PEERONQ_WEBSITE_MAXIMUM_ARCHIVE_BYTES 100663296 \
    PEERONQ_PLATFORM_UPGRADE_ENABLED false \
    PEERONQ_PLATFORM_UPGRADE_MAXIMUM_BUNDLE_BYTES 268435456
}

prepare_managed_tls_permissions() {
  config_root=$(dirname "$ENV_FILE")
  expected_tls_dir="$config_root/tls"
  proxy_tls_dir=$(read_environment_value PEERONQ_TLS_CERT_DIR) \
    || fail "PEERONQ_TLS_CERT_DIR is missing"
  turn_tls_dir=$(read_environment_value PEERONQ_TURN_CERT_DIR) \
    || fail "PEERONQ_TURN_CERT_DIR is missing"
  tls_key=$(read_environment_value PEERONQ_TLS_PRIVATE_KEY_FILE) \
    || fail "PEERONQ_TLS_PRIVATE_KEY_FILE is missing"
  [ "$proxy_tls_dir" = "$expected_tls_dir" ] \
    && [ "$turn_tls_dir" = "$expected_tls_dir" ] \
    && [ "$tls_key" = "$expected_tls_dir/privkey.pem" ] \
    || fail "TLS paths must use the installer-managed directory: $expected_tls_dir"
  [ -d "$expected_tls_dir" ] && [ ! -L "$expected_tls_dir" ] \
    || fail "managed TLS directory is missing or unsafe"
  [ "$(stat -c '%u' "$expected_tls_dir")" = "0" ] \
    || fail "managed TLS directory must be owned by root"
  for tls_file in "$expected_tls_dir/fullchain.pem" "$expected_tls_dir/privkey.pem"; do
    [ -f "$tls_file" ] && [ ! -L "$tls_file" ] \
      || fail "managed TLS file is missing or unsafe: $tls_file"
    [ "$(stat -c '%u' "$tls_file")" = "0" ] \
      || fail "managed TLS files must be owned by root"
  done
  chown 0:65534 "$expected_tls_dir" \
    "$expected_tls_dir/fullchain.pem" "$expected_tls_dir/privkey.pem"
  chmod 750 "$expected_tls_dir"
  chmod 640 "$expected_tls_dir/fullchain.pem" "$expected_tls_dir/privkey.pem"
}

required_tls_hosts() {
  for key in \
    PEERONQ_WEB_HOST PEERONQ_WEB_WWW_HOST PEERONQ_API_HOST PEERONQ_PORTAL_HOST \
    PEERONQ_ADMIN_HOST PEERONQ_GRAFANA_HOST PEERONQ_PROMETHEUS_HOST \
    PEERONQ_DOWNLOAD_HOST PEERONQ_UPDATE_HOST PEERONQ_PRESENCE_HOST \
    PEERONQ_SIGNAL_HOST PEERONQ_TURN_PUBLIC_HOST
  do
    read_environment_value "$key" || fail "$key is missing"
  done
}

certificate_covers_required_hosts() {
  cert=$1
  for name in $(required_tls_hosts); do
    # Trust this exact leaf only for the local hostname check so validation does
    # not depend on the host CA store while preserving reliable exit semantics.
    openssl verify -no-CAfile -no-CApath -trusted "$cert" -partial_chain \
      -verify_hostname "$name" "$cert" >/dev/null 2>&1 || return 1
  done
}

validate_tls_pair_material() (
  set -eu
  cert=$1
  key=$2
  openssl x509 -in "$cert" -noout -checkend 86400 >/dev/null 2>&1 || return 1
  certificate_covers_required_hosts "$cert" || return 1
  public_key_dir=$(mktemp -d)
  trap 'rm -rf "$public_key_dir"' EXIT HUP INT TERM
  openssl x509 -in "$cert" -pubkey -noout > "$public_key_dir/cert.pem" 2>/dev/null \
    || return 1
  openssl pkey -pubin -in "$public_key_dir/cert.pem" -outform DER \
    > "$public_key_dir/cert.der" 2>/dev/null || return 1
  openssl pkey -in "$key" -pubout -outform DER \
    > "$public_key_dir/key.der" 2>/dev/null </dev/null || return 1
  cmp -s "$public_key_dir/cert.der" "$public_key_dir/key.der"
)

validate_tls_import_source() {
  case "$TLS_CERT_SOURCE" in /*) ;; *) return 1 ;; esac
  case "$TLS_KEY_SOURCE" in /*) ;; *) return 1 ;; esac
  [ "$TLS_CERT_SOURCE" != "$TLS_KEY_SOURCE" ] || return 1
  [ -f "$TLS_CERT_SOURCE" ] && [ ! -L "$TLS_CERT_SOURCE" ] \
    && [ -f "$TLS_KEY_SOURCE" ] && [ ! -L "$TLS_KEY_SOURCE" ] || return 1
  [ "$(readlink -f "$TLS_CERT_SOURCE")" = "$TLS_CERT_SOURCE" ] \
    && [ "$(readlink -f "$TLS_KEY_SOURCE")" = "$TLS_KEY_SOURCE" ] || return 1
  [ "$(stat -c '%u:%h' "$TLS_CERT_SOURCE")" = "0:1" ] \
    && [ "$(stat -c '%u:%h' "$TLS_KEY_SOURCE")" = "0:1" ] || return 1
  case "$(stat -c '%a' "$TLS_CERT_SOURCE")" in 400|440|444|600|640|644) ;; *) return 1 ;; esac
  case "$(stat -c '%a' "$TLS_KEY_SOURCE")" in 400|600) ;; *) return 1 ;; esac
  cert_size=$(stat -c '%s' "$TLS_CERT_SOURCE")
  key_size=$(stat -c '%s' "$TLS_KEY_SOURCE")
  [ "$cert_size" -gt 0 ] && [ "$cert_size" -le 1048576 ] \
    && [ "$key_size" -gt 0 ] && [ "$key_size" -le 131072 ] || return 1

  tls_dir=$(read_environment_value PEERONQ_TLS_CERT_DIR) || return 1
  [ -d "$tls_dir" ] && [ ! -L "$tls_dir" ] || return 1
  managed_tls_dir=$(readlink -f "$tls_dir") || return 1
  [ "$TLS_CERT_SOURCE" != "$managed_tls_dir/fullchain.pem" ] \
    && [ "$TLS_KEY_SOURCE" != "$managed_tls_dir/privkey.pem" ] || return 1
  validate_tls_pair_material "$TLS_CERT_SOURCE" "$TLS_KEY_SOURCE"
}

run_standalone_acme_with_proxy_handoff() (
  set -eu
  acme_root=$1
  shift
  active_release=$(current_release || true)
  proxy_was_running=false

  restore_proxy() {
    [ "$proxy_was_running" = "true" ] || return 0
    compose "$active_release" start proxy || return 1
    proxy_was_running=false
  }

  trap 'restore_proxy || true' 0
  trap 'exit 129' HUP
  trap 'exit 130' INT
  trap 'exit 143' TERM

  if [ -n "$active_release" ] \
    && compose "$active_release" ps --status running --services 2>/dev/null \
      | grep -Fx proxy >/dev/null; then
    # Certbot standalone must own host TCP 80. Stop only the edge proxy and always
    # restore that exact existing container before returning to the upgrade flow.
    proxy_was_running=true
    compose "$active_release" stop -t 30 proxy || return 1
  fi

  certbot_status=0
  docker run --rm -p 80:80 -v "$acme_root:/etc/letsencrypt" "$CERTBOT_IMAGE" "$@" \
    || certbot_status=$?
  restore_proxy || return 1
  trap - 0 HUP INT TERM
  [ "$certbot_status" -eq 0 ]
)

run_spaceship_acme() {
  acme_root=$1
  domain=$2
  credentials_dir=$3
  hook_source=$4
  [ -f "$hook_source" ] && [ ! -L "$hook_source" ] || return 1
  docker run --rm --read-only --cap-drop=ALL --security-opt no-new-privileges \
    --tmpfs /tmp:rw,nosuid,nodev,noexec,size=64m \
    --tmpfs /var/lib/letsencrypt:rw,nosuid,nodev,noexec,size=64m \
    --tmpfs /var/log/letsencrypt:rw,nosuid,nodev,noexec,size=16m \
    -e "PEERONQ_DNS_PROPAGATION_SECONDS=180" \
    -v "$acme_root:/etc/letsencrypt:rw" \
    -v "$credentials_dir:/run/secrets/peeronq-spaceship:ro" \
    -v "$hook_source:/opt/peeronq/peeronq-spaceship-dns-hook.py:ro" \
    "$CERTBOT_IMAGE" certonly --manual --preferred-challenges dns \
    --manual-auth-hook "/usr/local/bin/python3 /opt/peeronq/peeronq-spaceship-dns-hook.py auth" \
    --manual-cleanup-hook "/usr/local/bin/python3 /opt/peeronq/peeronq-spaceship-dns-hook.py cleanup" \
    --no-directory-hooks --non-interactive --agree-tos --cert-name "$domain-dns01" \
    --keep-until-expiring --renew-with-new-domains -d "$domain" -d "*.$domain"
}

ensure_required_tls_hosts() {
  tls_dir=$(read_environment_value PEERONQ_TLS_CERT_DIR) \
    || fail "PEERONQ_TLS_CERT_DIR is missing"
  managed_cert="$tls_dir/fullchain.pem"
  managed_key="$tls_dir/privkey.pem"
  [ -f "$managed_cert" ] && [ ! -L "$managed_cert" ] \
    && [ -f "$managed_key" ] && [ ! -L "$managed_key" ] \
    || fail "managed TLS certificate or private key is missing or unsafe"
  validate_tls_pair_material "$managed_cert" "$managed_key" && return 0

  web_host=$(read_environment_value PEERONQ_WEB_HOST) \
    || fail "PEERONQ_WEB_HOST is missing"
  acme_root=/etc/letsencrypt
  if [ -n "${SPACESHIP_ACTIVE_CREDENTIALS_DIR:-}" ]; then
    [ "$web_host" = "peeronq.com" ] || fail "Spaceship DNS automation currently supports peeronq.com only"
    source_dir="$acme_root/live/$web_host-dns01"
    run_spaceship_acme "$acme_root" "$web_host" "$SPACESHIP_ACTIVE_CREDENTIALS_DIR" \
      "$temporary/release/scripts/linux/peeronq-spaceship-dns-hook.py" \
      || fail "ACME DNS-01 could not obtain a certificate; the existing certificate remains active"
    certificate_covers_required_hosts "$source_dir/fullchain.pem" \
      || fail "DNS-01 certificate does not cover every configured host"
    install_spaceship_credentials_dir "$SPACESHIP_ACTIVE_CREDENTIALS_DIR" "$SPACESHIP_CREDENTIALS_DIR" \
      || fail "Spaceship credentials could not be installed safely"
  else
    source_dir="$acme_root/live/$web_host"
    [ -r "$source_dir/fullchain.pem" ] && [ -r "$source_dir/privkey.pem" ] || fail \
      "TLS certificate does not cover every configured host. Import a trusted SAN/wildcard certificate covering Admin, Portal, Grafana and Prometheus before this upgrade."

    set --
    for name in $(required_tls_hosts); do
      set -- "$@" -d "$name"
    done
    run_standalone_acme_with_proxy_handoff "$acme_root" \
      certonly --standalone --non-interactive --agree-tos --cert-name "$web_host" \
      --keep-until-expiring --expand "$@" \
      || fail "ACME could not expand the TLS certificate. Every requested name must resolve publicly to this server during HTTP-01; for split/private DNS, import a trusted SAN or wildcard certificate instead."
  fi

  certificate_covers_required_hosts "$source_dir/fullchain.pem" \
    || fail "expanded TLS certificate still omits a configured host"
  cert_hash=$(openssl x509 -in "$source_dir/fullchain.pem" -pubkey -noout \
    | openssl pkey -pubin -outform DER 2>/dev/null | sha256sum | awk '{ print $1 }')
  key_hash=$(openssl pkey -in "$source_dir/privkey.pem" -pubout -outform DER 2>/dev/null \
    | sha256sum | awk '{ print $1 }')
  [ -n "$cert_hash" ] && [ "$cert_hash" = "$key_hash" ] \
    || fail "expanded TLS certificate and private key do not match"

  if ! (
    set -eu
    cert_tmp=$(mktemp "$tls_dir/.fullchain.XXXXXX")
    key_tmp=$(mktemp "$tls_dir/.privkey.XXXXXX")
    trap 'rm -f "$cert_tmp" "$key_tmp"' EXIT HUP INT TERM
    cp "$source_dir/fullchain.pem" "$cert_tmp"
    cp "$source_dir/privkey.pem" "$key_tmp"
    chown 0:65534 "$cert_tmp" "$key_tmp"
    chmod 640 "$cert_tmp" "$key_tmp"
    mv -f "$cert_tmp" "$managed_cert"
    mv -f "$key_tmp" "$managed_key"
    trap - EXIT HUP INT TERM
  ); then
    fail "expanded TLS certificate could not be published to the managed directory"
  fi
}

atomic_publish_tls_pair() (
  set -eu
  source_cert=$1
  source_key=$2
  tls_dir=$3
  cert_tmp=$(mktemp "$tls_dir/.fullchain.import.XXXXXX")
  key_tmp=$(mktemp "$tls_dir/.privkey.import.XXXXXX")
  trap 'rm -f "$cert_tmp" "$key_tmp"' EXIT HUP INT TERM
  cp "$source_cert" "$cert_tmp"
  cp "$source_key" "$key_tmp"
  chown 0:65534 "$cert_tmp" "$key_tmp"
  chmod 640 "$cert_tmp" "$key_tmp"
  sync -f "$cert_tmp"
  sync -f "$key_tmp"
  mv -f "$cert_tmp" "$tls_dir/fullchain.pem"
  mv -f "$key_tmp" "$tls_dir/privkey.pem"
  sync -f "$tls_dir"
  trap - EXIT HUP INT TERM
)

begin_tls_import_transaction() {
  tls_dir=$(read_environment_value PEERONQ_TLS_CERT_DIR) || return 1
  TLS_IMPORT_TRANSACTION_DIR="$temporary/tls-import"
  mkdir "$TLS_IMPORT_TRANSACTION_DIR" || return 1
  chmod 700 "$TLS_IMPORT_TRANSACTION_DIR" || return 1
  cp -p "$tls_dir/fullchain.pem" "$TLS_IMPORT_TRANSACTION_DIR/fullchain.pem" || return 1
  cp -p "$tls_dir/privkey.pem" "$TLS_IMPORT_TRANSACTION_DIR/privkey.pem" || return 1
  chmod 600 "$TLS_IMPORT_TRANSACTION_DIR/fullchain.pem" "$TLS_IMPORT_TRANSACTION_DIR/privkey.pem" \
    || return 1
  sync -f "$TLS_IMPORT_TRANSACTION_DIR/fullchain.pem" || return 1
  sync -f "$TLS_IMPORT_TRANSACTION_DIR/privkey.pem" || return 1

  renewal_count=0
  for renewal_file in "$TLS_RENEWAL_COMMAND" "$TLS_RENEWAL_SERVICE" "$TLS_RENEWAL_TIMER"; do
    if [ -e "$renewal_file" ] || [ -L "$renewal_file" ]; then
      renewal_count=$((renewal_count + 1))
    fi
  done
  case "$renewal_count" in
    0) ;;
    3)
      safe_root_runtime_file "$TLS_RENEWAL_COMMAND" 755 \
        && safe_root_runtime_file "$TLS_RENEWAL_SERVICE" 644 \
        && safe_root_runtime_file "$TLS_RENEWAL_TIMER" 644 \
        && grep -Fxq \
          "ExecStart=$TLS_RENEWAL_COMMAND --env-file $ENV_FILE" \
          "$TLS_RENEWAL_SERVICE" || return 1
      command -v systemctl >/dev/null 2>&1 || return 1
      cp -p "$TLS_RENEWAL_COMMAND" "$TLS_IMPORT_TRANSACTION_DIR/renew-command" || return 1
      cp -p "$TLS_RENEWAL_SERVICE" "$TLS_IMPORT_TRANSACTION_DIR/renew-service" || return 1
      cp -p "$TLS_RENEWAL_TIMER" "$TLS_IMPORT_TRANSACTION_DIR/renew-timer" || return 1
      systemctl is-enabled --quiet peeronq-tls-renew.timer >/dev/null 2>&1 \
        && TLS_IMPORT_RENEWAL_WAS_ENABLED=true
      systemctl is-active --quiet peeronq-tls-renew.timer >/dev/null 2>&1 \
        && TLS_IMPORT_RENEWAL_WAS_ACTIVE=true
      systemctl is-active --quiet peeronq-tls-renew.service >/dev/null 2>&1 \
        && TLS_IMPORT_RENEWAL_SERVICE_WAS_ACTIVE=true
      TLS_IMPORT_RENEWAL_PRESENT=true
      ;;
    *) return 1 ;;
  esac

  TLS_IMPORT_ACTIVE=true
  if [ "$TLS_IMPORT_RENEWAL_PRESENT" = "true" ]; then
    systemctl stop peeronq-tls-renew.timer peeronq-tls-renew.service >/dev/null 2>&1 \
      || return 1
    systemctl is-active --quiet peeronq-tls-renew.timer >/dev/null 2>&1 && return 1
    systemctl is-active --quiet peeronq-tls-renew.service >/dev/null 2>&1 && return 1
  fi
  atomic_publish_tls_pair "$TLS_CERT_SOURCE" "$TLS_KEY_SOURCE" "$tls_dir" || return 1
  validate_tls_pair_material "$tls_dir/fullchain.pem" "$tls_dir/privkey.pem"
}

retire_tls_renewal_management() {
  [ "$TLS_IMPORT_RENEWAL_PRESENT" = "true" ] || return 0
  systemctl disable --now peeronq-tls-renew.timer >/dev/null 2>&1 || return 1
  systemctl stop peeronq-tls-renew.service >/dev/null 2>&1 || return 1
  rm -f "$TLS_RENEWAL_COMMAND" "$TLS_RENEWAL_SERVICE" "$TLS_RENEWAL_TIMER" || return 1
  systemctl daemon-reload >/dev/null 2>&1
}

restore_tls_renewal_management() {
  [ "$TLS_IMPORT_RENEWAL_PRESENT" = "true" ] || return 0
  restore_status=0
  atomic_install_root_file "$TLS_IMPORT_TRANSACTION_DIR/renew-command" "$TLS_RENEWAL_COMMAND" 755 \
    || restore_status=1
  atomic_install_root_file "$TLS_IMPORT_TRANSACTION_DIR/renew-service" "$TLS_RENEWAL_SERVICE" 644 \
    || restore_status=1
  atomic_install_root_file "$TLS_IMPORT_TRANSACTION_DIR/renew-timer" "$TLS_RENEWAL_TIMER" 644 \
    || restore_status=1
  systemctl daemon-reload >/dev/null 2>&1 || restore_status=1
  if [ "$TLS_IMPORT_RENEWAL_WAS_ENABLED" = "true" ]; then
    systemctl enable peeronq-tls-renew.timer >/dev/null 2>&1 || restore_status=1
  else
    systemctl disable peeronq-tls-renew.timer >/dev/null 2>&1 || restore_status=1
  fi
  if [ "$TLS_IMPORT_RENEWAL_WAS_ACTIVE" = "true" ]; then
    systemctl start peeronq-tls-renew.timer >/dev/null 2>&1 || restore_status=1
  else
    systemctl stop peeronq-tls-renew.timer >/dev/null 2>&1 || restore_status=1
  fi
  if [ "$TLS_IMPORT_RENEWAL_SERVICE_WAS_ACTIVE" = "true" ]; then
    systemctl start peeronq-tls-renew.service >/dev/null 2>&1 || restore_status=1
  else
    systemctl stop peeronq-tls-renew.service >/dev/null 2>&1 || restore_status=1
  fi
  [ "$restore_status" -eq 0 ]
}

rollback_tls_import() {
  release_path=${1:-}
  [ "$TLS_IMPORT_ACTIVE" = "true" ] || return 0
  rollback_status=0
  tls_dir=$(read_environment_value PEERONQ_TLS_CERT_DIR 2>/dev/null || true)
  if [ -z "$tls_dir" ] \
    || ! atomic_publish_tls_pair \
      "$TLS_IMPORT_TRANSACTION_DIR/fullchain.pem" \
      "$TLS_IMPORT_TRANSACTION_DIR/privkey.pem" \
      "$tls_dir"; then
    rollback_status=1
  fi
  restore_tls_renewal_management || rollback_status=1
  if [ -n "$release_path" ] && [ -d "$release_path" ]; then
    compose "$release_path" up -d --no-deps --force-recreate \
      --wait --wait-timeout 120 proxy turn || rollback_status=1
  fi
  if [ "$rollback_status" -eq 0 ]; then
    TLS_IMPORT_ACTIVE=false
  fi
  [ "$rollback_status" -eq 0 ]
}

prepare_managed_secret_file() {
  managed_secret_value=$(read_environment_value "$1") \
    || fail "$1 is missing"
  managed_secret_expected="$expected_managed_secrets_dir/$2"
  [ "$managed_secret_value" = "$managed_secret_expected" ] \
    || fail "$1 must use the installer-managed file: $managed_secret_expected"
  if [ ! -s "$managed_secret_expected" ] || [ ! -f "$managed_secret_expected" ] \
    || [ -L "$managed_secret_expected" ]; then
    fail "managed secret file is missing, empty, or unsafe: $managed_secret_expected"
  fi
  [ "$(stat -c '%u' "$managed_secret_expected")" = "0" ] \
    || fail "managed secret files must be owned by root"
  chown "0:$3" "$managed_secret_expected"
  chmod "$4" "$managed_secret_expected"
}

prepare_managed_secret_permissions() {
  expected_managed_secrets_dir="$(dirname "$ENV_FILE")/secrets"
  if [ ! -d "$expected_managed_secrets_dir" ] || [ -L "$expected_managed_secrets_dir" ]; then
    fail "managed secrets directory is missing or unsafe"
  fi
  [ "$(stat -c '%u' "$expected_managed_secrets_dir")" = "0" ] \
    || fail "managed secrets directory must be owned by root"
  chown 0:0 "$expected_managed_secrets_dir"
  chmod 700 "$expected_managed_secrets_dir"

  prepare_managed_secret_file PEERONQ_ALERTMANAGER_WEBHOOK_TOKEN_FILE \
    alertmanager-webhook-token 1654 640
  prepare_managed_secret_file PEERONQ_GRAFANA_ADMIN_PASSWORD_FILE \
    grafana-admin-password 0 640
  prepare_managed_secret_file PEERONQ_TURN_SHARED_SECRET_FILE \
    turn-shared-secret 65534 640
  prepare_managed_secret_file PEERONQ_ADMIN_DATA_PROTECTION_CERTIFICATE_FILE \
    admin-data-protection.pfx 1654 640
  prepare_managed_secret_file PEERONQ_ADMIN_DATA_PROTECTION_PASSWORD_FILE \
    admin-data-protection-password 1654 640
  prepare_managed_secret_file PEERONQ_SIGNALING_ATTESTATION_PRIVATE_KEY_FILE \
    signaling-attestation-private.pem 1654 640
  prepare_managed_secret_file PEERONQ_SIGNALING_ATTESTATION_PUBLIC_KEY_FILE \
    signaling-attestation-public.pem 1654 640
  prepare_managed_secret_file PEERONQ_POSTGRES_BACKUP_PGPASS_FILE \
    postgres-backup.pgpass 0 600
  prepare_managed_secret_file PEERONQ_POSTGRES_RESTORE_PGPASS_FILE \
    postgres-restore.pgpass 0 600
}

scrub_admin_bootstrap() {
  scrubbed=$(mktemp "${ENV_FILE}.scrub.XXXXXX")
  awk '
    /^PEERONQ_ADMIN_BOOTSTRAP_ENABLED=/ { print "PEERONQ_ADMIN_BOOTSTRAP_ENABLED=false"; next }
    /^PEERONQ_ADMIN_BOOTSTRAP_(EMAIL|PASSWORD|TOTP_SECRET_BASE32|RECOVERY_CODE_[1-8])=/ {
      split($0, parts, "="); print parts[1] "="; next
    }
    { print }
  ' "$ENV_FILE" > "$scrubbed"
  chown 0:0 "$scrubbed"
  chmod 600 "$scrubbed"
  mv -f "$scrubbed" "$ENV_FILE"
}

compose() {
  release_path=$1
  shift
  docker compose \
    --env-file "$ENV_FILE" \
    -f "$release_path/src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml" \
    -f "$release_path/src/PeerOnQ.Infrastructure.Deployment/docker-compose.production.yml" \
    "$@"
}

set_link() {
  link_path=$1
  target_path=$2
  temporary_link="${link_path}.new.$$"
  ln -s "$target_path" "$temporary_link"
  mv -Tf "$temporary_link" "$link_path"
  link_parent=$(dirname "$link_path")
  sync -f "$link_parent"
}

current_release() {
  if [ -L "$INSTALL_ROOT/current" ]; then
    readlink -f "$INSTALL_ROOT/current"
  fi
}

previous_release() {
  if [ -L "$INSTALL_ROOT/previous" ]; then
    readlink -f "$INSTALL_ROOT/previous"
  fi
}

ensure_root_directory() {
  directory=$1
  mode=$2
  [ ! -L "$directory" ] || fail "protected path must not be a symbolic link: $directory"
  if [ ! -e "$directory" ]; then
    mkdir "$directory"
  fi
  if [ ! -d "$directory" ] || [ -L "$directory" ]; then
    fail "protected path is not a safe directory: $directory"
  fi
  [ "$(stat -c '%u' "$directory")" = "0" ] \
    || fail "protected directory must be owned by root: $directory"
  chown 0:0 "$directory"
  chmod "$mode" "$directory"
}

validate_platform_upgrade_trust() {
  [ -n "$PLATFORM_UPGRADE_KEYRING_SOURCE" ] \
    && [ -n "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT" ] \
    || fail "platform upgrade keyring and signer fingerprint must be provided together"
  case "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT" in
    *[!0-9A-Fa-f]*|'') fail "platform upgrade signer fingerprint must be hexadecimal" ;;
  esac
  case "${#PLATFORM_UPGRADE_SIGNER_FINGERPRINT}" in
    40|64) ;;
    *) fail "platform upgrade signer fingerprint must contain exactly 40 or 64 hexadecimal characters" ;;
  esac
  PLATFORM_UPGRADE_SIGNER_FINGERPRINT=$(printf '%s' "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT" | tr '[:lower:]' '[:upper:]')
  [ -f "$PLATFORM_UPGRADE_KEYRING_SOURCE" ] && [ ! -L "$PLATFORM_UPGRADE_KEYRING_SOURCE" ] \
    || fail "platform upgrade keyring must be a regular non-symlink file"
  PLATFORM_UPGRADE_KEYRING_SOURCE=$(readlink -f "$PLATFORM_UPGRADE_KEYRING_SOURCE")
  [ "$(stat -c '%s' "$PLATFORM_UPGRADE_KEYRING_SOURCE")" -gt 0 ] \
    && [ "$(stat -c '%s' "$PLATFORM_UPGRADE_KEYRING_SOURCE")" -le 1048576 ] \
    || fail "platform upgrade keyring must be between 1 byte and 1 MiB"
  require_command gpg
  require_command gpgv
  if gpg --batch --list-packets "$PLATFORM_UPGRADE_KEYRING_SOURCE" 2>/dev/null \
      | grep -Eq '^:secret (sub )?key packet:'; then
    fail "platform upgrade keyring must not contain secret-key material"
  fi
  gpg --batch --with-colons --show-keys --fingerprint --with-subkey-fingerprint "$PLATFORM_UPGRADE_KEYRING_SOURCE" 2>/dev/null \
    | awk -F: -v expected="$PLATFORM_UPGRADE_SIGNER_FINGERPRINT" \
      '$1 == "fpr" && toupper($10) == expected { found = 1 } END { exit !found }' \
    || fail "platform upgrade signer fingerprint is absent from the supplied keyring"
}

prepare_platform_upgrade_directories() {
  ensure_root_directory "$(dirname "$PLATFORM_UPGRADE_ROOT")" 755
  ensure_root_directory "$PLATFORM_UPGRADE_ROOT" 750
  ensure_root_directory "$PLATFORM_UPGRADE_ROOT/processing" 700
  ensure_root_directory "$PLATFORM_UPGRADE_ROOT/archive" 700
  ensure_root_directory "$LOG_ROOT" 700
  ensure_root_directory "$(dirname "$LOG_ROOT")" 700
  ensure_root_directory "/var/log/peeronq/platform-upgrade" 700

  for shared_directory in "$PLATFORM_UPGRADE_ROOT/inbox" "$PLATFORM_UPGRADE_ROOT/status"; do
    [ ! -L "$shared_directory" ] || fail "platform upgrade shared path must not be a symbolic link: $shared_directory"
    if [ ! -e "$shared_directory" ]; then
      mkdir "$shared_directory"
    fi
    [ -d "$shared_directory" ] && [ ! -L "$shared_directory" ] \
      || fail "platform upgrade shared path is unsafe: $shared_directory"
    [ "$(stat -c '%u' "$shared_directory")" = "0" ] \
      || fail "platform upgrade shared paths must be owned by root"
    chown "0:1654" "$shared_directory"
  done
  chmod 1730 "$PLATFORM_UPGRADE_ROOT/inbox"
  chmod 750 "$PLATFORM_UPGRADE_ROOT/status"
}

install_platform_upgrade_trust() {
  validate_platform_upgrade_trust
  config_root=$(dirname "$PLATFORM_UPGRADE_CONFIG")
  ensure_root_directory "$config_root" 700
  keyring_temp=$(mktemp "$config_root/.platform-upgrade-keyring.XXXXXX")
  config_temp=$(mktemp "$config_root/.platform-upgrade-config.XXXXXX")
  cp "$PLATFORM_UPGRADE_KEYRING_SOURCE" "$keyring_temp"
  chown 0:0 "$keyring_temp"
  chmod 400 "$keyring_temp"
  {
    printf 'keyring=%s\n' "$PLATFORM_UPGRADE_KEYRING"
    printf 'signer_fingerprint=%s\n' "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT"
  } > "$config_temp"
  chown 0:0 "$config_temp"
  chmod 400 "$config_temp"
  mv -f "$keyring_temp" "$PLATFORM_UPGRADE_KEYRING"
  mv -f "$config_temp" "$PLATFORM_UPGRADE_CONFIG"
  set_environment_value PEERONQ_PLATFORM_UPGRADE_ENABLED true
}

platform_upgrade_trust_is_safe() {
  [ -f "$PLATFORM_UPGRADE_CONFIG" ] && [ ! -L "$PLATFORM_UPGRADE_CONFIG" ] \
    && [ "$(stat -c '%u:%g:%a:%h' "$PLATFORM_UPGRADE_CONFIG")" = "0:0:400:1" ] \
    && [ -f "$PLATFORM_UPGRADE_KEYRING" ] && [ ! -L "$PLATFORM_UPGRADE_KEYRING" ] \
    && [ "$(stat -c '%u:%g:%a:%h' "$PLATFORM_UPGRADE_KEYRING")" = "0:0:400:1" ]
}

safe_root_runtime_file() {
  path=$1
  mode=$2
  [ -f "$path" ] && [ ! -L "$path" ] \
    && [ "$(stat -c '%u:%g:%a:%h' "$path")" = "0:0:$mode:1" ]
}

same_file_contents() {
  [ "$(sha256sum "$1" | awk '{ print $1 }')" = "$(sha256sum "$2" | awk '{ print $1 }')" ]
}

atomic_install_root_file() {
  source=$1
  destination=$2
  mode=$3
  destination_dir=$(dirname "$destination")
  temporary_file=$(mktemp "$destination_dir/.peeronq-platform-upgrade.XXXXXX") || return 1
  if ! cp "$source" "$temporary_file" \
    || ! chown 0:0 "$temporary_file" \
    || ! chmod "$mode" "$temporary_file" \
    || ! sync -f "$temporary_file" \
    || ! mv -f "$temporary_file" "$destination" \
    || ! sync -f "$destination_dir"; then
    rm -f "$temporary_file"
    return 1
  fi
}

write_tls_renewal_units() {
  cat > "$TLS_RENEWAL_SERVICE" <<EOF
[Unit]
Description=Renew PeerOnQ TLS certificate
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
CapabilityBoundingSet=
ReadWritePaths=/etc/letsencrypt $(dirname "$ENV_FILE")/tls /run/lock
TimeoutStartSec=15min
ExecStart=$TLS_RENEWAL_COMMAND --env-file $ENV_FILE
EOF
  cat > "$TLS_RENEWAL_TIMER" <<'EOF'
[Unit]
Description=Weekly PeerOnQ TLS renewal check

[Timer]
OnCalendar=weekly
RandomizedDelaySec=6h
Persistent=true

[Install]
WantedBy=timers.target
EOF
}

refresh_tls_renewal_command() {
  release_path=$1
  renewal_source="$release_path/scripts/linux/renew-peeronq-tls.sh"
  [ -f "$renewal_source" ] && [ ! -L "$renewal_source" ] || return 1
  sh -n "$renewal_source" || return 1

  dns_enabled=false
  if command -v validate_spaceship_credentials_dir >/dev/null 2>&1 \
    && validate_spaceship_credentials_dir "${SPACESHIP_CREDENTIALS_DIR:-}" \
    && [ -f "$release_path/scripts/linux/peeronq-spaceship-dns-hook.py" ]; then
    dns_enabled=true
  fi

  if [ ! -e "$TLS_RENEWAL_COMMAND" ] \
    && [ ! -e "$TLS_RENEWAL_SERVICE" ] \
    && [ ! -e "$TLS_RENEWAL_TIMER" ]; then
    [ "$dns_enabled" = "true" ] || return 0
    ensure_root_directory "$(dirname "$TLS_RENEWAL_COMMAND")" 755
    ensure_root_directory "$(dirname "$TLS_DNS_HOOK")" 755
    atomic_install_root_file "$renewal_source" "$TLS_RENEWAL_COMMAND" 755 || return 1
    atomic_install_root_file "$release_path/scripts/linux/peeronq-spaceship-dns-hook.py" "$TLS_DNS_HOOK" 755 || return 1
    write_tls_renewal_units || return 1
    chown 0:0 "$TLS_RENEWAL_SERVICE" "$TLS_RENEWAL_TIMER" && chmod 644 "$TLS_RENEWAL_SERVICE" "$TLS_RENEWAL_TIMER" || return 1
    systemctl daemon-reload >/dev/null 2>&1 || return 1
    systemctl enable --now peeronq-tls-renew.timer >/dev/null 2>&1 || return 1
    return 0
  fi

  safe_root_runtime_file "$TLS_RENEWAL_COMMAND" 755 \
    && safe_root_runtime_file "$TLS_RENEWAL_SERVICE" 644 \
    && safe_root_runtime_file "$TLS_RENEWAL_TIMER" 644 \
    && grep -Fxq \
      "ExecStart=$TLS_RENEWAL_COMMAND --env-file $ENV_FILE" \
      "$TLS_RENEWAL_SERVICE" \
    || return 1

  atomic_install_root_file "$renewal_source" "$TLS_RENEWAL_COMMAND" 755 || return 1
  same_file_contents "$renewal_source" "$TLS_RENEWAL_COMMAND" || return 1

  if [ "$dns_enabled" = "true" ]; then
    ensure_root_directory "$(dirname "$TLS_DNS_HOOK")" 755
    atomic_install_root_file "$release_path/scripts/linux/peeronq-spaceship-dns-hook.py" "$TLS_DNS_HOOK" 755 || return 1
    same_file_contents "$release_path/scripts/linux/peeronq-spaceship-dns-hook.py" "$TLS_DNS_HOOK" || return 1
  fi
}

install_platform_upgrade_agent() {
  release_path=$1
  enabled=$(read_environment_value PEERONQ_PLATFORM_UPGRADE_ENABLED 2>/dev/null || true)
  [ "$enabled" = "true" ] || return 0
  platform_upgrade_trust_is_safe || return 1
  require_command gpgv
  require_command sync
  require_command systemctl
  agent_source="$release_path/scripts/linux/peeronq-platform-upgrade-agent.sh"
  service_source="$release_path/scripts/linux/peeronq-platform-upgrade-agent.service"
  path_source="$release_path/scripts/linux/peeronq-platform-upgrade-agent.path"
  for source_file in "$agent_source" "$service_source" "$path_source"; do
    [ -f "$source_file" ] && [ ! -L "$source_file" ] || return 1
  done
  sh -n "$agent_source" || return 1

  if safe_root_runtime_file "$PLATFORM_UPGRADE_AGENT" 755 \
    && safe_root_runtime_file "$PLATFORM_UPGRADE_SERVICE" 644 \
    && safe_root_runtime_file "$PLATFORM_UPGRADE_PATH_UNIT" 644 \
    && systemctl is-enabled --quiet peeronq-platform-upgrade-agent.path \
    && same_file_contents "$agent_source" "$PLATFORM_UPGRADE_AGENT" \
    && same_file_contents "$service_source" "$PLATFORM_UPGRADE_SERVICE" \
    && same_file_contents "$path_source" "$PLATFORM_UPGRADE_PATH_UNIT"; then
    "$PLATFORM_UPGRADE_AGENT" --self-check
    return
  fi
  ensure_root_directory "/usr/local/libexec" 755
  [ -d "/etc/systemd/system" ] && [ ! -L "/etc/systemd/system" ] \
    && [ "$(stat -c '%u' "/etc/systemd/system")" = "0" ] || return 1
  for existing_file in "$PLATFORM_UPGRADE_AGENT" "$PLATFORM_UPGRADE_SERVICE" "$PLATFORM_UPGRADE_PATH_UNIT"; do
    if [ -e "$existing_file" ]; then
      [ ! -L "$existing_file" ] && [ -f "$existing_file" ] \
        && [ "$(stat -c '%u:%g:%h' "$existing_file")" = "0:0:1" ] || return 1
    fi
  done

  transaction=$(mktemp -d "$PLATFORM_UPGRADE_ROOT/processing/.agent-install.XXXXXX") || return 1
  chmod 700 "$transaction"
  had_agent=false
  had_service=false
  had_path=false
  if [ -e "$PLATFORM_UPGRADE_AGENT" ]; then
    cp -p "$PLATFORM_UPGRADE_AGENT" "$transaction/agent" || { rm -rf "$transaction"; return 1; }
    had_agent=true
  fi
  if [ -e "$PLATFORM_UPGRADE_SERVICE" ]; then
    cp -p "$PLATFORM_UPGRADE_SERVICE" "$transaction/service" || { rm -rf "$transaction"; return 1; }
    had_service=true
  fi
  if [ -e "$PLATFORM_UPGRADE_PATH_UNIT" ]; then
    cp -p "$PLATFORM_UPGRADE_PATH_UNIT" "$transaction/path" || { rm -rf "$transaction"; return 1; }
    had_path=true
  fi
  was_enabled=false
  systemctl is-enabled --quiet peeronq-platform-upgrade-agent.path && was_enabled=true

  install_ok=true
  atomic_install_root_file "$agent_source" "$PLATFORM_UPGRADE_AGENT" 755 || install_ok=false
  [ "$install_ok" = "false" ] \
    || atomic_install_root_file "$service_source" "$PLATFORM_UPGRADE_SERVICE" 644 \
    || install_ok=false
  [ "$install_ok" = "false" ] \
    || atomic_install_root_file "$path_source" "$PLATFORM_UPGRADE_PATH_UNIT" 644 \
    || install_ok=false
  [ "$install_ok" = "false" ] || systemctl daemon-reload || install_ok=false
  [ "$install_ok" = "false" ] || "$PLATFORM_UPGRADE_AGENT" --self-check || install_ok=false
  if [ "$install_ok" != "false" ]; then
    if [ "$was_enabled" = "true" ]; then
      systemctl try-restart peeronq-platform-upgrade-agent.path >/dev/null || install_ok=false
    else
      systemctl enable --now peeronq-platform-upgrade-agent.path >/dev/null || install_ok=false
    fi
  fi
  if [ "$install_ok" != "false" ]; then
    "$PLATFORM_UPGRADE_AGENT" --initialize || install_ok=false
  fi

  if [ "$install_ok" = "false" ]; then
    if [ "$had_agent" = "true" ]; then atomic_install_root_file "$transaction/agent" "$PLATFORM_UPGRADE_AGENT" 755 || true; else rm -f "$PLATFORM_UPGRADE_AGENT"; fi
    if [ "$had_service" = "true" ]; then atomic_install_root_file "$transaction/service" "$PLATFORM_UPGRADE_SERVICE" 644 || true; else rm -f "$PLATFORM_UPGRADE_SERVICE"; fi
    if [ "$had_path" = "true" ]; then atomic_install_root_file "$transaction/path" "$PLATFORM_UPGRADE_PATH_UNIT" 644 || true; else rm -f "$PLATFORM_UPGRADE_PATH_UNIT"; fi
    systemctl daemon-reload >/dev/null 2>&1 || true
    if [ "$was_enabled" = "true" ]; then
      systemctl enable --now peeronq-platform-upgrade-agent.path >/dev/null 2>&1 || true
    else
      systemctl disable --now peeronq-platform-upgrade-agent.path >/dev/null 2>&1 || true
    fi
    rm -rf "$transaction"
    return 1
  fi
  rm -rf "$transaction"
}

start_pending_platform_upgrade_request() {
  [ -e "$PLATFORM_UPGRADE_ROOT/inbox/.active-request" ] || return 0
  systemctl is-active --quiet peeronq-platform-upgrade-agent.service && return 0
  systemctl start --no-block peeronq-platform-upgrade-agent.service
}

quarantine_incomplete_release() {
  candidate=$1
  [ ! -L "$candidate" ] || fail "existing release path must not be a symbolic link: $candidate"
  [ -e "$candidate" ] || return 0
  if [ ! -d "$candidate" ] || [ -L "$candidate" ]; then
    fail "existing release path is not a safe directory: $candidate"
  fi
  [ "$(stat -c '%u' "$candidate")" = "0" ] \
    || fail "existing release directory must be owned by root: $candidate"
  candidate=$(readlink -f "$candidate")
  active=$(current_release || true)
  previous=$(previous_release || true)
  [ "$candidate" != "$active" ] \
    || fail "refusing to replace the active release: $candidate"
  [ "$candidate" != "$previous" ] \
    || fail "refusing to replace the rollback release: $candidate"
  quarantine=$(mktemp -d "$INSTALL_ROOT/failed-releases/${INSTALLER_VERSION}.XXXXXX")
  mv "$candidate" "$quarantine/release"
  printf 'Previous incomplete release was preserved at %s.\n' "$quarantine/release"
}

begin_install_log() {
  install_log=$(mktemp "$LOG_ROOT/install-${INSTALLER_VERSION}.XXXXXX.log")
  chown 0:0 "$install_log"
  chmod 600 "$install_log"
  printf 'PeerOnQ installer release %s\n' "$INSTALLER_VERSION" > "$install_log"
}

run_compose_step() {
  failed_step=$1
  release_path=$2
  shift 2
  status_file=$(mktemp "$LOG_ROOT/.status.XXXXXX")
  printf '\n==> %s\n' "$failed_step" | tee -a "$install_log"
  {
    if compose "$release_path" "$@"; then
      command_status=0
    else
      command_status=$?
    fi
    printf '%s\n' "$command_status" > "$status_file"
  } 2>&1 | tee -a "$install_log" || true
  command_status=$(awk 'NR == 1 { print; exit }' "$status_file")
  rm -f "$status_file"
  [ "$command_status" = "0" ]
}

run_compose_step_with_retry() {
  step_label=$1
  release_path=$2
  maximum_attempts=$3
  shift 3
  case "$maximum_attempts" in 1|2|3|4|5) ;; *) return 1 ;; esac

  attempt=1
  while [ "$attempt" -le "$maximum_attempts" ]; do
    if run_compose_step "$step_label (attempt $attempt/$maximum_attempts)" "$release_path" "$@"; then
      return 0
    fi
    [ "$attempt" -lt "$maximum_attempts" ] || return 1
    retry_delay=$((attempt * 2))
    printf 'Retrying %s in %s seconds after Docker reconciliation did not complete.\n' \
      "$step_label" "$retry_delay" | tee -a "$install_log"
    sleep "$retry_delay"
    attempt=$((attempt + 1))
  done
  return 1
}

capture_failure_diagnostics() {
  {
    printf '\nDeployment failed during: %s\n' "$failed_step"
    printf '\n--- docker compose ps --all ---\n'
    compose "$1" ps --all || true
    printf '\n--- bounded docker compose logs ---\n'
    compose "$1" logs --no-color --tail 200 || true
  } >> "$install_log" 2>&1 || true
  chown 0:0 "$install_log" 2>/dev/null || true
  chmod 600 "$install_log" 2>/dev/null || true
  printf 'Deployment failed during: %s\n' "$failed_step" >&2
  printf 'Failure diagnostics were saved to %s.\n' "$install_log" >&2
}

verify_embedded_windows_client() {
  release_path=$1
  downloads_path="$release_path/artifacts/peeronq/public/downloads"
  sh "$release_path/scripts/linux/verify-embedded-windows-client.sh" "$release_path" || return 1
  expected_hash=$(awk '
    $2 == "PeerOnQ-Windows-x64.msi" && length($1) == 64 && $1 !~ /[^0-9a-f]/ {
      print $1
      found = 1
      exit
    }
    END { if (!found) exit 1 }
  ' "$downloads_path/SHA256SUMS.txt") || return 1
  expected_version=$(awk 'NR == 1 { print; found = 1; exit } END { if (!found) exit 1 }' \
    "$downloads_path/embedded-windows-version.txt") || return 1
  release_type=$(awk 'NR == 1 { print; exit }' "$downloads_path/embedded-windows-release-type.txt")
  case "$release_type" in signed|unsigned-pilot) ;; *) return 1 ;; esac
  expected_download_path='/downloads/PeerOnQ-Windows-x64.msi'
  run_compose_step "verify embedded Windows client publication" "$release_path" \
    exec -T web-ui sh -ec '
      actual_hash=$(wget --quiet -O - "http://127.0.0.1:8080/downloads/PeerOnQ-Windows-x64.msi?v=$2" \
        | sha256sum | awk '\''{ print $1 }'\'')
      [ "$actual_hash" = "$1" ] || {
        printf "Public Windows client SHA-256 does not match the embedded release.\n" >&2
        exit 1
      }
      grep -R -F -q "$3" /usr/share/nginx/html/assets
      grep -R -F -q "?v=" /usr/share/nginx/html/assets
    ' sh "$expected_hash" "$expected_version" "$expected_download_path" || return 1
  run_compose_step "verify Windows client proxy streaming" "$release_path" \
    exec -T proxy sh -ec '
      nginx -T 2>&1 | awk '\''
        /^[[:space:]]*location = \/downloads\/PeerOnQ-Windows-x64\.msi \{/ {
          inside = 1
          found = 1
        }
        inside && /proxy_buffering off;/ { streaming = 1 }
        inside && /proxy_max_temp_file_size 0;/ { no_temp_file = 1 }
        inside && /^[[:space:]]*}/ { inside = 0 }
        END { exit !(found && streaming && no_temp_file) }
      '\''
      actual_hash=$(curl --fail --silent --show-error \
        --proto =https \
        --tlsv1.2 \
        --noproxy "*" \
        --connect-timeout 10 \
        --max-time 600 \
        --resolve "$PEERONQ_WEB_HOST:443:127.0.0.1" \
        "https://$PEERONQ_WEB_HOST/downloads/PeerOnQ-Windows-x64.msi?v=$2" \
        | sha256sum | awk '\''{ print $1 }'\'')
      [ "$actual_hash" = "$1" ]
      actual_version=$(curl --fail --silent --show-error \
        --proto =https \
        --tlsv1.2 \
        --noproxy "*" \
        --connect-timeout 10 \
        --max-time 30 \
        --resolve "$PEERONQ_WEB_HOST:443:127.0.0.1" \
        "https://$PEERONQ_WEB_HOST/downloads/embedded-windows-version.txt")
      [ "$actual_version" = "$2" ] || {
        printf "Public Windows client version does not match the embedded release.\n" >&2
        exit 1
      }
      cache_control=$(curl --fail --silent --show-error --head \
        --proto =https \
        --tlsv1.2 \
        --noproxy "*" \
        --connect-timeout 10 \
        --max-time 30 \
        --resolve "$PEERONQ_WEB_HOST:443:127.0.0.1" \
        "https://$PEERONQ_WEB_HOST/downloads/PeerOnQ-Windows-x64.msi?v=$2" \
        | tr -d "\r" \
        | awk '\''tolower($1) == "cache-control:" { print; exit }'\'')
      printf "%s\n" "$cache_control" | grep -Fq "no-store" || {
        printf "Public Windows client response is cacheable.\n" >&2
        exit 1
      }
      public_page_hash=$(curl --fail --silent --show-error \
        --proto =https \
        --tlsv1.2 \
        --noproxy "*" \
        --connect-timeout 10 \
        --max-time 30 \
        --resolve "$PEERONQ_WEB_HOST:443:127.0.0.1" \
        "https://$PEERONQ_WEB_HOST/downloads" \
        | sha256sum | awk '\''{ print $1 }'\'')
      base_page_hash=$(curl --fail --silent --show-error \
        --connect-timeout 10 \
        --max-time 30 \
        http://web-ui:8080/ \
        | sha256sum | awk '\''{ print $1 }'\'')
      [ "$public_page_hash" = "$base_page_hash" ] || {
        printf "Public Downloads page is not the current base web release.\n" >&2
        exit 1
      }
    ' sh "$expected_hash" "$expected_version"
}

transition_website_overlay() {
  action=$1
  release_path=$2
  run_compose_step "$3" "$release_path" \
    run --rm --no-deps \
    -e "PEERONQ_WEBSITE_PLATFORM_ACTION=$action" \
    website-platform-state
}

verify_public_control_plane() {
  release_path=$1
  api_host=$(read_environment_value PEERONQ_API_HOST) || return 1
  signal_host=$(read_environment_value PEERONQ_SIGNAL_HOST) || return 1
  [ -n "$api_host" ] && [ -n "$signal_host" ] || return 1
  run_compose_step "verify public API and signaling proxy routing" "$release_path" \
    exec -T proxy sh -ec '
      api_body=$(curl --fail --silent --show-error \
        --proto =https \
        --tlsv1.2 \
        --noproxy "*" \
        --connect-timeout 10 \
        --max-time 30 \
        --resolve "$1:443:127.0.0.1" \
        "https://$1/health/live")
      printf "%s" "$api_body" \
        | grep -Eq '"'"'"status"'"'"[[:space:]]*:[[:space:]]*"'"'"healthy"'"'"'

      signal_body=$(curl --fail --silent --show-error \
        --proto =https \
        --tlsv1.2 \
        --noproxy "*" \
        --connect-timeout 10 \
        --max-time 30 \
        --resolve "$2:443:127.0.0.1" \
        "https://$2/health/ready")
      printf "%s" "$signal_body" \
        | grep -Eq '"'"'"status"'"'"[[:space:]]*:[[:space:]]*"'"'"ready"'"'"'
    ' sh "$api_host" "$signal_host"
}

show_status() {
  current=$(current_release || true)
  if [ -z "$current" ] || [ ! -d "$current" ]; then
    printf 'PeerOnQ server is not installed.\n'
    exit 3
  fi
  printf 'Current release: %s\n' "$current"
  compose "$current" ps
}

rollback() {
  [ -L "$INSTALL_ROOT/previous" ] || fail "no previous release is recorded"
  previous=$(readlink -f "$INSTALL_ROOT/previous")
  [ -d "$previous" ] || fail "the recorded previous release is missing"
  current=$(current_release || true)
  compose "$previous" config --quiet
  ensure_root_directory "$(dirname "$LOG_ROOT")" 700
  ensure_root_directory "$LOG_ROOT" 700
  begin_install_log
  rollback_failed=false
  run_compose_step "start retained previous application release" "$previous" \
    up -d --build --wait --wait-timeout 300 --remove-orphans || rollback_failed=true
  if [ "$rollback_failed" = "false" ]; then
    failed_step="verify retained Windows client publication"
    verify_embedded_windows_client "$previous" || rollback_failed=true
  fi
  if [ "$rollback_failed" = "false" ]; then
    failed_step="verify retained public API and signaling routes"
    verify_public_control_plane "$previous" || rollback_failed=true
  fi
  if [ "$rollback_failed" = "true" ]; then
    capture_failure_diagnostics "$previous"
    if [ -n "$current" ] && [ -d "$current" ]; then
      compose "$current" up -d --build --wait --wait-timeout 300 --remove-orphans || true
    fi
    fail "rollback validation failed; the current release link was not changed"
  fi
  set_link "$INSTALL_ROOT/current" "$previous"
  if [ -n "$current" ] && [ -d "$current" ] && [ "$current" != "$previous" ]; then
    set_link "$INSTALL_ROOT/previous" "$current"
  fi
  rm -f "$install_log"
  printf 'Rolled back to %s\n' "$previous"
}

MODE=install
DRY_RUN=false
BOOTSTRAP=false
BOOTSTRAP_OPTIONS_PROVIDED=false
BASE_DOMAIN=peeronq.com
PUBLIC_IP=31.171.38.28
LOCAL_IP=""
ADMIN_ALLOWED_CIDR=""
REGION=az-1
ADMIN_EMAIL=admin@peeronq.com
CUSTOMER_SMTP_HOST=""
CUSTOMER_SMTP_PORT=587
CUSTOMER_SMTP_HOST_PROVIDED=false
CUSTOMER_SMTP_PORT_PROVIDED=false
CUSTOMER_MAIL_DISABLE_REQUESTED=false
CUSTOMER_REGISTRATION_MODE=""
CUSTOMER_MFA_ENABLED=""
ACME_EMAIL=""
SPACESHIP_CREDENTIALS_SOURCE=""
TLS_CERT_SOURCE=""
TLS_KEY_SOURCE=""
TLS_IMPORT_REQUESTED=false
TLS_IMPORT_ACTIVE=false
TLS_IMPORT_RENEWAL_PRESENT=false
TLS_IMPORT_RENEWAL_WAS_ENABLED=false
TLS_IMPORT_RENEWAL_WAS_ACTIVE=false
TLS_IMPORT_RENEWAL_SERVICE_WAS_ACTIVE=false
TLS_IMPORT_TRANSACTION_DIR=""
PLATFORM_UPGRADE_KEYRING_SOURCE=""
PLATFORM_UPGRADE_SIGNER_FINGERPRINT=""
PLATFORM_UPGRADE_TRUST_REQUESTED=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --env-file)
      [ "$#" -ge 2 ] || fail "--env-file requires a path"
      ENV_FILE=$2
      shift 2
      ;;
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    --bootstrap)
      BOOTSTRAP=true
      shift
      ;;
    --base-domain)
      [ "$#" -ge 2 ] || fail "--base-domain requires a value"
      BOOTSTRAP_OPTIONS_PROVIDED=true
      BASE_DOMAIN=$2
      shift 2
      ;;
    --public-ip)
      [ "$#" -ge 2 ] || fail "--public-ip requires a value"
      BOOTSTRAP_OPTIONS_PROVIDED=true
      PUBLIC_IP=$2
      shift 2
      ;;
    --local-ip)
      [ "$#" -ge 2 ] || fail "--local-ip requires a value"
      BOOTSTRAP_OPTIONS_PROVIDED=true
      LOCAL_IP=$2
      shift 2
      ;;
    --admin-allowed-cidr)
      [ "$#" -ge 2 ] || fail "--admin-allowed-cidr requires a value"
      BOOTSTRAP_OPTIONS_PROVIDED=true
      ADMIN_ALLOWED_CIDR=$2
      shift 2
      ;;
    --region)
      [ "$#" -ge 2 ] || fail "--region requires a value"
      BOOTSTRAP_OPTIONS_PROVIDED=true
      REGION=$2
      shift 2
      ;;
    --admin-email)
      [ "$#" -ge 2 ] || fail "--admin-email requires a value"
      BOOTSTRAP_OPTIONS_PROVIDED=true
      ADMIN_EMAIL=$2
      shift 2
      ;;
    --customer-smtp-host)
      [ "$#" -ge 2 ] || fail "--customer-smtp-host requires a value"
      CUSTOMER_SMTP_HOST=$2
      CUSTOMER_SMTP_HOST_PROVIDED=true
      shift 2
      ;;
    --customer-smtp-port)
      [ "$#" -ge 2 ] || fail "--customer-smtp-port requires a value"
      CUSTOMER_SMTP_PORT=$2
      CUSTOMER_SMTP_PORT_PROVIDED=true
      shift 2
      ;;
    --customer-registration-mode)
      [ "$#" -ge 2 ] || fail "--customer-registration-mode requires a value"
      case "$2" in Closed|InvitationOnly|Open) ;; *) fail "--customer-registration-mode must be Closed, InvitationOnly, or Open" ;; esac
      CUSTOMER_REGISTRATION_MODE=$2
      shift 2
      ;;
    --enable-customer-mfa) CUSTOMER_MFA_ENABLED=true; shift ;;
    --disable-customer-mfa) CUSTOMER_MFA_ENABLED=false; shift ;;
    --disable-customer-mail)
      CUSTOMER_MAIL_DISABLE_REQUESTED=true
      shift
      ;;
    --acme-email)
      [ "$#" -ge 2 ] || fail "--acme-email requires a value"
      BOOTSTRAP_OPTIONS_PROVIDED=true
      ACME_EMAIL=$2
      shift 2
      ;;
    --spaceship-dns-credentials)
      [ "$#" -ge 2 ] || fail "--spaceship-dns-credentials requires a directory"
      SPACESHIP_CREDENTIALS_SOURCE=$2
      shift 2
      ;;
    --tls-cert)
      [ "$#" -ge 2 ] || fail "--tls-cert requires a path"
      TLS_CERT_SOURCE=$2
      TLS_IMPORT_REQUESTED=true
      shift 2
      ;;
    --tls-key)
      [ "$#" -ge 2 ] || fail "--tls-key requires a path"
      TLS_KEY_SOURCE=$2
      TLS_IMPORT_REQUESTED=true
      shift 2
      ;;
    --platform-upgrade-keyring)
      [ "$#" -ge 2 ] || fail "--platform-upgrade-keyring requires a path"
      PLATFORM_UPGRADE_KEYRING_SOURCE=$2
      PLATFORM_UPGRADE_TRUST_REQUESTED=true
      shift 2
      ;;
    --platform-upgrade-signer-fingerprint)
      [ "$#" -ge 2 ] || fail "--platform-upgrade-signer-fingerprint requires a value"
      PLATFORM_UPGRADE_SIGNER_FINGERPRINT=$2
      PLATFORM_UPGRADE_TRUST_REQUESTED=true
      shift 2
      ;;
    --status)
      MODE=status
      shift
      ;;
    --rollback)
      MODE=rollback
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      usage >&2
      fail "unknown argument: $1"
      ;;
  esac
done

validate_installer_version
[ "$(id -u)" = "0" ] || fail "run this installer as root"
case "$(uname -m)" in
  x86_64|amd64) ;;
  *) fail "this server bundle currently supports x86_64 Linux only" ;;
esac
umask 077
for tool in awk base64 chmod chown cmp cp dirname docker find flock getent grep mkdir mktemp mv openssl readlink rm sha256sum sh sleep stat sync tail tar tee tr wc; do
  require_command "$tool"
done
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required"
if [ "$MODE" != "install" ] && [ "$BOOTSTRAP" = "true" ]; then
  fail "--bootstrap is valid only for a new installation"
fi
if [ "$BOOTSTRAP" = "false" ] && [ "$BOOTSTRAP_OPTIONS_PROVIDED" = "true" ]; then
  fail "bootstrap-specific options require --bootstrap"
fi
if [ "$TLS_IMPORT_REQUESTED" = "true" ]; then
  [ -n "$TLS_CERT_SOURCE" ] && [ -n "$TLS_KEY_SOURCE" ] \
    || fail "--tls-cert and --tls-key must be provided together"
  [ "$MODE" = "install" ] \
    || fail "TLS import options are valid only during an install or upgrade"
  [ -z "$ACME_EMAIL" ] \
    || fail "choose either ACME or an existing TLS certificate, not both"
fi
if [ -n "$SPACESHIP_CREDENTIALS_SOURCE" ]; then
  [ -n "$ACME_EMAIL" ] || [ "$BOOTSTRAP" = "false" ] \
    || fail "--spaceship-dns-credentials requires --acme-email during bootstrap"
  [ -z "$TLS_CERT_SOURCE" ] && [ -z "$TLS_KEY_SOURCE" ] \
    || fail "Spaceship DNS credentials cannot be combined with TLS import"
  validate_spaceship_credentials_dir "$SPACESHIP_CREDENTIALS_SOURCE" \
    || fail "Spaceship credential directory is missing or unsafe"
fi
if [ "$MODE" != "install" ] \
  && { [ "$CUSTOMER_SMTP_HOST_PROVIDED" = "true" ] \
    || [ "$CUSTOMER_SMTP_PORT_PROVIDED" = "true" ] \
    || [ "$CUSTOMER_MAIL_DISABLE_REQUESTED" = "true" ] \
    || [ -n "$CUSTOMER_REGISTRATION_MODE" ] || [ -n "$CUSTOMER_MFA_ENABLED" ]; }; then
  fail "customer mail migration options are valid only during an install or upgrade"
fi
if [ "$CUSTOMER_SMTP_PORT_PROVIDED" = "true" ] && [ "$CUSTOMER_SMTP_HOST_PROVIDED" = "false" ]; then
  fail "--customer-smtp-port requires --customer-smtp-host"
fi
if [ "$CUSTOMER_MAIL_DISABLE_REQUESTED" = "true" ] \
  && { [ "$CUSTOMER_SMTP_HOST_PROVIDED" = "true" ] || [ "$CUSTOMER_SMTP_PORT_PROVIDED" = "true" ]; }; then
  fail "--disable-customer-mail cannot be combined with customer SMTP options"
fi
if [ "$MODE" != "install" ] && [ "$PLATFORM_UPGRADE_TRUST_REQUESTED" = "true" ]; then
  fail "platform upgrade trust can be configured only during an install or upgrade"
fi
if [ "$PLATFORM_UPGRADE_TRUST_REQUESTED" = "true" ]; then
  [ ! -e "$PLATFORM_UPGRADE_CONFIG" ] && [ ! -e "$PLATFORM_UPGRADE_KEYRING" ] \
    || fail "platform upgrade trust is already configured; rotate it only through an existing trusted updater release"
  validate_platform_upgrade_trust
fi
if [ "$BOOTSTRAP" = "false" ]; then
  check_environment_file
fi
SPACESHIP_CREDENTIALS_DIR="$(dirname "$ENV_FILE")/acme-spaceship"
if [ -n "$SPACESHIP_CREDENTIALS_SOURCE" ]; then
  SPACESHIP_ACTIVE_CREDENTIALS_DIR="$SPACESHIP_CREDENTIALS_SOURCE"
elif validate_spaceship_credentials_dir "$SPACESHIP_CREDENTIALS_DIR"; then
  SPACESHIP_ACTIVE_CREDENTIALS_DIR="$SPACESHIP_CREDENTIALS_DIR"
else
  SPACESHIP_ACTIVE_CREDENTIALS_DIR=""
fi
mkdir -p "$(dirname "$LOCK_FILE")"
exec 9>"$LOCK_FILE"
flock -n 9 || fail "another PeerOnQ install or rollback is running"

case "$MODE" in
  status) show_status; exit 0 ;;
  rollback) rollback; exit 0 ;;
esac

ensure_root_directory "$INSTALL_ROOT" 755
ensure_root_directory "$INSTALL_ROOT/releases" 755
ensure_root_directory "$INSTALL_ROOT/failed-releases" 700
ensure_root_directory "$(dirname "$LOG_ROOT")" 700
ensure_root_directory "$LOG_ROOT" 700

releases_root=$(readlink -f "$INSTALL_ROOT/releases")
target="$releases_root/$INSTALLER_VERSION"
[ ! -L "$target" ] || fail "existing release path must not be a symbolic link: $target"
canonical_target=$(readlink -f "$target") \
  || fail "unable to resolve the embedded release target"
canonical_target_parent=$(dirname "$canonical_target")
[ "$canonical_target_parent" = "$releases_root" ] \
  || fail "embedded release target escaped the releases directory"
temporary=$(mktemp -d "$INSTALL_ROOT/.install-$INSTALLER_VERSION.XXXXXX")
platform_trust_new=false
install_completed=false
cleanup() {
  preserve_temporary=false
  if [ "$TLS_IMPORT_ACTIVE" = "true" ] && [ "$install_completed" != "true" ]; then
    recovery_release=$(current_release 2>/dev/null || true)
    if ! rollback_tls_import "$recovery_release"; then
      preserve_temporary=true
      printf 'CRITICAL: TLS import rollback was incomplete; recovery files remain at %s.\n' \
        "$TLS_IMPORT_TRANSACTION_DIR" >&2
    fi
  fi
  [ "$preserve_temporary" = "true" ] || rm -rf "$temporary"
  if [ "$platform_trust_new" = "true" ] && [ "$install_completed" != "true" ]; then
    set_environment_value PEERONQ_PLATFORM_UPGRADE_ENABLED false 2>/dev/null || true
    rm -f "$PLATFORM_UPGRADE_CONFIG" "$PLATFORM_UPGRADE_KEYRING" 2>/dev/null || true
  fi
}
trap cleanup EXIT HUP INT TERM

payload_line=$(awk -v marker="$PAYLOAD_MARKER" '$0 == marker { print NR + 1; exit }' "$0")
[ -n "$payload_line" ] || fail "embedded payload marker is missing"
tail -n "+$payload_line" "$0" > "$temporary/payload.tar.gz"
actual_sha256=$(sha256sum "$temporary/payload.tar.gz" | awk '{print $1}')
[ "$actual_sha256" = "$PAYLOAD_SHA256" ] || fail "embedded payload integrity check failed"

tar -tzf "$temporary/payload.tar.gz" | awk '
  /^\// { exit 1 }
  /(^|\/)\.\.($|\/)/ { exit 1 }
  END { if (NR == 0) exit 1 }
' || fail "embedded payload contains an unsafe path"
mkdir "$temporary/release"
tar --no-same-owner -xzf "$temporary/payload.tar.gz" -C "$temporary/release"
[ -z "$(find "$temporary/release" -type l -print -quit)" ] || fail "embedded payload must not contain symbolic links"
[ -f "$temporary/release/src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml" ] || fail "staging Compose file is missing"
[ -f "$temporary/release/src/PeerOnQ.Infrastructure.Deployment/docker-compose.production.yml" ] || fail "production Compose override is missing"
embedded_client_validator="$temporary/release/scripts/linux/verify-embedded-windows-client.sh"
[ -f "$embedded_client_validator" ] && [ ! -L "$embedded_client_validator" ] \
  || fail "embedded Windows client validator is missing or unsafe"
sh "$embedded_client_validator" "$temporary/release" \
  || fail "embedded Windows client metadata, artifact, or SHA-256 is missing or invalid"
for updater_file in \
  peeronq-platform-upgrade-agent.sh \
  peeronq-platform-upgrade-agent.service \
  peeronq-platform-upgrade-agent.path
do
  updater_path="$temporary/release/scripts/linux/$updater_file"
  [ -f "$updater_path" ] && [ ! -L "$updater_path" ] \
    || fail "constrained host updater payload is missing or unsafe: $updater_file"
done
spaceship_hook="$temporary/release/scripts/linux/peeronq-spaceship-dns-hook.py"
[ -f "$spaceship_hook" ] && [ ! -L "$spaceship_hook" ] \
  || fail "Spaceship DNS hook payload is missing or unsafe"

if [ "$BOOTSTRAP" = "true" ]; then
  bootstrap_script="$temporary/release/scripts/linux/bootstrap-peeronq-production.sh"
  [ -f "$bootstrap_script" ] || fail "production bootstrap script is missing"
  set -- --env-file "$ENV_FILE" --base-domain "$BASE_DOMAIN" --public-ip "$PUBLIC_IP" \
    --region "$REGION" --admin-email "$ADMIN_EMAIL"
  [ -z "$CUSTOMER_REGISTRATION_MODE" ] || set -- "$@" --customer-registration-mode "$CUSTOMER_REGISTRATION_MODE"
  case "$CUSTOMER_MFA_ENABLED" in true) set -- "$@" --enable-customer-mfa ;; false) set -- "$@" --disable-customer-mfa ;; esac
  if [ -n "$CUSTOMER_SMTP_HOST" ]; then
    set -- "$@" --customer-smtp-host "$CUSTOMER_SMTP_HOST" --customer-smtp-port "$CUSTOMER_SMTP_PORT"
  fi
  [ -z "$LOCAL_IP" ] || set -- "$@" --local-ip "$LOCAL_IP"
  [ -z "$ADMIN_ALLOWED_CIDR" ] || set -- "$@" --admin-allowed-cidr "$ADMIN_ALLOWED_CIDR"
  if [ -n "$ACME_EMAIL" ]; then
    set -- "$@" --acme-email "$ACME_EMAIL"
    [ -z "$SPACESHIP_CREDENTIALS_SOURCE" ] || set -- "$@" --spaceship-dns-credentials "$SPACESHIP_CREDENTIALS_SOURCE"
  else
    set -- "$@" --tls-cert "$TLS_CERT_SOURCE" --tls-key "$TLS_KEY_SOURCE"
  fi
  if [ "$PLATFORM_UPGRADE_TRUST_REQUESTED" = "true" ]; then
    set -- "$@" \
      --platform-upgrade-keyring "$PLATFORM_UPGRADE_KEYRING_SOURCE" \
      --platform-upgrade-signer-fingerprint "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT"
  fi
  sh "$bootstrap_script" "$@"
  check_environment_file
fi

if [ "$DRY_RUN" = "true" ] && [ "$BOOTSTRAP" = "false" ]; then
  protected_env_file=$ENV_FILE
  ENV_FILE="$temporary/peeronq-dry-run.env"
  SCHEMA_DRY_RUN=true
  cp "$protected_env_file" "$ENV_FILE"
  chown 0:0 "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  ensure_environment_schema
  compose "$temporary/release" config --quiet || fail "production configuration validation failed"
  if [ "$TLS_IMPORT_REQUESTED" = "true" ]; then
    validate_tls_import_source \
      || fail "TLS import source is unsafe, invalid, expired, incomplete, or does not match every configured host"
  fi
  ENV_FILE=$protected_env_file
  printf 'Dry run passed for PeerOnQ server release %s; the protected environment was not changed.\n' "$INSTALLER_VERSION"
  exit 0
fi

prepare_platform_upgrade_directories
ensure_environment_schema
if [ "$PLATFORM_UPGRADE_TRUST_REQUESTED" = "true" ]; then
  install_platform_upgrade_trust
  platform_trust_new=true
fi
compose "$temporary/release" config --quiet || fail "production configuration validation failed"
if [ "$TLS_IMPORT_REQUESTED" = "true" ] && [ "$BOOTSTRAP" = "false" ]; then
  validate_tls_import_source \
    || fail "TLS import source is unsafe, invalid, expired, incomplete, or does not match every configured host"
fi
if [ "$DRY_RUN" = "true" ]; then
  printf 'Dry run passed for PeerOnQ server release %s.\n' "$INSTALLER_VERSION"
  exit 0
fi

prepare_managed_tls_permissions
prepare_managed_secret_permissions
if [ -n "$SPACESHIP_CREDENTIALS_SOURCE" ]; then
  install_spaceship_credentials_dir "$SPACESHIP_CREDENTIALS_SOURCE" "$SPACESHIP_CREDENTIALS_DIR" \
    || fail "Spaceship credentials could not be installed safely"
fi
old_current=$(current_release || true)
if [ "$TLS_IMPORT_REQUESTED" = "true" ] && [ "$BOOTSTRAP" = "false" ]; then
  begin_tls_import_transaction \
    || fail "TLS import could not be published safely; the previous certificate and renewal state were retained"
fi
ensure_required_tls_hosts
quarantine_incomplete_release "$target"
mv "$temporary/release" "$target"
begin_install_log
deployment_failed=false
website_overlay_deactivated=false
if [ -z "$old_current" ]; then
  run_compose_step "clear incomplete first-install resources" "$target" down --remove-orphans \
    || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ] && [ -n "$old_current" ] && [ -d "$old_current" ]; then
  run_compose_step "quiesce previous application release without removing data" "$old_current" \
    down --remove-orphans || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  run_compose_step_with_retry "start database and cache prerequisites" "$target" 3 \
    up -d --wait --wait-timeout 180 postgres redis || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  run_compose_step_with_retry "start telemetry prerequisites" "$target" 3 \
    up -d --wait --wait-timeout 180 otel-collector || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  for initializer in release-storage-init diagnostic-storage-init website-storage-init; do
    run_compose_step "preflight $initializer" "$target" \
      run --rm --no-deps "$initializer" || {
        deployment_failed=true
        break
      }
  done
fi
if [ "$deployment_failed" = "false" ]; then
  run_compose_step "apply forward database migrations" "$target" \
    run --rm --build migrations || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  run_compose_step "apply runtime database permissions" "$target" \
    run --rm database-permissions || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  run_compose_step "build and start the complete production stack" "$target" \
    up -d --build --wait --wait-timeout 300 --remove-orphans || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ] && [ "$TLS_IMPORT_ACTIVE" = "true" ]; then
  run_compose_step "activate imported TLS certificate" "$target" \
    up -d --no-deps --force-recreate --wait --wait-timeout 120 proxy turn \
    || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  if transition_website_overlay deactivate "$target" "deactivate superseded website overlay"; then
    website_overlay_deactivated=true
  else
    deployment_failed=true
  fi
fi
if [ "$deployment_failed" = "false" ]; then
  failed_step="verify embedded Windows client payload and publication"
  verify_embedded_windows_client "$target" || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  verify_public_control_plane "$target" || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ] && grep -Fx 'PEERONQ_ADMIN_BOOTSTRAP_ENABLED=true' "$ENV_FILE" >/dev/null 2>&1; then
  scrub_admin_bootstrap
  run_compose_step "disable one-time Admin bootstrap credentials" "$target" \
    up -d --force-recreate --wait --wait-timeout 120 admin-api || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ]; then
  failed_step="install constrained host platform upgrade agent"
  install_platform_upgrade_agent "$target" || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ] && [ "$TLS_IMPORT_ACTIVE" != "true" ]; then
  failed_step="refresh managed TLS renewal command"
  refresh_tls_renewal_command "$target" || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ] && [ "$TLS_IMPORT_ACTIVE" = "true" ]; then
  failed_step="retire superseded HTTP-01 TLS renewal management"
  retire_tls_renewal_management || deployment_failed=true
fi
if [ "$deployment_failed" = "false" ] && [ "$website_overlay_deactivated" = "true" ]; then
  transition_website_overlay commit "$target" "commit base website activation" \
    || deployment_failed=true
  [ "$deployment_failed" = "true" ] || website_overlay_deactivated=false
fi

if [ "$deployment_failed" = "true" ]; then
  deployment_failure_step=$failed_step
  if [ "$website_overlay_deactivated" = "true" ]; then
    transition_website_overlay restore "$target" "restore website overlay after failed deployment" || true
  fi
  failed_step=$deployment_failure_step
  tls_rollback_failed=false
  if [ "$TLS_IMPORT_ACTIVE" = "true" ]; then
    rollback_tls_import "$old_current" || tls_rollback_failed=true
  fi
  if [ "$platform_trust_new" = "true" ]; then
    set_environment_value PEERONQ_PLATFORM_UPGRADE_ENABLED false
    rm -f "$PLATFORM_UPGRADE_CONFIG" "$PLATFORM_UPGRADE_KEYRING"
    platform_trust_new=false
  fi
  capture_failure_diagnostics "$target"
  if [ -n "$old_current" ] && [ -d "$old_current" ]; then
    printf 'Deployment failed; restoring the previous application release.\n' >&2
    compose "$old_current" up -d --build --wait --wait-timeout 300 --remove-orphans || true
  else
    printf 'Deployment failed; removing incomplete containers and networks while preserving volumes.\n' >&2
    compose "$target" down --remove-orphans || true
  fi
  [ "$tls_rollback_failed" = "false" ] \
    || fail "deployment and TLS rollback both failed; recovery files were preserved for manual restoration"
  fail "deployment failed; the new release was retained for investigation"
fi

if [ -n "$old_current" ] && [ -d "$old_current" ] && [ "$old_current" != "$target" ]; then
  set_link "$INSTALL_ROOT/previous" "$old_current"
fi
set_link "$INSTALL_ROOT/current" "$target"
if [ "$platform_trust_new" = "true" ]; then
  "$PLATFORM_UPGRADE_AGENT" --refresh-idle
fi
TLS_IMPORT_ACTIVE=false
install_completed=true
start_pending_platform_upgrade_request \
  || fail "the release is active, but the queued platform upgrade agent could not be started"
rm -f "$install_log"
printf 'PeerOnQ server release %s is healthy and active.\n' "$INSTALLER_VERSION"
exit 0

__PEERONQ_PAYLOAD_BELOW__
