# PeerOnQ Phase 6 Completion Report

## Release status

**Local implementation status: complete. Public staging acceptance: pending deployment.**

Phase 6 adds the production cloud control plane needed to operate PeerOnQ without replacing the
working Windows capture/control, WebRTC, signaling, TURN, file-transfer, authentication, installer,
or signed-update paths from Phases 1-5. The implementation has been built and tested on one Windows
11 workstation with real Dockerized PostgreSQL, Redis, signaling, coturn, monitoring, alerting, and
backup/restore. It has not yet been deployed behind public DNS and trusted public certificates, so
this report does not claim multi-region or public-internet production acceptance.

## 1. Repository inspection

The implementation was based on the repository maps and the existing security/session boundaries,
not a parallel rewrite. The inspected sources included `ARCHITECTURE.md`, `SECURITY.md`, Phase 1-5
reports, signaling protocol/registry, TURN credential issuance, signed update validation, Windows
identity and diagnostics, WiX installer, existing Compose/proxy files, current migrations/tests, and
the offline website/admin design references.

The legacy Express/Drizzle packages remain a Replit skeleton. They were not promoted into a second
production data model. Phase 6 is implemented in the requested ASP.NET Core projects with one
authoritative EF Core/PostgreSQL model.

## 2. Architecture

Dependency direction is intentionally one-way:

```text
PeerOnQ.Shared.Contracts       versioned wire contracts only
PeerOnQ.Cloud.Domain          entities and invariants
PeerOnQ.Cloud.Application     use cases and infrastructure ports
PeerOnQ.Cloud.Infrastructure  PostgreSQL, Redis, repositories, migrations, workers
Host services                 Cloud API, Presence, Admin API, Downloads
PeerOnQ.Observability         shared hosting/telemetry extensions
```

PostgreSQL owns durable device, installation, session, release, diagnostic, admin, audit, health,
alert, and retention-evidence state. Redis is limited to ephemeral/distributed concerns: one-use
authentication challenges, presence leases/ownership, and Admin Data Protection keys. Screen,
input, clipboard, file-transfer, and media payloads never enter these services.

The design is horizontally scalable at the stateless host boundary and region-aware without adding
Kubernetes prematurely. The staging Compose topology is the deployable baseline; the operations
documentation describes external managed PostgreSQL/Redis/object storage, load balancing, durable
Data Protection keys, and multi-region evolution.

## 3. Services created

| Project | Responsibility |
| --- | --- |
| `PeerOnQ.Shared.Contracts` | V1 DTOs, typed errors/enums, canonical ECDSA challenge contracts |
| `PeerOnQ.Cloud.Domain` | Device/install/session/release/diagnostic/admin/audit/retention invariants |
| `PeerOnQ.Cloud.Application` | Registration, authentication, telemetry, queries, retention abstractions |
| `PeerOnQ.Cloud.Infrastructure` | EF Core/Npgsql, Redis, repositories, migrations and retention worker |
| `PeerOnQ.Cloud.Api` | Device/install/session/update/diagnostics endpoints |
| `PeerOnQ.Presence.Server` | Authenticated installation-scoped SignalR presence leases |
| `PeerOnQ.Admin.Api` | MFA/RBAC/CSRF admin authentication, queries and privileged actions |
| `PeerOnQ.Downloads.Service` | Eligible signed-release streaming and privacy-safe download events |
| `PeerOnQ.Observability` | Structured logs, OTel, health and low-cardinality metrics |
| `PeerOnQ.Infrastructure.Deployment` | Development/staging plus single-node production-pilot override, public web/update proxy, monitoring and backup/restore |

`artifacts/peeronq-admin` is a separate production React/Vite admin SPA. The contractually offline
`artifacts/peeronq` preview remains offline; only its ordinary download links can be configured to an
HTTPS tracked-download origin.

## 4. Database changes

The PostgreSQL model contains 21 entities: Device, Installation, RemoteSession, SessionFailure,
DevicePresenceHistory, DownloadEvent, AppRelease, UpdateEvent, DiagnosticBundle,
DiagnosticAccessEvent, AdminUser, AdminRole, AdminUserRole, AdminRecoveryCode, AdminSession,
AuditEvent, InfrastructureRegion, ServiceHealthSnapshot, AlertEvent, RetentionBatchEvidence, and
RetentionPolicy.

Eight forward-only migrations are present:

