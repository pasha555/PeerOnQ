#!/bin/sh
set -eu

ENV_FILE="/etc/peeronq/peeronq.env"
CERTBOT_IMAGE="certbot/certbot:v5.7.0@sha256:34ee91d2f43008eb78a007d22f23ed4b2eaa9a454cb27ca2c042b49527a695b4"
SPACESHIP_CREDENTIALS_DIR=""
SPACESHIP_HOOK="/usr/local/libexec/peeronq-spaceship-dns-hook.py"

fail() {
  printf 'PeerOnQ TLS renewal: %s\n' "$1" >&2
  exit 1
}

read_env() {
  awk -v key="$1" '
    index($0, key "=") == 1 { sub("^[^=]*=", ""); print; found = 1; exit }
    END { if (!found) exit 1 }
  ' "$ENV_FILE"
}

validate_managed_tls_binding() {
  tls_dir=$1
  tls_key=$2
  expected_tls_dir="$(dirname "$ENV_FILE")/tls"
  [ "$tls_dir" = "$expected_tls_dir" ] || {
    fail "TLS directory must be the managed tls sibling of the environment file"
    return 1
  }
  [ "$tls_key" = "$tls_dir/privkey.pem" ] || {
    fail "TLS private-key path does not match the managed TLS directory"
    return 1
  }
}

required_tls_hosts() {
  for key in \
    PEERONQ_WEB_HOST PEERONQ_WEB_WWW_HOST PEERONQ_API_HOST PEERONQ_PORTAL_HOST \
    PEERONQ_ADMIN_HOST PEERONQ_GRAFANA_HOST PEERONQ_PROMETHEUS_HOST \
    PEERONQ_DOWNLOAD_HOST PEERONQ_UPDATE_HOST PEERONQ_PRESENCE_HOST \
    PEERONQ_SIGNAL_HOST PEERONQ_TURN_PUBLIC_HOST
  do
    read_env "$key" || fail "$key is missing"
  done
}

certificate_covers_required_hosts() {
  cert=$1
  for required_host in $(required_tls_hosts); do
    openssl verify -no-CAfile -no-CApath -trusted "$cert" -partial_chain \
      -verify_hostname "$required_host" "$cert" >/dev/null 2>&1 \
      || return 1
  done
}

compose() {
  release_path=$1
  shift
  docker compose --env-file "$ENV_FILE" \
    -f "$release_path/src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml" \
    -f "$release_path/src/PeerOnQ.Infrastructure.Deployment/docker-compose.production.yml" \
    "$@"
}

validate_spaceship_credentials_dir() {
  credentials_dir=$1
  [ -d "$credentials_dir" ] && [ ! -L "$credentials_dir" ] || fail "Spaceship credential directory is missing or unsafe"
  [ "$(readlink -f "$credentials_dir")" = "$credentials_dir" ] || fail "Spaceship credential directory must be canonical"
  [ "$(stat -c '%u:%a' "$credentials_dir")" = "0:700" ] || fail "Spaceship credential directory must be root-owned mode 0700"
  for credential_name in api-key api-secret; do
    credential_file="$credentials_dir/$credential_name"
    [ -f "$credential_file" ] && [ ! -L "$credential_file" ] || fail "Spaceship credential is missing: $credential_name"
    case "$(stat -c '%u:%h:%a' "$credential_file")" in 0:1:400|0:1:600) ;; *) fail "Spaceship credential permissions are unsafe" ;; esac
    value=$(tr -d '\r\n' < "$credential_file")
    [ -n "$value" ] && [ "$(printf '%s' "$value" | wc -c)" -ge 16 ] && [ "$(printf '%s' "$value" | wc -c)" -le 512 ] || fail "Spaceship credential length is invalid"
    case "$value" in *[!A-Za-z0-9_-]*) fail "Spaceship credential contains unsafe characters" ;; esac
    [ "$(wc -l < "$credential_file")" -le 1 ] || fail "Spaceship credential must be one line"
  done
}

