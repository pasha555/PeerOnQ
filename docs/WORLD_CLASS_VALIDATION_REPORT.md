# Phase 11 validation report

Status: **single-node pilot implementation validated locally; full production acceptance incomplete**.
The user's current scope explicitly defers failover. This does not change the Phase 9 `FAIL` report
and does not create an HA claim.

| Capability | Local evidence | Remaining gate |
| --- | --- | --- |
| Complete reconnect | Existing authenticated resume, fresh ICE, forced rekey, stale-input discard and permission-reduction tests passed in the 132-test Application and 108-test Signaling suites | Separate-network interruption timing and long soak not measured |
| Secure support invitations | 256-bit token, hash-only protected persistence, expiry/revoke/use limits, optional password and identity/permission binding; replay and malformed-input tests pass | Two physical devices and independent security review |
| Explicit consent | Support requests always enter the native permission dialog; use is consumed atomically only after Accept | Physical accessibility/UX review |
| Multi-session workspace | Session-bound viewer/frame/collaboration routing plus exclusive process-local input focus; cross-session focus test passes | Physical concurrent-session resource/latency test |
| Portable Support | 0.9.21 x64 self-contained ZIP built from the desktop codebase; marker, README, per-file checksums and archive SHA-256 verified; unattended disabled and abandoned-profile cleanup exercised | Authenticode signing, clean-VM x64/ARM64 lifecycle and distribution approval |
| Hybrid security | Existing ML-KEM-768 + X25519, ML-DSA-65 + Ed25519, HKDF-SHA-512 and AES-256-GCM path remains mandatory and fail-closed; secure-transport tests pass | Independent cryptographic review and physical interoperability |
| Downgrade/key rotation | Hybrid capability is required; record replay rejection and rekey on time/volume/record/reconnect bounds remain covered | Classical/Hybrid/Hybrid-Required product-mode matrix is not implemented; current pilot is Hybrid-Required only |
| HA/failover | Not in current scope | Phase 9 production data-tier and representative multi-host gates remain open |

Validated portable archive:

- File: `PeerOnQ-0.9.21-portable-support-unsigned-development-x64.zip`
- Entries: 572
- SHA-256: `27d37de67fda2b9bbb015ec3ce63038d6dab1730170bc79bd8edbc5446726758`
- Classification: development / unsigned; not an official release

No eight-hour soak, production signing, physical 4K session, physical multi-session run, penetration
test or independent accessibility assessment was performed. Phase 11 must not be called a full
production pass until those artifacts exist.

## 2026-08-26 current-source revalidation

The table and x64 archive above preserve the original 0.9.21 evidence. Canonical client 0.9.66 was
subsequently revalidated on .NET SDK 10.0.303 at source commit
`55635e57f770eb6ab20e022b52b9b148effb040e`:

- Application 160/160, Media 111/111 (+1 environment-gated live-TURN skip), Signaling 120/120
  (+4 environment-gated distributed/failover skips), Domain 64/64, Infrastructure 85/85,
  Transport 7/7 and End-to-End 49/49 passed.
- Native App Debug build completed with 0 warnings and 0 errors; the canonical-version invariant
  and native UI/accessibility contract passed.
- Unsigned 0.9.66 Portable Support packages built for x64 and ARM64. Manifest security properties,
  per-file SHA-256 values, archive SHA-256 values and absence of a PeerOnQ service payload passed.
- x64 archive: 572 entries,
  SHA-256 `456c72d2845aa6107414b9f3c48912053080554400b8ce25090ee605e35edf4c`.
- ARM64 archive: 568 entries,
  SHA-256 `718525965b67ac4794db09bc3e3549daaac5254cbfa2e92c2552600bd878ea99`.

The operator reported a successful connection lasting more than ten minutes and successful
connection establishment behind NAT. That feedback supports the controlled pilot, but it does not
close the formal matrix without retained version, topology, selected route, timestamp and diagnostic
evidence. The overall decision therefore remains: locally revalidated for the controlled single-node
pilot, not a full production/HA/world-class acceptance pass.
