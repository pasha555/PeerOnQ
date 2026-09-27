#!/bin/sh
set -eu

installer_source=${1:?Usage: validate-nginx.sh PATH_TO_INSTALLER}

export PEERONQ_API_HOST=api-staging.peeronq.invalid
export PEERONQ_ADMIN_HOST=admin-staging.peeronq.invalid
export PEERONQ_PORTAL_HOST=portal-staging.peeronq.invalid
export PEERONQ_GRAFANA_HOST=grafana-staging.peeronq.invalid
export PEERONQ_PROMETHEUS_HOST=prometheus-staging.peeronq.invalid
# Separate loopback sources exercise both sides of the operator allowlist without host networking.
export PEERONQ_ADMIN_ALLOWED_CIDR=127.0.0.2/32
export PEERONQ_DOWNLOAD_HOST=download-staging.peeronq.invalid
export PEERONQ_WEB_HOST=peeronq.invalid
export PEERONQ_WEB_WWW_HOST=www.peeronq.invalid
export PEERONQ_UPDATE_HOST=updates-staging.peeronq.invalid
export PEERONQ_PRESENCE_HOST=presence-staging.peeronq.invalid
export PEERONQ_SIGNAL_HOST=signal-staging.peeronq.invalid
export PEERONQ_CLOUD_API_UPSTREAMS='server cloud-api:8080 resolve max_fails=1 fail_timeout=10s;'
export PEERONQ_PRESENCE_UPSTREAMS='server presence:8080 resolve max_fails=1 fail_timeout=10s;'
export PEERONQ_DOWNLOADS_UPSTREAMS='server downloads:8080 resolve max_fails=1 fail_timeout=10s;'
export PEERONQ_SIGNALING_UPSTREAMS='server signaling:8080 resolve max_fails=1 fail_timeout=10s;'

mkdir -p /certs /run/secrets /var/lib/peeronq/releases /var/lib/peeronq/website/releases
printf '%s\n' \
  '127.0.0.1 cloud-api' \
  '127.0.0.1 admin-api' \
  '127.0.0.1 presence' \
  '127.0.0.1 downloads' \
  '127.0.0.1 admin-ui' \
  '127.0.0.1 portal-ui' \
  '127.0.0.1 web-ui' \
  '127.0.0.1 grafana' \
  '127.0.0.1 prometheus' \
  '127.0.0.1 signaling' >> /etc/hosts
if [ ! -s /certs/fullchain.pem ] || [ ! -s /run/secrets/peeronq_tls_private_key ]; then
  if ! command -v openssl >/dev/null 2>&1; then
    apk add --no-cache openssl >/dev/null
  fi
  openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=peeronq.invalid' \
    -addext 'subjectAltName=DNS:peeronq.invalid,DNS:www.peeronq.invalid,DNS:*.peeronq.invalid' \
    -keyout /run/secrets/peeronq_tls_private_key -out /certs/fullchain.pem >/dev/null 2>&1
fi
cp /workspace/nginx/nginx.conf /etc/nginx/nginx.conf
cp /workspace/nginx/proxy_params /etc/nginx/proxy_params
cp /workspace/nginx/default.conf /etc/nginx/conf.d/default.conf
envsubst '${PEERONQ_WEB_HOST} ${PEERONQ_WEB_WWW_HOST} ${PEERONQ_API_HOST} ${PEERONQ_ADMIN_HOST} ${PEERONQ_PORTAL_HOST} ${PEERONQ_GRAFANA_HOST} ${PEERONQ_PROMETHEUS_HOST} ${PEERONQ_ADMIN_ALLOWED_CIDR} ${PEERONQ_DOWNLOAD_HOST} ${PEERONQ_UPDATE_HOST} ${PEERONQ_PRESENCE_HOST} ${PEERONQ_SIGNAL_HOST} ${PEERONQ_CLOUD_API_UPSTREAMS} ${PEERONQ_PRESENCE_UPSTREAMS} ${PEERONQ_DOWNLOADS_UPSTREAMS} ${PEERONQ_SIGNALING_UPSTREAMS}' \
  < /workspace/nginx/peeronq.conf.template > /etc/nginx/conf.d/peeronq.conf