run_spaceship_acme() {
  acme_root=$1
  domain=$2
  source_hook=$3
  [ -f "$source_hook" ] && [ ! -L "$source_hook" ] || fail "Spaceship DNS hook is missing or unsafe"
  docker run --rm --read-only --cap-drop=ALL --security-opt no-new-privileges \
    --tmpfs /tmp:rw,nosuid,nodev,noexec,size=64m \
    --tmpfs /var/lib/letsencrypt:rw,nosuid,nodev,noexec,size=64m \
    --tmpfs /var/log/letsencrypt:rw,nosuid,nodev,noexec,size=16m \
    -e "PEERONQ_DNS_PROPAGATION_SECONDS=180" \
    -v "$acme_root:/etc/letsencrypt:rw" \
    -v "$SPACESHIP_CREDENTIALS_DIR:/run/secrets/peeronq-spaceship:ro" \
    -v "$source_hook:/opt/peeronq/peeronq-spaceship-dns-hook.py:ro" \
    "$CERTBOT_IMAGE" certonly --manual --preferred-challenges dns \
    --manual-auth-hook "/usr/local/bin/python3 /opt/peeronq/peeronq-spaceship-dns-hook.py auth" \
    --manual-cleanup-hook "/usr/local/bin/python3 /opt/peeronq/peeronq-spaceship-dns-hook.py cleanup" \
    --no-directory-hooks --non-interactive --agree-tos --cert-name "$domain-dns01" \
    --keep-until-expiring --renew-with-new-domains -d "$domain" -d "*.$domain"
}

current_release() {
  printf '%s\n' "$release"
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

while [ "$#" -gt 0 ]; do
  case "$1" in
    --env-file) [ "$#" -ge 2 ] || fail "--env-file requires a path"; ENV_FILE=$2; shift 2 ;;
    --help|-h) printf 'Usage: renew-peeronq-tls.sh [--env-file PATH]\n'; exit 0 ;;
    *) fail "unknown argument: $1" ;;
  esac
done

[ "$(id -u)" = "0" ] || fail "run this command as root"
[ -x "$(command -v flock 2>/dev/null || true)" ] || fail "flock is required"
exec 9>/run/lock/peeronq-server-install.lock
flock -n 9 || fail "another PeerOnQ install or TLS renewal is already running"
[ -f "$ENV_FILE" ] && [ ! -L "$ENV_FILE" ] || fail "environment file is missing or unsafe"
[ "$(stat -c '%h' "$ENV_FILE")" = "1" ] || fail "environment file must not have hard links"
[ "$(stat -c '%u' "$ENV_FILE")" = "0" ] || fail "environment file must be owned by root"
case "$(stat -c '%a' "$ENV_FILE")" in 600|400) ;; *) fail "environment file must use mode 0600 or 0400" ;; esac
ENV_FILE=$(readlink -f "$ENV_FILE") || fail "environment file path could not be canonicalized"

domain=$(read_env PEERONQ_WEB_HOST) || fail "PEERONQ_WEB_HOST is missing"
tls_dir=$(read_env PEERONQ_TLS_CERT_DIR) || fail "PEERONQ_TLS_CERT_DIR is missing"
tls_key=$(read_env PEERONQ_TLS_PRIVATE_KEY_FILE) || fail "PEERONQ_TLS_PRIVATE_KEY_FILE is missing"
case "$domain" in *[!a-z0-9.-]*|'') fail "configured web domain is invalid" ;; esac
validate_managed_tls_binding "$tls_dir" "$tls_key"
[ -d "$tls_dir" ] && [ ! -L "$tls_dir" ] || fail "managed TLS directory is missing or unsafe"
for tls_file in "$tls_dir/fullchain.pem" "$tls_dir/privkey.pem"; do
  [ -f "$tls_file" ] && [ ! -L "$tls_file" ] || fail "managed TLS file is missing or unsafe: $tls_file"
  [ "$(stat -c '%u' "$tls_file")" = "0" ] || fail "managed TLS files must be owned by root"
done
[ "$(stat -c '%u' "$tls_dir")" = "0" ] || fail "managed TLS directory must be owned by root"
chown 0:65534 "$tls_dir" "$tls_dir/fullchain.pem" "$tls_dir/privkey.pem"
chmod 750 "$tls_dir"
chmod 640 "$tls_dir/fullchain.pem" "$tls_dir/privkey.pem"

source_dir="/etc/letsencrypt/live/$domain"
[ -r "$source_dir/fullchain.pem" ] && [ -r "$source_dir/privkey.pem" ] \
  || fail "managed Let's Encrypt certificate was not found"
