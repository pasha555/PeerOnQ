# PeerOnQ Phase 6 completion report

## 1. Phase and final gate

**Phase:** 6 — self-hosted cloud services, Admin Panel, downloads, deployment,
observability, backup, and operations.

**Final gate:** `PASS_WITH_EXTERNAL_BLOCKERS`.

The reproducible single-node local stack, operational metadata boundary, migrations, Redis
coordination, authenticated Presence/signaling, private Admin surface, observability, protected local
backup, and isolated restore satisfy the core dependency for the next phase. Public/physical proof
that requires an authorized Internet environment, trusted signed Windows artifact, two separate
laptops, or off-host infrastructure remains explicitly blocked and is not represented as complete.

## 2. Repository snapshot

- Branch at validation: `feat/phase1-remote-view`.
- Base `HEAD`: `086631df7d95e71b5e13ce563de55b2a9e844e63`.
- Date: 2026-08-17; Windows host in Asia/Baku.
- The worktree already contained cumulative, uncommitted Phase 1–5 work. Phase 6 changes were made
  in place without resetting, deleting, or overwriting those changes.
- No commit, push, deployment, DNS, router, or firewall mutation was performed.

## 3. Areas inspected

- Cloud Domain/Application/Infrastructure and the PostgreSQL migration/permission boundary.
- Redis ACL identities, persistence, AOF replay, Presence leases, and token-reader separation.
- Cloud, Presence, Signaling, Downloads, Admin API, Admin SPA, public Web UI, Nginx, and coturn.
- Development, validation, staging, and production Compose configuration.
- OpenTelemetry Collector, Prometheus, Loki, Tempo, Grafana, Alertmanager, blackbox, and node
  exporter configuration.
- PostgreSQL backup and isolated restore-test jobs.
- Phase 6 enrollment/attestation acceptance client and current signaling protocol.
- Dockerfiles, image provenance, operational documentation, and relevant Phase 6 tests.

## 4. Components reused

- Existing .NET 10 Cloud/Presence/Admin/Downloads/Signaling services and shared observability host.
- Existing PostgreSQL model, four forward-only migrations, runtime grant job, and Redis atomic stores.
- Existing React/Vite Admin SPA and public Web UI; no alternate or fake UI was introduced.
- Existing Nginx routing, coturn REST authentication, Phase 3 physical-LAN controller, and Phase 5
  signed-release boundary.
- Existing Prometheus rules/data sources, Loki/Tempo storage, backup/restore scripts, and Windows
  Phase 6 controller.

## 5. Confirmed defects and proven root causes

1. Redis 8.2.1 replayed persisted transactions with ACL rules changed between `MULTI` and `EXEC`,
   producing repeated `NOPERM` AOF startup errors. Redis 8.4.4 contains the upstream fix; the same
   preserved volume starts cleanly after the upgrade.
2. The OTel Collector used deprecated component aliases (`fluentforward`, `otlp/tempo`, and
   `otlphttp/loki`), causing startup warnings. Current aliases remove them.
3. Development coturn advertised TLS 5349 without mounted TLS material and used a PID location that
   was not guaranteed writable. TLS is now conditional in the reusable entrypoint and required with
   generated certificates by the Phase 6 development stack; the PID is written under `/tmp`.
4. Grafana provisioning mounted data sources but no real dashboard, alerting, or plugin
   configuration. Empty valid provisioning documents and the real `PeerOnQ Operations` dashboard
   now provision without invalid-file warnings.
5. Local Grafana/Alertmanager/backup secret paths were not created by the controller; an existing
   Grafana volume could also retain a different administrator password. Stable ignored secrets are
   now created, and the Grafana password is synchronized through binary standard input.
6. Shared request logging wrapped the exception handler in the wrong order and mapped handled 403
   responses as 500 with exception detail. Middleware order and level selection now record the real
   4xx status without a false server error.
7. The Phase 6 acceptance WebSocket hello omitted the current signaling protocol version, so the
   server correctly rejected it before issuing a registration challenge. The acceptance client now
   uses `SignalingProtocol.CurrentVersion`.
8. Production examples used mutable image tags. Every production Compose image and Dockerfile base
   is now pinned to a reviewed immutable manifest digest and guarded by a regression test.

## 6. Files created and modified

Created:

