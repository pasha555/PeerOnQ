# PeerOnQ - Phase 3 Completion Report

## 1. Phase and final gate

**Phase:** 3 - Internet connectivity, STUN/TURN, authenticated reconnect, and stable session transport.

**Final gate:** `PASS_WITH_EXTERNAL_BLOCKERS`

The local implementation has no known P0 defect in the Phase 3 objective. The full real-container
controller passes, Phase 1-2 application regressions pass, the signaling protocol now rejects
incompatible peers before authentication/session work, and reconnect performs fail-closed
reauthentication plus fresh ICE. Required two-device/public-network conditions cannot be reproduced
on this one host and remain explicitly blocked; this is not a production-release approval.

## 2. Repository snapshot

- Date: 2026-08-17 (Asia/Baku).
- Branch: `feat/phase1-remote-view`.
- Baseline commit: `086631df7d95`.
- Worktree: 57 modified/untracked entries at the recorded snapshot; pre-existing user changes were
  preserved and no reset, deletion, or unrelated rewrite was performed.
- Environment: Windows 11 Pro `10.0.26200`, .NET SDK `10.0.303`, Docker Engine `29.7.2`, Docker
  Compose `5.3.1`.

## 3. Areas inspected

- Repository/project/route maps, architecture, security, threat model, deployment, operations,
  standards baseline, test matrix, performance record, and Phase 2/3 reports.
- WSS registration/version/authentication, heartbeat/PONG handling, duplicate ownership, session
  resume, TURN credential issuance, ICE configuration/nomination, media reconnect, input fail-close,
  desktop VP8 encoding, viewer scaling/pointer geometry, local Compose, and installer tooling.
- Physical/server logs available on the host for the reported Wi-Fi interruption. The physical
  endpoint logs were not locally available, so no absent evidence was inferred.

## 4. Components reused

- `WebSocketSignalingClient`, `SignalingConnectionHandler`, `SessionRegistry`, and
  `TurnCredentialService`.
- `SessionCoordinator`, `SessionCollaborationContext`, `RemoteInputSession`, and the existing typed
  reconnect state machine.
- `WebRtcMediaSession`, `Vp8ScreenEncoder`, Windows Graphics Capture, and the existing nominated-pair
  statistics path.
- Existing Nginx/coturn Compose topology, development CA boundary, WiX installer, and validation
  scripts. No second signaling, media, ICE, or input stack was introduced.

## 5. Confirmed defects and proven root causes

- **Half-open WSS was not detected:** application Ping frames were emitted but Pong age was not
  enforced, while `ClientWebSocket` had no finite keepalive timeout. A short Wi-Fi interruption could
  therefore leave the client apparently connected until an OS TCP timeout.
- **Reconnect order race:** media loss could enter reconnect before signaling loss, while a fast WSS
  registration could complete before the recovery loop observed it. Resume was not guaranteed in
  both orderings.
- **One-sided resume:** the server acknowledged only the reconnecting participant. The still-online
  sharer, which is the deterministic ICE offerer, did not learn that it must renegotiate.
- **Transient recovery send ended the session:** one failed resume/offer send escaped the bounded
  retry loop instead of remaining a reconnect attempt.
- **LAN image quality ceiling was too low for native desktop detail:** the Quality profile used a
  lower ceiling and the encoder lacked its screen-content control; small text was over-quantized.
- **Viewer sizing control was ambiguous:** the old Fit action did not expose crop, stretch, or
  DPI-correct actual-size alternatives requested for different endpoint displays.
- **Signaling compatibility was implicit:** no explicit wire protocol number existed, allowing an
  incompatible peer to begin a partial handshake.

## 6. Files created and modified

Created:

- `docs/PROTOCOL_COMPLIANCE.md`.
- `tests/PeerOnQ.Signaling.Tests/WebSocketSignalingClientTests.cs`.

Principal modified files:

- `src/PeerOnQ.Transport/Protocol/SignalingMessages.cs` and
  `src/PeerOnQ.Transport/WebSocketSignalingClient.cs`.
- `src/PeerOnQ.Signaling.Server/SignalingConnectionHandler.cs` and
  `src/PeerOnQ.Signaling.Server/Sessions/SessionRegistry.cs`.
