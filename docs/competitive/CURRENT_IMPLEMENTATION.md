# PeerOnQ Phase 6.5 current implementation

Status date: 2026-08-24

This inventory describes the current working tree. It does not treat the presence of a class or a
test name as proof that a physical-device requirement passed. Existing implementation is retained;
Phase 6.5 work must extend the owning abstraction instead of creating a parallel stack.

## Runtime boundaries

| Area | Owning implementation | Current status | Evidence limit |
| --- | --- | --- | --- |
| Screen capture | `PeerOnQ.Platform.Windows/Capture/WindowsGraphicsCaptureSource` | Implemented for explicit display capture, monitor switching, resize, cursor policy, D3D11/WARP fallback, device-loss stop and deterministic disposal | Selected-window capture, HDR/rotation recovery and automatic GPU-device recreation are not implemented |
| Frame pipeline | `PeerOnQ.Media/WebRtcMediaSession`, `VideoFrameQueue`, `FrameRateLimiter` | Bounded latest-frame queue (capacity one in production), capture pacing, stale-frame drop and cancellation are implemented | No GPU zero-copy encode path and no dirty-region/tile suppression |
| Codec | `Vp8ScreenEncoder` through SIPSorcery | Real software VP8 encode/decode, CBR, key-frame recovery and 720p/1080p/4K synthetic coverage | Hardware encode/decode, H.264, VP9 and AV1 are not active paths |
| Adaptation | `AdaptiveQualityController` | Real RTCP loss, RTT, jitter, outgoing estimate and queue-drop inputs; quick degradation, gradual recovery and a ten-second resolution dwell | No local content classifier or user/organization bandwidth limit |
| Media diagnostics | `MediaStatisticsCollector`, `ConnectionDiagnosticsExporter` | Capture/encode/decode/render FPS, source/requested/encoded/decoded/rendered dimensions, actual codec/hardware state, bitrate, RTT, loss, jitter, frame drops and two local latency segments are measured | Input-to-photon is not measured |
| Connection routing | SIPSorcery ICE in `WebRtcMediaSession` | Host/server-reflexive paths are preferred by ICE; the nominated pair determines Direct LAN, Direct Internet or Relayed | Relay transport subtype and multi-region TURN scoring are not reported/implemented |
| Native QUIC migration | `RemoteSessionTransport`, `PeerOnQ.Transport/DataPlane` | Protocol-v1 channel contract and real mutually authenticated QUIC reliable streams pass two-peer loopback tests; mouse, keyboard, control, clipboard and parallel file lanes are isolated | Source-only and not advertised; native MsQuic DATAGRAM, candidate racing, relay and app routing remain required before QUIC can be primary |
| Signaling/reconnect | signaling v3, `SessionCoordinator`, `ReconnectStateMachine` | Authenticated WSS, bounded PONG detection, network-change refresh, one-use resume, identity revalidation, fresh ICE and input release are implemented | Sleep/wake and physical adapter changes still require real-device evidence |
| File transfer | collaboration v2 `FileTransferService` over authenticated SCTP | Streaming 64 KiB chunks, bounded backpressure, pause/resume, hashes, partial files, disk/path checks and priority lanes are implemented | High-speed/100 GB/process-restart benchmarks are absent; file sessions deliberately require a direct path |
| Session security | `SecureSessionProtocol`, `SessionTrafficProtector` | Mandatory ML-KEM-768 + X25519 handshake, ML-DSA-65 + Ed25519 identity, transcript binding and per-channel AES-256-GCM records protect video/control/input/clipboard/file | Independent cryptographic review and hostile physical-network evidence are open |
| Local logging | `PeerOnQLogging`, `DiagnosticSanitizer`, `SessionTimelineStore` | Bounded rolling structured JSON logs plus a bounded, address-free timeline sourced from real coordinator transitions | Distributed admin correlation remains outside the client timeline |
| Diagnostic bundle | `DiagnosticBundleService`, `NetworkDoctorService` | Explicit consent, allowlisted manifest fields, bounded sanitized logs, session timeline and deterministic health statuses; no screen/clipboard/file content | Standalone STUN/TURN and packet-loss probes require approved session-scoped credentials and are reported as not configured outside a session |

## Verified existing user behavior

- Attended View Only, Full Control and File Transfer requests preserve explicit consent.
- Unattended access is default-off, credential/trust-bound, permission-limited and visibly indicated.
- Viewer and host have visible session-end and input-release controls.
- Signaling and relay services never receive plaintext session records.
- File data, clipboard data and remote input do not travel over the signaling WebSocket.
- The renderer, encoder handoff and data-channel bulk traffic all have bounded ownership/backpressure.

## Incomplete or missing Phase 6.5 behavior

- Automatic desktop-content classification and changed-region/repeated-frame suppression.
- Real production hardware encoder and decoder selection.
- Automatic local desktop-content classification and user/organization bandwidth limits.
- Session-scoped Network Doctor STUN/TURN and packet-loss sampling during an approved connection.
- Current active-session, file-transfer, impairment, two-device and soak benchmark artifacts.
- Native MsQuic QUIC DATAGRAM integration, authenticated candidate exchange/path racing, and migration
  of the live media/collaboration composition away from WebRTC.
- A distribution decision for dependencies whose embedded terms are not plain OSI/MIT-compatible.
