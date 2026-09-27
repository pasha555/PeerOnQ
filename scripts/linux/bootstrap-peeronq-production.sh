#!/bin/sh
set -eu

ENV_FILE="/etc/peeronq/peeronq.env"
BASE_DOMAIN="peeronq.com"
PUBLIC_IP="31.171.38.28"
LOCAL_IP=""
ADMIN_ALLOWED_CIDR=""
REGION="az-1"
ADMIN_EMAIL="admin@peeronq.com"
CUSTOMER_SMTP_HOST=""
CUSTOMER_SMTP_PORT=587
CUSTOMER_SMTP_PORT_PROVIDED=false
ACME_EMAIL=""
TLS_CERT_SOURCE=""
TLS_KEY_SOURCE=""
SPACESHIP_CREDENTIALS_SOURCE=""
ACME_DNS_PROPAGATION_SECONDS=180
PLATFORM_UPGRADE_KEYRING_SOURCE=""
PLATFORM_UPGRADE_SIGNER_FINGERPRINT=""
PLATFORM_UPGRADE_ENABLED=false
CERTBOT_IMAGE="certbot/certbot:v5.7.0@sha256:34ee91d2f43008eb78a007d22f23ed4b2eaa9a454cb27ca2c042b49527a695b4"
SCRIPT_DIR=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)

usage() {
  cat <<'EOF'
Usage: bootstrap-peeronq-production.sh [options]

Required TLS choice (select one):
  --acme-email EMAIL       Obtain a public certificate with Let's Encrypt. HTTP-01 is
                           used unless --spaceship-dns-credentials is supplied.
                           Every certificate DNS name, including the internal surfaces,
                           must resolve to the public IP and TCP 80 must reach this server
                           during issuance and renewal. Admin/Grafana/Prometheus stay CIDR-restricted.
  --tls-cert PATH --tls-key PATH
                            Import an existing full certificate chain and private key.
  --spaceship-dns-credentials DIR
                            Root-only directory containing api-key and api-secret;
                            enables Spaceship DNS-01 for peeronq.com and *.peeronq.com.
  --acme-dns-propagation-seconds N
                            Maximum authoritative TXT propagation wait (default: 180).

Options:
  --env-file PATH          Root-owned environment file to create.
  --base-domain DOMAIN     Default: peeronq.com
  --public-ip IPV4         Default: 31.171.38.28
  --local-ip IPV4          Server LAN IP; detected from the default route when omitted.
  --admin-allowed-cidr CIDR Internal Admin/Grafana/Prometheus source allowlist; Portal is public.
                           Default: the local IP's /24 LAN.
  --region REGION          Default: az-1
  --admin-email EMAIL      Initial Admin Panel login email.
  --customer-smtp-host HOST Optional; enables customer SMTP delivery.
  --customer-smtp-port PORT Default: 587 when SMTP is enabled.
  --platform-upgrade-keyring PATH
  --platform-upgrade-signer-fingerprint HEX
                           Enable the constrained host updater with an exact,
                           operator-verified OpenPGP signer fingerprint.

The script creates strong independent secrets and stores the one-time Admin MFA
onboarding record in a root-only file. It never prints credentials.
EOF
}

fail() {
  printf 'PeerOnQ bootstrap: %s\n' "$1" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command is missing: $1"
}

validate_spaceship_credentials_dir() {
  credentials_dir=$1
  [ -d "$credentials_dir" ] && [ ! -L "$credentials_dir" ] || fail "Spaceship credential directory is missing or unsafe"
  [ "$(readlink -f "$credentials_dir")" = "$credentials_dir" ] || fail "Spaceship credential directory must be canonical"
  [ "$(stat -c '%u:%a' "$credentials_dir")" = "0:700" ] || fail "Spaceship credential directory must be root-owned mode 0700"
  for credential_name in api-key api-secret; do
    credential_file="$credentials_dir/$credential_name"
    [ -f "$credential_file" ] && [ ! -L "$credential_file" ] || fail "Spaceship credential is missing: $credential_name"
    [ "$(readlink -f "$credential_file")" = "$credential_file" ] || fail "Spaceship credential must be canonical"
    case "$(stat -c '%u:%h:%a' "$credential_file")" in 0:1:400|0:1:600) ;; *) fail "Spaceship credential permissions are unsafe: $credential_name" ;; esac
    value=$(tr -d '\r\n' < "$credential_file")
    [ -n "$value" ] && [ "$(printf '%s' "$value" | wc -c)" -ge 16 ] && [ "$(printf '%s' "$value" | wc -c)" -le 512 ] \
      || fail "Spaceship credential length is invalid: $credential_name"
    case "$value" in *[!A-Za-z0-9_-]*) fail "Spaceship credential contains unsafe characters: $credential_name" ;; esac
    [ "$(wc -l < "$credential_file")" -le 1 ] || fail "Spaceship credential must be one line: $credential_name"
  done
}

install_spaceship_credentials() {
  source_dir=$1
  destination_dir=$2
  validate_spaceship_credentials_dir "$source_dir"
  mkdir -p "$destination_dir"
  chmod 700 "$destination_dir"
  chown 0:0 "$destination_dir"
  for credential_name in api-key api-secret; do
    cp "$source_dir/$credential_name" "$destination_dir/$credential_name"
    chown 0:0 "$destination_dir/$credential_name"
    chmod 400 "$destination_dir/$credential_name"
  done
}