- `src/PeerOnQ.Application/Abstractions/Connectivity.cs` and
  `src/PeerOnQ.Application/Sessions/SessionCoordinator.cs`.
- `src/PeerOnQ.App/AppServices.cs`, `ViewerWindow.xaml`, and `ViewerWindow.xaml.cs`.
- `src/PeerOnQ.Application/Collaboration/RemotePointerMapper.cs`.
- `src/PeerOnQ.Application/Abstractions/IMediaSession.cs` and
  `src/PeerOnQ.Media/Codecs/Vp8ScreenEncoder.cs`.
- Related Application, Signaling, Media, and pointer-mapper tests plus repository maps, Phase 2/3
  records, architecture/security/performance/design docs, LAN controller guidance, and changelog.

## 7. Migrations

None. No database schema, persisted client state, or data migration changed.

## 8. API, protocol, and schema changes

- Added signaling protocol version `1` to `hello` and `registered` frames.
- Added machine-readable `unsupported_version`; missing/older/newer clients fail before challenge
  and do not enter the reconnect loop indefinitely.
- Stored the negotiated protocol version beside each protected resume hash and require an exact
  match before the one-use credential can be consumed.
- Added typed `SessionPeerResumedNotification`; successful resume now notifies the connected peer so
  both participants can perform fresh ICE.
- No public HTTP API, OpenAPI schema, database schema, or route changed.
- The full standards/library map is [PROTOCOL_COMPLIANCE.md](../PROTOCOL_COMPLIANCE.md).

## 9. Security and privacy changes

- WSS reconnect repeats device authentication; a production re-registration fetches a fresh
  memory-only attestation. A revoked/invalid attestation is rejected after interruption.
- Resume remains short-lived, rotating, replay-protected, session/device/mode/participant bound, and
  server-stored only as SHA-256. Tokens and TURN secrets are not logged.
- Input/collaboration is disabled and held state is released before recovery. It is not restored
  until authenticated resume, permission revalidation, fresh ICE, and a new input-focus generation.
- Protocol mismatch fails closed before challenge. TLS validation remains enabled; the local CA is
  bundled only in the explicitly unsigned LAN Development build and pinned to its configured WSS
  host. Production trust/startup policy was not weakened.
- Candidate addresses remain absent from normal UI/log/diagnostic exports. No screen, key, clipboard,
  file-content, password, private-key, or session-token logging was added.

## 10. Exact tests and commands

```powershell
.\scripts\windows\peeronq-phase3-local.ps1 test -BindAddress 10.0.0.10
dotnet test tests/PeerOnQ.Application.Tests/PeerOnQ.Application.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.Media.Tests/PeerOnQ.Media.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.EndToEnd.Tests/PeerOnQ.EndToEnd.Tests.csproj -c Release --no-restore
dotnet build src/PeerOnQ.App/PeerOnQ.App.csproj -c Release -r win-x64 --no-restore
dotnet build PeerOnQ.slnx -c Release --no-restore
.\scripts\quality\test-brand-purity.ps1
.\scripts\windows\build-phase5-development.ps1 -Version 0.7.5 -SignalingUrl 'wss://signal.10.0.0.10.sslip.io:5443/ws' -Architectures x64 -DevelopmentRootCertificate '.peeronq-phase3\certs\root-ca.cer' -SkipWebsitePublish
.\scripts\windows\test-phase5-installer.ps1 -MsiPath 'dist\lan-development\signal.10.0.0.10.sslip.io\0.7.5\PeerOnQ-0.7.5-unsigned-development-x64.msi' -Architecture x64 -ExpectedPublishDirectory 'dist\lan-development\signal.10.0.0.10.sslip.io\0.7.5\x64\app'
.\scripts\windows\peeronq-phase3-local.ps1 start -BindAddress 10.0.0.10
.\scripts\windows\peeronq-phase3-local.ps1 status -BindAddress 10.0.0.10
```

## 11. Exact build and test results

- Full local Phase 3 controller: exit `0`, 147.6 s; Signaling 67 passed/1 normal opt-in skip,
  End-to-End 40/40, then all live STUN/TURN/WSS/restart checks passed.
