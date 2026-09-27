# PeerOnQ — Phase 2 Completion Report

## 1. Phase and final gate

**Phase:** 2 — Explicit consent, secure keyboard/mouse control, and visible session safety.

**Final gate:** `PASS_WITH_EXTERNAL_BLOCKERS`

The implementation, automated regressions, real WebRTC loopback, local server, packaged-payload
runtime, revocation, local release, emergency stop, and installer gates pass. The contract's required
two-physical-Windows-computer matrix and ten-minute session were not available and are not claimed.

## 2. Repository snapshot

- Date: 2026-08-17 (Asia/Baku)
- Branch: `feat/phase1-remote-view`
- Baseline commit: `086631df7d95e71b5e13ce563de55b2a9e844e63`
- Worktree: dirty before this phase; all pre-existing modified/untracked user work was preserved.
- Toolchain: .NET SDK 10.0.303, Node v25.2.1, pnpm 10.33.0, Docker 29.7.2.
- Local runtime: Windows 11 Pro 10.0.26200; Docker signaling/TURN/proxy healthy on `10.0.0.10`.
- Deliverable: unsigned x64 LAN Development MSI `0.7.5`.

## 3. Areas inspected

- Session permission profiles, attended permission dialog, timeout, accept/decline/block, signaling
  ownership, reconnect, audit, and application shutdown.
- Collaboration framing, authorization, WebRTC data-channel ownership, queueing, replay/rate checks,
  viewer input capture, pointer mapping, monitor switching, and Windows `SendInput`.
- Host indicator, tray lifecycle, UIAutomation exposure, installer build/validation, architecture,
  security model, threat model, maps, and prior phase reports.

## 4. Components reused

- `SessionCoordinator`, `Phase1SessionScope`, `SessionPermissionPolicy`, and the existing attended
  signaling/media session.
- `MediaCollaborationTransport` and the existing authenticated ordered DTLS/SCTP data channel.
- `RemoteInputSession`, `RemotePointerMapper`, `WindowsInputController`, and selected-display capture.
- Existing WinUI viewer, permission dialog, sharing indicator, tray icon, audit log, and WiX pipeline.

No second signaling, media, input, authentication, or permission stack was created.

## 5. Confirmed defects and root causes

- Input commands had sequence/rate/range checks but no explicit session, reconnect, or focus
  generation. A delayed pre-reconnect or wrong-session frame was not independently bound at the
  application protocol layer.
- Viewer capture became active without a host application-level focus acknowledgment.
- The accepted mask was immutable, but no monotonic live `ControlInput` revocation existed.
- Horizontal wheel was not represented; monitor switching did not explicitly pause viewer focus.
- There was no viewer-local release chord or host global emergency stop chord.
- A partial/failed `SendInput` release cleared held-state bookkeeping even when Windows rejected the
  up events, preventing a later retry.
- A short Wi-Fi interruption could leave the client WebSocket half-open because the application
  heartbeat did not enforce a PONG deadline. If the viewer resumed, the online sharer was not told
  to create the deterministic ICE-restart offer, so media could remain stuck after registration.
- The LAN Quality profile used native dimensions but did not enable libvpx desktop screen-content
  mode and its bitrate/quantizer limits made small text visibly blocky.

## 6. Files created and modified

Product/protocol:

- `src/PeerOnQ.Application/Abstractions/Connectivity.cs`
- `src/PeerOnQ.Application/Collaboration/CollaborationProtocol.cs`
- `src/PeerOnQ.Application/Collaboration/MediaCollaborationTransport.cs`
- `src/PeerOnQ.Application/Collaboration/RemoteInputSession.cs`
- `src/PeerOnQ.Application/Sessions/SessionCoordinator.cs`
- `src/PeerOnQ.Application/Abstractions/IMediaSession.cs`
- `src/PeerOnQ.App/AppServices.cs`
- `src/PeerOnQ.Transport/WebSocketSignalingClient.cs`
- `src/PeerOnQ.Signaling.Server/SignalingConnectionHandler.cs`
- `src/PeerOnQ.Media/Codecs/Vp8ScreenEncoder.cs`
- `src/PeerOnQ.Platform.Windows/Input/WindowsInputController.cs`
- `src/PeerOnQ.App/ViewerWindow.xaml.cs`
- `src/PeerOnQ.App/MainWindow.xaml`
- `src/PeerOnQ.App/MainWindow.xaml.cs`
- `src/PeerOnQ.App/SharingIndicatorWindow.xaml`
- `src/PeerOnQ.App/SharingIndicatorWindow.xaml.cs`
- `src/PeerOnQ.App/TrayIcon.cs`