write_tls_renewal_units() {
  renewal_service=$1
  renewal_timer=$2
  environment_file=$3
  cat > "$renewal_service" <<EOF
[Unit]
Description=Renew PeerOnQ public TLS certificate
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
ReadWritePaths=/etc/letsencrypt $(dirname "$environment_file")/tls /run/lock
TimeoutStartSec=15min
ExecStart=/usr/local/sbin/peeronq-renew-tls --env-file $environment_file
EOF
  cat > "$renewal_timer" <<'EOF'
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

random_hex() {
  openssl rand -hex "$1"
}

random_base64() {
  openssl rand -base64 "$1" | tr -d '\r\n'
}

random_base32() {
  openssl rand 20 | base32 | tr -d '=\r\n'
}

write_secret() {
  target=$1
  value=$2
  printf '%s\n' "$value" > "$target"
  chmod 600 "$target"
}

prepare_managed_secret_permissions() {
  managed_secrets_dir=$1
  chown 0:1654 \
    "$managed_secrets_dir/alertmanager-webhook-token" \
    "$managed_secrets_dir/admin-data-protection-password" \
    "$managed_secrets_dir/admin-data-protection.pfx" \
    "$managed_secrets_dir/signaling-attestation-private.pem" \
    "$managed_secrets_dir/signaling-attestation-public.pem"
  chmod 640 \
    "$managed_secrets_dir/alertmanager-webhook-token" \
    "$managed_secrets_dir/admin-data-protection-password" \
    "$managed_secrets_dir/admin-data-protection.pfx" \
    "$managed_secrets_dir/signaling-attestation-private.pem" \
    "$managed_secrets_dir/signaling-attestation-public.pem"
  chown 0:65534 "$managed_secrets_dir/turn-shared-secret"
  chmod 640 "$managed_secrets_dir/turn-shared-secret"
  chown 0:0 \
    "$managed_secrets_dir/grafana-admin-password" \
    "$managed_secrets_dir/postgres-backup.pgpass" \
    "$managed_secrets_dir/postgres-restore.pgpass"
  chmod 640 "$managed_secrets_dir/grafana-admin-password"
  chmod 600 \
    "$managed_secrets_dir/postgres-backup.pgpass" \
    "$managed_secrets_dir/postgres-restore.pgpass"
}

validate_domain() {
  value=$1
  case "$value" in
    *[!a-z0-9.-]*|.*|*..*|*.-*|*-.*|*.) fail "base domain is invalid" ;;
  esac
  [ "${#value}" -le 253 ] || fail "base domain is too long"
}

validate_smtp_host() {
  value=$1
  case "$value" in
    *[!a-z0-9.-]*|.*|*..*|*.-*|*-.*|*.) fail "customer SMTP host is invalid" ;;
  esac
  [ "${#value}" -le 253 ] || fail "customer SMTP host is too long"
}

validate_smtp_port() {
  value=$1
  case "$value" in *[!0-9]*|'') fail "customer SMTP port is invalid" ;; esac
  [ "${#value}" -le 5 ] && [ "$value" -ge 1 ] && [ "$value" -le 65535 ] \
    || fail "customer SMTP port must be between 1 and 65535"
}

validate_ipv4() {
  printf '%s\n' "$1" | awk -F. '
    NF != 4 { exit 1 }
    {
      for (i = 1; i <= 4; i++) {
        if ($i !~ /^[0-9]+$/ || $i < 0 || $i > 255) exit 1
      }
    }
  ' || fail "public IP must be a valid IPv4 address"
}

validate_ipv4_cidr() {
  printf '%s\n' "$1" | awk -F/ '
    NF != 2 || $2 !~ /^[0-9]+$/ || $2 < 8 || $2 > 32 { exit 1 }
    {
      count = split($1, octets, ".")
      if (count != 4) exit 1
      for (i = 1; i <= 4; i++) {
        if (octets[i] !~ /^[0-9]+$/ || octets[i] < 0 || octets[i] > 255) exit 1
      }
    }
  ' || fail "admin allowed CIDR must be a valid IPv4 /8-/32 network"
}

validate_email() {
  case "$1" in
    *[!A-Za-z0-9._%+@-]*|*@*@*|@*|*@|*..*) fail "email address is invalid" ;;
  esac
  printf '%s\n' "$1" | grep -Eq '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,63}$' \
    || fail "email address is invalid"
}

validate_region() {
  case "$1" in
    *[!a-z0-9-]*|''|-*|*-) fail "region must contain lowercase letters, numbers, and hyphens" ;;
  esac
  [ "${#1}" -ge 2 ] && [ "${#1}" -le 32 ] || fail "region must contain 2-32 characters"
}

check_dns() {
  for name in \
    "$BASE_DOMAIN" "www.$BASE_DOMAIN" "api.$BASE_DOMAIN" "portal.$BASE_DOMAIN" \
    "admin.$BASE_DOMAIN" "grafana.$BASE_DOMAIN" "prometheus.$BASE_DOMAIN" \
    "download.$BASE_DOMAIN" "updates.$BASE_DOMAIN" "presence.$BASE_DOMAIN" \
    "signal.$BASE_DOMAIN" "turn.$BASE_DOMAIN"
  do
    getent ahostsv4 "$name" | awk '{ print $1 }' | grep -Fx "$PUBLIC_IP" >/dev/null 2>&1 \
      || fail "$name does not resolve to $PUBLIC_IP"
  done
}