nginx -t

awk '
  /^[[:space:]]*location = \/downloads\/PeerOnQ-Windows-x64\.msi \{/ {
    inside = 1
    found = 1
  }
  inside && /proxy_buffering off;/ { streaming = 1 }
  inside && /proxy_max_temp_file_size 0;/ { no_temp_file = 1 }
  inside && /add_header Cache-Control "no-store, max-age=0" always;/ { no_store = 1 }
  inside && /proxy_pass http:\/\/web_ui;/ { base_owned = 1 }
  inside && /^[[:space:]]*}/ { inside = 0 }
  END { exit !(found && streaming && no_temp_file && no_store && base_owned) }
' /etc/nginx/conf.d/peeronq.conf
awk '
  /^[[:space:]]*location = \/downloads \{/ { inside = 1; found = 1 }
  inside && /root \/var\/lib\/peeronq\/website\/current;/ { patch_root = 1 }
  inside && /try_files \/peeronq-downloads-ui-v1\.html \/_peeronq\/base-downloads;/ { patch_index = 1 }
  inside && /proxy_pass/ { proxy = 1 }
  inside && /^[[:space:]]*}/ { inside = 0 }
  END { exit !(found && patch_root && patch_index && !proxy) }
' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'location = /_peeronq/base-downloads {' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'proxy_pass http://web_ui/;' /etc/nginx/conf.d/peeronq.conf
awk '
  /^[[:space:]]*location = \/_peeronq\/base-downloads \{/ { inside = 1; found = 1 }
  inside && /add_header Cache-Control "no-store, max-age=0" always;/ { no_store = 1 }
  inside && /^[[:space:]]*}/ { inside = 0 }
  END { exit !(found && no_store) }
' /etc/nginx/conf.d/peeronq.conf
for metadata_path in SHA256SUMS.txt embedded-windows-version.txt embedded-windows-release-type.txt UNSIGNED-PILOT-NOTICE.txt; do
  awk -v path="$metadata_path" '
    $0 ~ "^[[:space:]]*location = /downloads/" path " \\{" { inside = 1; found = 1 }
    inside && /add_header Cache-Control "no-store, max-age=0" always;/ { no_store = 1 }
    inside && /proxy_pass http:\/\/web_ui;/ { base_owned = 1 }
    inside && /^[[:space:]]*}/ { inside = 0 }
    END { exit !(found && no_store && base_owned) }
  ' /etc/nginx/conf.d/peeronq.conf
done
grep -Fq 'location ^~ /downloads/ { return 410; }' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'location = /health/live { proxy_pass http://cloud_api/health/live; include /etc/nginx/proxy_params;' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'location = /health/ready { proxy_pass http://signaling_server/health/ready; include /etc/nginx/proxy_params;' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'proxy_set_header X-Forwarded-For $remote_addr;' /etc/nginx/proxy_params
! grep -Fq 'proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;' /etc/nginx/proxy_params
grep -Fq 'resolver 127.0.0.11 valid=10s ipv6=off;' /etc/nginx/conf.d/peeronq.conf
test "$(grep -Ec 'server [a-z-]+:8080 resolve' /etc/nginx/conf.d/peeronq.conf)" -eq 8
grep -Fq 'server grafana:3000 resolve;' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'server prometheus:9090 resolve;' /etc/nginx/conf.d/peeronq.conf
test "$(grep -Fxc "  allow $PEERONQ_ADMIN_ALLOWED_CIDR;" /etc/nginx/conf.d/peeronq.conf)" -eq 3
grep -Fq 'server_name grafana-staging.peeronq.invalid;' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'proxy_pass http://grafana_server;' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'server_name prometheus-staging.peeronq.invalid;' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'limit_except GET { deny all; }' /etc/nginx/conf.d/peeronq.conf
grep -Fq 'proxy_pass http://prometheus_server;' /etc/nginx/conf.d/peeronq.conf