- Final Application suite: exit `0`, 87/87 passed, 0 skipped, 6 s test time (8.7 s command).
- Final Signaling suite: exit `0`, 70/70 passed, 1 normal opt-in Docker-restart skip, 2 s test time
  (5.7 s command).
- Final Media suite: exit `0`, 58/58 passed, 1 normal opt-in live-TURN skip, 6 s test time
  (9.0 s command).
- Final End-to-End suite: exit `0`, 40/40 passed, 0 skipped, 28 s test time (32.6 s command).
- WinUI x64 Release: exit `0`, 0 warnings, 0 errors, 17.3 s.
- Full solution Release build: exit `0`, 0 warnings, 0 errors, 16.5 s in the final run.
- MSI build/ICE03: exit `0`, 0 warnings, 0 errors, 109.4 s. Administrative extraction and exact
  payload validation: exit `0`, 11.2 s.
- Brand purity: exit `0`; no forbidden former product brand outside the compatibility manifest.
- Final MSI: 78,438,400 bytes; SHA-256
  `D097DDDAEED809247B6ED5EC534B4A5AFF868BE328C189373A3ED33E4AD2FC00`.

The controller ran after the reconnect/quality changes and before the additive protocol-version
field was introduced. The final protocol source was then covered by the complete Signaling and E2E
suites, WinUI/solution builds, MSI payload validation, and a rebuilt healthy local server. The live
TURN controller was not redundantly rerun because those media/deployment paths did not change.

## 12. Runtime and physical/external evidence

- Real local Docker Nginx/signaling/coturn passed readiness, real WSS upgrade, STUN reflexive-address
  discovery, authenticated TURN allocation over UDP/TCP, DTLS 1.2 and TLS 1.3, invalid/expired
  credential rejection, forced relay video over UDP and TCP, two-second TURN interruption recovery,
  relay-only file transfer, and signaling restart/re-authentication.
- The final signaling protocol v1 image was rebuilt and is healthy at
  `wss://signal.10.0.0.10.sslip.io:5443/ws`; TURN is healthy on 3478/5349 with relay UDP 49160-49200.
  The Windows Wi-Fi profile is Private.
- Post-restart logs show all three containers healthy with restart count zero. Transient Nginx
  upstream errors occurred only while the signaling container was being recreated. A still-running
  installed `0.7.1` client is repeatedly dropped because it predates protocol v1; this is expected
  incompatibility, not a server-health failure, and ends when all endpoints upgrade to `0.7.5`.
- Earlier user-run two-laptop evidence proved same-LAN registration, attended acceptance, video, and
  input on an older package. It does not count as final `0.7.5` reconnect/quality acceptance.
- No final two-physical-device, separate-ISP, hotspot, sleep/wake, IPv6-only/dual-stack, long soak,
  relay credential rotation, or clean public deployment evidence exists.

## 13. Feature truth matrix