validate_tls_pair() {
  cert=$1
  key=$2
  [ -f "$cert" ] && [ ! -L "$cert" ] || fail "TLS certificate must be a regular non-symlink file"
  [ -f "$key" ] && [ ! -L "$key" ] || fail "TLS private key must be a regular non-symlink file"
  openssl x509 -in "$cert" -noout -checkend 86400 >/dev/null \
    || fail "TLS certificate is invalid or expires in less than 24 hours"
  for name in \
    "$BASE_DOMAIN" "www.$BASE_DOMAIN" "api.$BASE_DOMAIN" "portal.$BASE_DOMAIN" \
    "admin.$BASE_DOMAIN" "grafana.$BASE_DOMAIN" "prometheus.$BASE_DOMAIN" \
    "download.$BASE_DOMAIN" "updates.$BASE_DOMAIN" "presence.$BASE_DOMAIN" \
    "signal.$BASE_DOMAIN" "turn.$BASE_DOMAIN"
  do
    openssl verify -no-CAfile -no-CApath -trusted "$cert" -partial_chain \
      -verify_hostname "$name" "$cert" >/dev/null 2>&1 \
      || fail "TLS certificate does not cover $name"
  done
  cert_hash=$(openssl x509 -in "$cert" -pubkey -noout | openssl pkey -pubin -outform DER 2>/dev/null | sha256sum | awk '{ print $1 }')
  key_hash=$(openssl pkey -in "$key" -pubout -outform DER 2>/dev/null | sha256sum | awk '{ print $1 }')
  [ -n "$cert_hash" ] && [ "$cert_hash" = "$key_hash" ] || fail "TLS certificate and private key do not match"
}

run_spaceship_acme() {
  acme_root=$1
  credentials_dir=$2
  propagation_seconds=$3
  hook_source="$SCRIPT_DIR/peeronq-spaceship-dns-hook.py"
  [ -f "$hook_source" ] && [ ! -L "$hook_source" ] || fail "Spaceship DNS hook is missing or unsafe"
  docker run --rm --read-only --cap-drop=ALL --security-opt no-new-privileges \
    --tmpfs /tmp:rw,nosuid,nodev,noexec,size=64m \
    --tmpfs /var/lib/letsencrypt:rw,nosuid,nodev,noexec,size=64m \
    --tmpfs /var/log/letsencrypt:rw,nosuid,nodev,noexec,size=16m \
    -e "PEERONQ_DNS_PROPAGATION_SECONDS=$propagation_seconds" \
    -v "$acme_root:/etc/letsencrypt:rw" \
    -v "$credentials_dir:/run/secrets/peeronq-spaceship:ro" \
    -v "$hook_source:/opt/peeronq/peeronq-spaceship-dns-hook.py:ro" \
    "$CERTBOT_IMAGE" certonly --manual --preferred-challenges dns \
    --manual-auth-hook "/usr/local/bin/python3 /opt/peeronq/peeronq-spaceship-dns-hook.py auth" \
    --manual-cleanup-hook "/usr/local/bin/python3 /opt/peeronq/peeronq-spaceship-dns-hook.py cleanup" \
    --no-directory-hooks --non-interactive --agree-tos --email "$ACME_EMAIL" \
    --cert-name "$BASE_DOMAIN-dns01" --keep-until-expiring --renew-with-new-domains \
    -d "$BASE_DOMAIN" -d "*.$BASE_DOMAIN"
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --env-file) [ "$#" -ge 2 ] || fail "--env-file requires a path"; ENV_FILE=$2; shift 2 ;;
    --base-domain) [ "$#" -ge 2 ] || fail "--base-domain requires a value"; BASE_DOMAIN=$2; shift 2 ;;
    --public-ip) [ "$#" -ge 2 ] || fail "--public-ip requires a value"; PUBLIC_IP=$2; shift 2 ;;
    --local-ip) [ "$#" -ge 2 ] || fail "--local-ip requires a value"; LOCAL_IP=$2; shift 2 ;;
    --admin-allowed-cidr) [ "$#" -ge 2 ] || fail "--admin-allowed-cidr requires a value"; ADMIN_ALLOWED_CIDR=$2; shift 2 ;;
    --region) [ "$#" -ge 2 ] || fail "--region requires a value"; REGION=$2; shift 2 ;;
    --admin-email) [ "$#" -ge 2 ] || fail "--admin-email requires a value"; ADMIN_EMAIL=$2; shift 2 ;;
    --customer-smtp-host) [ "$#" -ge 2 ] || fail "--customer-smtp-host requires a value"; CUSTOMER_SMTP_HOST=$2; shift 2 ;;
    --customer-smtp-port) [ "$#" -ge 2 ] || fail "--customer-smtp-port requires a value"; CUSTOMER_SMTP_PORT=$2; CUSTOMER_SMTP_PORT_PROVIDED=true; shift 2 ;;
    --acme-email) [ "$#" -ge 2 ] || fail "--acme-email requires a value"; ACME_EMAIL=$2; shift 2 ;;
    --spaceship-dns-credentials) [ "$#" -ge 2 ] || fail "--spaceship-dns-credentials requires a directory"; SPACESHIP_CREDENTIALS_SOURCE=$2; shift 2 ;;
    --acme-dns-propagation-seconds) [ "$#" -ge 2 ] || fail "--acme-dns-propagation-seconds requires a value"; ACME_DNS_PROPAGATION_SECONDS=$2; shift 2 ;;
    --tls-cert) [ "$#" -ge 2 ] || fail "--tls-cert requires a path"; TLS_CERT_SOURCE=$2; shift 2 ;;
    --tls-key) [ "$#" -ge 2 ] || fail "--tls-key requires a path"; TLS_KEY_SOURCE=$2; shift 2 ;;
    --platform-upgrade-keyring) [ "$#" -ge 2 ] || fail "--platform-upgrade-keyring requires a path"; PLATFORM_UPGRADE_KEYRING_SOURCE=$2; shift 2 ;;
    --platform-upgrade-signer-fingerprint) [ "$#" -ge 2 ] || fail "--platform-upgrade-signer-fingerprint requires a value"; PLATFORM_UPGRADE_SIGNER_FINGERPRINT=$2; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) usage >&2; fail "unknown argument: $1" ;;
  esac
