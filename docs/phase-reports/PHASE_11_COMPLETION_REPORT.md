# Phase 11 completion report

## 1. Phase and final gate

**Phase 11 — implemented and locally verified for the authorized single-node pilot; full production
acceptance remains `PARTIAL`.** Failover is deferred by the user, not passed or silently bypassed.
Authentication, consent, permission and hybrid-crypto checks remain fail-closed.

## 2. Repository snapshot

- Branch: `feat/phase1-remote-view`.
- Base commit: `8afe5fc8bc9b76b8ac1e668599a486704852edb0`.
- Toolchain: .NET SDK 10.0.303, Node.js v25.2.1, pnpm 10.33.0, Docker 29.7.2.
- Validation package: PeerOnQ 0.9.21 x64 Portable Support, self-contained,
  development/unsigned.
- No reset, clean, commit, tag, deployment or publication was performed.

## 3. Areas inspected

Reconnect and secure-transport tests, signaling capability negotiation, permission prompts, DPAPI
secret storage, audit events, collaboration routing, viewer lifetime/focus, installer protocol
registration, portable publish/cleanup and the Phase 11 contract.

## 4. Components reused

Existing authenticated resume/fresh ICE/forced rekey, ML-KEM-768 + X25519, ML-DSA-65 + Ed25519,
HKDF-SHA-512, AES-256-GCM, permission dialog, DPAPI store, audit log, WebRTC collaboration channel,
WinUI shell and WiX installer were extended rather than replaced.

## 5. Confirmed defects and proven root causes

- There was no session access kind or lifecycle for support invitations.
- The native client held only one viewer/collaboration reference, so concurrent frames and transfer
  callbacks were not explicitly session-routed.
- Hosted sessions shared the Windows input sink without a process-wide exclusive focus owner.
- There was no no-install package mode or `peeronq://support` registration.

## 6. Files created and modified

- Added protected support invitation service/store/link model and lifecycle tests.
- Added session-bound frame routing and exclusive remote-input focus coordination.
- Added native invitation create/revoke/use UI, invitation deep-link consumption and consent labels.
- Added portable mode/build script, WiX URL protocol registration and installer validation.
- Added `docs/WORLD_CLASS_VALIDATION_REPORT.md` and updated maps/security/architecture/deployment.

## 7. Migrations

None. Invitations use a versioned DPAPI-protected local secret record; no database schema changed.

## 8. API/protocol/schema changes

Signaling protocol v3 adds the capability-gated `support-invitation` access kind and bounded token/
password fields routed only to the target connection. Collaboration v2 adds session-bound focus-loss.
`peeronq://support?...` is registered by MSI and loaded without auto-connecting.

## 9. Security/privacy changes

Tokens are 256-bit CSPRNG values; only SHA-256 hashes are persisted under DPAPI. Optional passwords
use the existing PBKDF2-SHA512 600,000-iteration verifier. Expiry, revoke, maximum uses, exact mode/
permissions, requester/fingerprint restrictions, bounded attempts and constant-time comparisons are
enforced. A use is consumed atomically only after local Accept. Tokens/passwords/notes are excluded
from audit. Portable mode disables unattended access and uses a process-bound ephemeral profile.

## 10. Exact tests and commands

```powershell
dotnet test tests/PeerOnQ.Application.Tests/PeerOnQ.Application.Tests.csproj -c Debug --no-restore --verbosity minimal
dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Debug --no-restore --verbosity minimal
dotnet test tests/PeerOnQ.Domain.Tests/PeerOnQ.Domain.Tests.csproj -c Debug --no-restore --verbosity minimal
dotnet test tests/PeerOnQ.EndToEnd.Tests/PeerOnQ.EndToEnd.Tests.csproj -c Debug --no-restore --verbosity minimal
dotnet build src/PeerOnQ.App/PeerOnQ.App.csproj -c Debug --no-restore
scripts/windows/build-phase11-portable-support.ps1 -Version 0.9.21 -Architectures x64 -IUnderstandThisIsNotProductionSigned
dotnet build installer/PeerOnQ.Installer.wixproj --no-restore -c Release -t:Rebuild -p:ProductVersion=0.9.21 -p:Platform=x64
scripts/windows/test-phase5-installer.ps1 -MsiPath <0.9.21 x64 MSI> -Architecture x64 -ExpectedPublishDirectory <publish>
```

## 11. Exact build/test results

- Application: 132 passed, 0 failed, including secure transport, reconnect, invitations and
  multi-session focus.
- Signaling: 108 passed, 0 failed, 4 environment-gated distributed/failover tests skipped.
- Domain: 61 passed, 0 failed.
- End-to-End: 49 passed, 0 failed.
- Native Debug build: 0 warnings/errors.
- Portable 0.9.21 x64 Release publish/ZIP: passed; 572 entries and archive checksum verified.
- WiX 0.9.21 x64 MSI build and administrative payload/protocol validation: passed, 0 warnings/errors.

## 12. Runtime and physical/external evidence

The portable executable opened twice. Each forced-timeout residue used a PID-bound profile; the next
portable start removed the prior abandoned profile without touching a live/legacy profile. No real
two-device support session, physical concurrent workspace or eight-hour soak was performed.

