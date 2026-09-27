#!/bin/sh
set -eu

secret="${PEERONQ_TURN_SHARED_SECRET:-}"
if [ -r /run/secrets/peeronq_turn_shared_secret ]; then
    # Docker secrets created on Windows can end with CRLF. Remove only a trailing carriage
    # return from each line; embedded line breaks remain and are rejected by validation below.
    secret="$(sed 's/\r$//' /run/secrets/peeronq_turn_shared_secret)"
fi

if [ "${#secret}" -lt 32 ]; then
    echo "PEERONQ_TURN_SHARED_SECRET must contain at least 32 characters." >&2
    exit 78
fi

case "$secret" in
    *[!A-Za-z0-9_+=/-]*)
        echo "PEERONQ_TURN_SHARED_SECRET contains unsupported characters." >&2
        exit 78
        ;;
esac

realm="${PEERONQ_TURN_REALM:-turn.example.com}"
external_ip="${PEERONQ_TURN_EXTERNAL_IP:-}"
cert_file="${PEERONQ_TURN_CERT_FILE:-/certs/fullchain.pem}"
key_file="${PEERONQ_TURN_KEY_FILE:-/certs/privkey.pem}"
relay_min_port="${PEERONQ_TURN_MIN_PORT:-49160}"
relay_max_port="${PEERONQ_TURN_MAX_PORT:-49200}"

case "$realm" in
    *[!A-Za-z0-9._-]*)
        echo "PEERONQ_TURN_REALM contains unsupported characters." >&2
        exit 78
        ;;
esac

case "$external_ip" in
    192.0.2.*|198.51.100.*|203.0.113.*|2001:db8:*|2001:DB8:*)
        echo "PEERONQ_TURN_EXTERNAL_IP still uses a documentation-only address." >&2
        exit 78
        ;;
esac

case "$relay_min_port:$relay_max_port" in
    *[!0-9:]*|:*|*::* )
        echo "PEERONQ_TURN_MIN_PORT and PEERONQ_TURN_MAX_PORT must be numeric." >&2
        exit 78
        ;;
esac

if [ "$relay_min_port" -lt 1024 ] || [ "$relay_max_port" -gt 65535 ] || [ "$relay_min_port" -gt "$relay_max_port" ]; then
    echo "PEERONQ_TURN relay ports must be in 1024-65535 and ordered." >&2
    exit 78
fi

# Keep the secret out of argv/process listings. Coturn officially supports long-form options in
# its configuration file, so create a private runtime copy and append only validated values.
umask 077
runtime_config="$(mktemp /tmp/peeronq-turn.XXXXXX.conf)"
cp /etc/coturn/turnserver.conf "$runtime_config"
printf '\nrealm=%s\nstatic-auth-secret=%s\n' "$realm" "$secret" >> "$runtime_config"
unset secret

set -- -c "$runtime_config" --min-port="$relay_min_port" --max-port="$relay_max_port" "$@"

if [ -n "$external_ip" ]; then
    set -- "$@" --external-ip="$external_ip"
fi

if [ -r "$cert_file" ] && [ -r "$key_file" ]; then
    set -- "$@" --tls-listening-port=5349 --cert="$cert_file" --pkey="$key_file"
else
    if [ "${PEERONQ_TURN_REQUIRE_TLS:-false}" = "true" ]; then
        echo "TURN TLS/DTLS certificates are required but were not mounted." >&2
        exit 78
    fi

    echo "TURN TLS/DTLS disabled: certificate files are not mounted (development mode)." >&2
    set -- "$@" --no-tls --no-dtls
fi

if [ "${PEERONQ_TURN_ALLOW_PRIVATE_PEERS:-false}" = "true" ]; then
    set -- "$@" --allow-loopback-peers
else
    set -- "$@" \
        --denied-peer-ip=10.0.0.0-10.255.255.255 \
        --denied-peer-ip=172.16.0.0-172.31.255.255 \
        --denied-peer-ip=192.168.0.0-192.168.255.255 \
        --denied-peer-ip=169.254.0.0-169.254.255.255 \
        --denied-peer-ip=100.64.0.0-100.127.255.255 \
        --denied-peer-ip=127.0.0.0-127.255.255.255 \
        --denied-peer-ip=198.18.0.0-198.19.255.255 \
        --denied-peer-ip=0.0.0.0-0.255.255.255
fi

exec turnserver "$@"
