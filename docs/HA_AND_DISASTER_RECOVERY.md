# High availability and disaster recovery

## Status and claim boundary

PeerOnQ has a repeatable **single-host, single-region validation overlay** for the application tier.
It includes management-service warm standbys and two active Signaling instances, but it is not a
production multi-zone, data-tier HA, or multi-region topology. The open-source single-node profile
remains the default reference. Apply
`docker-compose.ha.yml` only with `docker-compose.development.yml` for the bounded local gate.

Phase 9 is not complete: the reference PostgreSQL and Redis services each have one node, and the
representative physical active-session, data-failover, soak, capacity and PITR gates are outstanding.

## State ownership inventory

| Area | Authority and consistency | Scale/failure behavior |
| --- | --- | --- |
| Cloud API | PostgreSQL is durable authority; Redis owns one-use challenges, access-token state and short leases | Warm standby is safe. Singleton workers require the renewable Redis operation lease before mutating state. Redis uncertainty cancels the owned run. |
| Presence | Redis lease and SignalR backplane state; PostgreSQL stores bounded history | Two instances are safe. Shutdown reconnect notices target only sockets local to the stopping node; a healthy node's clients are not disrupted. |
| Signaling/device routing | Local `DeviceRegistry` sockets plus Redis owner leases/pub-sub routes | Each device has one expiring distributed owner. Cross-node frames target the owning connection; hard-crash lease expiry enters bounded reconnect state. |
| Live sessions/resume ownership | `ISessionStore`; Redis implementation in cluster mode | Permission/state changes, owner binding, one-use resume-token hashes and per-device limits are atomic across nodes. The in-memory implementation remains single-process test/local compatibility only. |
| Session telemetry | PostgreSQL | Shared across Cloud API replicas; it is not the live media/signaling authority. |
| TURN credentials | Short-lived credentials derived by Signaling from the mounted shared secret | Credential derivation is stateless, but the reference has one coturn instance and no tested capacity/failover pool. |
| Downloads | Release metadata in PostgreSQL; immutable origin; verified cache local to each Downloads replica | Two service instances are safe only with separate cache volumes. A cache miss is independently size/SHA-256 verified before streaming. |
| Diagnostics | PostgreSQL metadata; development file volume or configured HTTP object-storage boundary | Multi-node production requires the external object-storage boundary; the local filesystem provider is explicitly single-node. |
| Admin sessions/MFA | PostgreSQL session rows; rotating token hashes; Redis Data Protection key ring | API replicas can share state. Redis persistence/restore is mandatory because MFA ciphertext depends on the key ring. |
| Rate limits | Per-edge Nginx shared-memory zones and service-local connection/message limits | Limits are intentionally local to each edge/process. They are overload protection, not a global quota or billing control. |
| Distributed locks | Redis renewable leases under the PeerOnQ key prefix | Used only for presence expiration, stale-session reconciliation and retention. Random owners are never logged; compare-and-renew/delete prevents one replica from extending or deleting another lease. |
| Audit and alert events | PostgreSQL with application and database append-only controls | Shared durable authority. Restore/failover must preserve triggers, role grants and legal-hold policy. |
| Support invitations | Issuing device's DPAPI-protected local profile; only token hash/verifier and lifecycle record persist | Implemented for the single-device pilot. No server replication or HA claim; loss of the issuing profile invalidates its invitations. |

## Traffic admission and rolling behavior

Cloud API, Presence and Downloads expose process, startup and readiness probes. Application stop
marks the instance draining; readiness becomes unhealthy and non-probe HTTP requests receive a
bounded `503 service_draining` response with `Retry-After`. Nginx keeps probe locations outside
public request/connection limits, retries safe upstream failures, and uses an explicit warm backup
peer in the local HA overlay. Nginx does not retry non-idempotent methods.

The overlay uses one active and one warm standby for each of Cloud API, Presence and Downloads, and
two active Signaling instances behind `least_conn` WebSocket routing. Both Signaling instances use
the dedicated `peeronq_signaling` Redis ACL and have separately scraped metrics targets.
That arrangement avoided concurrent first-failure `504` responses observed with two passive-active
Nginx peers. It proves local service continuity, not horizontal throughput scaling.

## Data-service production options

The repository accepts PostgreSQL and Redis through network connection strings and does not require
a proprietary provider. A production operator may supply:

- a PostgreSQL cluster with one writer, automatic leader election, synchronous or asynchronous
  replicas chosen to match an approved data-loss policy, a stable writer endpoint, WAL archiving
  for point-in-time recovery, and exactly one migration job;
- Redis Sentinel/replication or another protocol-compatible highly available Redis deployment with
  ACLs, encryption, persistence and a stable endpoint; Admin Data Protection recovery must be
  tested, not inferred;
- an encrypted S3-compatible or equivalent HTTP object-storage adapter for immutable release and
  diagnostic objects, with lifecycle policy and tenant/object-prefix authorization;
- an external secret manager or orchestrator secret mount. Secret values must never enter Compose
  files, images, logs, evidence JSON or command arguments.

Those are integration contracts, not evidence that a data cluster exists. The checked-in Compose
PostgreSQL/Redis containers remain single-node and must not be presented as highly available. Redis
is now also authoritative for live clustered Signaling state, so loss/failover testing must cover
session resume and one-use security state in addition to Admin MFA/Data Protection recovery.

## Backup, recovery and measured evidence

PostgreSQL backup uses `pg_dump --format=custom`, validates with `pg_restore --list`, writes a
SHA-256 sidecar, and restores only into a uniquely named isolated database for the test drill. A real
restore over the production writer requires the explicit destructive confirmation and operator
approvals in `OPERATIONS.md`.

The 2026-08-17 local drill produced a 164,090-byte archive, SHA-256
`47D9963052176F0DDAE4BE01FAB2BB369B37748184ED64E0C3D23F0D271B194D`, and restored 35 public tables
before dropping the isolated test database. This proves archive readability on one host. It does
not prove off-host durability, point-in-time recovery, a production RPO, or a production RTO.

The local interruption gate measured PostgreSQL and Redis container restart-to-healthy plus API
recovery, but made no test-owned durable write and therefore reports data loss as **not measured**.
No RPO is declared. See the Phase 9 report and ignored `.peeronq-phase9/evidence/` JSON for exact
hardware-qualified observations.

The implementation gate additionally ran two real Signaling servers against one ephemeral Redis
8.4.4 instance. It proved cross-node registration/presence, permission, SDP and end routing; shared
session visibility; atomic single-use resume/challenge/replay state; hard-crash owner expiry
consumption; and an exact 64-session concurrent device bound. This is real process/network evidence,
not a physical multi-host or Redis-failover result.

## Multi-region readiness

No multi-region implementation or claim exists. Before adding regional routing, PeerOnQ requires
two genuinely separate environments, regional TURN capacity/health, tenant data-residency policy,
cross-region ownership semantics, replication conflict decisions, safe fallback, and measured
regional-failure tests. One Docker host is never evidence for those properties.
