# PeerOnQ Phase 5 Completion Report

## 1. Phase and final gate

- Phase: 5 - native Windows UX, packaging, open-source/release safety, update hardening,
  privacy-safe diagnostics, and local release evidence.
- Date: 2026-08-17 (Asia/Baku).
- Gate: **PASS_WITH_EXTERNAL_BLOCKERS** for continuing core development.
- Public/production release: **not approved**. Trusted signing, clean-VM lifecycle evidence, real
  ARM64 hardware, current physical accessibility/performance testing, and production update
  infrastructure remain external gates.

## 2. Repository snapshot

- Branch: `feat/phase1-remote-view`.
- HEAD before Phase 5 work: `086631df7d95e71b5e13ce563de55b2a9e844e63`.
- Working tree: intentionally dirty with the user's cumulative Phase 1-4 work; no reset, clean,
  checkout, commit, push, deployment, DNS, firewall, or signing action was performed.
- Final local package version: `0.9.0`, x64, `Development/Unsigned`.

## 3. Areas inspected

- Maps and history: `AGENTS.md`, `PROJECT_MAP.md`, `ROUTES_MAP.md`, `AI_CHANGELOG.md`.
- Native UX: `MainWindow`, `ViewerWindow`, sharing indicator, permission dialog, tray and AppServices.
- Web product surface: router, portal navigation, About page, route-separation tests and Vite config.
- Packaging: WiX product/license, application project payload, development/release build scripts,
  MSI structural test, and installer lifecycle behavior.
- Release/security: update models/service, logging formatter, update/audit tests, license/notices,
  privacy/update/installer/design documentation, SBOM/provenance/dependency tooling.

## 4. Components reused

- Existing WinUI shell, session coordinator, sharing indicator, permission host and tray lifecycle.
- Existing WiX 6 package, administrative extraction/payload comparer and development certificate flow.
- Existing ECDSA manifest, Authenticode publisher verification, staged update and SHA-256 pipeline.
- Existing Serilog setup and sanitization primitives.
- Existing Microsoft SBOM tool 4.1.5, `dotnet list package`, pnpm audit/license commands, secret scan,
  brand gate, solution builds and test projects.
- No second UI shell, installer, updater, logger, entitlement service, or dependency was introduced.

## 5. Confirmed defects and root causes

1. A commercial Billing route/navigation contradicted the MIT/no-activation product contract.
2. Installer license text did not contain the complete MIT grant or explicit security/data notice.
3. About/Settings did not expose enough runtime, local-data and open-source truth to the user.
4. Navigation/status/indicator layouts had gaps for narrow windows, live announcements and scaled text.
5. Update transport allowed same-host redirects and launch trusted a previously checked staged hash,
   leaving a redirect/substitution window.
6. Default structured JSON logging could serialize sensitive property values and exception text/stack.
7. The initial local SBOM used the publish directory as its component root; it produced file hashes but
   no dependency packages, while the validator command could return exit 0 with `Result=Failure`.
8. Frontend test/build output contained avoidable Node 25 web-storage and Vite vendor warning noise.

## 6. Files created or modified

### Native/product

- `src/PeerOnQ.App/MainWindow.xaml`, `MainWindow.xaml.cs`, `PermissionDialogHost.cs`,
  `SharingIndicatorWindow.xaml`, `SharingIndicatorWindow.xaml.cs`, `PeerOnQ.App.csproj`.
- `artifacts/peeronq/src/app/router/index.tsx`, `layouts/PortalLayout.tsx`, `pages/AboutPage.tsx`,
  `src/test/routeSeparation.test.tsx`, `vite.config.ts`, product/admin `package.json`.
- Removed `artifacts/peeronq/src/pages/portal/BillingPage.tsx`.

### Security/release

- `src/PeerOnQ.Infrastructure/Updates/UpdateModels.cs`, `UpdateService.cs`,
  `Diagnostics/PeerOnQLogging.cs`.
- `tests/PeerOnQ.Infrastructure.Tests/Phase5UpdateAndAuditTests.cs`.
- `installer/Package.wxs`, `installer/license.rtf`.
- `scripts/windows/test-phase5-installer.ps1`, `build-phase5-release.ps1`.
- Added `new-phase5-local-evidence.ps1`, `test-phase5-native-ui.ps1`,
  `test-phase5-local-release.ps1`, and `test-phase5-installer-lifecycle.ps1`.

### Documentation