patch_root=/var/lib/peeronq/website/current
base_root=/tmp/peeronq-base-web
mkdir -p "$patch_root/downloads" "$base_root/downloads"
printf '%s\n' 'active website downloads page' > "$patch_root/index.html"
printf '%s\n' 'patch-owned bytes must not escape' > "$patch_root/downloads/PeerOnQ-Windows-x64.msi"
printf '%s\n' 'patch-owned hash must not escape' > "$patch_root/downloads/SHA256SUMS.txt"
printf '%s\n' 'patch-owned classification must not escape' > "$patch_root/downloads/embedded-windows-release-type.txt"
printf '%s\n' 'patch-owned arbitrary installer' > "$patch_root/downloads/Other.msi"
printf '%s\n' 'base downloads page' > "$base_root/index.html"
printf '%s\n' 'base server msi' > "$base_root/downloads/PeerOnQ-Windows-x64.msi"
printf '%s\n' 'base server hash' > "$base_root/downloads/SHA256SUMS.txt"
printf '%s\n' '0.5.1' > "$base_root/downloads/embedded-windows-version.txt"
printf '%s\n' 'unsigned-pilot' > "$base_root/downloads/embedded-windows-release-type.txt"
printf '%s\n' 'base server pilot notice' > "$base_root/downloads/UNSIGNED-PILOT-NOTICE.txt"

# Route the runtime fixture directly after the production DNS assertions above. The route ownership
# test must not depend on a Compose network or Docker's embedded resolver being present.
sed -i 's/server [a-z-][a-z-]*:8080 resolve;/server 127.0.0.1:8080;/g' /etc/nginx/conf.d/peeronq.conf
sed -i 's/server [a-z-][a-z-]*:8080 resolve max_fails=[12] fail_timeout=[0-9]*s;/server 127.0.0.1:8082;/g' /etc/nginx/conf.d/peeronq.conf
sed -i 's/server prometheus:9090 resolve;/server 127.0.0.1:8082;/g' /etc/nginx/conf.d/peeronq.conf
sed -i 's/server grafana:3000 resolve;/server 127.0.0.1:8082;/g' /etc/nginx/conf.d/peeronq.conf
cat >> /etc/nginx/conf.d/peeronq.conf <<'EOF'
server {
  listen 127.0.0.1:8080;
  access_log off;
  root /tmp/peeronq-base-web;
  location / { try_files $uri $uri/ /index.html; }
}
server {
  listen 127.0.0.1:8082;
  access_log off;
  # This fixture proves route ownership, not customer authentication implementation.
  location = /portal/v1/account/profile { return 401 'customer authentication required'; }
  location = /portal/v1/auth/login { return 200 "customer-api:$request_method:$uri:$http_host:$http_x_forwarded_proto"; }
  location = /metrics { return 200 'internal metrics fixture'; }
  location / { return 200 'internal backend fixture'; }
}
EOF
cleanup() {
  nginx -s quit >/dev/null 2>&1 || true
}
trap cleanup EXIT
nginx

https_request() {
  request_host=$1
  request_path=$2
  shift 2
  curl --noproxy '*' --silent --show-error --connect-timeout 2 --max-time 5 \
    --cacert /certs/fullchain.pem --resolve "$request_host:443:127.0.0.1" \
    "$@" "https://$request_host$request_path"
}

fetch_status() {
  status_host=$1
  status_path=${2:-/}
  shift
  [ "$#" -eq 0 ] || shift
  https_request "$status_host" "$status_path" --output /dev/null --write-out '%{http_code}' "$@"
}

for internal_host in \
  "$PEERONQ_ADMIN_HOST" \
  "$PEERONQ_GRAFANA_HOST" "$PEERONQ_PROMETHEUS_HOST"
do
  test "$(fetch_status "$internal_host")" = 403
  test "$(fetch_status "$internal_host" / --header 'X-Forwarded-For: 127.0.0.2')" = 403
done

fetch_public() {
  https_request "$PEERONQ_WEB_HOST" "$1" --fail
}

