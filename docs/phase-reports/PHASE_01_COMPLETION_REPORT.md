# PeerOnQ — Phase 1 Completion Report

## 1. Phase and final gate

**Phase:** 1 — Buildable Windows foundation, stable identity, attended view-only session, and media correctness.

**Final gate:** `BLOCKED`

The code and local automated gates are green, but the primary objective requires a documented two-physical-Windows-device visual test. That test was not available in this environment. A loopback or local two-process success is not a substitute, so Phase 1 is not passed and work must not advance to Phase 2 on this evidence alone.

## 2. Repository snapshot

- Date: 2026-08-12
- Branch: feat/phase1-remote-view
- Baseline commit: ed8942e172367cf8c11d8968cb77005bae47bf54
- Pre-existing worktree state: the Recovery Gate R0 migration had extensive uncommitted changes. They were preserved; this report identifies only the Phase 1 additions made after that baseline.

## 3. Areas inspected

- Domain session primitives and device identity persistence.
- WinUI request, permission, viewer, and active-session affordances.
- SessionCoordinator request, approval, capture, media creation, and cleanup flow.
- WebSocket signaling request validation.
- WebRTC VP8/data-channel session factory and media tests.
- Existing local end-to-end, signaling, identity, frame-scaling, and cleanup evidence.

## 4. Components reused

- DeviceProvisioningService, DPAPI-backed identity storage, and PeerOnQId.
- SessionCoordinator, PermissionDialogHost, and the existing consent/audit lifecycle.
- SignalingConnectionHandler, replay/authentication infrastructure, and typed signaling messages.
- WebRtcMediaSession, bounded frame pipeline, Windows capture path, and existing deterministic media harness.

## 5. Confirmed defect and root cause

**Root cause:** SessionPermissionPolicy intentionally represents later session profiles, but the Phase 1 app, coordinator, signaling server, and media factory accepted that general policy without an additional phase-scope restriction. The WinUI also displayed controls for full control, file transfer, custom permissions, and unattended access. A request could therefore reach a later-phase capability even though the Phase 1 contract forbids granting it.

## 6. Files created and modified

Created:

- src/PeerOnQ.Domain/Sessions/Phase1SessionScope.cs
- tests/PeerOnQ.Domain.Tests/Phase1SessionScopeTests.cs
- docs/phase-reports/PHASE_01_COMPLETION_REPORT.md

Modified:

- src/PeerOnQ.Application/Sessions/SessionCoordinator.cs
- src/PeerOnQ.Signaling.Server/SignalingConnectionHandler.cs
- src/PeerOnQ.Media/WebRtcMediaSession.cs
- src/PeerOnQ.App/MainWindow.xaml and MainWindow.xaml.cs
- src/PeerOnQ.App/PermissionDialogHost.cs
- src/PeerOnQ.App/ViewerWindow.xaml and ViewerWindow.xaml.cs
- tests/PeerOnQ.Application.Tests/Fakes.cs and SessionCoordinatorTests.cs
- tests/PeerOnQ.Media.Tests/WebRtcLoopbackTests.cs
- tests/PeerOnQ.Signaling.Tests/SignalingFlowTests.cs
- PHASE1.md, PROJECT_MAP.md, docs/CURRENT_STATE.md, and AI_CHANGELOG.md

## 7. Migrations

None. Device identifiers, persisted records, wire schemas, and storage keys were not changed.

## 8. API, protocol, and schema changes

No schema or message shape changed. The signaling server now accepts only the existing view-only + ViewScreen + attended combination; all other existing request combinations fail with the established unsupported_mode error. This is a deliberate Phase 1 capability restriction.

## 9. Security and privacy changes

Phase1SessionScope supplies the same immutable capability test at independent boundaries:

- WinUI sends the sole valid scope and hides incompatible affordances.
- SessionCoordinator.RequestSessionAsync rejects invalid outgoing scope before session creation.
- SessionCoordinator.HandleIncomingAsync declines invalid scope before prompt, capture, or media.
- PermissionDialogHost.AskAsync defaults invalid requests to decline.
- SignalingConnectionHandler.TryValidateSessionScope rejects invalid protocol input.
- WebRtcMediaSession.CreateSharer and CreateViewer reject any non-view-only permission mask, so a collaboration data channel cannot be instantiated by the Phase 1 media runtime.

This adds defense in depth; it neither weakens authentication nor logs secrets or screen content.

## 10. Exact commands and tests

Executed from the repository root on 2026-08-12:

```text
dotnet test tests/PeerOnQ.Domain.Tests/PeerOnQ.Domain.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.Application.Tests/PeerOnQ.Application.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.Media.Tests/PeerOnQ.Media.Tests.csproj -c Release --no-restore
dotnet build PeerOnQ.slnx -c Release --no-restore
dotnet test PeerOnQ.slnx -c Release --no-build --no-restore
```

## 11. Exact build and test results

- Release solution build, including PeerOnQ.App: **passed**, 0 warnings, 0 errors, 31.63 s.
- Domain: **61 passed**, 0 skipped, 68 ms.
- Application: **59 passed**, 0 skipped, 6 s.
- Signaling: **63 passed**, 1 skipped, 3 s. The skipped test requires opt-in Docker restart acceptance.
- Media: **56 passed**, 1 skipped, 3 s. The skipped test requires opt-in live TURN configuration.
- Full .NET regression: **455 passed**, **2 skipped**, 33.1 s; no failures. End-to-end subset: **37 passed**.

## 12. Runtime and physical/external evidence