done

[ "$(id -u)" = "0" ] || fail "run this bootstrap as root"
umask 077
for tool in awk base32 base64 cp docker getent grep ip openssl readlink sha256sum stat tr wc; do
  require_command "$tool"
done
docker version >/dev/null 2>&1 || fail "Docker Engine is required"

if [ -n "$PLATFORM_UPGRADE_KEYRING_SOURCE" ] || [ -n "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT" ]; then
  [ -n "$PLATFORM_UPGRADE_KEYRING_SOURCE" ] && [ -n "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT" ] \
    || fail "platform upgrade keyring and signer fingerprint must be provided together"
  case "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT" in
    *[!0-9A-Fa-f]*|'') fail "platform upgrade signer fingerprint must be hexadecimal" ;;
  esac
  case "${#PLATFORM_UPGRADE_SIGNER_FINGERPRINT}" in 40|64) ;; *) fail "platform upgrade signer fingerprint must contain exactly 40 or 64 hexadecimal characters" ;; esac
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
  PLATFORM_UPGRADE_ENABLED=true
fi

validate_domain "$BASE_DOMAIN"
validate_ipv4 "$PUBLIC_IP"
if [ -z "$LOCAL_IP" ]; then
  LOCAL_IP=$(ip -4 route get 1.1.1.1 | awk '{ for (i = 1; i <= NF; i++) if ($i == "src") { print $(i + 1); exit } }')
  [ -n "$LOCAL_IP" ] || fail "could not detect the server LAN IP; provide --local-ip"
fi
validate_ipv4 "$LOCAL_IP"
if [ -z "$ADMIN_ALLOWED_CIDR" ]; then
  ADMIN_ALLOWED_CIDR=$(printf '%s\n' "$LOCAL_IP" | awk -F. '{ print $1 "." $2 "." $3 ".0/24" }')
fi
validate_ipv4_cidr "$ADMIN_ALLOWED_CIDR"
validate_region "$REGION"
validate_email "$ADMIN_EMAIL"
CUSTOMER_MAIL_PROVIDER=Disabled
CUSTOMER_REQUIRE_EMAIL_VERIFICATION=false
if [ "$CUSTOMER_SMTP_PORT_PROVIDED" = "true" ] && [ -z "$CUSTOMER_SMTP_HOST" ]; then
  fail "--customer-smtp-port requires --customer-smtp-host"
fi
if [ -n "$CUSTOMER_SMTP_HOST" ]; then
  validate_smtp_host "$CUSTOMER_SMTP_HOST"
  validate_smtp_port "$CUSTOMER_SMTP_PORT"
  CUSTOMER_MAIL_PROVIDER=Smtp
  CUSTOMER_REQUIRE_EMAIL_VERIFICATION=true
fi

case "$ENV_FILE" in /*) ;; *) fail "environment file path must be absolute" ;; esac
case "$ENV_FILE" in
  *[!A-Za-z0-9_./-]*|*/|*//*|*/./*|*/.|*/../*|*/..)
    fail "environment file path must be normalized and contain only letters, numbers, '.', '_', '-' and '/'"
    ;;
esac
[ ! -e "$ENV_FILE" ] || fail "refusing to overwrite existing environment file: $ENV_FILE"
[ ! -L "$(dirname "$ENV_FILE")" ] || fail "environment directory must not be a symbolic link"

case "$ACME_DNS_PROPAGATION_SECONDS" in ''|*[!0-9]*) fail "ACME DNS propagation seconds must be an integer" ;; esac
[ "$ACME_DNS_PROPAGATION_SECONDS" -ge 30 ] && [ "$ACME_DNS_PROPAGATION_SECONDS" -le 900 ] \
  || fail "ACME DNS propagation seconds must be between 30 and 900"

if [ -n "$ACME_EMAIL" ]; then
  validate_email "$ACME_EMAIL"
  [ -z "$TLS_CERT_SOURCE" ] && [ -z "$TLS_KEY_SOURCE" ] \
    || fail "choose either ACME or an existing TLS certificate, not both"
else
  [ -n "$TLS_CERT_SOURCE" ] && [ -n "$TLS_KEY_SOURCE" ] \
    || fail "provide --acme-email or both --tls-cert and --tls-key"
fi
[ -z "$SPACESHIP_CREDENTIALS_SOURCE" ] || [ -n "$ACME_EMAIL" ] \
  || fail "Spaceship DNS credentials require --acme-email"
[ -z "$SPACESHIP_CREDENTIALS_SOURCE" ] || [ "$BASE_DOMAIN" = "peeronq.com" ] \
  || fail "Spaceship DNS automation currently supports peeronq.com only"

config_root=$(dirname "$ENV_FILE")
secrets_dir="$config_root/secrets"
tls_dir="$config_root/tls"
signing_dir="$config_root/pilot-update-signing"
website_signing_dir="$config_root/pilot-website-signing"
spaceship_credentials_dir="$config_root/acme-spaceship"
backup_dir="/var/backups/peeronq"
platform_upgrade_root="/var/lib/peeronq/platform-upgrade"
onboarding_file="$config_root/admin-onboarding.txt"
mkdir -p "$config_root" "$secrets_dir" "$tls_dir" "$signing_dir" "$website_signing_dir" "$backup_dir" \
  "$platform_upgrade_root/inbox" "$platform_upgrade_root/status" \
  "$platform_upgrade_root/processing" "$platform_upgrade_root/archive" \
  "/var/log/peeronq/platform-upgrade"
