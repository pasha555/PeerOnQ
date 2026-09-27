# PeerOnQ current state

## Current source contract — 2026-09-27

Source facts below were checked against the current checkout. They describe implementation, not
production approval or a fresh execution of the historical tests farther down this page.

- `Directory.Build.props` defines the canonical server/Windows client version as `0.9.70`. Linux, Android
  and Apple client versions derive from it; Android and Apple bundle codes are `9070`. A source
  version does not establish that a matching signed package has been built or published.
- `SignalingProtocol` in `src/PeerOnQ.Transport/Protocol/SignalingMessages.cs` accepts exactly v3:
  minimum, current and maximum are all `3`. Missing/pre-v3/newer versions fail compatibility checks.
- Windows supports permission-gated attended `ViewOnly`, `FullControl`, `FileTransferOnly` and
  valid custom scopes. `Phase1SessionScope` retains its historical name but no longer restricts
  every session to view-only. Full Control includes screen, input and file permissions; text
  clipboard is separate. Input still requires accepted scope and a fresh focus acknowledgment.
- Native projects exist for Windows (WinUI), Linux (Avalonia), Android (.NET Android) and Apple
  (shared iOS/iPadOS UIKit and Mac Catalyst). Non-Windows clients are attended viewer/controller
  previews; their capability profiles omit hosting, capture, input injection, file/clipboard and
  unattended access. See [the capability/evidence matrix](CROSS_PLATFORM_CAPABILITIES.md).
- `PeerOnQ.slnx` includes Windows/Linux apps and the platform-neutral Android/Apple layers and
  tests. Android and Apple app builds are separate platform-workload gates; a solution build alone
  does not prove they compile or run.
- The offline product prototype is separate from the API-backed Admin and customer Portal SPAs
  and the .NET Cloud/Presence/Signaling/Downloads services. Node 24 is the workspace runtime target.
- Runtime, release-license, physical-device and performance gates remain evidence-bound. See
  [known limitations](KNOWN_LIMITATIONS.md), [PHASE5](../PHASE5.md) and dated
  [AI_CHANGELOG](../AI_CHANGELOG.md) results; no production approval follows from this inventory.

## Operator deployment evidence — 2026-09-27

- The operator supplied the successful installer result: server 0.9.70 is healthy and active, with
  current release `/opt/peeronq/releases/0.9.70`. The log passed embedded Windows publication,
  proxy streaming/cache validation, public API/signaling routing and base website activation.
- Independent read-only HTTPS requests from the development workstation returned 200 for the
  public website, www, customer portal and embedded client version metadata. Public API liveness
  returned healthy and signaling readiness returned ready. DNS resolution and certificate
  validation succeeded without a TLS bypass or a forced local address.
- A complete public x64 MSI GET returned 200 and 78602240 bytes; SHA-256 matched the validated
  0.9.70 package: `d72f617c028ee3b435f3106fa6ce8a06420ed50c1a123f46655752a0a696a2bb`.
  Metadata reported 0.9.70 and the response retained `Cache-Control: no-store, max-age=0`.
- Operator-host CIDR isolation was not established: this workstation received Admin 200 and
  Grafana/Prometheus 302. It may be an allowed operator source. An independent web-tool attempt
  could not access those hosts but supplied no usable HTTP status. The configured operator CIDR
  was requested; do not claim public exposure or successful isolation from these observations.
- This confirms installation and the tested public endpoints, not release-signing approval,
  account authentication, TURN/media behavior or physical 4K/latency acceptance. Existing client
  installations do not upgrade automatically. No new patch or version bump was needed to record
  this successful deployment, and the immutable 0.9.70 bundle/release notes were not changed.

## Current investigation boundaries — 2026-09-27

