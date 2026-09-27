# PeerOnQ Phase 5 - Windows release readiness

## Gate

The **2026-08-17** phase decision was **PASS_WITH_EXTERNAL_BLOCKERS** for continuing core work.
Its x64 development MSI and local checks are historical evidence below, not validation of the
current source or package. Public/production release is not approved: trusted code signing,
a disposable clean-VM lifecycle run, real ARM64 hardware, current physical-device
accessibility/performance acceptance, a production update origin and dependency-license approval
remain separate gates. See [current state](docs/CURRENT_STATE.md) and the
[release-license audit](docs/competitive/THIRD_PARTY_LICENSE_AUDIT.md).

## Current source version

`Directory.Build.props` defines `PeerOnQWindowsClientVersion = 0.9.66`; Linux, Android and Apple
client versions derive from it. Source version alone does not establish a current downloadable
artifact. Each Windows publication requires the matching validated x64/ARM64 pair and checksums
on every intended download surface; historical artifacts below cannot satisfy that gate.

## Historical artifact — 2026-08-17

- Version: `0.9.0`
- Architecture: x64
- Classification: `Development/Unsigned`
- MSI: `dist/phase5-handoff/0.9.0/PeerOnQ-0.9.0-unsigned-development-x64.msi`
- Size: 78,483,456 bytes
- SHA-256: `C98A0D74511145619370D884A3651E8656C9709CBEC0F15C2CFA6C1173D79F95`
- Endpoint: `wss://signal.10.0.0.10.sslip.io:5443/ws`

The filename and adjacent warning identified this package as unsigned and restricted it to its
trusted LAN development environment. Its recorded size/hash are preserved without claiming the
artifact still exists or matches current source. It must not be presented as a current or
production-signed release.

## Implemented scope

- Native WinUI pages and commands are wired to real local behavior; Settings exposes the exact data
  folder, runtime version, MIT license, and an Open folder action.
- Navigation is adaptive, status/consent surfaces expose live accessibility metadata, and the sharing
  indicator is sized for text scaling and keeps an explicit local stop path.
- The product and web About surface state that PeerOnQ is MIT open source and requires no license key,
  activation, subscription, or online entitlement. The obsolete Billing route/navigation is gone.
- The MSI embeds the complete MIT license plus security/data-retention notice and installs
  `LICENSE.txt` and `THIRD_PARTY_NOTICES.md` beside the application.
- Update download and launch reject any unexpected redirect, revalidate signed length/SHA-256 before
  launch, and retain the existing Authenticode/publisher gates.
- Structured JSON logs sanitize message templates/properties and record exception type without
  exception message or stack content.
- Local release tooling generates a file-complete SPDX 2.2 SBOM, .NET/pnpm inventories and
  vulnerability reports, SLSA-v1-shaped local provenance, legal files, checksums, and a clean-machine
  checklist. Validation fails if the SBOM has no detected packages or reports any error.
- A guarded installer lifecycle harness covers clean install, launch, repair, upgrade, downgrade
  rejection, uninstall, user-data preservation, and absence of service/task/firewall side effects.

## Historical local verification — 2026-08-17

- Full .NET phase-regression run: 516 passed, 0 failed, 2 explicit opt-in live Docker/TURN tests
  skipped.
- Phase 5 update/audit tests: 24/24 passed.
- Web tests: PeerOnQ 51/51 and admin 19/19 passed; root typecheck/build passed without build warnings.
- Native x64 Release build: 0 warnings, 0 errors.
- `dotnet format --verify-no-changes`: passed.
- Native UI/accessibility/open-source static gate: passed.
- MSI ICE/payload/self-contained/license invariant: passed.
- Secret scan: 891 repository files passed.
- Brand-purity gate: passed.
- SPDX 2.2: 18 packages and 573 files; validation `Success`, 0 errors, 0 private path matches.
- Dependency reports: 0 known vulnerable .NET packages; pnpm production audit reports 0 info/low/
  moderate/high/critical findings across 121 production dependencies.

Evidence was recorded under `dist/phase5-handoff/0.9.0/evidence/`; generated evidence/artifacts are
excluded from public source. That phase did not install the MSI over the user's environment;
destructive lifecycle verification remains guarded for a disposable clean VM.

## External acceptance still required

1. Sign and RFC 3161 timestamp the exact MSI/PE payload with the approved publisher certificate and
   repeat signature, update, tamper, upgrade, rollback, and provenance checks.
2. Run `test-phase5-installer-lifecycle.ps1` as administrator in an explicitly disposable clean x64
   Windows VM with the supported baseline MSI.
3. Payload-validate the matching ARM64 package, then install and launch it on real Windows ARM64
   hardware; cross-building alone does not establish physical ARM64 behavior.
4. Run Narrator/screen-reader, keyboard-only, high-contrast, 200% text-scaling, and multi-monitor DPI
   checks on the packaged application.
5. Repeat current two-physical-laptop view/control/file/clipboard/reconnect tests and record sustained
   quality, CPU/GPU, FPS, latency, throughput, and Wi-Fi interruption recovery for this exact build.
6. Configure the protected production update/signing origin and independently review security/legal
   release material before any public distribution.

The detailed evidence and truth matrix are in
`docs/phase-reports/PHASE_05_COMPLETION_REPORT.md`.