assert_public_no_store() {
  https_request "$PEERONQ_WEB_HOST" "$1" --dump-header - --output /dev/null \
    | tr -d '\r' \
    | grep -Eqi '^[[:space:]]*Cache-Control:.*no-store'
}

attempt=0
until [ "$(fetch_public /downloads 2>/dev/null || true)" = 'base downloads page' ]; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 10 ] || exit 1
  sleep 1
done
test "$(fetch_status "$PEERONQ_PORTAL_HOST")" = 200
test "$(fetch_status "$PEERONQ_WEB_HOST")" = 200
test "$(fetch_status "$PEERONQ_WEB_WWW_HOST")" = 200
test "$(fetch_status "$PEERONQ_PORTAL_HOST" /portal/v1/account/profile)" = 401
test "$(https_request "$PEERONQ_PORTAL_HOST" /portal/v1/auth/login --request POST)" = \
  "customer-api:POST:/portal/v1/auth/login:$PEERONQ_PORTAL_HOST:https"

# Check both success and backend error responses; same-origin customer cookies need no CORS.
for header_target in "$PEERONQ_WEB_HOST:/" "$PEERONQ_WEB_WWW_HOST:/" \
  "$PEERONQ_PORTAL_HOST:/" "$PEERONQ_PORTAL_HOST:/portal/v1/account/profile"
do
  headers=$(https_request "${header_target%%:*}" "${header_target#*:}" --dump-header - --output /dev/null | tr -d '\r')
  printf '%s\n' "$headers" | grep -Eqi '^Strict-Transport-Security: max-age=31536000; includeSubDomains$'
  printf '%s\n' "$headers" | grep -Eqi '^X-Content-Type-Options: nosniff$'
  printf '%s\n' "$headers" | grep -Eqi '^X-Frame-Options: DENY$'
  printf '%s\n' "$headers" | grep -Eqi '^Referrer-Policy: (no-referrer|strict-origin-when-cross-origin)$'
  ! printf '%s\n' "$headers" | grep -Eqi '^Access-Control-Allow-Origin:'
  case "$header_target" in
    "$PEERONQ_WEB_HOST:/"|"$PEERONQ_WEB_WWW_HOST:/")
      printf '%s\n' "$headers" | grep -Eqi "^Content-Security-Policy:.*connect-src 'self';.*frame-ancestors 'none'" ;;
  esac
done

# The internal listener remains scrapeable; no public hostname can publish its metrics.
test "$(curl --noproxy '*' --silent --fail http://127.0.0.1:8082/metrics)" = 'internal metrics fixture'
for public_host in "$PEERONQ_WEB_HOST" "$PEERONQ_WEB_WWW_HOST" "$PEERONQ_PORTAL_HOST" \
  "$PEERONQ_API_HOST" "$PEERONQ_PRESENCE_HOST" "$PEERONQ_DOWNLOAD_HOST" \
  "$PEERONQ_UPDATE_HOST" "$PEERONQ_SIGNAL_HOST"
do
  for metrics_path in /metrics /metrics/ /METRICS /metrics/nested '/metrics?format=prometheus'; do
    metrics_status=$(fetch_status "$public_host" "$metrics_path")
    test "$metrics_status" = 403 || test "$metrics_status" = 404
  done
done

# Allowed operators may read Prometheus but cannot mutate it, including with a spoofed header.
test "$(fetch_status "$PEERONQ_PROMETHEUS_HOST" / --interface 127.0.0.2)" = 200
test "$(fetch_status "$PEERONQ_PROMETHEUS_HOST" / --interface 127.0.0.2 --head)" = 200
test "$(fetch_status "$PEERONQ_PROMETHEUS_HOST" / --interface 127.0.0.2 --request POST)" = 403

# Unknown SNI fails the handshake; known SNI plus an unknown Host receives no application response.
unknown_exit=0
https_request unknown.peeronq.invalid / --output /dev/null 2>/dev/null || unknown_exit=$?
test "$unknown_exit" = 35
unknown_exit=0
unknown_status=$(https_request "$PEERONQ_PORTAL_HOST" / --http1.1 --header 'Host: unknown.peeronq.invalid' \
  --output /dev/null --write-out '%{http_code}' 2>/dev/null) || unknown_exit=$?
