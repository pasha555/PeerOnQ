# Phase 9 completion report

## 1. Phase and final gate

**Phase 9 — FAIL (internal Signaling blocker resolved).** The local application tier now supports
two active Signaling instances with shared ownership/session state and cross-node routing. Phase 9
still cannot pass because PostgreSQL and Redis are single-node in the reference deployment and the
required representative active-media failover, rolling, soak, capacity, PITR and physical-host
evidence has not been produced.

## 2. Repository snapshot

- Branch: `feat/phase1-remote-view`
- Base commit: `086631df7d95e71b5e13ce563de55b2a9e844e63`
- Current cumulative worktree: 217 entries (156 tracked, 61 untracked), including the user's Phase
  1–8 work and later fixes. No reset, clean, commit, tag, publication or volume removal was performed.
- Host date/time zone: 2026-08-17, Asia/Baku.

## 3. Areas inspected

Signaling device/connection ownership, live session state and resume tokens, replay/unattended
state, DI/options/readiness, WebSocket routing, Redis key/channel ACLs, Nginx upstreams, Compose HA
overlay, Prometheus replica discovery, Phase 9 fault harness, operations/security/deployment maps
and the prior Phase 9 evidence.

## 4. Components reused

Existing Signaling protocol v3, authenticated WebSocket client, session state machine, device
attestation, TURN issuance, StackExchange.Redis package, Nginx edge, Redis 8.4.4, service health and
metrics contracts, Compose HA overlay, local fault harness, PostgreSQL backup/restore jobs and prior
management-plane warm standbys.

## 5. Confirmed defects and root causes

- `DeviceRegistry`, `SessionRegistry`, replay nonces and unattended challenges were process-local;
  devices on different nodes could not discover or route to one another and a restart lost resume
  authority. They now have Redis-backed cluster implementations.
- Permission/session/resume mutations needed cross-node atomicity. The Redis session store uses
  optimistic compare-and-set for state changes and atomic Lua for creation/security limits; raw
  resume tokens are never stored.
- A hard-crashed process could leave active sessions orphaned. Device owner leases now have a Redis
  expiry index; the janitor converts expired ownership to the existing bounded reconnect state.
- Concurrent requests on different nodes could race past the per-device session bound. Creation is
  now one atomic Redis operation; a real two-node test proves the exact 64-session cap.
- Nginx and the HA overlay had only one Signaling upstream. The overlay now runs two unique active
  instances, separate writable pin volumes and per-replica metrics targets.
- Redis had no Signaling workload identity. A dedicated ACL now limits it to the hash-tagged
  Signaling key/channel namespace; a live Redis ACL smoke proves permitted scripts/pub-sub and
  cross-namespace denials.
- During implementation, a Lua numeric comparison and JSON serialization of `PeerOnQId` failed the
  first real Redis runs. `tonumber` and bounded string DTOs fixed them; the failed runs were not
  relabeled as passing. One ACL harness attempt also failed before container creation because of a
  PowerShell path conversion error; the corrected isolated run passed.

## 6. Files created and modified

Created Redis Signaling backplane, session store, unattended challenge store, replay guard,
distributed tests and Prometheus target files. Modified Signaling options/validation/DI/handler and
store boundaries, device/session janitors, development/HA Compose, Redis ACL/bootstrap, Nginx,
Prometheus, the Windows controller, Phase 9 harness, deployment configuration tests, maps, security,
architecture and operations documents. Linux bootstrap/upgrade paths generate or migrate the new
credential and instance ID without printing either value.

## 7. Migrations

No database migration. Redis keys are internal ephemeral cluster state under
`peeronq:{signaling}:*`; enabling cluster mode requires the dedicated ACL and a cleanly configured
Redis endpoint. No existing PostgreSQL or Redis data was deleted.

## 8. API/protocol/schema changes

No public HTTP API, Signaling protocol version or PostgreSQL schema change. New server-only settings:

- `Signaling__Cluster__Enabled`
- `Signaling__Cluster__InstanceId`
- `Signaling__Cluster__RedisConnectionString`
- `Signaling__Cluster__KeyPrefix`
- `Signaling__Cluster__DeviceLeaseDuration`
- `Signaling__Cluster__DeviceLeaseRefreshInterval`
- `PEERONQ_REDIS_SIGNALING_PASSWORD`

## 9. Security/privacy changes

The dedicated Redis user is key/channel namespace restricted. Owner routes contain only bounded
device/session routing metadata, not tokens, private keys, media, input, clipboard or file content.
Resume tokens are SHA-256 hashed, compared in fixed time and atomically consumed; hash buffers are
zeroed. Replay and unattended challenges are single-use across nodes. Redis/backplane loss fails
readiness and new routing/session operations closed. Metrics/logs retain bounded labels and masked
device identifiers.

## 10. Exact tests and commands

```powershell
dotnet build src/PeerOnQ.Signaling.Server/PeerOnQ.Signaling.Server.csproj -c Release --no-restore

# Ephemeral redis:8.4.4-alpine on 127.0.0.1:6410; removed in finally.
$env:PEERONQ_TEST_REDIS='127.0.0.1:6410,abortConnect=false'
dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~DistributedSignaling'

dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.Observability.Tests/PeerOnQ.Observability.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~DeploymentConfigurationTests'

docker compose --env-file src/PeerOnQ.Infrastructure.Deployment/.env `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml config --quiet
docker compose --env-file src/PeerOnQ.Infrastructure.Deployment/.env `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.ha.yml config --quiet

# Isolated redis:8.4.4-alpine using the checked-in ACL entrypoint and smoke script; removed in finally.
docker exec peeronq-phase9-redis-acl sh /peeronq-acl-smoke.sh
```

