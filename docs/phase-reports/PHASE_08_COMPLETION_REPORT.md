# PeerOnQ Phase 8 completion report

## 1. Phase and final gate

**Phase:** 8 — cross-platform architecture and evidence-gated native clients.

**Final gate:** `PASS_WITH_EXTERNAL_BLOCKERS`.

Shared signaling versioning, bounded endpoint capability negotiation, optional-feature negotiation,
safe unknown-message handling, structured incompatibility errors and canonical byte vectors are
implemented and locally verified. Windows remains the only native client. Linux desktop, macOS,
Android and iOS/iPadOS clients are explicitly `NOT_FOUND`; no placeholder is advertised as support.

## 2. Repository snapshot

- Branch: `feat/phase1-remote-view`; base `HEAD`:
  `086631df7d95e71b5e13ce563de55b2a9e844e63`.
- Validation environment: Windows, Asia/Baku, 2026-08-17.
- The pre-change dirty status was captured and preserved. Final cumulative status has 178 entries
  (135 modified, 1 deleted and 42 untracked), mostly from earlier phases; no reset/clean was used.
- Toolchains: .NET SDK 10.0.303, Node 25.2.1, pnpm 10.33.0, Docker 29.7.2 and Compose 5.3.1.
- Native application and installer version remain 0.5.1. Portal package version is 0.7.0; public
  and Admin prototypes remain 0.0.0. Signaling changed from protocol 2 to 3; Cloud enrollment
  protocol remains its separate version 1 contract.
- No commit, push, tag, package publication, public deployment, DNS/firewall change, database
  migration, volume deletion or release artifact was performed.

## 3. Areas inspected

- Shared domain/application/transport/platform boundaries and current Windows composition.
- Signaling hello/register, codec, server dispatch, device registry, session creation and errors.
- Collaboration/input protocol boundaries to ensure signaling v3 did not silently relabel them.
- Phase 6 raw signaling acceptance caller and existing signaling/end-to-end tests.
- Platform restrictions and secure-storage/package evidence requirements for Linux, macOS, Android
  and iOS/iPadOS using current official platform documentation.
- Architecture, security, protocol, deployment, operations, standards, maps and Phase 7 report.

## 4. Components reused

- Existing `PeerOnQ.Transport` signaling codec/client and authenticated v2 handshake structure.
- Existing `SessionPermission`, `SessionAccessKind` and immutable session-scope validation.
- Existing `DeviceConnection` registry and pre-session server authorization path.
- Existing Windows Graphics Capture, `SendInput`, WebRTC, collaboration, DPAPI, MSI and update trust
  implementations. None was forked or weakened.
- Existing test harness with real loopback WebSocket server and Windows end-to-end media/session path.

## 5. Confirmed defects and proven root causes

1. Signaling version 2 authenticated identity but declared no native endpoint abilities. A viewer
   could ask the server to create a session whose host had no implemented capture/input/file feature;
   capability truth existed only in local code/UI assumptions.
2. Unknown, syntactically valid message discriminators were classified as malformed. This was safe
   but did not support explicit forward-compatible optional-message handling.
3. Version rejection exposed only a prose message, not received/minimum/maximum machine fields.
4. The Phase 6 acceptance client hand-built `hello`; after a v3 bump it would have omitted the new
   manifest unless updated separately. It now declares an honest empty synthetic capability set.
5. The repository had no per-platform truth ledger. A Linux server `.run` could be confused with a
   desktop client despite no Linux native client source existing.

## 6. Files created and modified

Created:

- `src/PeerOnQ.Transport/Protocol/CapabilityNegotiation.cs`.
- `src/PeerOnQ.Platform.Windows/WindowsClientCapabilityProfile.cs`.
- `tests/PeerOnQ.Signaling.Tests/CapabilityNegotiationTests.cs`.
- `test-vectors/signaling-v3/hello-linux-viewer.json`, `unsupported-version-v2.json` and README.
- `docs/CROSS_PLATFORM_CAPABILITIES.md` and this report.

Modified:

- Signaling messages/codec/client, server registry/handler and the Phase 6 acceptance caller.
- Windows platform project reference and application composition root.
- Signaling harness/flow/project, Windows end-to-end setup and affected architecture/security/
  threat/protocol/standards/limitations/project-map/changelog documentation.

## 7. Migrations

None. No database, local-storage, key, installer identity or persisted session format changed.

## 8. API/protocol/schema changes

- Signaling is now version 3 with exact supported range 3-3. Version 2 and future versions fail
  before challenge with `unsupported_version` and structured received/minimum/maximum fields.
