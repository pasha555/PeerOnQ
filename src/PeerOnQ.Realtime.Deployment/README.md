# PeerOnQ Infrastructure Deployment

This directory deploys the Phase 3 signaling and coturn services. It contains a development
Compose stack and a fail-closed, single-signaling-node production stack. The production stack is
deployable, but it is not a horizontal-HA claim: a shared session/presence implementation is still
required before running multiple signaling replicas.

## DNS and certificates

Create separate records so signaling and relay traffic can be moved independently:

| Record | Example | Target |
| --- | --- | --- |
| `A` / `AAAA` | `signal.example.com` | HTTPS reverse proxy/load balancer |
| `A` / `AAAA` | `turn.example.com` | coturn public address |
| optional `SRV` | `_turn._udp.turn.example.com` | port 3478 |
| optional `SRV` | `_turns._tcp.turn.example.com` | port 5349 |

Issue publicly trusted certificates for both names. The signaling certificate terminates at the
reverse proxy. Mount the TURN certificate and key read-only as `/certs/fullchain.pem` and
`/certs/privkey.pem`. The production stack refuses to start TURN when either TLS file is missing.

## Ports and firewall

| Service | Protocol/port | Exposure |
| --- | --- | --- |
| Signaling proxy | TCP 443 | public; TCP 80 is not published by the production Compose stack |
| Signaling app | TCP 8080 | private network only |
| TURN/STUN | UDP 3478 | public |
| TURN fallback | TCP 3478 | public |
| TURN TLS | TCP 5349 | public |
| TURN DTLS | UDP 5349 | public when supported |
| TURN relay allocation | UDP 49160-49200 | public, same range on NAT and host |
| Signaling metrics | TCP 8080 `/metrics` | monitoring network only |
| coturn metrics | TCP 9641 | monitoring network only |

If coturn is behind 1:1 NAT, set `PEERONQ_TURN_EXTERNAL_IP` and forward the entire relay UDP range
without port translation. Do not expose coturn's CLI or web-admin interface. The baseline denies
private, link-local and multicast relay peers in production to reduce SSRF/abuse risk; local TURN
tests explicitly set `PEERONQ_TURN_ALLOW_PRIVATE_PEERS=true`.

## Development

1. Copy `.env.example` to `.env`.
2. Generate a local secret, for example 32 random bytes encoded as base64, and place it only in
   `.env` as `PEERONQ_TURN_SHARED_SECRET`.
3. Set `PEERONQ_TURN_PUBLIC_HOST` to an address the two test clients can resolve/reach.
4. Run `docker compose up --build` from this directory.
5. Use `ws://127.0.0.1:5080/ws` only for same-host development. For another machine, put the
   signaling service behind TLS and use `wss://signal.example.com/ws`.

The development Compose file advertises TURN UDP and TCP only. To test TURN over TLS, set
`PEERONQ_TURN_CERT_DIR` to a directory containing `fullchain.pem` and `privkey.pem`, then run:

```bash
docker compose -f docker-compose.yml -f docker-compose.tls.yml up --build
```

The TLS override mounts certificates read-only and advertises `turns:` on TCP 5349. Keep the base
file for certificate-free development so clients are not given a TLS endpoint that is disabled.

Development publishes signaling on loopback and permits private TURN peers. Production must not
reuse those two settings.

## Windows local Phase 3 acceptance

The repository includes a repeatable local controller that does not edit the Windows hosts file.
Its default loopback binding uses `127.0.0.1` directly, so local acceptance never depends on
external DNS. Setup generates a repository-scoped development CA/server certificate with the IP
SAN, trusts only its public root in the current-user certificate store, and stores every private
artifact under the ignored `.peeronq-phase3` directory. An explicit non-loopback `-BindAddress`
keeps the `signal.<address>.sslip.io` and `turn.<address>.sslip.io` LAN hostnames covered by the
generated certificate.

```powershell
.\scripts\windows\peeronq-phase3-local.ps1 setup
.\scripts\windows\peeronq-phase3-local.ps1 start
.\scripts\windows\peeronq-phase3-local.ps1 test
.\scripts\windows\peeronq-phase3-local.ps1 stop
```

With a running Docker Desktop engine, `start` launches `docker-compose.local.yml`: Nginx HTTPS,
signaling, and coturn UDP/TCP/TLS/DTLS are published on loopback only. `test` verifies health and
metrics, the signaling/E2E suites, authenticated TURN REST allocations over UDP, TCP, DTLS, and
TLS, plus rejection of invalid and expired relay credentials. It also runs two real host WebRTC
peers with `RelayOnly` and requires relay candidates, a nominated `Relayed` path on both peers, and
decoded/rendered video.

Without Docker, the controller starts native HTTPS signaling on
`https://127.0.0.1:5443` and explicitly skips live coturn checks. This fallback is
useful for TLS/signaling work but is not a TURN acceptance result.

## Production deployment

`docker-compose.production.yml` exposes only the TLS proxy and TURN listener/allocation ports. It
runs the application containers with read-only root filesystems, drops Linux capabilities, uses an
exact trusted-proxy address, keeps signaling and metrics on internal networks, and provides the
same Docker secret file to signaling and coturn. Production option validation fails before the
server listens when TLS enforcement, the durable pin path, STUN/TURN URLs, UDP/TCP fallback,
deployment identifiers, or the TURN secret are unsafe or incomplete.