| Feature and status | Source/symbol, entry point, caller/wiring | Automated evidence: proves / does not prove | Runtime/external evidence, limitation, risk |
| --- | --- | --- | --- |
| Signaling protocol compatibility - `IMPLEMENTED_AND_VERIFIED` | `SignalingProtocol.CurrentVersion`; client `ConnectCoreAsync`; server `RegisterAsync`; every WSS registration | Version mismatch tests prove missing/old/new rejection and matching registration; do not prove future migration UX | Final server v1 is healthy; old clients intentionally cannot connect and must upgrade |
| Authenticated WSS/proxy - `IMPLEMENTED_LOCAL_ONLY` | `WebSocketSignalingClient.ConnectAsync`; `SignalingConnectionHandler`; Nginx `/ws` | Registration, proof, replay/skew, proxy and malformed-frame tests pass; not a public CA/browser interop test | Real local TLS/WSS passes; public Host/SNI/certificate rotation remains external |
| STUN/direct ICE - `IMPLEMENTED_LOCAL_ONLY` | `WebRtcMediaSession.BuildConfiguration` and `GetSelectedPath`; session media setup | Direct candidate and nominated-pair tests prove selection/reporting; not NAT64/symmetric NAT | Real same-host direct and STUN reflexive address pass; separate networks unavailable |
| TURN UDP/TCP/TLS credentials and quotas - `IMPLEMENTED_LOCAL_ONLY` | `TurnCredentialService`; accepted-session ICE request; coturn entry point | Expiry/invalid-HMAC/authorization tests prove bounded issuance/rejection; not public abuse/load resilience | Real coturn UDP/TCP relay media, TLS/DTLS allocation and file transfer pass locally |
| Reconnect/reauth/resume - `IMPLEMENTED_LOCAL_ONLY` | `SessionCoordinator.BeginReconnect/RunReconnectLoopAsync`; `WebSocketSignalingClient.ResumeSessionAsync`; `SessionRegistry` | Media-first, signaling-first, heartbeat, one-use/expiry/replay, cancellation and final failure tests pass; not a physical adapter transition | Local restart/interruption paths pass; final physical Wi-Fi/hotspot/sleep testing is blocked |
| Peer resume and fresh ICE - `IMPLEMENTED_LOCAL_ONLY` | server resume handler -> `SessionPeerResumedNotification` -> `OnPeerResumed`; sharer offerer | Tests prove online peer notification, both race orders, and a new offer; not a real NAT rebinding | Real changing-router/ISP nomination is externally blocked |
| Input fail-close and stale rejection - `IMPLEMENTED_AND_VERIFIED` | `ConnectionInterrupted`, `RemoteInputSession`, `IInputSafetyController`; coordinator interruption/end wiring | Phase 2/application tests prove held release, stale generation/session/sequence rejection, revocation and no reconnect regrant; not elevated/UAC control | Same-host input reached Windows boundary previously; physical final package remains external |
| Device revoke during interruption - `IMPLEMENTED_AND_VERIFIED` | `ISignalingAttestationProvider` -> client registration -> `CloudSignalingAttestationValidator` | New test proves the second registration fetches current attestation and invalid/revoked proof is rejected; not a deployed IdP revocation run | Production identity deployment is external; LAN mode intentionally has no cloud account authority |
| Direct/relay status - `IMPLEMENTED_LOCAL_ONLY` | `WebRtcMediaSession.GetSelectedPath` -> statistics -> viewer/diagnostics | Tests reject unknown/fabricated paths and use nominated candidates; not every dual-stack candidate combination | Direct and relayed values observed locally; addresses stay redacted |
| Direct-to-relay and relay-to-direct recovery - `PARTIAL` | fresh ICE recovery plus current media statistics | Coordinator test proves fresh ICE can report Direct -> Relayed -> Direct without cached status; does not prove real path switching through changing NAT | Real changing-path matrix is externally blocked; no claim of measured switching time |
| Proactive stable relay-to-direct probing - `PLANNED` | No periodic probe; normal fresh ICE only | No test claims the optional optimizer | Deferred to Phase 10 Smart Connection; current sessions can change path only during recovery/new ICE |
| Adaptive quality and desktop profile - `IMPLEMENTED_LOCAL_ONLY` | `MediaProfile.For`, adaptive controller, `Vp8ScreenEncoder` | Media tests prove native 3840x2160/24 Mbps configuration, desktop mode, quantizer bound, scale and key-frame behavior; not sustained physical 4K FPS | Real local relay video passed; endpoint performance and visual judgment remain external |
| Viewer scaling/pointer geometry - `IMPLEMENTED_AND_VERIFIED` | `ViewerWindow` scale menu -> layout modes -> `RemotePointerMapper` | Tests prove Fit/Fill/Stretch/Actual geometry, crop, letterbox, DPI and offsets; not full UI automation on every DPI/display | WinUI build passes; two different physical displays remain to be observed |
| Local self-hosted stack - `IMPLEMENTED_LOCAL_ONLY` | `peeronq-phase3-local.ps1`; local Compose/Nginx/coturn | Controller validates config, health, WSS, allocations, media, transfer and restart; not a production topology | Healthy on `10.0.0.10`; explicitly development-only CA and unsigned MSI |
| IPv6-only/NAT64/dual-stack physical matrix - `EXTERNALLY_BLOCKED` | SIPSorcery/configured ICE URLs; no fake result path | Existing unit tests cannot substitute for carrier/router behavior | Required networks are unavailable; exact physical failure/success remains unknown |
| Public/two-device acceptance matrix - `EXTERNALLY_BLOCKED` | Operator runbook and final MSI | No mock or same-host test is counted as physical evidence | Requires two laptops, separate ISPs/hotspot, UDP blocking, sleep/wake, soak and rotation |