- `tests/PeerOnQ.Observability.Tests/DeploymentConfigurationTests.cs`
- `src/PeerOnQ.Infrastructure.Deployment/observability/grafana/dashboards/peeronq-operations.json`
- `src/PeerOnQ.Infrastructure.Deployment/observability/grafana/provisioning/dashboards/dashboards.yml`
- `src/PeerOnQ.Infrastructure.Deployment/observability/grafana/provisioning/alerting/empty.yml`
- `src/PeerOnQ.Infrastructure.Deployment/observability/grafana/provisioning/plugins/empty.yml`
- `docs/phase-reports/PHASE_06_COMPLETION_REPORT.md`

Modified:

- Phase 6 development/validation/production Compose files and the migration Dockerfile.
- Cloud, Admin, Downloads, Presence, Signaling, public Web UI, Admin UI, and TURN Dockerfiles.
- OTel Collector, Tempo, coturn config/entrypoint, and shared observability request logging.
- Phase 6 acceptance project/program and Windows development controller.
- `PROJECT_MAP.md`, `docs/DEPLOYMENT.md`, `docs/OPERATIONS.md`, and `AI_CHANGELOG.md`.

No generated API client, vendored UI primitive, or unrelated route was edited for this phase.

## 7. Migrations

- No new database migration was required.
- The existing four forward-only migrations were applied/checked by the real migration job.
- Restart output reported: `No migrations were applied. The database is already up to date.`
- The runtime database-permission job completed successfully after migration.
- Named PostgreSQL and Redis volumes were preserved; no down-migration or destructive volume action
  occurred.

## 8. API/protocol/schema changes

- No Cloud/Admin/Downloads HTTP route or response schema changed.
- No PostgreSQL entity, table, column, index, or Redis key schema changed.
- The acceptance client now sends the already-required current signaling protocol version; this is
  a test-client compatibility repair, not a production wire-protocol change.
- The Grafana dashboard consumes existing Prometheus metrics only.

## 9. Security/privacy changes

- Production containers and bases are reproducible by immutable SHA-256 digest.
- Local operations secrets and backup credentials are stable, random, gitignored, and not passed in
  command arguments. The Grafana reset path uses binary standard input.
- Redis continues to disable the default user and preserves purpose-specific key/command ACLs.
- Development TURN TLS uses generated local certificate material; staging/production still require
  their external trusted certificate paths.
- Tempo anonymous usage reporting is disabled.
- Request logging reports handled authorization failures accurately and avoids false exception
  output. Existing OTel redaction and low-cardinality label rules remain intact.
- No license, activation, billing, subscription, seat, entitlement, session-content upload, or
  vendor-only client dependency was introduced.

## 10. Exact tests and commands

Affected automated suites:

```powershell
dotnet test tests/PeerOnQ.Cloud.Domain.Tests/PeerOnQ.Cloud.Domain.Tests.csproj -c Release
dotnet test tests/PeerOnQ.Cloud.Application.Tests/PeerOnQ.Cloud.Application.Tests.csproj -c Release
dotnet test tests/PeerOnQ.Cloud.Infrastructure.Tests/PeerOnQ.Cloud.Infrastructure.Tests.csproj -c Release
dotnet test tests/PeerOnQ.Presence.Tests/PeerOnQ.Presence.Tests.csproj -c Release
dotnet test tests/PeerOnQ.Admin.Api.Tests/PeerOnQ.Admin.Api.Tests.csproj -c Release
dotnet test tests/PeerOnQ.Downloads.Tests/PeerOnQ.Downloads.Tests.csproj -c Release
dotnet test tests/PeerOnQ.Observability.Tests/PeerOnQ.Observability.Tests.csproj -c Release
```

Deployment/runtime gates:

```powershell
.\scripts\windows\peeronq-phase6-dev.ps1 restart
.\scripts\windows\peeronq-phase6-dev.ps1 status
docker compose -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.production.yml `
  config --no-interpolate --quiet
docker compose --env-file src/PeerOnQ.Infrastructure.Deployment/.env `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml build --check
docker compose --env-file src/PeerOnQ.Infrastructure.Deployment/.env `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  --profile operations run --rm backup
docker compose --env-file src/PeerOnQ.Infrastructure.Deployment/.env `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  --profile operations run --rm restore-test
dotnet run --project src/PeerOnQ.Infrastructure.Deployment/acceptance/PeerOnQ.Phase6.Acceptance.csproj `
  --configuration Release
