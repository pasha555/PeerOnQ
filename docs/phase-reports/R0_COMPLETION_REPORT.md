# PeerOnQ Recovery Gate R0 completion report

## 1. Phase and final gate

**R0 - `PASS_WITH_EXTERNAL_BLOCKERS`**

The repository has no unresolved internally reproducible P0 build, test, startup, active-brand, or
prototype-truth defect. The remaining physical-device, public-endpoint, production-signing, and
interactive OS/tool gates are explicit below and do not block beginning Phase 1 repair. R0 does not
authorize Phase 7 or another major feature.

## 2. Repository snapshot

- Branch: `feat/phase1-remote-view`
- Commit before changes: `ed8942e172367cf8c11d8968cb77005bae47bf54`
- Pre-change worktree: clean
- Snapshot date: 2026-08-12
- Toolchain: Git 2.53.0.windows.2; Node 25.2.1; pnpm 10.33.0; .NET SDK 10.0.302;
  PowerShell 5.1.26100.8972; Docker 29.7.2; Compose 5.3.1.

## 3. Areas inspected

Repository maps and governance; Phase 1-6 reports; native client/domain/application/infrastructure;
capture/input/media/transport/signaling/TURN; cloud/presence/admin/downloads/observability;
PostgreSQL migrations and local state; offline product/Admin/API web workspaces; installer,
update/signing/release/SBOM paths; local/staging Compose; security/privacy/accessibility/performance
documents; tests, CI workflows, configuration, storage keys, protocol identifiers, and brand paths.

## 4. Components reused

The existing 30-project solution, 10 pnpm workspaces, localStorage repositories, generated API
contract pipeline, WebRTC/coturn path, Windows capture/input implementation, Phase 5 release
tooling, Phase 6 services/Compose topology, migration tests, invariant scripts, shadcn primitives,
and existing design tokens were retained. No production feature, dependency, mock backend, or
replacement architecture was added.

## 5. Confirmed defects and proven root causes

1. Root pnpm commands included `artifacts/mockup-sandbox`; its required `PORT` caused the product
   build to fail. Root filters now exclude that non-product workspace.
2. Product Vite configuration required `BASE_PATH` during a production build. Build now safely
   defaults to `/`; served development still requires explicit host configuration.
3. The offline prototype API adapter synthesized configured success. It now always reports
   `NotConfigured`, and a source invariant rejects browser network primitives.
4. Phase 3 and Phase 6 generated different root keys with the same certificate subject. Windows
   chain selection was ambiguous. Each phase now has a distinct required root subject and metadata.
5. Active .NET paths/namespaces/types retained the former product identity. They were moved to the
   canonical `PeerOnQ.*` tree; only tested persisted/wire/install compatibility identifiers remain
   isolated by the compatibility register and allowlist.
6. Product/API/installer/package version sources disagree. This is documented P1 drift, not hidden
   or assigned an invented release value.

## 6. Files created and modified

- Created the required R0 documents, `CONTRIBUTING.md`, `THIRD_PARTY_NOTICES.md`, brand allowlist,
  purity script, isolated compatibility modules, TRX/JUnit/JSON evidence, and this report.
- Renamed active core source roots to `src/PeerOnQ.{App,Application,Domain,Infrastructure,Media,
  Platform.Windows,Signaling.Server,Transport,Turn.Configuration}` and six corresponding test roots.
- Renamed the real-time deployment root to `src/PeerOnQ.Realtime.Deployment`; retained the Phase 6
  cloud deployment root at `src/PeerOnQ.Infrastructure.Deployment`.
- Updated `PeerOnQ.slnx`, project references, namespaces/types, Windows scripts, installer/workflow,
  root package orchestration, product storage/API/Vite tests, maps, README, and related documents.

## 7. Migrations

Existing native data directories, database/log filenames, DPAPI secret entropy, browser storage,
update product history, database migration IDs, Device IDs, and MSI upgrade identifiers are
preserved through explicit copy/read-rewrite or opaque compatibility adapters. Sources are not
destructively deleted. No database schema migration was added or removed in R0.