1. `20260811105749_InitialCloudPlatform`
2. `20260811110947_SeedAdminRoles`
3. `20260811123000_EnforceAppendOnlyAudit`
4. `20260811124500_AddGovernedRetention`
5. `20260811125928_ProofBoundDeviceEnrollment`
6. `20260811130500_HardenGovernedRetentionExecution`
7. `20260811131500_EnforceDatabaseRetentionPolicies`
8. `20260811141015_AddSessionActivityHeartbeat`

The schema uses UUID keys, UTC timestamps, foreign keys, bounded fields, uniqueness constraints,
indexes for admin/filter/retention paths, optimistic state where ownership changes, and PostgreSQL
triggers that reject update/delete of append-only audit/access/evidence rows. Governed audit/alert
retention requires an explicit policy version, hard retention floors, legal-hold approval, bounded
batches, and immutable SHA-256 aggregate evidence. Audit retention defaults to disabled.

The complete migration set was applied to a real PostgreSQL 17 container. Backup and restore were
also exercised against PostgreSQL 17; the restored database contained 22 public relations (21
application tables plus `__EFMigrationsHistory`).

## 5. Client integration

- Official Windows Release builds receive Development/Staging/Production API, Presence, Signaling,
  Updates, Downloads, and Diagnostics endpoints through assembly metadata. Production users cannot
  edit or persist arbitrary endpoints.
- Production requires HTTPS/WSS with normal certificate validation. Loopback HTTP/WS overrides are
  available only to development builds.
- First run requests a one-use device challenge bound to InstallationId, SPKI fingerprint, platform,
  version, architecture, channel and protocol. ECDSA P-256/SHA-256 proof is consumed atomically;
  Cloud creates the Device and Installation and assigns the non-authoritative 12-digit routing alias.
  The client then confirms the installation through the authenticated installation endpoint.
- Successful proof-bound authentication also returns a five-minute, versioned ECDSA cloud
  attestation binding the assigned numeric alias, cloud DeviceId, InstallationId and exact SPKI
  fingerprint. Every signaling registration/reconnect supplies the latest token and still signs a
  fresh signaling nonce. Staging/Production signaling validates the cloud signature statelessly and
  never treats a missing/corrupt node-local TOFU pin as a trust root; only explicit
  Development/Testing configuration may use legacy TOFU.
- Tokens are short-lived and refreshed with bounded exponential backoff/jitter. Permanent auth and
  identity mismatch errors are not retried indefinitely.
- Presence and telemetry are optional cloud integrations around the existing local/signaling path;
  a cloud outage does not prevent the local app from starting or weaken session security.
- SQLite-backed outboxes emit real session started/connected/ended and update
  offered/downloaded/failed/installed-after-restart events idempotently.
- While media is actually connected, the existing 25-second cloud loop sends a bounded,
  authenticated session heartbeat. Cloud stores server-observed `LastActivityAtUtc`; process loss
  stops heartbeats instead of reconstructing false live state from disk. Negotiations expire after
  five minutes, while connected sessions expire after three minutes without activity. Both
  thresholds, the one-minute sweep and the 500-row batch are validated configuration. A later
  authenticated terminal event can correct a reconciled stale record.
- The existing stronger TURN rule is preserved: expiring coturn credentials are issued only after
  both authenticated peers accept a session. No static TURN credential was moved into a client or
  exposed merely because an installation token exists.

## 6. Presence implementation

`/presence/v1/hub` authenticates the device token and owns one active lease per InstallationId while
allowing multiple installations for one device. A lease includes server ID, region, app version,
state, epoch and expiry. Duplicate connections displace the previous epoch; stale disconnects cannot
delete a newer lease. Heartbeat, graceful disconnect, unexpected timeout, server sweep, blocked and
unsupported states are explicit. Redis is the live source for distributed online state; PostgreSQL
stores bounded presence summaries rather than every heartbeat.

Local defaults use a 25-second heartbeat, 75-second lease, and 15-second stale sweep. Admin online
counts use distinct devices represented by valid leases, not a persistent database boolean.

## 7. Download analytics

`GET /windows/latest` and `GET /windows/{version}/{architecture}` resolve only active eligible
release rows that contain the signed-manifest digest produced by the Phase 5 release pipeline.
There is no unsigned publish endpoint. Local acceptance correctly returns 404 when no signed release
is seeded instead of serving a placeholder.