- `PHASE5.md`, `PROJECT_MAP.md`, `ROUTES_MAP.md`, `AI_CHANGELOG.md`,
  `THIRD_PARTY_NOTICES.md`, `docs/INSTALLER.md`, `docs/UPDATE_SECURITY.md`, `docs/PRIVACY.md`, and
  `artifacts/peeronq/DESIGN_SYSTEM.md`.

`dotnet format` made whitespace-only corrections in two already-modified cumulative files:
`SessionCoordinator.cs` and `tests/PeerOnQ.Application.Tests/Fakes.cs`.

## 7. Migrations

- Database/schema migration: none.
- User-data migration: none.
- Installer component additions are stable legal notice files; per-user data remains outside MSI
  ownership and is preserved by repair/upgrade/uninstall.

## 8. API, protocol, and schema changes

- Signaling/media/collaboration protocol: no Phase 5 change.
- Public HTTP API: no Phase 5 change.
- Web route removal: `/app/billing` now intentionally resolves to the existing 404 boundary.
- Update result enum adds `UnexpectedRedirect`; this is a local client security outcome.
- Structured log JSON retains event semantics but now sanitizes templates/properties and emits only
  exception type, not exception message/stack.

## 9. Security and privacy changes

- Manifest and package requests require the exact expected HTTPS URI; redirects are rejected.
- Staged MSI size and SHA-256 are rechecked immediately before Authenticode/publisher verification and
  launch; compromised staged content is deleted.
- Logs redact sensitive property names, sanitize bounded nested values, and exclude exception text.
- MSI and native/web About surfaces clearly state MIT, no key/activation/subscription/entitlement, the
  exact local-data boundary, and security implications.
- No authentication, permission, encryption, signing, update trust, UAC, firewall, antivirus, or
  operating-system security boundary was weakened.

## 10. Exact tests and commands

```powershell
dotnet test PeerOnQ.slnx -c Release --no-restore
dotnet test tests\PeerOnQ.Infrastructure.Tests\PeerOnQ.Infrastructure.Tests.csproj `
  --no-restore -c Release --filter "FullyQualifiedName~Phase5UpdateAndAuditTests"
dotnet build src\PeerOnQ.App\PeerOnQ.App.csproj --no-restore -c Release -r win-x64
dotnet format PeerOnQ.slnx --verify-no-changes --no-restore
pnpm run test
pnpm run build
.\scripts\windows\test-phase5-native-ui.ps1
.\scripts\security\scan-repository-secrets.ps1
.\scripts\quality\test-brand-purity.ps1
.\scripts\windows\test-phase5-installer.ps1 -MsiPath <approved-msi> `
  -Architecture x64 -ExpectedPublishDirectory <approved-app>
.\scripts\windows\new-phase5-local-evidence.ps1 `
  -ArtifactRoot dist\phase5-handoff\0.9.0 -Version 0.9.0 -Architecture x64