[ "$(readlink -f "$config_root")" = "$config_root" ] \
  || fail "environment directory path must not traverse symbolic links"
chmod 700 "$config_root" "$secrets_dir" "$tls_dir" "$signing_dir" "$website_signing_dir" "$backup_dir"
chown 0:0 "$platform_upgrade_root" "$platform_upgrade_root/processing" "$platform_upgrade_root/archive" \
  "/var/log/peeronq/platform-upgrade"
chmod 750 "$platform_upgrade_root"
chmod 700 "$platform_upgrade_root/processing" "$platform_upgrade_root/archive" \
  "/var/log/peeronq/platform-upgrade"
chown 0:1654 "$platform_upgrade_root/inbox" "$platform_upgrade_root/status"
chmod 1730 "$platform_upgrade_root/inbox"
chmod 750 "$platform_upgrade_root/status"

if [ "$PLATFORM_UPGRADE_ENABLED" = "true" ]; then
  [ ! -e "$config_root/platform-upgrade-trustedkeys.gpg" ] \
    && [ ! -e "$config_root/platform-upgrade.conf" ] \
    || fail "platform upgrade trust is already configured"
  cp "$PLATFORM_UPGRADE_KEYRING_SOURCE" "$config_root/platform-upgrade-trustedkeys.gpg"
  {
    printf 'keyring=/etc/peeronq/platform-upgrade-trustedkeys.gpg\n'
    printf 'signer_fingerprint=%s\n' "$PLATFORM_UPGRADE_SIGNER_FINGERPRINT"
  } > "$config_root/platform-upgrade.conf"
  chown 0:0 "$config_root/platform-upgrade-trustedkeys.gpg" "$config_root/platform-upgrade.conf"
  chmod 400 "$config_root/platform-upgrade-trustedkeys.gpg" "$config_root/platform-upgrade.conf"
fi

if [ -n "$ACME_EMAIL" ]; then
  acme_root="/etc/letsencrypt"
  mkdir -p "$acme_root"
  if [ -n "$SPACESHIP_CREDENTIALS_SOURCE" ]; then
    validate_spaceship_credentials_dir "$SPACESHIP_CREDENTIALS_SOURCE"
    run_spaceship_acme "$acme_root" "$SPACESHIP_CREDENTIALS_SOURCE" "$ACME_DNS_PROPAGATION_SECONDS"
    TLS_CERT_SOURCE="$acme_root/live/$BASE_DOMAIN-dns01/fullchain.pem"
    TLS_KEY_SOURCE="$acme_root/live/$BASE_DOMAIN-dns01/privkey.pem"
  else
    check_dns
    docker run --rm -p 80:80 -v "$acme_root:/etc/letsencrypt" "$CERTBOT_IMAGE" \
      certonly --standalone --non-interactive --agree-tos --email "$ACME_EMAIL" \
      --cert-name "$BASE_DOMAIN" --keep-until-expiring \
      -d "$BASE_DOMAIN" -d "www.$BASE_DOMAIN" -d "api.$BASE_DOMAIN" \
      -d "portal.$BASE_DOMAIN" -d "admin.$BASE_DOMAIN" \
      -d "grafana.$BASE_DOMAIN" -d "prometheus.$BASE_DOMAIN" -d "download.$BASE_DOMAIN" \
      -d "updates.$BASE_DOMAIN" \
      -d "presence.$BASE_DOMAIN" -d "signal.$BASE_DOMAIN" -d "turn.$BASE_DOMAIN"
    TLS_CERT_SOURCE="$acme_root/live/$BASE_DOMAIN/fullchain.pem"
    TLS_KEY_SOURCE="$acme_root/live/$BASE_DOMAIN/privkey.pem"
  fi
fi

TLS_CERT_SOURCE=$(readlink -f "$TLS_CERT_SOURCE")
TLS_KEY_SOURCE=$(readlink -f "$TLS_KEY_SOURCE")
validate_tls_pair "$TLS_CERT_SOURCE" "$TLS_KEY_SOURCE"
cp "$TLS_CERT_SOURCE" "$tls_dir/fullchain.pem"
cp "$TLS_KEY_SOURCE" "$tls_dir/privkey.pem"
if [ -n "$SPACESHIP_CREDENTIALS_SOURCE" ]; then
  install_spaceship_credentials "$SPACESHIP_CREDENTIALS_SOURCE" "$spaceship_credentials_dir"
  mkdir -p /usr/local/libexec
  cp "$SCRIPT_DIR/peeronq-spaceship-dns-hook.py" /usr/local/libexec/peeronq-spaceship-dns-hook.py
  chown 0:0 /usr/local/libexec/peeronq-spaceship-dns-hook.py
  chmod 755 /usr/local/libexec/peeronq-spaceship-dns-hook.py
fi
chown 0:65534 "$tls_dir" "$tls_dir/fullchain.pem" "$tls_dir/privkey.pem"
chmod 750 "$tls_dir"
chmod 640 "$tls_dir/fullchain.pem" "$tls_dir/privkey.pem"

