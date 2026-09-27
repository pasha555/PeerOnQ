# PeerOnQ Phase 3 - internet connectivity, TURN and secure reconnect

Phase 3 reuses the existing authenticated signaling, WebRTC, consent, media, and Phase 2 input
implementations. The current Windows client supports attended `ViewOnly`, `FullControl`, and
`FileTransferOnly`; reconnect never expands the accepted permission mask.

## Connection strategy

The signaling server issues ICE configuration only after device authentication and explicit session
acceptance. With policy `all`, SIPSorcery gathers configured host, STUN server-reflexive, and TURN
relay candidates and ICE nominates a working pair. `RelayOnly` is available for deterministic
diagnostics and policy enforcement.

The UI and diagnostics classify only the nominated pair:

- `Direct LAN`: nominated non-relay endpoints are local addresses.
- `Direct internet`: nominated non-relay pair is not local.
- `Relayed`: either nominated candidate is a relay candidate.
- `Unknown / negotiating`: there is no inspectable nominated pair.

Candidate addresses are excluded from ordinary UI, structured logs, and diagnostic exports. The
implementation does not claim a path merely from configuration.

## Signaling compatibility

PeerOnQ signaling is a bounded JSON application protocol over authenticated WSS.
`SignalingProtocol.CurrentVersion` is `1`. The client sends the version in `hello`; the server checks
it before issuing a challenge and echoes it in `registered`. Missing, older, or newer versions receive
`unsupported_version`, and the client stops rather than partially connecting.

Every registration repeats the ECDSA device proof. Production also requires a fresh short-lived
Cloud attestation bound to the alias and device public key. Duplicate connections replace prior
ownership cleanly.

## TURN security

`PeerOnQ.Signaling.Server` issues coturn REST credentials only for authenticated participants in an
accepted session. Usernames contain expiration plus an opaque subject; passwords are HMAC values
derived from a server-only secret. Credentials are short-lived and are never embedded in clients.

The pinned coturn deployment supports STUN, TURN UDP/TCP, and TLS/DTLS listeners, with configured
allocation/bandwidth quotas, nonce expiry, peer restrictions, and a bounded relay UDP range. The
production Compose topology keeps metrics and service internals private. Local development uses an
explicit local CA and LAN-bound ports; production rejects development trust and unsafe defaults.

See [protocol compliance](docs/PROTOCOL_COMPLIANCE.md) and the
[real-time deployment guide](src/PeerOnQ.Realtime.Deployment/README.md).

## Reconnect and resume

The typed recovery flow is equivalent to:

`Connected -> Interrupted -> Reconnecting -> Reauthenticating -> Renegotiating -> Resumed -> Connected`

Final failure ends in `ReconnectFailed`; user or peer termination ends in `Ended`. Retry uses bounded
exponential backoff with jitter and a maximum reconnect window. LAN development detects a silent
half-open WSS path through WebSocket and application PONG deadlines in roughly 12-15 seconds under
the configured 5-second heartbeat/12-second timeout.

On uncertainty PeerOnQ immediately disables collaboration/input and releases held state. After WSS
returns, the client repeats device authentication, consumes and rotates its short-lived resume token,
and the server notifies the participant that stayed online. Both peers then perform a fresh ICE
offer/answer with the sharer as deterministic offerer. View/input is restored only after media is
connected, and input requires a fresh focus acknowledgment; stale input is never replayed.

Resume credentials are 256-bit random values stored server-side only as SHA-256, bound to the session,
device, participants, mode, and protocol version, rotated after use, expired automatically, and
invalidated on session end. A revoked/invalid device attestation prevents reauthentication.

The new ICE negotiation may move from direct to relay or relay to direct when the network makes that
path viable. A periodic proactive relay-to-direct probe after a stable relay session is not present;
that optional optimization remains planned for Phase 10 Smart Connection.

## Quality, display scaling, and diagnostics

The LAN Development client starts with `Quality` and an explicit 4K/UHD resolution: up to the
source dimensions without upscaling, a 30 fps target, desktop-tuned VP8, and a 36 Mbps ceiling.
Adaptive pressure reduces frame rate/bitrate while retaining the requested pixels. This is a target, not a measured 4K FPS
guarantee. Production retains `Automatic`, which adapts from real RTCP loss/jitter, ICE RTT, and local
queue pressure.

RTCP reports expire after ten seconds and are reset on connection changes or ICE restart. Missing
reports hold recovery rather than imply spare bandwidth. Video latency decisions allow an estimated
one-way transit cost of half a fresh path-minimum ICE RTT, capped at 75 ms, so normal WAN delay alone does not repeatedly
shrink the image. Genuine encoder/render pressure and excessive network delay still reduce load;
bulk pacing continues to use full input latency to protect responsiveness.

Viewer display options are:

- `Fit to window`: show the entire remote screen and preserve aspect ratio; bars may remain.
- `Fill window`: fill the viewer while preserving aspect ratio; centered edges may be cropped.
- `Stretch to window`: use the whole viewer; aspect ratio may distort.
- `Actual size`: one source pixel per local physical pixel, with DPI-aware scrolling.

Pointer mapping follows the real rendered/cropped image rectangle for each mode. Displayed path,
resolution, loss, jitter, bitrate, FPS, drops, and reconnect state come from the active session; an
unavailable value is not fabricated.

## Current evidence and gate

On 2026-08-17 the full local Phase 3 controller passed in 147.6 seconds against the real Docker
Nginx/signaling/coturn stack: WSS, STUN, authenticated TURN UDP/TCP/TLS/DTLS allocation, invalid and
expired credential rejection, forced relay video over UDP/TCP, a two-second TURN interruption,
relay-only file transfer, and signaling restart/re-authentication. After signaling protocol v1 was
added, the full Signaling, Application, Media, and End-to-End suites passed and the rebuilt LAN stack
reported healthy.

This is local and real-container evidence, not public or two-physical-device proof. Same-LAN final
`0.7.5`, separate internet connections, mobile hotspot, sleep/wake, IPv6-only/dual-stack, repeated
network transitions, credential rotation, and a 10-15 minute soak remain externally blocked.
Therefore the Phase 3 gate is `PASS_WITH_EXTERNAL_BLOCKERS`, not production approval.

The detailed evidence/status matrix is in
[PHASE_03_COMPLETION_REPORT.md](docs/phase-reports/PHASE_03_COMPLETION_REPORT.md).