## 11. Exact build/test results

| Command/gate | Result |
| --- | --- |
| Signaling Release build | passed; 0 warnings, 0 errors |
| Real Redis distributed Signaling integration | 4 passed, 0 failed, 0 skipped; 6 s |
| Full Signaling suite | 85 passed, 0 failed, 4 explicit external-integration skips; 2 s |
| Deployment configuration tests | 11 passed, 0 failed, 0 skipped; 31 ms |
| Base and HA Compose merged config | passed |
| Staging/production Compose structural merge (`--no-interpolate`) | passed; full interpolation blocked by the local environment's unrelated missing release public key |
| Prometheus config/rules | passed with 15 rules; first invocation used the image entrypoint incorrectly, corrected invocation passed |
| Redis ACL live smoke | permitted signaling key/script/channel passed; cross-namespace access denied |
| PowerShell Phase 9 harness parse | passed |
| Linux bootstrap/installer shell syntax and installer containment test | passed |

## 12. Runtime and physical/external evidence

Two real ASP.NET Signaling processes communicated through real Redis 8.4.4 and real WebSockets in
one process-host test environment. Cross-node presence, session request, permission, SDP and end
routing were exercised. A real TCP proxy then pinned the viewer/host to different nodes, stopped the
viewer's node, routed reconnect to the survivor, resumed the already-active signaling session and
relayed fresh SDP before a clean end. Shared resume/challenge/replay state, owner-expiry consumption
and parallel capacity enforcement were exercised directly against Redis. No physical second computer, active
WebRTC media session during node loss, real HA Redis/PostgreSQL, public endpoint or second region was
part of this revision.

## 13. Feature truth matrix

| Capability | Status | Evidence |
| --- | --- | --- |
| Shared Cloud worker ownership | `IMPLEMENTED_LOCAL_ONLY` | Prior real Redis lease transfer |
| Cloud/Presence/Downloads warm standbys | `IMPLEMENTED_LOCAL_ONLY` | Prior 60/60 local stop gate |
| Multiple Signaling instances | `IMPLEMENTED_CURRENT_ENV` | Two server processes, WebSockets and real Redis |
| Cross-node live session/routing state | `IMPLEMENTED_CURRENT_ENV` | Complete cross-node session flow and atomic state tests |
| Hard-crash ownership cleanup | `IMPLEMENTED_CURRENT_ENV` | Expiry index consumption and disconnect transition test |
| HA PostgreSQL | `NOT_FOUND` | One Compose node; external contract only |
| HA Redis | `NOT_FOUND` | One Compose node; external contract only |
| Active Signaling session node-loss resume | `VERIFIED_CURRENT_ENV` | Real WebSockets/TCP proxy, node stop, survivor resume and fresh SDP |
| Physical active-media node-loss resume | `UNKNOWN` | No WebRTC media/physical host participated |
| PostgreSQL backup/isolated restore | `VERIFIED_PRIOR_CURRENT_HOST` | Prior new archive and 35-table restore |
| Sustained soak/capacity/download/TURN load | `UNKNOWN` | Not run representatively |
| Multi-region | `NOT_FOUND` | No second region |

## 14. Measured performance and environment

Toolchain: .NET SDK 10.0.303, Docker 29.7.2, Node 25.2.1 and pnpm 10.33.0. The new distributed
integration completed in about one second on one Windows host. This is functional evidence, not a
throughput, latency, SLO, RTO or RPO claim. The prior local management-plane measurements remain in
the ignored Phase 9 evidence bundle and are not recharacterized here.

## 15. Brand purity

New scoped runtime/deployment files use PeerOnQ identifiers only. No new legacy product identifier
was introduced.

## 16. Dependency/SBOM/provenance impact

The Signaling project now directly references the centrally pinned existing `StackExchange.Redis`
package. Redis and Nginx image versions are unchanged. No package version, lockfile, generated client,
image digest or signing input changed.

## 17. P0/P1/P2 issues

- **P0:** none discovered in the affected cluster path.
- **P1:** reference PostgreSQL and Redis are not highly available; no physical active-media
  Signaling-loss, data failover/data-loss, sustained soak/capacity, PITR or TURN failover proof.
- **P2:** no new code-level issue remains open in the affected cluster path; the remaining evidence
  gaps are deployment/scale prerequisites listed as P1.

## 18. Known limitations

One host, one Redis/PostgreSQL/coturn, local Redis AOF, no physical media failover, no migration under
load, no off-host backup, no multi-region routing, no representative capacity threshold. The local
in-memory Signaling store remains intentionally single-process and must not be mixed with replicas.

## 19. External blockers

A real PostgreSQL leader/replica environment, Redis Sentinel/managed failover with persistence and
Admin MFA recovery, physical client/server hosts, routable TURN redundancy, production-like load
generators, off-host encrypted WAL/PITR storage and an approved production secret manager are not
available in this workspace. The former internal distributed-Signaling blocker is resolved.

## 20. Compatibility impact

Protocol v3 clients are unchanged. Development/staging Compose now enables Redis-backed Signaling
even with one instance; the Windows controller adds the new ignored Redis password automatically.
Standalone tests/local hosts that do not enable cluster mode continue using the in-memory stores.
HA overlay users receive a second active Signaling instance and per-replica metrics.

## 21. Readiness decision

**Do not start Phase 10 under the execution contract yet.** Distributed Signaling implementation is
no longer the blocker. Validate real PostgreSQL/Redis failover and recovery/data loss, then run the
physical active-session, rolling, soak, capacity, migration-under-load, download, TURN, security and
cross-tenant concurrency gates. Reissue this report only after those gates.