Start/complete events are idempotent and distinguish total starts, completed downloads, privacy-safe
unique estimates, installations, and active installations. Raw IP addresses are not stored as durable
analytics; source/campaign/country/user-agent data is bounded and minimized. Completion uses a
one-use keyed capability. Release adoption distinguishes offered, downloaded, installed, failed and
rollback events.

Origin bytes are verified completely into a quota-controlled digest cache before any response byte
is sent. Size, SHA-256, strong ETag, concurrency and stream deadline are enforced; range responses
are served only from the immutable verified cache and are recorded as Partial rather than Completed.

## 8. Admin authentication

- Separate admin identity boundary; device credentials cannot authenticate an administrator.
- ASP.NET password hashing, mandatory TOTP/recovery-code MFA for privileged roles, short access
  lifetime, rotating hashed refresh tokens, lockout and rate limits.
- `__Host-peeronq_refresh` is HttpOnly, Secure, SameSite=Strict; unsafe authenticated requests use a
  double-submit CSRF value.
- Roles: Owner, SecurityAdministrator, OperationsAdministrator, SupportAgent, ReleaseManager and
  ReadOnlyAnalyst. Navigation and server authorization both enforce least privilege.
- Session listing/revocation and all privileged actions generate immutable audit records.
- Access tokens carry the exact AdminSession ID. Every authenticated request checks that the bound
  database session is active and unexpired, so logout or operator revocation invalidates the current
  access token immediately rather than waiting for its five-minute expiry.
- Bootstrap credentials are deployment-only inputs and are absent from source control.

The live Docker acceptance completed login, `mfa_required`, TOTP verification, authenticated session,
overview, distributions, and infrastructure metrics against real PostgreSQL/Redis.

## 9. Admin API

The versioned API implements overview, devices, installations, presence, sessions, downloads,
releases, diagnostics, infrastructure, audit, alerts and admin-session routes with bounded paging,
filtering, sorting, date windows, validation, rate limits and RBAC. Mutations cover installation
block/unblock, irreversible device revoke, admin-session revoke, diagnostic open/access evidence,
and rollout changes for an already signed release. A release cannot be published unsigned.

The admin SPA consumes the real API contract. It contains responsive Overview plus ten resource
pages, role-aware navigation, loading/empty/offline/stale/degraded states, and an honest unavailable
state when the API is absent. It contains no fake production metrics or embedded credentials.

## 10. Logging

Cloud, Presence, Admin and Downloads hosts share structured Serilog output and OTel correlation with
UTC time, service, environment, region, severity, event, correlation/trace/request IDs and sanitized
error metadata. Signaling/TURN issuance retain their existing structured security logging. Loki and
the OTel Collector provide centralized query/correlation in the deployment stack.

The Windows client keeps bounded rolling JSON logs under `%LOCALAPPDATA%\PeerOnQ\logs`. Device IDs,
IPv4/IPv6, paths, tokens, credentials, private material and numeric PeerOnQ IDs are sanitized. Log and
diagnostic tests cover redaction. Metric labels never include device, installation, user or session IDs.

## 11. Metrics and tracing

Every Phase 6 host exposes `/health/live`, `/health/ready`, `/health/startup` and `/metrics`.
Readiness checks required PostgreSQL/Redis/storage dependencies; liveness remains process-only.
Metrics are low-cardinality and include installations, auth/session/update/download/diagnostic
results, presence connections, latency/error counters and worker failures. Fixed recording rules map
real coturn exporter allocation/traffic counters to PeerOnQ TURN views.

The final local stack had 11/11 Prometheus targets UP: four Phase 6 APIs, signaling, coturn, OTel,
node exporter and three dependency probes. Fifteen alert rules loaded. Grafana, Loki, Tempo,
Prometheus, Alertmanager, Blackbox and node exporter returned healthy status; the OTel Collector had
zero errors in the final 30-second check. Metrics bodies returned Prometheus content type and matched
zero sensitive-data patterns.

## 12. Diagnostics