## 14. Measured performance results and environment

No end-to-end latency, FPS, CPU/RAM/GPU, reconnect duration, or throughput benchmark was produced.
Test/build durations above are command timings, not product performance. The local reference host is
Windows 11 Pro `10.0.26200`, AMD Ryzen 7 3700X, AMD Radeon RX 5600 XT, and the server LAN address
`10.0.0.10`. Native 4K/30 fps and 24 Mbps are configured targets only. Required synchronized
two-device performance measurement remains external.

## 15. Brand-purity result

`scripts/quality/test-brand-purity.ps1` passed with exit `0`: no forbidden former product brand was
found outside the documented compatibility manifest.

## 16. Dependency, SBOM, and provenance impact

No dependency was added or upgraded by this Phase 3 repair. The implementation reuses pinned
SIPSorcery `10.0.15`, media encoders `10.0.4`, digest-pinned coturn `4.15.0-r0`, Nginx
`1.28.3-alpine`, and .NET. No SBOM was regenerated and no release was published. The deliverable is
explicitly unsigned Development MSI; production signing/timestamp/provenance gates remain unchanged.

## 17. P0, P1, and P2 issues

- **P0:** none known in the locally executable Phase 3 objective.
- **P1:** final `0.7.5` two-laptop Wi-Fi reconnect and different-network/TURN acceptance are not run.
- **P1:** real direct-to-relay/relay-to-direct transition and 10-15 minute repeated-reconnect soak are
  not measured.
- **P2:** IPv6-only/NAT64, sleep/wake, lock/unlock, credential rotation, and full performance matrix
  remain unexecuted.
- **P2:** proactive relay-to-direct recovery after a stable relay path is planned for Phase 10.

## 18. Known limitations

- The signaling/session store is in-memory and single-node; restart reauthenticates but does not
  promise in-flight session continuity without a shared atomic store and distributed routing.
- LAN heartbeat failure detection is bounded but not instantaneous; configured worst-case detection
  is roughly 12-15 seconds before reconnect work begins.
- Native 4K quality depends on endpoint codec/capture/render capacity and LAN throughput.
- Standard-user Windows input cannot control elevated/UAC Secure Desktop targets.
- Actual-size scrolling is disabled while Full Control is active; Fit/Fill/Stretch remain available
  so pointer mapping stays deterministic.

## 19. External blockers

- Two physical Windows laptops with the final `0.7.5` MSI and an operator.
- Separate internet connections, mobile hotspot, controllable UDP blocking, IPv6-only/dual-stack or
  NAT64 access, sleep/wake/lock transitions, and time for a 10-15 minute soak.
- Public DNS/CA/signing/deployment authority and real secret/credential rotation. Those mutations were
  neither requested nor performed.

## 20. Compatibility impact

Signaling protocol v1 is a deliberate fail-closed wire boundary. Clients without version `1` receive
`unsupported_version`; they cannot partially connect. Install the same unsigned LAN Development
`0.7.5` MSI on both laptops. The previous input protocol v2 boundary remains unchanged. There is no
database migration and rollback is code/server/client replacement only.

This paragraph records the historical Phase 3 artifact. Phase 4 subsequently advances signaling and
collaboration to v2; current clients and server must use the matching Phase 4 version documented in
`PHASE_04_COMPLETION_REPORT.md`.

## 21. Exact readiness decision for the next phase

Phase 3 is ready for continued internal engineering under `PASS_WITH_EXTERNAL_BLOCKERS`: its local
core dependency is implemented, tested, packaged, and running, so work that does not depend on
unproven public/physical behavior may proceed. Do not mark Phase 3 `PASS`, publish a production
release, or claim reliable internet/4K performance until the external matrix in section 19 passes
with the exact final artifact and recorded logs.