old_hash=$(sha256sum "$tls_dir/fullchain.pem" | awk '{ print $1 }')
release=$(readlink -f /opt/peeronq/current)
[ -d "$release" ] || fail "active PeerOnQ release was not found"

config_root=$(dirname "$ENV_FILE")
SPACESHIP_CREDENTIALS_DIR="$config_root/acme-spaceship"
spaceship_mode=false
if [ -e "$SPACESHIP_CREDENTIALS_DIR" ]; then
  validate_spaceship_credentials_dir "$SPACESHIP_CREDENTIALS_DIR"
  [ "$domain" = "peeronq.com" ] || fail "Spaceship DNS automation currently supports peeronq.com only"
  spaceship_mode=true
fi

if [ "$spaceship_mode" = "true" ]; then
  source_dir="/etc/letsencrypt/live/$domain-dns01"
  if [ ! -r "$source_dir/fullchain.pem" ] || ! certificate_covers_required_hosts "$source_dir/fullchain.pem" \
    || ! openssl x509 -in "$source_dir/fullchain.pem" -noout -checkend 2592000 >/dev/null; then
    run_spaceship_acme /etc/letsencrypt "$domain" "$SPACESHIP_HOOK" \
      || fail "ACME DNS-01 renewal failed; the existing certificate remains active"
  fi
elif certificate_covers_required_hosts "$source_dir/fullchain.pem"; then
  if ! openssl x509 -in "$source_dir/fullchain.pem" -noout -checkend 2592000 >/dev/null; then
    run_standalone_acme_with_proxy_handoff /etc/letsencrypt \
      renew --standalone --non-interactive --cert-name "$domain" \
      || fail "ACME could not renew the TLS certificate; the existing proxy was restored"
  fi
else
  set --
  for required_host in $(required_tls_hosts); do
    set -- "$@" -d "$required_host"
  done
  run_standalone_acme_with_proxy_handoff /etc/letsencrypt \
    certonly --standalone --non-interactive --cert-name "$domain" \
    --keep-until-expiring --expand "$@" \
    || fail "ACME could not expand the TLS certificate. Every requested name must resolve publicly to this server during HTTP-01; for split/private DNS, import a trusted SAN or wildcard certificate instead."
fi

openssl x509 -in "$source_dir/fullchain.pem" -noout -checkend 86400 >/dev/null \
  || fail "renewed certificate is invalid or expires in less than 24 hours"
for required_host in $(required_tls_hosts); do
  openssl verify -no-CAfile -no-CApath -trusted "$source_dir/fullchain.pem" -partial_chain \
    -verify_hostname "$required_host" "$source_dir/fullchain.pem" >/dev/null 2>&1 \
    || fail "renewed certificate does not cover configured host: $required_host"
done
cert_hash=$(openssl x509 -in "$source_dir/fullchain.pem" -pubkey -noout \
  | openssl pkey -pubin -outform DER 2>/dev/null | sha256sum | awk '{ print $1 }')
key_hash=$(openssl pkey -in "$source_dir/privkey.pem" -pubout -outform DER 2>/dev/null \
  | sha256sum | awk '{ print $1 }')
[ "$cert_hash" = "$key_hash" ] || fail "renewed certificate and private key do not match"

new_hash=$(sha256sum "$source_dir/fullchain.pem" | awk '{ print $1 }')
if [ "$new_hash" = "$old_hash" ]; then
  printf 'PeerOnQ TLS certificate is current; no service restart was required.\n'
  exit 0
fi

umask 077
cert_tmp=$(mktemp "$tls_dir/.fullchain.XXXXXX")
key_tmp=$(mktemp "$tls_dir/.privkey.XXXXXX")
trap 'rm -f "$cert_tmp" "$key_tmp"' EXIT HUP INT TERM
cp "$source_dir/fullchain.pem" "$cert_tmp"
cp "$source_dir/privkey.pem" "$key_tmp"
chown 0:65534 "$cert_tmp" "$key_tmp"
chmod 640 "$cert_tmp" "$key_tmp"
mv -f "$cert_tmp" "$tls_dir/fullchain.pem"
mv -f "$key_tmp" "$tls_dir/privkey.pem"
trap - EXIT HUP INT TERM

compose "$release" \
  up -d --force-recreate --wait --wait-timeout 120 proxy turn
printf 'PeerOnQ TLS certificate was renewed and edge services were reloaded.\n'