postgres_bootstrap=$(random_hex 32)
postgres_migrator=$(random_hex 32)
cloud_db=$(random_hex 32)
presence_db=$(random_hex 32)
admin_db=$(random_hex 32)
downloads_db=$(random_hex 32)
backup_db=$(random_hex 32)
restore_db=$(random_hex 32)
redis_cloud=$(random_hex 32)
redis_presence=$(random_hex 32)
redis_presence_reader=$(random_hex 32)
redis_admin=$(random_hex 32)
redis_downloads=$(random_hex 32)
redis_signaling=$(random_hex 32)
redis_health=$(random_hex 32)
grafana_password=$(random_hex 24)
cloud_hmac=$(random_base64 32)
admin_hmac=$(random_base64 32)
downloads_hmac=$(random_base64 32)
admin_token=$(random_base64 48)
customer_token=$(random_base64 48)
admin_refresh=$(random_base64 48)
download_token=$(random_base64 48)
turn_secret=$(random_base64 48)
diagnostics_token=$(random_base64 48)
alert_token=$(random_base64 48)
data_protection_password=$(random_hex 32)
admin_password=$(random_hex 24)
admin_totp=$(random_base32)
release_key_id="peeronq-public-pilot-1"
website_key_id="peeronq-website-pilot-1"

write_secret "$secrets_dir/alertmanager-webhook-token" "$alert_token"
write_secret "$secrets_dir/grafana-admin-password" "$grafana_password"
write_secret "$secrets_dir/turn-shared-secret" "$turn_secret"
printf 'postgres:5432:peeronq_cloud:peeronq_backup:%s\n' "$backup_db" > "$secrets_dir/postgres-backup.pgpass"
printf 'postgres:5432:*:peeronq_restore_operator:%s\n' "$restore_db" > "$secrets_dir/postgres-restore.pgpass"
chmod 600 "$secrets_dir/postgres-backup.pgpass" "$secrets_dir/postgres-restore.pgpass"

openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 \
  -out "$secrets_dir/signaling-attestation-private.pem" >/dev/null 2>&1
openssl pkey -in "$secrets_dir/signaling-attestation-private.pem" -pubout \
  -out "$secrets_dir/signaling-attestation-public.pem" >/dev/null 2>&1
chmod 600 "$secrets_dir/signaling-attestation-private.pem"
chmod 644 "$secrets_dir/signaling-attestation-public.pem"

dp_key="$secrets_dir/.admin-data-protection-key.pem"
dp_cert="$secrets_dir/.admin-data-protection-cert.pem"
openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 3650 \
  -subj '/CN=PeerOnQ Admin Data Protection/' -keyout "$dp_key" -out "$dp_cert" >/dev/null 2>&1
write_secret "$secrets_dir/admin-data-protection-password" "$data_protection_password"
openssl pkcs12 -export -out "$secrets_dir/admin-data-protection.pfx" \
  -inkey "$dp_key" -in "$dp_cert" -passout "pass:$data_protection_password" >/dev/null 2>&1
rm -f "$dp_key" "$dp_cert"
chmod 600 "$secrets_dir/admin-data-protection.pfx"
prepare_managed_secret_permissions "$secrets_dir"

openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 \
  -out "$signing_dir/private-key.pem" >/dev/null 2>&1
openssl pkey -in "$signing_dir/private-key.pem" -pubout \
  -out "$signing_dir/public-key.pem" >/dev/null 2>&1
chmod 400 "$signing_dir/private-key.pem"
chmod 644 "$signing_dir/public-key.pem"
release_public_key=$(openssl pkey -pubin -in "$signing_dir/public-key.pem" -outform DER 2>/dev/null | base64 | tr -d '\r\n')

openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 \
  -out "$website_signing_dir/private-key.pem" >/dev/null 2>&1
openssl pkey -in "$website_signing_dir/private-key.pem" -pubout \
  -out "$website_signing_dir/public-key.pem" >/dev/null 2>&1
chmod 400 "$website_signing_dir/private-key.pem"
chmod 644 "$website_signing_dir/public-key.pem"
website_public_key=$(openssl pkey -pubin -in "$website_signing_dir/public-key.pem" -outform DER 2>/dev/null | base64 | tr -d '\r\n')

script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(readlink -f "$script_dir/../..")
cp "$repo_root/src/PeerOnQ.Infrastructure.Deployment/observability/alertmanager.development.yml" \
  "$config_root/alertmanager.yml"
chmod 644 "$config_root/alertmanager.yml"

recovery_1=$(random_hex 8)
recovery_2=$(random_hex 8)
recovery_3=$(random_hex 8)
recovery_4=$(random_hex 8)
recovery_5=$(random_hex 8)
recovery_6=$(random_hex 8)
recovery_7=$(random_hex 8)
recovery_8=$(random_hex 8)

