# PeerOnQ current state

Verified on 2026-08-12 against branch `feat/phase1-remote-view`, original snapshot commit
`ed8942e172367cf8c11d8968cb77005bae47bf54`. The pre-change worktree was clean.

## Repository shape

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

## Verified toolchain

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

## Phase 1-6 truth matrix

| Phase | Source-backed implementation | R0 evidence | Truth |
|---|---|---|---|
| 1 | Windows identity, permission flow, signaling, display capture and an attended view-only session pipeline; Phase 1 scope rejects input, file transfer, clipboard, and unattended access at UI/application/protocol/media boundaries | Release build and 455 .NET tests pass; the required two-physical-device visual run was not performed | `EXTERNALLY_BLOCKED` |
| 2 | Current source contains later-phase input components, but the supported Phase 1 runtime deliberately rejects `ControlInput`; no Phase 2 capability has been enabled | Phase 2 did not begin because the Phase 1 physical acceptance gate is blocked | `EXTERNALLY_BLOCKED` |
| 3 | TLS signaling, coturn REST credentials, ICE path reporting, reconnect and local controller | Targeted local TURN media checks pass, but the full local controller's first UDP relay-media test times out after 45 seconds; Phase 2 runtime is not accepted | `KNOWN_BROKEN` |
| 4 | Clipboard, file transfer, address book, groups, trusted devices, unattended policy and later full-control integration | Automated persistence/protocol coverage exists; public two-device collaboration/control cases are unverified | `PARTIAL / EXTERNALLY_BLOCKED` |
| 5 | Performance controls, installer/update verification, release builder, SBOM/checksum paths | Local build/tests exist; official signing, timestamping, hosted update and install/upgrade matrix require external inputs | `PARTIAL / EXTERNALLY_BLOCKED` |
| 6 | PostgreSQL/Redis cloud control plane, Presence, Admin, Downloads, observability and deployment topology | Code and local Compose topology exist; public staging, multi-host failover and signed-release ingestion are unverified | `PARTIAL / EXTERNALLY_BLOCKED` |

## R0 baseline results

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

Machine-readable TRX and command logs are in `artifacts/test-results/r0-baseline/`. Final results
are written to `artifacts/test-results/r0-final/`.

## Version truth

The native product and installer default to `0.5.1` (`0.5.1-beta.1` informational); the OpenAPI
document is `0.1.0`; workspace package manifests are mostly `0.0.0`. Examples elsewhere mention
newer values. This is P1 release/version drift and must be resolved before an official release; R0
does not invent a release number.
