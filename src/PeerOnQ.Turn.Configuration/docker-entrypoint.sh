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
relay_ip="${PEERONQ_TURN_RELAY_IP:-}"

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

case "$relay_ip" in
    *[!0-9.]*)
        echo "PEERONQ_TURN_RELAY_IP must be an IPv4 address." >&2
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

# Docker publishes the relay ports only to the container address on the network that carries
# its default route. On several networks (edge + observability) coturn otherwise relays from
# each address in turn, and allocations on the unpublished one never receive peer packets.
gateway_network_ipv4() {
    [ -r /proc/net/route ] || return 0
    awk -v addresses="$(hostname -I 2>/dev/null || true)" '
        function hex(value,   i, result) {
            result = 0
            for (i = 1; i <= length(value); i++)
                result = result * 16 + index("0123456789ABCDEF", toupper(substr(value, i, 1))) - 1
            return result
        }
        # /proc/net/route stores IPv4 addresses as little-endian hexadecimal.
        function route(value) {
            return ((hex(substr(value, 7, 2)) * 256 + hex(substr(value, 5, 2))) * 256 \
                + hex(substr(value, 3, 2))) * 256 + hex(substr(value, 1, 2))
        }
        function dotted(value,   parts) {
            if (split(value, parts, ".") != 4) return -1
            return ((parts[1] * 256 + parts[2]) * 256 + parts[3]) * 256 + parts[4]
        }
        NR > 1 && $2 == "00000000" && $8 == "00000000" { gateway = $1 }
        NR > 1 && $3 == "00000000" && $8 != "00000000" { subnets[$1] = subnets[$1] " " route($2) ":" route($8) }
        END {
            if (gateway == "") exit
            count = split(addresses, candidates, " ")
            routes = split(subnets[gateway], ranges, " ")
            for (i = 1; i <= count; i++) {
                address = dotted(candidates[i])
                if (address < 0) continue
                for (j = 1; j <= routes; j++) {
                    split(ranges[j], range, ":")
                    if (address >= range[1] && address < range[1] + 4294967296 - range[2]) {
                        print candidates[i]
                        exit
                    }
                }
            }
        }' /proc/net/route
}

if [ -z "$relay_ip" ]; then
    relay_ip="$(gateway_network_ipv4 || true)"
fi

if [ -n "$relay_ip" ]; then
    # Listen where the published ports arrive, plus loopback for the container health check.
    set -- "$@" --relay-ip="$relay_ip" --listening-ip="$relay_ip" --listening-ip=127.0.0.1
else
    echo "TURN relay address not pinned: no default-route IPv4 address was found." >&2
fi

if [ -n "$external_ip" ]; then
    # Map the public address to the pinned relay address unless the operator mapped it already.
    case "$external_ip" in
        */*) ;;
        *)
            if [ -n "$relay_ip" ]; then
                external_ip="$external_ip/$relay_ip"
            fi
            ;;
    esac
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