cat > "$ENV_FILE" <<EOF
PEERONQ_REGION=$REGION
PEERONQ_API_HOST=api.$BASE_DOMAIN
PEERONQ_PORTAL_HOST=portal.$BASE_DOMAIN
PEERONQ_ADMIN_HOST=admin.$BASE_DOMAIN
PEERONQ_GRAFANA_HOST=grafana.$BASE_DOMAIN
PEERONQ_PROMETHEUS_HOST=prometheus.$BASE_DOMAIN
PEERONQ_ADMIN_ALLOWED_CIDR=$ADMIN_ALLOWED_CIDR
PEERONQ_DOWNLOAD_HOST=download.$BASE_DOMAIN
PEERONQ_WEB_HOST=$BASE_DOMAIN
PEERONQ_WEB_WWW_HOST=www.$BASE_DOMAIN
PEERONQ_UPDATE_HOST=updates.$BASE_DOMAIN
PEERONQ_PRESENCE_HOST=presence.$BASE_DOMAIN
PEERONQ_SIGNAL_HOST=signal.$BASE_DOMAIN
PEERONQ_TURN_PUBLIC_HOST=turn.$BASE_DOMAIN
PEERONQ_TURN_REALM=turn.$BASE_DOMAIN
PEERONQ_SIGNAL_SERVER_ID=signal-$REGION-1
PEERONQ_SIGNALING_INSTANCE_ID=signaling-$REGION-1
PEERONQ_SIGNALING_ATTESTATION_ISSUER=https://api.$BASE_DOMAIN
PEERONQ_SIGNALING_ATTESTATION_AUDIENCE=peeronq-signaling
PEERONQ_SIGNALING_ATTESTATION_LIFETIME=00:05:00
PEERONQ_SIGNALING_ATTESTATION_MAXIMUM_LIFETIME=00:15:00
PEERONQ_SIGNALING_ATTESTATION_CLOCK_SKEW=00:00:30
PEERONQ_TURN_SERVER_ID=turn-$REGION-1
PEERONQ_PRESENCE_SERVER_ID=presence-$REGION-1
PEERONQ_HTTPS_PORT=443
PEERONQ_HTTPS_BIND_ADDRESS=0.0.0.0
PEERONQ_GRAFANA_PORT=3000
PEERONQ_PROMETHEUS_PORT=9090
PEERONQ_ADMIN_PROMETHEUS_ENDPOINT=
PEERONQ_ADMIN_PROMETHEUS_ALLOWED_HOST=
PEERONQ_FLUENT_FORWARD_PORT=24224
PEERONQ_COTURN_FLUENT_FORWARD_PORT=24225
PEERONQ_RETENTION_POLICY_VERSION=phase6-v1
PEERONQ_AUDIT_RETENTION_DAYS=365
PEERONQ_AUDIT_RETENTION_ENABLED=false
PEERONQ_AUDIT_LEGAL_HOLD=false
PEERONQ_ALERT_RETENTION_DAYS=365
PEERONQ_ALERT_RETENTION_ENABLED=true
PEERONQ_ALERT_LEGAL_HOLD=false
PEERONQ_DIAGNOSTICS_LEGAL_HOLD=false
PEERONQ_DIAGNOSTICS_ALLOW_SINGLE_NODE_FILESYSTEM=true
PEERONQ_SESSION_RECONCILIATION_INTERVAL=00:01:00
PEERONQ_SESSION_NEGOTIATION_TIMEOUT=00:05:00
PEERONQ_SESSION_CONNECTED_INACTIVITY_TIMEOUT=00:03:00
PEERONQ_SESSION_RECONCILIATION_BATCH_SIZE=500
PEERONQ_TURN_BIND_ADDRESS=0.0.0.0
PEERONQ_TURN_EXTERNAL_IP=$PUBLIC_IP
PEERONQ_TURN_ALLOW_PRIVATE_PEERS=false
PEERONQ_TURN_RELAY_ONLY=false
PEERONQ_RELEASE_ARTIFACT_HOST=updates.$BASE_DOMAIN
PEERONQ_RELEASE_PUBLICATION_ENABLED=true
PEERONQ_RELEASE_SIGNING_KEY_ID=$release_key_id
PEERONQ_RELEASE_SIGNING_PUBLIC_KEY_SPKI_BASE64=$release_public_key
PEERONQ_RELEASE_MAXIMUM_PACKAGE_BYTES=100663296
PEERONQ_WEBSITE_PUBLICATION_ENABLED=true
PEERONQ_WEBSITE_SIGNING_KEY_ID=$website_key_id
PEERONQ_WEBSITE_SIGNING_PUBLIC_KEY_SPKI_BASE64=$website_public_key
PEERONQ_WEBSITE_MAXIMUM_ARCHIVE_BYTES=100663296
PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE=false
PEERONQ_MAXIMUM_ARTIFACT_BYTES=1073741824
PEERONQ_DOWNLOAD_MAX_CONCURRENT_STREAMS=16
PEERONQ_DOWNLOAD_STREAM_BUFFER_BYTES=131072
PEERONQ_DOWNLOAD_STREAM_DEADLINE_MINUTES=30
PEERONQ_DOWNLOAD_CACHE_MAX_BYTES=4294967296
PEERONQ_PLATFORM_UPGRADE_ENABLED=$PLATFORM_UPGRADE_ENABLED
PEERONQ_PLATFORM_UPGRADE_MAXIMUM_BUNDLE_BYTES=268435456
PEERONQ_TLS_CERT_DIR=$tls_dir
PEERONQ_TLS_PRIVATE_KEY_FILE=$tls_dir/privkey.pem
PEERONQ_ALERTMANAGER_CONFIG=$config_root/alertmanager.yml
PEERONQ_ALERTMANAGER_WEBHOOK_TOKEN_FILE=$secrets_dir/alertmanager-webhook-token
PEERONQ_POSTGRES_BACKUP_PGPASS_FILE=$secrets_dir/postgres-backup.pgpass
PEERONQ_POSTGRES_RESTORE_PGPASS_FILE=$secrets_dir/postgres-restore.pgpass
PEERONQ_TURN_SHARED_SECRET_FILE=$secrets_dir/turn-shared-secret
PEERONQ_TURN_CERT_DIR=$tls_dir
PEERONQ_ADMIN_DATA_PROTECTION_CERTIFICATE_FILE=$secrets_dir/admin-data-protection.pfx
PEERONQ_ADMIN_DATA_PROTECTION_PASSWORD_FILE=$secrets_dir/admin-data-protection-password
PEERONQ_SIGNALING_ATTESTATION_PRIVATE_KEY_FILE=$secrets_dir/signaling-attestation-private.pem
PEERONQ_SIGNALING_ATTESTATION_PUBLIC_KEY_FILE=$secrets_dir/signaling-attestation-public.pem
PEERONQ_BACKUP_DIR=$backup_dir
PEERONQ_BACKUP_RETENTION_DAYS=14
PEERONQ_RESTORE_TEST_FILE=/backups/not-selected.dump
PEERONQ_POSTGRES_BOOTSTRAP_PASSWORD=$postgres_bootstrap
PEERONQ_POSTGRES_MIGRATOR_PASSWORD=$postgres_migrator
PEERONQ_CLOUD_DB_PASSWORD=$cloud_db
PEERONQ_PRESENCE_DB_PASSWORD=$presence_db
PEERONQ_ADMIN_DB_PASSWORD=$admin_db
PEERONQ_DOWNLOADS_DB_PASSWORD=$downloads_db
PEERONQ_POSTGRES_BACKUP_PASSWORD=$backup_db
PEERONQ_POSTGRES_RESTORE_PASSWORD=$restore_db
PEERONQ_REDIS_CLOUD_PASSWORD=$redis_cloud
PEERONQ_REDIS_PRESENCE_PASSWORD=$redis_presence
PEERONQ_REDIS_PRESENCE_TOKEN_READER_PASSWORD=$redis_presence_reader
PEERONQ_REDIS_ADMIN_PASSWORD=$redis_admin
PEERONQ_REDIS_DOWNLOADS_PASSWORD=$redis_downloads
PEERONQ_REDIS_SIGNALING_PASSWORD=$redis_signaling
PEERONQ_REDIS_HEALTH_PASSWORD=$redis_health
PEERONQ_GRAFANA_ADMIN_PASSWORD_FILE=$secrets_dir/grafana-admin-password
PEERONQ_CLOUD_PUBLIC_DEVICE_ID_HMAC_KEY_BASE64=$cloud_hmac
PEERONQ_ADMIN_PRIVACY_HMAC_KEY_BASE64=$admin_hmac
PEERONQ_DOWNLOADS_PRIVACY_HMAC_KEY_BASE64=$downloads_hmac
PEERONQ_ADMIN_TOKEN_SIGNING_KEY=$admin_token
PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY=$customer_token
PEERONQ_CUSTOMER_REGISTRATION_MODE=Closed
PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION=$CUSTOMER_REQUIRE_EMAIL_VERIFICATION
PEERONQ_CUSTOMER_MAIL_PROVIDER=$CUSTOMER_MAIL_PROVIDER
PEERONQ_CUSTOMER_SMTP_HOST=$CUSTOMER_SMTP_HOST
PEERONQ_CUSTOMER_SMTP_PORT=$CUSTOMER_SMTP_PORT
PEERONQ_CUSTOMER_SMTP_USERNAME=
PEERONQ_CUSTOMER_SMTP_PASSWORD=
PEERONQ_ADMIN_REFRESH_HASH_KEY=$admin_refresh
PEERONQ_DOWNLOAD_COMPLETION_TOKEN_KEY=$download_token
PEERONQ_TURN_SHARED_SECRET=$turn_secret
PEERONQ_DIAGNOSTICS_HTTP_BEARER_TOKEN=$diagnostics_token
PEERONQ_ADMIN_BOOTSTRAP_ENABLED=true
PEERONQ_ADMIN_BOOTSTRAP_EMAIL=$ADMIN_EMAIL
PEERONQ_ADMIN_BOOTSTRAP_PASSWORD=$admin_password
PEERONQ_ADMIN_BOOTSTRAP_TOTP_SECRET_BASE32=$admin_totp
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_1=$recovery_1
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_2=$recovery_2
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_3=$recovery_3
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_4=$recovery_4
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_5=$recovery_5
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_6=$recovery_6
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_7=$recovery_7
PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_8=$recovery_8
PEERONQ_DIAGNOSTICS_HTTP_ENDPOINT=
PEERONQ_DIAGNOSTICS_HTTP_ALLOWED_HOST=
EOF
chmod 600 "$ENV_FILE"