The client flow is explicit Settings/Advanced consent, a visible inclusion list, sanitized bounded
ZIP generation, short-lived upload authorization, SHA-256 verification and a reference/status ID.
The 20 MiB ingress boundary rejects malformed, oversized, wrong-hash and non-ZIP content. Before any
blob persistence, the server parses the ZIP and enforces the exact manifest/log allowlist, safe paths,
unique entries, CRC, strict UTF-8/JSON schema, decompression/ratio limits, and sensitive-data patterns;
encrypted, symlink, traversal, binary, personal-file and hidden-content archives fail closed. Allowed
content is structured logs/health/version/OS/architecture/WebRTC summary/failure/update/schema
metadata. Keys, tokens, passwords, clipboard, frames, personal files, paths, full IDs, raw dumps and
file contents are excluded. Diagnostic open and access are separately audited; expiry is visible and
enforced by governed retention.

## 13. Privacy controls

`docs/PRIVACY_ARCHITECTURE.md`, `docs/DATA_CLASSIFICATION.md`, and `docs/DATA_RETENTION.md` define
Public/Internal/Confidential/Restricted classes, purposes, minimization, access, deletion,
anonymization, legal holds and default retention. Authentication material and secrets are Restricted.
Diagnostics are consented and expire. Download uniqueness avoids invasive fingerprinting. Session
analytics contain operational metadata only. No screen, clipboard, keystroke, pointer, filename,
file-content, password, private key or personal-document field exists in the cloud contracts/schema.

## 14. Tests

Coverage includes device/installation registration, canonical challenge, expiry/replay/clone/
fingerprint rejection, malformed/oversized/rate-limited requests, presence heartbeat/duplicate/
timeout/outage, real session lifecycle, participant-bound idempotent session heartbeat, separate
negotiation/connected stale reconciliation and late terminal correction, download idempotency/completion,
release adoption, update events, diagnostics consent/sanitization/hash/size/expiry, admin MFA/RBAC/
CSRF/refresh/session revoke, immutable audit/access records, governed retention/legal holds,
metrics/health, PostgreSQL migrations and Redis atomicity.

The complete solution test run passed 415 tests with zero failures. Two explicit opt-in
Docker tests were skipped by that generic command; equivalent signaling/TURN and current Phase 6
container behavior was exercised separately in the live Compose acceptance.

## 15. Deployment

The Phase 6 deployment directory contains development and staging Compose, an environment template,
TLS reverse-proxy config, service Dockerfiles, PostgreSQL/Redis, signaling/coturn, admin UI, OTel,
Prometheus, Loki, Tempo, Grafana, Alertmanager, Blackbox/node exporters, alert/recording rules, and
backup/restore scripts. Operator documents cover DNS, certificates, ports/firewall, secret rotation,
database/Redis durability, object/log/metric storage, scaling, regions, incidents and runbooks.
PostgreSQL is not published to run schema updates: a profile-scoped, non-root, read-only one-shot EF
migration-bundle container runs on the private control network and must exit successfully before API
traffic is started.

On Windows, `peeronq-start.bat` is the complete development entry point. It creates a scoped trusted
`*.dev.localhost` certificate under ignored `.peeronq-phase6/`, runs migrations and runtime grants,
rebuilds the full Compose stack, waits for `https://admin.dev.localhost:8443`, and starts the offline
preview with visible links to Admin, Cloud health, Grafana and Prometheus. Stop preserves all named
PostgreSQL/Redis volumes.

All five new service images build without production secrets. The four .NET images run as UID 1654;
the admin Nginx image runs as non-root UID 101, uses patched Alpine nginx `1.28.3-r7`, removes unused
modules/curl and had no detected critical/high package vulnerability.

Production Redis cannot be treated as disposable while it holds ASP.NET Data Protection keys used
to protect admin MFA secrets. Use managed durable Redis with encrypted backup/restore tests or move
Data Protection keys to a durable external key store.

## 16. Actual build results

Validation host: Windows NT `10.0.26200.0`, 16 logical processors, .NET SDK `10.0.302`, local Node
`25.2.1`, pnpm `10.33.0`, Docker Engine `29.7.2`, Docker Compose `5.3.1`. Production frontend build
containers use pinned Node 24 and patched Alpine packages.

| Command | Actual result |
| --- | --- |
| `dotnet build PeerOnQ.slnx -c Release --no-restore` | 30 projects; 0 warnings; 0 errors; 46.26 s |
| `pnpm run typecheck` | All libraries, scripts and artifact workspaces passed |
| `pnpm run build` | All workspace builds passed |
| `pnpm --filter @workspace/peeronq-admin run build` | 245.34 kB JS / 77.59 kB gzip; passed |
| Five Phase 6 Docker builds | Cloud, Presence, Admin API, Downloads and Admin UI passed |
| Compose configuration validation | Development and staging passed without interpolation errors |