Tests/docs:

- `tests/PeerOnQ.Application.Tests/Fakes.cs`
- `tests/PeerOnQ.Application.Tests/Phase4CollaborationTests.cs`
- `tests/PeerOnQ.Application.Tests/SessionCoordinatorTests.cs`
- `tests/PeerOnQ.Signaling.Tests/TurnAndResumeTests.cs`
- `tests/PeerOnQ.Signaling.Tests/WebSocketSignalingClientTests.cs`
- `tests/PeerOnQ.Media.Tests/Vp8ScreenEncoderTests.cs`
- `tests/PeerOnQ.Media.Tests/WebRtcLoopbackTests.cs`
- `PROJECT_MAP.md`, `AI_CHANGELOG.md`
- `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/THREAT_MODEL.md`
- `docs/phase-reports/PHASE_02_COMPLETION_REPORT.md`

## 7. Migrations

None. No database, persisted identity, secret, or installed profile was changed or deleted.

## 8. API, protocol, and schema changes

Input protocol v2 adds required `inputVersion`, `sessionId`, `sessionGeneration`, `focusGeneration`,
and `sequence` fields to every remote-input message. New discriminators are:

- `input.focusRequest`
- `input.focusResult`
- `input.permissionRevoked`

`input.command` and `input.releaseAll` now use the same binding envelope. `RemoteInputEvent` adds
`IsHorizontalWheel`. `SessionCoordinator.RevokeControlAsync` exposes sharer-side live revocation.

The Wi-Fi recovery change adds no wire discriminator: it reuses the authenticated
`SessionResumedMessage` with `peer_resumed` reason to notify the still-online participant.

Compatibility impact: Full Control requires both endpoints to run `0.7.1` or a later compatible
input-v2 client. A mismatch times out/halts input fail closed; it must not be shown as working input.

## 9. Security and privacy changes

- The host acknowledges a fresh focus generation before the viewer forwards input.
- Host enforcement rechecks immutable permission, effective revocation state, direction, protocol
  version, session/reconnect/focus binding, sequence, rate, value range, and platform acceptance.
- Revocation disables/releases locally before network notification, is permanent for that session,
  survives reconnect, and writes only permission/outcome metadata—never keys or coordinates—to audit.
- Bounded pending sends and pointer coalescing prevent unbounded input backlog.
- Viewer focus loss, monitor change, local release, reconnect, revocation, close, end, and shutdown
  stop forwarding and release held state.
- `SendInput` remains standard-user only. Ctrl+Alt+Delete synthesis is rejected; UIPI, UAC, Secure
  Desktop, elevated-window, antivirus, firewall, and Windows integrity boundaries are not bypassed.
- Failed releases retain held-state bookkeeping for retry and record only sanitized Win32 codes.
- LAN clients enforce bounded app/WebSocket heartbeat PONG deadlines. A resumed participant causes
  both sides to disable input and finish a fresh ICE negotiation before the session is restored.

## 10. Exact tests and commands

```powershell
dotnet test tests\PeerOnQ.Application.Tests\PeerOnQ.Application.Tests.csproj -c Release --no-restore
dotnet test tests\PeerOnQ.Media.Tests\PeerOnQ.Media.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Full_control_uses_video_and_an_authorized_input_data_channel"
dotnet test PeerOnQ.slnx -c Release --no-restore
dotnet build PeerOnQ.slnx -c Release --no-restore
.\scripts\quality\test-brand-purity.ps1
dotnet list PeerOnQ.slnx package --vulnerable --include-transitive
.\scripts\windows\build-phase5-development.ps1 -Version 0.7.5 -SignalingUrl 'wss://signal.10.0.0.10.sslip.io:5443/ws' -Architectures x64 -DevelopmentRootCertificate '.peeronq-phase3\certs\root-ca.cer' -SkipWebsitePublish
.\scripts\windows\test-phase5-installer.ps1 -MsiPath 'dist\lan-development\signal.10.0.0.10.sslip.io\0.7.5\PeerOnQ-0.7.5-unsigned-development-x64.msi' -Architecture x64 -ExpectedPublishDirectory 'dist\lan-development\signal.10.0.0.10.sslip.io\0.7.5\x64\app'
.\.peeronq-phase3\runtime\validate-064-final.ps1
```

## 11. Exact build and test results

- Application tests: 87 passed, 0 failed, 0 skipped; the Wi-Fi/ICE/path regressions also pass as a
  focused gate.