- `hello.clientCapabilities` carries platform, bounded capability names, optional features and
  required server capabilities. It remains nullable in decoding only so a v2 hello can receive the
  correct version error instead of a malformed-frame error.
- `registered` returns accepted endpoint abilities, server abilities and negotiated optional features;
  the client verifies all three before becoming Registered.
- Session creation derives directional requester/target requirements from permissions/access kind
  and returns `capability_mismatch` before allocating state.
- A well-formed unknown discriminator becomes a bounded non-executable frame and receives
  `unsupported_message`; malformed JSON remains `malformed_message`.
- Collaboration and remote-input framing remain version 2 inside signaling-v3 sessions. HTTP APIs,
  routes, Cloud schema and storage keys did not change, so `ROUTES_MAP.md` required no update.

## 9. Security/privacy changes

- Capability declarations are bounded in count/name length, canonicalized, duplicate-checked and
  dependency-checked. Unknown well-formed capability names are inert unless a later protocol uses them.
- Input, capture, file, clipboard and unattended scopes require the correct directional endpoint
  abilities before consent/session state is created; this does not replace data-plane authorization.
- No security flag, consent, TLS validation, OS permission, UAC/UIPI/Secure Desktop restriction or
  audit control was weakened.
- No new secret, token persistence, telemetry field, screen/input content log or external service
  was added.

## 10. Exact tests and commands

```powershell
dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.Application.Tests/PeerOnQ.Application.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.EndToEnd.Tests/PeerOnQ.EndToEnd.Tests.csproj -c Release --no-restore
dotnet build src/PeerOnQ.App/PeerOnQ.App.csproj -c Release -r win-x64 --no-restore
dotnet build src/PeerOnQ.Infrastructure.Deployment/acceptance/PeerOnQ.Phase6.Acceptance.csproj -c Release --no-restore
.\scripts\security\scan-repository-secrets.ps1
.\scripts\quality\test-brand-purity.ps1
git diff --check
```

Targeted capability/version/unknown-frame tests were run first (15/15), then the complete affected
signaling suite. Earlier web, portal, database, media-only, installer and deployment suites were not
rerun from the beginning because this slice did not change those implementations.

## 11. Exact build/test results

- Signaling: **84 passed, 0 failed, 1 skipped**. The skip is the existing explicit opt-in Docker
  restart test; no test was disabled for Phase 8.
- Application: **104 passed, 0 failed, 0 skipped**.
- Windows end-to-end: **40 passed, 0 failed, 0 skipped**.
- WinUI x64 Release build: **0 warnings, 0 errors**.
- Phase 6 real-acceptance executable Release build: **0 warnings, 0 errors**.
- Capability/version vectors perform exact UTF-8 byte comparison including canonical property order and LF.
- Deployment acceptance regression: **1 passed, 0 failed, 0 skipped**.
- Secret scan: **942 repository files**, pass. Brand purity: pass.
- `git diff --check`: exit 0; only existing LF-to-CRLF conversion notices were printed.

## 12. Runtime and physical/external evidence

- Real loopback WebSocket client/server registration, capability echo, session denial and ordinary
  Windows sessions passed through production transport/server classes.
- Forty end-to-end tests exercised the existing Windows session/media/collaboration composition
  against signaling v3.
- No Linux desktop session, Mac, Android device, iPhone/iPad, separate network, app store, production
  signer or ten-minute physical non-Windows session was available. Those gates remain external and
  are not replaced by the Linux-named JSON vector.

## 13. Feature truth matrix

| Feature/platform | Status | Evidence and limitation |
| --- | --- | --- |
| Shared signaling v3/version errors | `IMPLEMENTED_AND_VERIFIED` | Real WebSocket tests; no third-party implementation interop |
| Capability/session negotiation | `IMPLEMENTED_AND_VERIFIED` | Pure + server flow tests reject missing host input before state creation |
| Optional-feature negotiation | `IMPLEMENTED_AND_VERIFIED` | Implemented-feature intersection and client verification tests |
| Unknown-message handling | `IMPLEMENTED_AND_VERIFIED` | Codec and live server response; no arbitrary payload execution |
| Canonical byte vectors | `IMPLEMENTED_AND_VERIFIED` | Exact UTF-8 hello/error encode/decode comparison; not a full protocol corpus |
| Windows host/viewer | `IMPLEMENTED_LOCAL_ONLY` | Build + local E2E; Phase 8 added no new physical-device proof |
| Windows unattended/background | `PARTIAL` | Signed-in user process only; no Windows service/secure desktop control |
| Windows hardware codec | `NOT_FOUND` | Capability probe is not an encoder/decoder path |
| Linux desktop client | `NOT_FOUND` | No project, executable, package or runtime; server `.run` excluded |
| macOS client | `NOT_FOUND` | No Xcode project/build/runtime/signing evidence |
| Android client | `NOT_FOUND` | No APK/AAB/project/device evidence |
| iOS/iPadOS client | `NOT_FOUND` | No Xcode project/archive/device/entitlement evidence |