1. Create the DNS records and certificates described above.
2. Copy `.env.production.example` to an owner-readable file **outside the repository** and replace
   every example/documentation address.
3. Generate at least 32 random bytes, base64-encode them, and store only the resulting line at the
   path named by `PEERONQ_TURN_SHARED_SECRET_FILE`. Restrict the file to the deployment account.
4. Confirm that each certificate directory contains `fullchain.pem` and `privkey.pem`. The coturn
   image runs as UID/GID 65534, so both mounted files must be readable by that identity and must not
   be writable from the container.
5. Validate and start the stack from this directory:

```bash
docker compose --env-file /secure/peeronq.env -f docker-compose.production.yml config
docker compose --env-file /secure/peeronq.env -f docker-compose.production.yml up -d --build
docker compose --env-file /secure/peeronq.env -f docker-compose.production.yml ps
```

The `.example` hostnames and documentation IP are placeholders and must all be replaced; placeholder
TURN realms deliberately fail production validation. Do not place a populated `.env`, TURN secret,
or private key under the repository. Pin deployed image digests in the environment's release
manifest after the first verified pull.

## Production topology and reverse proxy

The Nginx example terminates TLS, preserves WebSocket upgrade headers, disables response buffering,
caps request bodies at 64 KB, and keeps metrics private. Only the proxy should reach signaling port
8080. Production keeps `Signaling__RequireTlsOutsideLoopback=true`; the application accepts the
proxy's HTTPS decision only because `Signaling__TrustedProxyIp` names that exact container address.
The proxy overwrites, rather than appends, client-supplied forwarding headers.

WebSocket connections are long-lived and have an owning signaling node. During horizontal scale:

- route an existing WebSocket to one node for its lifetime;
- use the `ISessionStore` boundary for a shared, atomic session/resume store before claiming
  signaling-restart continuity;
- preserve connection ownership/version checks so a stale socket cannot relay after replacement;
- use a shared device-presence adapter or route peer messages through a broker;
- drain a node before termination. The server sends `server_restarting`, clients reauthenticate,
  and deployments should allow at least the reconnect window before force-kill.

The repository intentionally does not add Redis yet: the current deployable is a single signaling
node, and an unused Redis dependency would not make it highly available. Redis (or an equivalent
shared store plus pub/sub) becomes required when a second signaling replica is enabled.

## Secrets and rotation

Never store the TURN shared secret, TLS private keys, database credentials, or production `.env`
files in Git or container images. The production Compose stack mounts the secret at
`/run/secrets/peeronq_turn_shared_secret` in both services. Signaling reads that path directly;
coturn copies it into a randomly named mode-0600 runtime configuration on its private `/tmp` tmpfs
so the secret is not exposed in its process arguments.

TURN credential rotation is a pool rotation:

1. Start a new TURN pool with the new secret/server identifier.
2. Update signaling to issue credentials for the new pool.
3. Keep the old pool for at least the maximum credential lifetime plus session drain time.
4. Remove the old pool and secret, then verify allocation/authentication failure metrics.

Rotate signaling TLS normally with overlapping certificate validity. A device public-key pin reset
is an identity/security operation and is not part of routine secret rotation.

## Health and monitoring

- `/health/live`: process liveness only.
- `/health/ready`: returns 503 when TURN endpoints exist but the server-side shared secret is absent
  or shorter than 32 characters.
- `/health`: compatibility summary with aggregate online/session counts.
- `/metrics`: low-cardinality signaling counters with no IDs, tokens, addresses, or credentials.
- coturn port 9641: allocation/traffic metrics; bind it only to the monitoring network.

The production stack names its internal metrics network `peeronq-monitoring`. Attach a trusted
Prometheus container to that Docker network and use `monitoring/prometheus.yml`; do not publish
ports 8080 or 9641 on the host.

Alert on readiness failure, reconnect failure rate, rejected resume rate, TURN allocation failures,
quota saturation, relay bandwidth saturation, certificate expiry, and unusual authentication
failures. Logs must retain masked device IDs and must never include SDP, ICE addresses, resume
tokens, TURN credentials, or the shared secret.

## Backup scope

Back up device public-key pins, signaling configuration metadata, audit retention data (when a
durable implementation exists), monitoring configuration, and encrypted secret-manager versions.
Do not back up ephemeral connection ownership, resume tokens, TURN allocations, media, packet
captures, or coturn temporary credentials. Test restoration separately from production.

## Release checks

Before production rollout:

- verify UDP 3478 and the relay range from outside the hosting network;
- verify TCP 3478 and TLS 5349 with UDP blocked;
- confirm `RelayOnly` selects a relay candidate and reports `Relayed`;
- confirm normal policy selects host/srflx when viable and never labels a path before nomination;
- expire and invalidate TURN credentials and confirm allocations fail;
- interrupt signaling/media during a session and confirm bounded reconnect, frozen screen overlay,
  released input state, unchanged permission scope, and explicit failure after the security window;
- run the two-physical-device internet matrix recorded in `PHASE3.md`.