## 8. API, protocol, and schema changes

No public route, OpenAPI schema, database schema, or wire-message shape changed. The deployed
Device ID prefix remains unchanged. Obsolete configuration aliases were removed because they are
not persisted data. The offline web API adapter contract now truthfully returns `NotConfigured`.

## 9. Security and privacy changes

- Removed synthetic network success and added a static no-browser-network invariant.
- Removed obsolete configuration/CI secret fallbacks; retained no new secret value.
- Separated local CA identities and validated their subjects in certificate metadata.
- Preserved fail-closed data/secret/update migrations; telemetry remains default-off/local-first.
- Added source purity, repository secret, dependency vulnerability, and release invariant evidence.

## 10. Exact tests and commands

```powershell
pnpm install --frozen-lockfile
dotnet restore PeerOnQ.slnx --locked-mode
dotnet build PeerOnQ.slnx -c Release --no-restore
dotnet test PeerOnQ.slnx -c Release --no-build --no-restore --logger "trx;LogFilePrefix=r0-final"
pnpm run typecheck
pnpm run lint
pnpm run test
pnpm run build
pnpm --filter @workspace/peeronq exec vitest run --reporter=junit --outputFile=../../artifacts/test-results/r0-final/peeronq-junit.xml
pnpm --filter @workspace/peeronq-admin exec vitest run --reporter=junit --outputFile=../../artifacts/test-results/r0-final/admin-junit.xml
pnpm audit --audit-level high
dotnet list PeerOnQ.slnx package --vulnerable --include-transitive
./scripts/security/scan-repository-secrets.ps1
./scripts/quality/test-brand-purity.ps1
./scripts/windows/test-peeronq-server-run-invariant.ps1
./scripts/windows/test-peeronq-website-patch-invariant.ps1
docker compose --env-file .peeronq-phase3/docker.env -f src/PeerOnQ.Realtime.Deployment/docker-compose.local.yml config --quiet
./scripts/windows/peeronq-phase6-dev.ps1 -Action start
./peeronq-status.bat
```

## 11. Exact build and test results

- Release build: 30 projects; 0 warnings; 0 errors.
- .NET tests: 452 passed; 2 explicit opt-in external skips; 0 failed; 13 TRX files.
- Product Vitest: 10 files, 49 passed, 0 failed; Admin Vitest: 2 files, 19 passed, 0 failed.
- Root typecheck, lint, test, and build: pass. Final frontend total: 68 passed.
- Product bundle: 668.87 KiB minified / 202.64 KiB gzip; Admin: 276.44 / 85.15 KiB.
- Server/MSI and website trust-boundary invariant scripts: pass.
- Root format check remains a disclosed non-gating failure from pre-existing style debt and the WiX
  parser; no error was suppressed and no unrelated formatting sweep was performed.

## 12. Runtime and physical/external evidence

- Phase 3 and Phase 6 Compose configuration validation: pass after path migration.
- Phase 6 warm-cache rebuild/migrations/start: 157.3 seconds; 19 persistent services running;
  every health-checked service healthy.
- Admin, Cloud API, Signaling, and Downloads HTTPS probes: 4/4 HTTP 200 with normal chain
  validation and `--ssl-no-revoke` because the repository-scoped development CA publishes no CRL.
- The first-time Phase 3 root trust operation requires interactive Windows confirmation and was not
  approved by the headless runner. Its certificate generation/configuration passed; trusted runtime
  is not claimed.
- No two-physical-device, public DNS/TLS/NAT, public MSI, signed artifact, clean VM, ARM64, or
  production restore/failover gate was substituted by a mock.
- The in-app browser controller could not initialize because the host omitted required sandbox
  metadata. No visual-browser result is claimed; HTTPS and Compose probes are the runtime evidence.

## 13. Feature truth matrix