The detailed per-capability/per-platform matrix is in `docs/CROSS_PLATFORM_CAPABILITIES.md`.

## 14. Measured performance results and environment

- Local Windows environment; .NET 10 Release.
- Signaling suite completed in about 6.1 seconds wall time (2 seconds test duration).
- Application suite completed in about 11.8 seconds wall time (7 seconds test duration).
- Windows end-to-end suite completed in about 35.2 seconds wall time (29 seconds test duration).
- WinUI build completed in about 22.8 seconds; acceptance build in about 1.8 seconds.
- These are build/test observations, not startup, FPS, WAN latency, mobile battery, physical-device or
  cross-platform performance benchmarks.

## 15. Brand-purity result

- All new identifiers use PeerOnQ-owned namespaces and protocol names.
- No competitor code, asset, layout or identifier was introduced.
- Automated brand purity passed; the repository secret scan also passed 942 files.

## 16. Dependency/SBOM/provenance impact

- No NuGet, pnpm, native codec or external runtime dependency was added.
- Existing SBOM/provenance inputs therefore do not gain a package entry; source and vector files will
  be covered by the next normal artifact checksum/provenance run.
- No MSI, mobile package, notarized archive, signed update or new SBOM was produced.

## 17. P0/P1/P2 issues

- **P0:** none known in the exercised Phase 8 protocol/capability objective.
- **P1 external:** no native Linux/macOS/Android/iOS project or physical/toolchain evidence; no
  production Windows signing or new two-device Phase 8 run.
- **P2 compatibility:** signaling v3 is intentionally incompatible with v2. Rebuild/deploy server
  and client as one controlled version transition; mixed versions fail clearly instead of half-connecting.
- **P2 coverage:** the canonical corpus currently contains hello and version-error vectors. Add
  vectors with each future native implementation and protocol message it consumes.

## 18. Known limitations

- Capability declarations prove protocol gating, not that a malicious third-party binary truthfully
  implements what it declares. Official packages still require signing, review and native tests.
- Windows is the only platform profile and remains local/controlled-beta evidence.
- No non-Windows secure storage, permission UI, capture/input, package/update, accessibility or
  background lifecycle implementation exists.
- Shared code remains .NET 10. A native client may reuse wire vectors/contracts without being forced
  to use .NET, but byte interoperability must be demonstrated.

## 19. External blockers

1. Linux desktop: choose and build a real native viewer/toolkit, then test package/install/render,
   secure storage and Wayland/X11 permission-denial behavior on physical Linux desktops.
2. macOS: supply a Mac/Xcode environment, implement ScreenCaptureKit/Accessibility/Keychain paths,
   then prove hardened runtime, signing and notarization.
3. Android: implement viewer/controller first and validate Keystore, lifecycle/network changes,
   TalkBack and package/store policy on physical devices; MediaProjection/accessibility host work is
   separately permission/policy gated.
4. iOS/iPadOS: implement viewer/controller first on real Apple hardware and prove Keychain,
   background/entitlement limits, VoiceOver and signed archive/App Store constraints. General input
   injection/unattended host is not claimed.
5. Run the required ten-minute physical and Windows/self-hosted interoperability matrix for every
   platform before changing any `NOT_FOUND` support claim.

## 20. Compatibility impact

- A signaling-v2 client receives structured `unsupported_version` from a v3 server before challenge.
  A v3 client similarly rejects a v2/newer server and does not enter Registered state.
- Current Windows application and server source both use v3. Existing installed v2 clients must be
  upgraded together with their self-hosted server; there is no misleading compatibility shim.
- Collaboration/input v2, Cloud protocol v1, HTTP APIs, database schema, installer identity and
  stored user data remain unchanged.

## 21. Exact readiness decision for the next phase

**Decision:** Phase 8 is `PASS_WITH_EXTERNAL_BLOCKERS`. The shared protocol/capability foundation and
Windows reference regressions are ready for the next phase. A next phase that builds on Windows or
shared signaling may proceed; no non-Windows platform may be advertised, packaged or called
production-ready until its section 19 native/physical gates pass.