Local evidence includes real in-process VP8/WebRTC loopback, real socket signaling, deterministic frame/stride/scaling tests, and local end-to-end tests. It does **not** include a launched WinUI host and viewer on two separate physical Windows computers, a 10-minute session, monitor switching, or human visual inspection of static text, motion, colors, bands, tiling, tearing, and stale-frame symptoms. Physical evidence is therefore `EXTERNALLY_BLOCKED`.

## 13. Feature truth matrix

| Feature and status | Source symbol / entry / real caller | Test and what it proves | What it does not prove; external evidence, limitation, risk |
| --- | --- | --- | --- |
| Stable device identity — `IMPLEMENTED_LOCAL_ONLY` | DeviceProvisioningService.GetOrCreateAsync; app composition loads identity before registration. | Domain/infrastructure identity tests and full regression prove local persistence, protected storage, and consistency checks. | Not restart-tested on two physical computers in this phase; alias is not asserted as the cryptographic identity. |
| Authenticated signaling and attended consent — `IMPLEMENTED_LOCAL_ONLY` | SignalingConnectionHandler and SessionCoordinator.HandleIncomingAsync; client request and host prompt are the real callers. | 64 signaling and 59 application tests prove local challenge/proof, routing, accept/decline/timeout, and capture-after-approval paths. | Not a deployed or physical LAN run. |
| Phase 1 view-only enforcement — `IMPLEMENTED_AND_VERIFIED` | Phase1SessionScope; MainWindow.OnRequestSession; SessionCoordinator; PermissionDialogHost; SignalingConnectionHandler.TryValidateSessionScope; WebRtcMediaSession.CreateSharer/CreateViewer. | Phase1SessionScopeTests, coordinator negative tests, signaling rejection test, and WebRTC media-boundary tests prove invalid scopes reject before prompt/capture and cannot create a data channel. | Does not prove protection against a malicious locally modified binary. The supported Phase 1 runtime has no input/file/clipboard/unattended grant path. |
| Native session UI and visible end action — `IMPLEMENTED_LOCAL_ONLY` | MainWindow.OnRequestSession, ViewerWindow, and existing sharing-indicator wiring. | Release build includes the WinUI app; application flow tests compile and exercise the session state flow. | Not manually launched, accessibility-reviewed, or observed on physical devices this cycle. |
| Windows capture-to-render pipeline — `IMPLEMENTED_LOCAL_ONLY` | Windows capture source, frame pipeline, WebRtcMediaSession, and viewer renderer. | 57 media and 37 end-to-end tests cover deterministic patterns, conversion, stride, scaling, encode/decode, loopback, and cleanup. | No physical visual correctness evidence across displays and drivers; corruption acceptance remains blocked. |
| Cleanup and fail-closed input release — `IMPLEMENTED_LOCAL_ONLY` | SessionCoordinator.CloseAsync and existing media/capture cleanup. | Application lifecycle and media cleanup tests prove local disposal and terminal state behavior. | Not a 10-minute physical soak or driver/process-loss test. |
| Two physical Windows-device test — `EXTERNALLY_BLOCKED` | Manual entry: launched host + viewer on the same LAN. | None; no substitute asserted. | Does not prove physical color fidelity, resize/switch, repeated reconnect, or identity persistence. Required host, viewer, LAN, and operator were unavailable. |

## 14. Performance results and environment

No new benchmark was run. This report deliberately records no latency, FPS, memory, first-frame, or queue-depth values as measured performance. Existing counters and deterministic harnesses are implementation evidence only. Reference hardware, OS build, network profile, and a 10-minute physical measurement record are still required.

## 15. Brand-purity result

Recovery Gate R0 recorded the brand-purity gate as passing. This change adds only PeerOnQ names and does not add a user-visible legacy identifier. The R0 command was not repeated in this slice; therefore this is inherited evidence, not a fresh Phase 1 brand scan.

## 16. Dependency, SBOM, and provenance impact

No dependency, package, codec, SBOM, installer, artifact, provenance, or deployment configuration changed. R0 dependency/SBOM evidence remains the applicable baseline.

## 17. P0, P1, and P2 issues

- **P0:** physical two-device visual acceptance is missing; it blocks the primary Phase 1 objective.
- **P1:** later-phase collaboration implementation remains in source for compatibility, but Phase 1 boundaries now reject it. A future phase must deliberately re-authorize it with its own evidence.
- **P2:** no new P2 issue identified by the modified test suites.

## 18. Known limitations

- No physical test of chroma correctness, repeated/tiled areas, horizontal bands, tearing, or stale frames.
- No distinct local two-process native-app invocation; existing end-to-end evidence runs real
  components inside the test process.
- No 10-minute soak, monitor switch, repeated connect/disconnect, or physical identity restart test.
- Live TURN and Docker restart tests were not opt-in configured for this run.
- No fresh benchmark or independent security/accessibility review was performed.

## 19. External blockers

The required two physical Windows devices, shared LAN, and a human-run visual test script were not available to this execution environment. Required evidence remains `EXTERNALLY_BLOCKED`.

## 20. Compatibility impact

Existing clients that request full control, file transfer, clipboard, custom permissions, or unattended access now receive the existing unsupported_mode signaling error. The request schema and persisted data were not changed. The restriction is intentionally not backward-capable because the Phase 1 contract forbids granting those capabilities.

## 21. Exact readiness decision for the next phase

**Do not begin Phase 2.** Run and document the required physical Windows-to-Windows acceptance matrix first: at least 10 minutes, static text, motion, color/stride patterns, monitor resize or switch, repeated connect/disconnect, decline/timeout, application restart, identity persistence, and explicit review for corruption/stale-frame symptoms. Only then may Phase 1 be reconsidered for `PASS` or a justified `PASS_WITH_EXTERNAL_BLOCKERS` consistent with the contract.