## 17. Actual test results

| Command/condition | Actual result |
| --- | --- |
| `dotnet test PeerOnQ.slnx -c Release --no-build --no-restore` | 415 passed, 0 failed, 2 explicit opt-in Docker tests skipped |
| Real PostgreSQL 17.6 + Redis 8 infrastructure suite | 31/31 passed; 8 migrations, triggers, retention, heartbeat and Redis atomicity ran |
| `pnpm run test` | Admin 15/15 plus public/offline product 38/38; 53/53 passed |
| Live Phase 6 host probes | Four services x live/ready/startup/metrics = 16/16 HTTP 200 |
| Live proof-bound connection | Cloud ECDSA enrollment -> server alias -> Presence register/heartbeat -> signed signaling passed; four invalid-attestation cases rejected |
| Live admin authentication | Password -> MFA -> session -> CSRF logout passed; the same sid-bound access token then returned 401 |
| Live monitoring | Prometheus targets 11/11 UP; 15 rules; monitoring components 7/7 healthy |
| Alert end-to-end | Alertmanager -> authenticated Admin webhook -> PostgreSQL AlertEvent passed |
| Backup/restore | `pg_dump` backup and isolated clean restore passed; 22 public relations verified |
| Unauthorized probes | Cloud/Presence/Admin returned 401; tokenless alert webhook returned 401 |
| Empty release store | Downloads latest returned honest 404; no placeholder/unsigned package served |
| Dependency scans | pnpm and NuGet reported no known vulnerable dependency; image critical/high scan clean |
| Repository secret scan | No production secret/private key credential pattern found |

Local Docker smoke used real PostgreSQL, Redis, SignalR, HTTP/TLS proxying, signaling, coturn,
Prometheus exporters, Alertmanager and the production host binaries. Test containers and temporary
credentials were removed after validation.

## 18. Known limitations

- Public DNS, trusted public TLS, production object storage, real SMTP/identity-provider alerts,
  managed database backup, multi-node presence ownership and multi-region failover are deployment
  acceptance work and were not claimed from a local workstation.
- No signed production release was available to seed the local release table; therefore the download
  service correctly returned 404. End-to-end CDN range/performance and real signed MSI delivery must
  be rerun after Phase 5 release signing in staging.
- A real installed Windows client on two physical machines was not exercised against this local
  Phase 6 stack. The client protocol/bootstrap is covered by unit/integration tests; public staging
  must validate installation registration, sleep/wake, version reporting and diagnostics upload.
- Audit retention is deliberately disabled by default. Enabling it requires an approved policy,
  legal-hold review and the configured PostgreSQL function permission.
- The combined public/portal/desktop-preview bundle still reports its pre-existing large-chunk and
  vendored sourcemap reporting warnings; it builds successfully. Third-party font requests were removed.

## 19. Security risks

- Migration, backup, retention-executor and per-service PostgreSQL roles are separated. Redis uses
  per-service ACL users and key prefixes; Presence validates device tokens through a dedicated
  read-only token keyspace user. Production must preserve these grants instead of collapsing them
  into an owner credential.
- Compromise of the HMAC, admin token/refresh, download completion, diagnostics bearer, TURN shared
  secret or Data Protection keys requires the documented rotation and session/token revocation
  sequence. These values must live in a secret manager, not Compose `.env` in production.
- Admin bootstrap variables are one-time deployment inputs and must be removed after successful
  audited bootstrap. Recovery codes must be rotated and protected offline.
- Diagnostics object storage, Loki/Tempo/Grafana and backup destinations need environment-specific
  access controls, encryption and retention enforcement; the local Compose defaults are not a public
  security boundary.
- Public launch still requires external penetration testing and a server-side configuration/secret
  review. Local dependency and protocol tests do not replace that independent assessment.

## 20. Phase 7 integration points

- Nullable ownership IDs on Device/Installation allow future account, organization and team binding
  without changing the device cryptographic identity.
- Admin roles/policies are isolated from future customer organization roles; do not reuse operator
  tokens as customer tokens.
- Release/channel/adoption and installation activity can feed licensing, but Phase 6 contains no
  billing entitlement bypass or hidden account requirement for personal quick support.
- Shared V1 contracts and application ports provide stable boundaries for account linking, company
  policies, seat/device limits and enterprise audit export.