docker scout cves redis:8.4.4-alpine --format sarif
.\scripts\security\scan-repository-secrets.ps1
.\scripts\quality\test-brand-purity.ps1
git diff --check
```

Live probes also checked Redis ACL allow/deny behavior, Redis/OTel/Tempo/Loki/Grafana/coturn logs,
TURN certificate/TLS listener state, a missing-release download response, and the handled-CSRF 403
request log.

## 11. Exact build/test results

- Cloud Domain: **7/7 passed**.
- Cloud Application: **19/19 passed**.
- Cloud Infrastructure: **33/33 passed**.
- Presence: **4/4 passed**.
- Admin API: **20/20 passed**.
- Downloads: **17/17 passed**.
- Observability/deployment configuration: **12/12 passed**.
- Affected Phase 6 total: **112 passed, 0 failed**.
- Phase 6 acceptance project: Release build passed with **0 warnings, 0 errors**.
- Eight custom Docker images: BuildKit `--check` passed with **0 warnings**.
- Production Compose model: valid.

Per the user's instruction, unchanged earlier-phase suites were not rerun from the beginning. Shared
Phase 6 boundaries changed here were covered by their affected suites and live gates.

## 12. Runtime and physical/external evidence

- The Windows controller restarted **20** development containers. Admin, Cloud, Downloads,
  Presence, Signaling, Nginx, PostgreSQL, Redis 8.4.4, coturn, and both Web UIs were healthy; Grafana
  and Prometheus were ready on loopback.
- Redis started against the preserved volume with zero warning/error/critical replay lines. Reader,
  normal runtime, Presence token-reader, and Admin ACL cross-boundary denials passed.
- TURN found the mounted certificate/key, enabled TLS 1.2/1.3 and DTLS 1.2, listened on loopback
  5349 TCP/UDP, and was healthy.
- Enrollment acceptance passed Cloud enrollment, bounded challenge, confirmation, Presence
  register/heartbeat, attested Signaling, routing alias format, and four negative attestation cases.
- PostgreSQL backup produced `peeronq-cloud-20260816T233913Z.dump`; the restore test created an
  isolated database and verified **22 public tables** without touching the source database.
- The unsigned/no-active-production-release route failed closed with HTTP 404 problem JSON,
  `no-store`, and `nosniff`; it did not return an HTML fallback or activate an unsafe artifact.
- No current Phase 6 physical two-laptop, public Internet, trusted MSI, external NAT, or off-host
  restore evidence was available. These remain external blockers, not simulated passes.

## 13. Feature truth matrix

| Capability | State | Evidence |
| --- | --- | --- |
| Single-node self-hosted development stack | PASS | Reproducible 20-container restart and health |
| Real Cloud/Presence/Signaling metadata path | PASS | Live enrollment/heartbeat/attestation acceptance |
| Session payload excluded from cloud | PASS | Existing architecture/contracts; no new payload route |
| Admin MFA/RBAC/CSRF and private default | PASS locally | 20 Admin tests; private proxy rules; external deny still blocked |
| Downloads hash/ETag/Range/304/concurrency | PASS in integration | 17 tests; no approved public signed artifact available |
| PostgreSQL migrations/grants | PASS | Current migrations and permission job completed |
| Redis persistence/ACL boundary | PASS | Preserved-volume startup and explicit allow/deny probes |
| TURN TLS configuration | PASS locally | Loopback TLS/DTLS configuration and listener proof |
| Observability and real dashboard | PASS locally | Collector clean startup, private endpoints, provisioned dashboard |
| PostgreSQL backup/isolated restore | PASS locally | Archive validation and isolated 22-table restore |
| Public signed MSI delivery | EXTERNALLY_BLOCKED | No authorized trusted signed production artifact/environment |
| Physical attended two-laptop session | EXTERNALLY_BLOCKED | Cannot be replaced by same-host or mock proof |
| Multi-region/global scale | NOT CLAIMED | Explicitly outside Phase 6 scope |

## 14. Measured performance results and environment

- Environment: Windows, Docker Engine 29.7.2, Docker Compose 5.3.1, .NET SDK 10.0.303, local
  loopback Docker Desktop deployment.
- Two full controller restarts completed in approximately **196.8 s** and **260.6 s**, including
  build/migration/readiness orchestration.
- The successful live enrollment/Presence/signaling acceptance completed in approximately **4.8 s**
  after build.
- Local PostgreSQL backup completed in approximately **2.5 s**; isolated restore validation in
  approximately **3.3 s**.
- These are engineering observations on one workstation, not production SLO, capacity, WAN, or
  multi-region measurements.

## 15. Brand-purity result

- The repository brand-purity gate passed after the Phase 6 documentation/configuration changes.
- No new legacy product name, activation/billing promise, or misleading multi-region claim was
  introduced.

## 16. Dependency/SBOM/provenance impact

- No NuGet or pnpm dependency was added.
- Redis runtime changed from 8.2.1 to 8.4.4 to consume the upstream ACL/AOF fix.
- Production image/base references are now immutable tag-plus-digest pairs.
- `.NET --vulnerable --include-transitive` found no vulnerability in the inspected Observability
  dependency graph.
- Docker Scout found **0 critical**, **0 high**, and **2 unfixed medium** findings in
  `redis:8.4.4-alpine`: CVE-2026-27456 (`util-linux`, EPSS 0.001180) and CVE-2025-60876 (`busybox`,
  EPSS 0.002910). Both remain monitored because no fixed Alpine package was available.
- Existing Phase 5 SBOM/signing provenance remains authoritative for Windows release artifacts; no
  new release artifact was produced or activated.

## 17. P0/P1/P2 issues

- **P0:** none open in the locally executable Phase 6 primary objective.
- **P1:** public trusted signed MSI delivery, external Admin deny, Internet TURN/NAT, two-device
  attended session, and off-host restore are unexecuted external release gates.
- **P2:** coturn logs expected development-only auto-discovered listener/relay and
  `allow_loopback_peers` warnings; Loki/Tempo can log benign single-node ring/WAL startup warnings;
  Grafana's bundled table plugin can emit a duplicate-registration message. Services remain healthy,
  and none is hidden as a successful public-production proof.

## 18. Known limitations

- Compose is a production-shaped single-node reference, not high availability or multi-region.
- Development TLS is locally trusted only; it is not a public certificate chain.
- The provisioned Grafana dashboard is the single-node baseline, not the complete production
  executive/security/privacy dashboard set.
- Docker Desktop does not enforce Compose secret uid/gid/mode metadata; backup jobs copy secrets to
  a private tmpfs file with mode 0600 before use.
- The live download route cannot serve real MSI bytes until a verified, signed, approved release is
  intentionally activated.
- Browser-assisted visual Admin verification was unavailable because the in-app browser MCP startup
  rejected missing sandbox metadata; API/UI tests and live health were used, without claiming a
  visual pass.

## 19. External blockers

1. Publish a trusted, timestamped, signed MSI in an authorized non-production public environment;
   verify content type, exact SHA-256, full download, Range/206, 304, cache behavior, and clean-machine
   install.
2. From a disallowed external source, prove Admin and observability endpoints are denied while the
   approved LAN/VPN source still requires application MFA/RBAC.
3. From another Internet connection, prove TURN UDP/TCP/TLS allocation, relay UDP range, NAT hairpin
   behavior, and an attended session between two physical laptops.
4. Copy an encrypted backup off-host, verify immutability/checksum, and complete an authorized
   disaster-recovery restore including Redis Data Protection state and Admin MFA.
5. Supply production certificate management, external secret management, signing/timestamping,
   monitoring receiver, and pilot infrastructure.

No router, pfSense, DNS, firewall, production secret, or public environment was changed automatically.

## 20. Compatibility impact

- Existing PostgreSQL/Redis data and named volumes remain compatible and were preserved.
- No API, database, client protocol, or MSI compatibility break was introduced.
- Redis 8.4.4 is a runtime patch/minor upgrade required for correct persisted ACL transaction replay.
- Production deployment now requires immutable image digests; operators updating a tag must update
  and review the matching digest.
- Existing local `.env` credentials remain the source for database roles; the controller adds stable
  ignored operation-secret files without changing public configuration contracts.

## 21. Exact readiness decision for the next phase

**Decision:** Phase 6 is `PASS_WITH_EXTERNAL_BLOCKERS`; the next phase may begin only where its core
work depends on the validated local single-node cloud/operations boundary, not on the unproven public
release or physical-network gates.

Do not call the deployment production-ready, public, highly available, multi-region, or globally
scalable. Before any public pilot, close every blocker in section 19 with real external evidence and
rerun the affected Phase 6 security, deployment, download, backup, and physical-client gates.