| Surface | R0 truth |
|---|---|
| Offline web prototype | `PASS`: localStorage-only, explicit no transport |
| Native Windows source/build | `PASS`: build and automated tests; physical behavior external |
| Remote image/input/reconnect | `PARTIAL / EXTERNALLY_BLOCKED`: automated coverage only |
| Signaling/TURN | `PARTIAL / EXTERNALLY_BLOCKED`: config/loopback evidence; public NAT absent |
| Collaboration/unattended | `PARTIAL / EXTERNALLY_BLOCKED`: automated evidence; physical matrix absent |
| Installer/update/release | `PARTIAL / EXTERNALLY_BLOCKED`: local validation; signing/public install absent |
| Phase 6 local cloud/Admin stack | `PASS`: local Compose health; public/multi-host gates external |
| Production readiness | `NOT CLAIMED` |

## 14. Measured performance and environment

R0 workstation timings: pnpm install 2.529 s; locked .NET restore 5.287 s; pre-change Release build
44.211 s; pre-change .NET tests 32.592 s; root typecheck 6.771 s; root lint 32.100 s; root frontend
tests 43.115 s; final Phase 6 warm-cache start 157.3 s. Environment is the Windows x64 toolchain in
section 2. Physical media/input latency, motion correctness, throughput, active CPU/GPU, and
reconnect measurements remain unclaimed.

## 15. Brand-purity result

`scripts/quality/test-brand-purity.ps1`: pass. The former identity is rejected case-insensitively
outside the exact historical/compatibility allowlist. Active product code, paths, assemblies,
namespaces, packages, services, installer display, release names, and documentation are PeerOnQ.

## 16. Dependency, SBOM, and provenance impact

No dependency was added. Final secret scan passed 1,039 files. pnpm audit found no known vulnerability;
the full transitive .NET audit found none. Release tooling still emits and validates SPDX 2.2 and
supports hosted attestations; R0 did not publish an artifact, regenerate a release SBOM, or claim
SLSA level. SPDX 3.0 evaluation remains P1.

## 17. P0, P1, and P2 issues

- P0 external closure gates: two-device image/input/reconnect; clean native install/run; exact public
  MSI bytes; separate-network/NAT path; independent session-content/secret inspection.
- P1: unified version source; official signing/timestamp/provenance; public staging/failover/restore;
  historical report reconciliation; current SBOM evaluation; WCAG manual audit; hosted CodeQL and
  independent security/privacy review.
- P2: scoped format/WiX parser debt; product code splitting; Node 24 alignment; OpenAPI generator
  evaluation; measured codec/GPU/dirty-region work; versioned ID-prefix design only if justified;
  fair competitive benchmarks.

## 18. Known limitations

The authoritative list is `docs/KNOWN_LIMITATIONS.md`. Key limits are absent physical/public
evidence, software-only VP8 selection, single-host development topology, health-only Express
skeleton, version drift, incomplete manual accessibility/security audits, SPDX 2.2 output, Vite
chunk/sourcemap warnings, Node 25 test warning, and root formatting debt.

## 19. External blockers

Two suitable Windows devices and networks; public DNS/TLS/NAT/CDN; production signing/timestamp
authority; clean x64 and physical ARM64 systems; backup/failover hosts; independent audit resources;
interactive Phase 3 Windows root trust; and a browser-controller host that supplies its required
sandbox metadata. None prevents source-level Phase 1 repair from starting.

## 20. Compatibility impact

Project paths and namespaces change for downstream IDE/scripts. Persisted user state and deployed
identifiers remain migration-tested. The browser migration copies without deleting old keys; native
data migration copies/renames in the new tree while preserving the source; DPAPI rewrites only after
a successful read; update/MSI/database/wire identities remain opaque and stable.

## 21. Exact next-phase readiness decision

**Begin Phase 1 repair.** First reproduce the remote-image correctness gate on two physical Windows
devices with deterministic test frames and instrumentation. If corruption, reconnect instability,
permission expansion, or install/run failure is observed, record it as an active P0 and fix it before
Phase 1 passes. Do not start Phase 2 continuation, Phase 7, or another major feature until that gate
is closed or explicitly classified as a non-dependent external blocker.