- Add organization-scoped authorization and row ownership before exposing current admin query models
  to customer portals. Raw operator endpoints must remain internal.
- Multi-region presence and session ownership can add a shared broker/region router behind existing
  leases and event contracts without moving remote media through the cloud control plane.

## 21. Production-pilot and signed release hardening

- Admin Releases now uploads an offline-signed ECDSA manifest and matching MSI, verifies the trusted
  key, origin, immutable path, metadata, validity, exact size and SHA-256, then publishes atomically.
- Rollout changes require a newly offline-signed manifest; the legacy mutable-percentage endpoint
  fails with 409. The signing private key never enters the Admin browser or server.
- All protected Admin mutations now enforce the existing double-submit CSRF cookie/header contract,
  in addition to short-lived bearer authentication, MFA policy, RBAC and rate limiting.
- `peeronq.com` public web and `updates.peeronq.com` immutable artifact routing are included in the
  single-node stack. Release storage is writable only by the Admin container identity and read-only
  at the proxy.
- The Linux server `.run` builder/installer adds payload integrity, safe extraction, fail-closed
  Compose validation, health wait, previous-release application rollback and optional required GPG
  signing. It does not claim or install a Linux desktop client.
- The first-run `--bootstrap` path now generates independent root-only secrets, MFA onboarding,
  Data Protection/signaling material, validates or obtains public TLS, detects the LAN address, and
  writes coturn's required `public-ip/private-ip` mapping for the `31.171.38.28` NAT pilot. A renewal
  timer validates changed certificates before restarting only the proxy and TURN services.
- A private persistent diagnostic volume is available only behind an explicit single-node production
  opt-in. Admin bootstrap values are atomically scrubbed and Admin API is recreated after the first
  healthy deployment; the remaining onboarding record stays root-readable only.
- Admin **Releases** remains the client upgrade surface for a verified Authenticode MSI and offline
  ECDSA-signed manifest. Linux host upgrades intentionally remain verified `.run` commands rather
  than an Admin-triggered remote-code-execution capability.
- The guarded public-pilot client compiles the final PeerOnQ API, Presence, Signaling, Updates,
  Downloads and Diagnostics origins for two-device acceptance, while remaining visibly unsigned and
  unable to consume automatic updates until production signing trust exists.
- Dependency, secret, Nginx, Compose, container critical/high CVE and focused security tests passed.
  Independent external penetration testing and public server acceptance remain launch gates.

## Acceptance criteria matrix

| # | Criterion | Result |
| ---: | --- | --- |
| 1 | Windows client registers securely | Passed in client/core integration tests; physical staging install pending |
| 2 | Device authentication verified | Passed: canonical ECDSA, expiry, replay, clone and fingerprint tests |
| 3 | Online/offline status accurate | Passed against Redis lease state |
| 4 | Heartbeat timeout works | Passed |
| 5 | Admin shows real online count | Passed; distinct valid Redis leases, no fake rows |
| 6 | Active session count is real | Passed; requires actual Connected lifecycle event |
| 7 | Download count tracked | Passed |
| 8 | Installations distinct from downloads | Passed in separate entities/metrics |
| 9 | Version distribution real | Passed; bounded top-N plus Other |
| 10 | TURN usage visible | Passed from session UsedTurn plus coturn recording rules |
| 11 | Client diagnostics export works | Passed |
| 12 | Upload requires consent | Passed |
| 13 | Sensitive data redacted | Passed sanitization/metrics/secret-pattern tests |
| 14 | Admin MFA works | Passed live TOTP flow |
| 15 | Role authorization works | Passed |
| 16 | Privileged actions audited | Passed, including diagnostic open/session revoke |
| 17 | Metrics emitted | Passed |
| 18 | Health checks work | Passed 16/16 live checks |
| 19 | Alert runbooks documented | Passed; 15 rules with owner/severity/window/runbook/escalation |
| 20 | Backup and restore tested | Passed locally against PostgreSQL 17 |
| 21 | No screen/clipboard/file content in analytics | Passed by contract/schema review and tests |
| 22 | No private key/password in logs | Passed redaction and secret scans |
| 23 | Production secrets absent | Passed repository scan; populated local env is ignored |
| 24 | All tests pass | Passed; 468 automated tests across .NET and frontend, 0 failures; 2 explicit opt-in Docker tests skipped |
| 25 | Actual command outputs reported | Passed in sections 16-17 |