- Signaling tests: 70 passed, 0 failed, 1 explicit opt-in Docker-restart test skipped.
- Media tests: 58 passed, 0 failed, 1 explicit opt-in live-TURN test skipped.
- Target real WebRTC Full Control loopback: 1 passed, 0 failed.
- The unrelated complete-solution test matrix was not rerun for this follow-up, per the operator's
  phase-scoped testing instruction. Application, Signaling, Media, and End-to-End suites above are
  the current relevant regressions; the complete Release solution build still passed.
- Release solution build: passed, 0 warnings, 0 errors, 16.5 seconds in the final run.
- LAN Development publish/MSI: passed, 0 warnings, 0 errors; WiX ICE03 passed.
- MSI extraction/self-contained/exact-payload validation: passed.
- `0.7.5` MSI: 78,438,400 bytes; SHA-256
  `D097DDDAEED809247B6ED5EC534B4A5AFF868BE328C189373A3ED33E4AD2FC00`.
- Brand purity: passed.
- NuGet vulnerability query: all 30 projects reported no known vulnerable direct or transitive
  package from the configured source.

## 12. Runtime and physical/external evidence

Local runtime evidence used two isolated `0.7.1` packaged-payload clients, separate data directories,
the real LAN WSS endpoint, real WebRTC, and UIAutomation on one Windows host. The final run proved:

- explicit Full Control acceptance and host indicator;
- 1920x1080 rendering with no adaptive downscale event during the sampled interval;
- raw viewer pointer movement reached the host Windows injection boundary;
- Ctrl+Alt+Shift+Esc paused viewer input;
- host revocation disabled the viewer toggle while view remained active;
- Ctrl+Alt+Shift+F12 closed both peers while the viewer held focus;
- viewer end, sharer end, viewer title-bar close, and host application shutdown closed both sides;
- six captures started and six stopped; final logs had zero Warning/Error/Fatal,
  NegotiationTimeout, or input-rejection matches.

This is `IMPLEMENTED_LOCAL_ONLY` runtime evidence, not two-physical-device evidence. No physical
keyboard/button/wheel, elevated-window, multi-monitor, separate-network, 4K, or ten-minute claim is
made.

Physical-client/server logs later exposed the half-open Wi-Fi/peer-resume defect described above.
The corrected `0.7.5` client, relevant automated suites, exact MSI payload, and updated LAN server
pass locally; the requested two-laptop Wi-Fi interruption and image-quality confirmation remains the
external acceptance gate.

## 13. Feature truth matrix

| Feature / status | Source, symbol, entry point, real caller | Automated evidence: proves / does not prove | Runtime/external evidence, limitation, risk |
| --- | --- | --- | --- |
| Explicit consent and immutable accepted mask — `IMPLEMENTED_LOCAL_ONLY` | `PermissionDialogHost.AskAsync`; `SessionCoordinator.HandleIncomingAsync`; signaling request caller | Domain/coordinator/signaling tests prove exact Full Control mask, deny/block/timeout, and no implied file/clipboard; not physical UI timing | Same-host packaged run accepted visible Full Control; two physical devices blocked |
| Host authorization and input binding — `IMPLEMENTED_AND_VERIFIED` | `RemoteInputSession.OnMessageReceived`; called by `MediaCollaborationTransport` data receive | Tests prove unauthorized construction, host-boundary rejection, sequence replay, wrong session, stale reconnect/focus generation, range and rate failure; not OS injection on another machine | Real loopback and same-host data path passed; malicious cross-version peer UX remains a compatibility risk |
| Bounded ordered transport and coalescing — `IMPLEMENTED_AND_VERIFIED` | `RemoteInputSession.SendMessageAsync` / `PumpPointerAsync`; viewer event handlers | Tests prove latest-pointer coalescing, ordered frames and rate fail-close; not WAN latency/load benchmark | Real WSS/WebRTC pointer reached Windows boundary; sustained stress not measured |
| Mouse/keyboard/device correctness — `IMPLEMENTED_LOCAL_ONLY` | `ViewerWindow` pointer/key handlers; `RemotePointerMapper`; `WindowsInputController.TryInject` | Tests prove normalization, DPI/letterbox/scroll math, negative offsets, buttons/key flow, horizontal-wheel schema; not every physical mouse/keyboard | 1920x1080 raw move passed; physical all-button/wheel/text/modifier/multi-monitor matrix blocked |
| Live permission revocation — `IMPLEMENTED_LOCAL_ONLY` | `SessionCoordinator.RevokeControlAsync`; main/indicator/tray callers; viewer permission event | Tests prove immediate disable, audit, viewer notification, view preservation, and no reconnect regrant | Packaged run proved host UI revoke kept view and disabled input; physical evidence blocked |
| Held-state and reconnect safety — `IMPLEMENTED_AND_VERIFIED` | `RemoteInputSession.ConnectionInterrupted`; `WindowsInputController.ReleaseAllCore`; coordinator teardown | Tests prove release, stale-frame rejection, shutdown cleanup, reconnect reduced scope; native `SendInput` failure is source-reviewed but not deterministically fault-injected | Local release/reconnect/close paths passed; UIPI failure hardware matrix blocked |
| Visible safety and emergency stop — `IMPLEMENTED_LOCAL_ONLY` | `SharingIndicatorWindow`; `TrayIcon` hotkey/menu; main taskbar session actions | Build/source tests prove wiring; no automated Win32 unit substitutes for registration behavior | Packaged UIA proved affinity `0x11`, local visibility, click-through, revoke, and global stop; Windows configurations/hotkey conflicts remain |
| Windows elevated/UAC behavior — `EXTERNALLY_BLOCKED` | `WindowsInputController` standard `SendInput`; asInvoker manifest | Source and build prove no service/driver/elevation bypass; do not prove physical integrity-level behavior | Expected: lower-integrity PeerOnQ cannot control elevated/UAC/Secure Desktop; required physical test absent |
| Two physical Windows devices and ten-minute session — `EXTERNALLY_BLOCKED` | Manual acceptance matrix | No automated substitute is accepted | Requires operator, two devices, physical peripherals, scaling/monitors, restart/network fault, elevated target, and ten minutes |