cat > "$onboarding_file" <<EOF
PeerOnQ public-pilot Admin onboarding
Admin URL: https://admin.$BASE_DOMAIN
Email: $ADMIN_EMAIL
Password: $admin_password
TOTP secret: $admin_totp
TOTP URI: otpauth://totp/PeerOnQ:$ADMIN_EMAIL?secret=$admin_totp&issuer=PeerOnQ
Recovery codes:
$recovery_1
$recovery_2
$recovery_3
$recovery_4
$recovery_5
$recovery_6
$recovery_7
$recovery_8

Pilot update signing private key: $signing_dir/private-key.pem
Copy that key to the offline Windows release workstation over a protected channel,
then delete the server copy before production. Replace this pilot trust root before launch.

Pilot website signing private key: $website_signing_dir/private-key.pem
Use it only with build-peeronq-website-patch.ps1 on an offline workstation,
then delete the server copy before production. Website uploads accept signed static ZIP files only.

After the first successful login, delete this onboarding file.
EOF
chmod 600 "$onboarding_file"

if [ -n "$ACME_EMAIL" ] && command -v systemctl >/dev/null 2>&1; then
  cp "$repo_root/scripts/linux/renew-peeronq-tls.sh" /usr/local/sbin/peeronq-renew-tls
  chmod 755 /usr/local/sbin/peeronq-renew-tls
  write_tls_renewal_units \
    /etc/systemd/system/peeronq-tls-renew.service \
    /etc/systemd/system/peeronq-tls-renew.timer \
    "$ENV_FILE"
  chmod 644 /etc/systemd/system/peeronq-tls-renew.service /etc/systemd/system/peeronq-tls-renew.timer
  systemctl daemon-reload
  systemctl enable --now peeronq-tls-renew.timer >/dev/null
fi

printf 'Production-pilot environment created at %s.\n' "$ENV_FILE"
printf 'Admin credentials and MFA were written to %s (root-only).\n' "$onboarding_file"
printf 'No credential or private key was printed.\n'