```

The cumulative .NET solution tests were run after Phase 4 completion and before the final
release-only documentation/SBOM script correction; all changed Phase 5 product code then received
the focused 24-test suite plus final WinUI build and frontend regression run.

## 11. Exact build and test results

- Full .NET regression: **516 passed, 0 failed, 2 skipped**. Both skips are explicit opt-in live
  Docker/TURN cases, not failures.
- Phase 5 update/audit: **24 passed, 0 failed**.
- PeerOnQ web: **51 passed**; admin web: **19 passed**.
- Root pnpm typecheck/build: passed; final product/admin Vite builds contain no warning in output.
- WinUI x64 Release: passed, **0 warnings, 0 errors**.
- `dotnet format --verify-no-changes`: passed.
- Native UI/open-source gate, MSI invariant, brand purity and repository secret scan: passed.
- Secret scan scope: **891 files**.

## 12. Runtime, physical, and external evidence

- Approved MSI invariant performed ICE validation, administrative extraction, self-contained runtime
  checks, embedded-license assertions and exact publish/MSI payload comparison: passed.
- Current package was not installed over the user's active workstation; lifecycle work is guarded for
  an explicitly disposable VM.
- A local idle benchmark was intentionally not started because an existing user-owned PeerOnQ process
  was detected; it was not terminated or disturbed.
- No claim is made for current-build two-laptop, screen-reader, ARM64, public-network, signed-update,
  or sustained 4K performance evidence.

## 13. Feature truth matrix

| Capability | Current truth |
| --- | --- |
| Native x64 app and unsigned LAN MSI | Implemented and locally validated |
| Native navigation/settings/about/consent wiring | Implemented; build/static gate passed |
| MIT/no activation/billing surface | Implemented; route/UI tests passed |
| MSI legal notice and payload ownership | Implemented; extraction/invariant passed |
| Redirect/tamper-safe update staging | Implemented; automated tests passed |
| Privacy-safe structured JSON logs | Implemented; raw-output redaction tests passed |
| SPDX SBOM/provenance/checksums | Generated and validated locally |
| Clean install/upgrade/repair/uninstall | Harness implemented; current package externally blocked |
| Trusted Authenticode/RFC 3161 release | Not available; artifact correctly marked unsigned |
| ARM64 | Not built or claimed in this phase; real hardware required |
| Physical accessibility and performance | Not run for 0.9.0 |
| Production updater/CDN/signing environment | Not configured in this local task |

## 14. Performance measured and environment

- Current 0.9.0 idle/active benchmark: not measured. The measurement script refused to mix state with
  an already-running user process, and the process was deliberately left untouched.
- Build output: product JS chunks are 108.13 KiB UI vendor, 130.09 KiB app, and 432.17 KiB vendor
  before gzip; no Vite size warning remains.
- No current numeric claim is made for capture/encode/decode/render FPS, CPU/GPU, input latency,
  end-to-end latency, reconnect duration, bitrate/loss, or file-transfer throughput.

## 15. Brand purity

- `test-brand-purity.ps1`: passed.
- No forbidden former product brand appears outside the compatibility manifest.
- Product, MSI and notices consistently use PeerOnQ.

## 16. Dependency, SBOM, and provenance

- Approved artifact: `dist/phase5-handoff/0.9.0`.
- MSI size: **78,483,456 bytes**.
- MSI SHA-256: `C98A0D74511145619370D884A3651E8656C9709CBEC0F15C2CFA6C1173D79F95`.
- Authenticode: `NotSigned`, intentionally and visibly labeled Development/Unsigned.
- SPDX 2.2: **18 packages, 573 files, Result=Success, 0 validation errors**.
- Root package license: MIT; no private workstation path occurs in SBOM/provenance evidence.
- .NET vulnerability report: 0 known vulnerable direct/transitive packages.
- pnpm production audit: 121 dependencies, 0 info/low/moderate/high/critical findings.
- Local provenance records source/material digests and nondeterministic unsigned-build conditions; it
  is evidence, not a hosted/signed attestation.

## 17. P0, P1, and P2 issues

- Open local code defects: **P0=0, P1=0, P2=0** from executed checks.
- External P0 release gate: no trusted publisher certificate/timestamp or protected production update
  signing/origin; unsigned MSI must not be publicly distributed as production.
- External P1 gates: current clean-VM lifecycle, real ARM64 launch, physical accessibility and exact
  two-laptop acceptance.
- External P2 evidence gap: current-build idle/active performance benchmark is not recorded.

## 18. Known limitations

- English-only release surface.
- Unsigned local-development certificate workflow is for the trusted LAN only.
- ARM64 and physical device behavior are unverified for this version.
- Static accessibility checks do not replace Narrator, high-contrast, keyboard and 200% text tests.
- Existing cumulative Phase 1-4 changes remain uncommitted; this report does not claim a clean tree.

## 19. External blockers

1. Approved code-signing certificate, timestamp service and protected signing environment.
2. Disposable clean x64 Windows VM plus supported baseline MSI for lifecycle testing.
3. Real Windows ARM64 device.
4. Two physical laptops and assistive-technology/manual accessibility test environment.
5. Production update HTTPS origin, manifest signing key custody and staging/canary controls.
6. Independent security/legal release review.

## 20. Compatibility impact

- Existing native session protocols and local data stay compatible.
- Update servers that redirect are now intentionally incompatible; they must serve the signed exact
  manifest/package URI directly.
- Consumers of raw JSON logs should tolerate sanitized values and exception type without message/stack.
- `/app/billing` bookmarks now return 404 by design because PeerOnQ has no commercial entitlement UI.
- The MSI contains two additional legal files; application behavior and user-data location are unchanged.

## 21. Readiness for the next phase

Core next-phase work may proceed because all locally executable Phase 5 gates are green and no P0/P1
code defect remains. Public release work must remain blocked until every item in section 19 has exact
artifact hashes, UTC conditions and retained evidence. For physical testing, install only the approved
0.9.0 x64 development MSI on both trusted-LAN laptops after verifying its SHA-256 above.