- The unchanged 35 ms input-to-injection p95 gate was reproduced in Release on both dedicated
  input and primary fallback. The corrected probe localizes the excess to post-admission
  transport/delivery; a Windows timer-precision candidate was tested and removed after inconsistent
  results. No production latency fix, fork or transport migration was adopted. See the dated
  [pipeline measurements and options](PERFORMANCE_REPORT.md#input-latency-investigation--2026-09-27).
- The Node contract now requires 24.x (`engines`, `.nvmrc`, pnpm engine enforcement), and the
  catalog/lockfile use `@types/node` 24.19.0. Frontend validation used a verified Node 24.21.0 runtime;
  the host's default Node 25 installation was not replaced. Activate Node 24 before workspace commands.
- Exact restored dependency licenses/provenance and current advisories are recorded in the
  [release audit](competitive/THIRD_PARTY_LICENSE_AUDIT.md). The current TURN-over-TLS receive-loop
  advisory, native codec provenance and external legal review remain open. The separate
  [Node audit](../DEPENDENCIES.md) reports 9 existing findings (5 high, 4 moderate).

## Historical R0 snapshot — 2026-08-12

The following inventory, tool versions, phase decisions and test counts belong to branch
`feat/phase1-remote-view`, original snapshot commit
`ed8942e172367cf8c11d8968cb77005bae47bf54`. They are retained as historical evidence, not current
capability or validation claims. The pre-change worktree was clean.

### R0 repository shape

- 30 .NET projects in `PeerOnQ.slnx`: native Windows client, domain/application/infrastructure,
  media, Windows capture/input, transport, signaling, cloud/presence/admin/download/observability
  services, shared contracts, and 13 test projects.
- 10 pnpm workspaces. `artifacts/peeronq` is the offline product prototype,
  `artifacts/peeronq-admin` is the Admin SPA, and `artifacts/api-server` is a health-only skeleton.
  `artifacts/mockup-sandbox` is explicitly excluded from product commands.
- PostgreSQL, Redis, Nginx, coturn, OpenTelemetry Collector, Prometheus, Grafana, Loki, and Tempo
  are composed by the development/deployment definitions.
- WiX/MSBuild installer, update manifest, signing, SBOM, checksum, and release scripts exist. No
  official artifact was published during R0.

### R0 detected toolchain

| Tool | Detected |
|---|---:|
| Git | 2.53.0.windows.2 |
| Node.js | 25.2.1 (repository/container target is Node 24) |
| pnpm | 10.33.0 |
| .NET SDK | 10.0.302, pinned by `global.json` |
| Docker Engine | 29.7.2 |
| Docker Compose | 5.3.1 |
| PowerShell | 5.1.26100.8972 |
| WiX CLI | not on `PATH`; the project uses the pinned WiX SDK packages |

### R0 Phase 1-6 truth matrix

| Phase | Implementation at R0 | R0 evidence | R0 decision |
|---|---|---|---|
| 1 | Windows identity, permission flow, signaling, display capture and an attended view-only session pipeline; Phase 1 scope rejects input, file transfer, clipboard, and unattended access at UI/application/protocol/media boundaries | Release build and 455 .NET tests pass; the required two-physical-device visual run was not performed | `EXTERNALLY_BLOCKED` |
| 2 | R0 source contained later-phase input components, but that snapshot's supported Phase 1 runtime deliberately rejected `ControlInput`; no Phase 2 capability had been enabled | Phase 2 did not begin because the Phase 1 physical acceptance gate was blocked | `EXTERNALLY_BLOCKED` |
| 3 | TLS signaling, coturn REST credentials, ICE path reporting, reconnect and local controller | Targeted local TURN media checks pass, but the full local controller's first UDP relay-media test times out after 45 seconds; Phase 2 runtime is not accepted | `KNOWN_BROKEN` |
| 4 | Clipboard, file transfer, address book, groups, trusted devices, unattended policy and later full-control integration | Automated persistence/protocol coverage exists; public two-device collaboration/control cases are unverified | `PARTIAL / EXTERNALLY_BLOCKED` |
| 5 | Performance controls, installer/update verification, release builder, SBOM/checksum paths | Local build/tests exist; official signing, timestamping, hosted update and install/upgrade matrix require external inputs | `PARTIAL / EXTERNALLY_BLOCKED` |
| 6 | PostgreSQL/Redis cloud control plane, Presence, Admin, Downloads, observability and deployment topology | Code and local Compose topology exist; public staging, multi-host failover and signed-release ingestion are unverified | `PARTIAL / EXTERNALLY_BLOCKED` |

### R0 baseline results

- `pnpm install --frozen-lockfile`: pass.
- `dotnet restore PeerOnQ.slnx --locked-mode`: pass.
- Pre-change Release build: 30 projects, zero warnings/errors.
- Pre-change .NET tests: 452 passed, 2 intentionally skipped.
- Frontend: 67 tests passed across product/Admin; root typecheck and lint passed.
- Product/Admin/API builds passed independently. The original root build failed first on a required
  `BASE_PATH`, then on the throwaway sandbox's unrelated `PORT` requirement.
- `pnpm audit --audit-level high` and .NET vulnerable-package audit found no known vulnerabilities.
- Repository secret scan passed 841 files.
- Phase 6 development Compose config passed with the ignored local environment. Staging config
  correctly rejected missing staging-only identity inputs.

Machine-readable TRX and command logs were recorded under `artifacts/test-results/r0-baseline/`
and `artifacts/test-results/r0-final/`; generated reports are excluded from public source. See the
[R0 completion report](phase-reports/R0_COMPLETION_REPORT.md) for the original evidence.

### R0 version snapshot (superseded)

At R0 the native product and installer defaulted to `0.5.1` (`0.5.1-beta.1` informational), the
OpenAPI document was `0.1.0`, and workspace package manifests were mostly `0.0.0`. R0 recorded
release/version drift. The current client source contract above supersedes that diagnosis;
independent API/workspace package versions are not client-release versions.