## 13. Feature truth matrix

| Phase 11 capability | Status | Evidence and limitation |
| --- | --- | --- |
| Complete reconnect | `PRESERVED_VERIFIED_LOCAL` | authenticated resume/fresh ICE/rekey/stale-input tests pass; no physical timing |
| Secure support invitation | `IMPLEMENTED_VERIFIED_LOCAL` | hash-only lifecycle, restrictions, replay/rate tests and target-only signaling route |
| Explicit approval | `IMPLEMENTED` | support request always shows Accept; never auto-connects |
| Portable Support | `IMPLEMENTED_UNSIGNED` | real self-contained ZIP, checksum, ephemeral profile, unattended disabled |
| Multi-session workspace | `IMPLEMENTED_VERIFIED_LOCAL` | per-session viewer/frame/collaboration maps and exclusive input focus test |
| Hybrid PQ security | `PRESERVED_REQUIRED` | existing mandatory hybrid path and secure-transport suite pass |
| Classical fallback modes | `NOT_IMPLEMENTED` | pilot deliberately remains Hybrid-Required; no downgrade path added |
| Validation report | `CREATED_PARTIAL` | local evidence and every external gap recorded |

## 14. Measured performance results and environment

Portable startup/idle results are in `docs/PERFORMANCE_REPORT.md`. No reconnect duration,
multi-session resource growth, invitation latency, hybrid-handshake timing or eight-hour soak was
measured.

## 15. Brand-purity result

Deep link, installer, portable manifest/readme and UI use PeerOnQ. No repository-wide audit claimed.

## 16. Dependency/SBOM/provenance impact

No new dependency or lockfile. The portable ZIP is an ignored development artifact, explicitly
unsigned and excluded from official release/update trust.

## 17. P0/P1/P2 issues

- **P0:** none found in local single-node Phase 11 tests.
- **P1:** production Authenticode, clean-VM x64/ARM64, two-device/separate-network, physical
  multi-session, long-soak, accessibility and independent security evidence are absent.
- **P1 deferred by user:** failover/HA remains a Phase 9 production blocker, outside this pilot.
- **P2:** Classical/Hybrid selection is absent; current behavior is Hybrid-Required only.

## 18. Known limitations

Portable identity is intentionally ephemeral. Crash/forced shutdown may leave a temporary directory
until the next portable start safely reaps its dead PID-bound profile. Support invitations are local
to the issuing device and cross-organization requests remain denied by existing organization policy.

## 19. External blockers

Production code signing, clean VMs, physical endpoints, authorized penetration/cryptographic review,
eight-hour soak and independent accessibility review were unavailable.

## 20. Compatibility impact

Support sessions require both clients to advertise new support capabilities and otherwise fail with
capability mismatch. Attended/unattended flows remain. Hybrid capability is still mandatory; no
security downgrade or persistence migration was introduced.

## 21. Readiness decision

Phase 10 and 11 implementation is ready for controlled single-node pilot testing. It is not a full
production/HA/world-class acceptance pass until the listed physical, signing, soak, security and
deferred Phase 9 gates are evidenced.

## 22. Current-source revalidation (2026-08-26)

The original sections above are the 0.9.21 implementation record. The same Phase 11 implementation
was audited against canonical Windows client version 0.9.66 at source commit
`55635e57f770eb6ab20e022b52b9b148effb040e`; no duplicate replacement or runtime correction was
required.

Current local evidence:

- Support-invitation lifecycle and multi-session input-focus tests: 5 passed, 0 failed.
- Application: 160 passed, 0 failed.
- Signaling: 120 passed, 0 failed, 4 distributed/failover tests skipped by their environment gates.
- Domain: 64 passed, 0 failed.
- Infrastructure: 85 passed, 0 failed.
- Transport: 7 passed, 0 failed.
- Media: 111 passed, 0 failed, 1 live-TURN test skipped by its environment gate.
- End-to-End: 49 passed, 0 failed.
- Native App Debug build: 0 warnings, 0 errors.
- Canonical Windows client version invariant and native UI/accessibility contract: passed.
- Unsigned Portable Support 0.9.66 Release packages built for both x64 and ARM64. Both manifests
  declare an ephemeral profile and unattended access disabled; every payload checksum and both
  archive checksums passed, and neither package contains a PeerOnQ Windows service payload.

Validated archives:

- x64: `PeerOnQ-0.9.66-portable-support-unsigned-development-x64.zip`, 572 entries,
  SHA-256 `456c72d2845aa6107414b9f3c48912053080554400b8ce25090ee605e35edf4c`.
- ARM64: `PeerOnQ-0.9.66-portable-support-unsigned-development-arm64.zip`, 568 entries,
  SHA-256 `718525965b67ac4794db09bc3e3549daaac5254cbfa2e92c2552600bd878ea99`.

These are ignored, unsigned development validation artifacts, not website downloads or an official
release. Current decision: `IMPLEMENTED_REVALIDATED_LOCAL` for the controlled single-node pilot.
Full production acceptance remains partial until signed clean-VM lifecycle, two-device invitation,
physical concurrent-session, accessibility/security review, eight-hour soak and the deferred Phase
9 HA gates have retained evidence.