## 14. Measured performance results and environment

Reference environment: Windows 11 Pro build 26200, one 1920x1080 display, same host, local WSS/WebRTC
through the configured LAN endpoint. The live gate sampled native 1920x1080 for six seconds and found
zero logged downscale events. No input-latency, FPS, CPU/RAM/GPU, sustained 4K, WAN, or ten-minute
benchmark was performed; no theoretical number is reported as measured.

## 15. Brand-purity result

`scripts/quality/test-brand-purity.ps1` passed: no forbidden former product brand outside the
compatibility manifest. All new identifiers and UI strings use PeerOnQ.

## 16. Dependency, SBOM, and provenance impact

No dependency was added or upgraded by this Phase 2 slice. The fresh NuGet query reported no known
vulnerable direct or transitive package in all 30 projects. No SBOM schema or provenance pipeline
change was required. The MSI is explicitly unsigned Development output and is not a production
release or signed provenance claim.

## 17. P0, P1, and P2 issues

- **P0 external gate:** required two-physical-device Phase 2 matrix and ten-minute session are absent.
- **P1:** deterministic native fault injection for partial `SendInput`/release rejection is not yet
  automated; source retains state and fails closed, while physical UIPI/elevation behavior remains to
  be exercised.
- **P2:** the global emergency chord can conflict with another application; registration failure is
  detected and the visible tray/taskbar/end controls remain available.

## 18. Known limitations

- Both endpoints must use input protocol v2 (`0.7.1` or compatible) for Full Control.
- Standard-user PeerOnQ cannot control elevated windows, UAC prompts, Secure Desktop, or synthesize
  Ctrl+Alt+Delete. This is intentional.
- The development MSI uses a local CA and is unsigned; it is not suitable for public distribution.
- Same-host runtime does not prove physical LAN firewall/NAT, separate keyboards/mice, multi-monitor
  hardware, 4K performance, or a ten-minute soak.

## 19. External blockers

- Two physical Windows computers and an operator to execute/document every required input case.
- A normal and elevated target application/UAC prompt on the host device.
- Physical multi-monitor/scaling and network interruption/restart scenarios.
- At least one uninterrupted ten-minute Full Control session with measurements.

## 20. Compatibility impact

- Input v2 and signaling protocol v1 are fail-closed wire changes. Install the same `0.7.5` MSI on both test laptops.
- View-only media/signaling stays on the existing stack.
- Accepted permission history remains immutable; only effective live `ControlInput` is reduced.
- No persistent-data migration or destructive rollback is required.

## 21. Exact readiness decision for the next phase

The Phase 2 implementation is locally ready and has no known code P0 in the tested path. It may be
used for the requested two-laptop acceptance with the `0.7.5` LAN Development MSI. Do not declare a
full Phase 2 `PASS`, production readiness, or physical-device completion until the external matrix in
section 19 is executed and attached. Non-dependent engineering may continue under
`PASS_WITH_EXTERNAL_BLOCKERS`; release/signing/public-deployment work remains gated.