test "$unknown_status" = 000
# A TLS 444 close is reported as an empty reply or receive reset, depending on the curl TLS build.
case "$unknown_exit" in 52|56) ;; *) exit 1 ;; esac
unknown_exit=0
curl --noproxy '*' --silent --max-time 5 --header 'Host: unknown.peeronq.invalid' \
  http://127.0.0.1/ --output /dev/null 2>/dev/null || unknown_exit=$?
test "$unknown_exit" = 52
# Only releases created by the current builder contain the Admin-verified, byte-identical UI entry.
# Its presence switches the page, but never any client artifact, to the active website release.
cp "$patch_root/index.html" "$patch_root/peeronq-downloads-ui-v1.html"
test "$(fetch_public /downloads)" = 'active website downloads page'
test "$(fetch_public /downloads/PeerOnQ-Windows-x64.msi)" = 'base server msi'
test "$(fetch_public /downloads/SHA256SUMS.txt)" = 'base server hash'
test "$(fetch_public /downloads/embedded-windows-version.txt)" = '0.5.1'
test "$(fetch_public /downloads/embedded-windows-release-type.txt)" = 'unsigned-pilot'
test "$(fetch_public /downloads/UNSIGNED-PILOT-NOTICE.txt)" = 'base server pilot notice'
for no_store_path in \
  /downloads \
  '/downloads/PeerOnQ-Windows-x64.msi?v=0.5.1' \
  /downloads/SHA256SUMS.txt \
  /downloads/embedded-windows-version.txt \
  /downloads/embedded-windows-release-type.txt \
  /downloads/UNSIGNED-PILOT-NOTICE.txt
do
  assert_public_no_store "$no_store_path"
done
if fetch_public /downloads/Other.msi >/dev/null 2>&1; then
  exit 1
fi
# Exercise the complete installer publication function through this real TLS proxy too.
# Keep fixture data explicit; this is a transport/header test, not MSI payload validation.
publication_release=/tmp/peeronq-publication-release
publication_downloads="$publication_release/artifacts/peeronq/public/downloads"
mkdir -p "$publication_downloads" "$publication_release/scripts/linux" /usr/share/nginx/html/assets
cp "$base_root/downloads/PeerOnQ-Windows-x64.msi" "$publication_downloads/"
cp "$base_root/downloads/embedded-windows-version.txt" "$publication_downloads/"
cp "$base_root/downloads/embedded-windows-release-type.txt" "$publication_downloads/"
publication_hash=$(sha256sum "$publication_downloads/PeerOnQ-Windows-x64.msi" | awk '{ print $1 }')
printf '%s  PeerOnQ-Windows-x64.msi\n' "$publication_hash" > "$publication_downloads/SHA256SUMS.txt"
printf '%s\n' 'This x64 MSI is unsigned and is authorized only for controlled PeerOnQ pilot testing.' \
  > "$publication_downloads/UNSIGNED-PILOT-NOTICE.txt"
cp "${installer_source%/*}/verify-embedded-windows-client.sh" "$publication_release/scripts/linux/"
printf '%s\n' '/downloads/PeerOnQ-Windows-x64.msi?v=0.5.1' > /usr/share/nginx/html/assets/publication-fixture.js
rm "$patch_root/peeronq-downloads-ui-v1.html"
awk '
  /^verify_embedded_windows_client\(\)/ { copy = 1 }
  copy && /^transition_website_overlay\(\)/ { exit }
  copy { print }
' "$installer_source" > /tmp/peeronq-publication-function.sh
. /tmp/peeronq-publication-function.sh
run_compose_step() {
  printf 'Installer fixture: %s\n' "$1"
  shift 5 # step, release, exec, -T, service; the fixture serves both origins in this container.
  "$@"
}
export CURL_CA_BUNDLE=/certs/fullchain.pem
verify_embedded_windows_client "$publication_release"
printf '%s\n' 'Nginx local ingress checks passed: verified TLS, public portal/API, operator isolation, private metrics and unknown-host rejection.'
