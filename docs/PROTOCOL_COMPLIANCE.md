# PeerOnQ real-time protocol compliance

Source contracts reviewed on 2026-09-27. Runtime evidence is from the explicitly dated historical
runs below and is not a fresh test result. This is an implementation and evidence map, not an IETF
conformance certification.

## Implementations and versions

| Component | Pinned/current version | Role |
| --- | --- | --- |
| SIPSorcery | `10.0.15` | ICE, SDP offer/answer, DTLS-SRTP, RTP/RTCP, SCTP data channels |
| SIPSorceryMedia.Abstractions | `10.0.15` | media contracts |
| SIPSorceryMedia.Encoders | `10.0.4` | VP8 decode integration |
| Bouncy Castle | `2.6.2` | X25519 and Ed25519 classical hybrid components |
| coturn | `4.15.0-r0`, digest-pinned in deployment | STUN/TURN UDP, TCP, TLS and DTLS |
| .NET | `global.json`: SDK `10.0.302`, `latestPatch`; historical local gate used `10.0.303` | WSS client/server, TLS and cancellation |
| Nginx | `1.28.3-alpine` | TLS termination and WebSocket upgrade proxy |

## Standards and recorded evidence

The status labels describe earlier local evidence, not full conformance or validation of every
current-source path. The historical command section identifies the Phase 3 and Phase 8 runs;
later test changes/results are recorded in [AI_CHANGELOG](../AI_CHANGELOG.md).

| Protocol/reference | PeerOnQ implementation | Recorded local evidence | Limitation |
| --- | --- | --- | --- |
| WebRTC overview, RFC 8825 | `WebRtcMediaSession` uses a primary media/control peer plus negotiated dedicated input/bulk peers, with primary compatibility fallback | `IMPLEMENTED_LOCAL_ONLY`: real direct and relay-only peers negotiate and render VP8 | No independent browser/interoperability laboratory |
| ICE, RFC 8445 | SIPSorcery gathering, connectivity checks, nomination, selected-pair inspection and ICE restart | `IMPLEMENTED_LOCAL_ONLY`: direct loopback/LAN and forced-relay nominated paths pass | IPv6-only, NAT64, symmetric-NAT and separate-ISP matrices remain external |
| Trickle ICE, RFC 8838 | `LocalIceCandidate` -> authenticated signaling `ice` frame -> `AddRemoteIceCandidateAsync` | `IMPLEMENTED_AND_VERIFIED`: ordering/routing tests and real local WebRTC negotiation pass | Candidate interoperability with other WebRTC implementations is untested |
| STUN, RFC 8489 | coturn STUN listener and SIPSorcery STUN URLs | `IMPLEMENTED_LOCAL_ONLY`: controller receives a reflexive address | Public IPv4/IPv6 STUN reachability is untested |
| TURN, RFC 8656 | coturn REST credentials; UDP/TCP listener, TLS/DTLS listener, bounded relay range and quotas | `IMPLEMENTED_LOCAL_ONLY`: historical allocation/forced-relay video evidence; WebRTC file payloads remain direct-only | Negotiated `file.relay.v1` uses authenticated opaque signaling records, not TURN file payloads; TLS/DTLS media routing is not claimed |
| SDP offer/answer, RFC 3264 | Sharer is the deterministic offerer; viewer answers; reconnect uses fresh ICE offer/answer | `IMPLEMENTED_AND_VERIFIED`: initial and reconnect SDP tests pass | No third-party SDP interop claim |
| DTLS-SRTP, RFC 5764; SRTP, RFC 3711 | SIPSorcery WebRTC protection plus mandatory hybrid-PQ application AEAD on encoded video | `IMPLEMENTED_LOCAL_ONLY`: authenticated hybrid handshake and protected direct video pass locally; the protected live-relay test is opt-in | Independent cryptographic/interoperability review and a current external TURN rerun are absent |
| RTP/RTCP, RFC 3550; PLI, RFC 4585 | VP8 RTP, RTCP statistics and bounded PLI key-frame recovery | `IMPLEMENTED_AND_VERIFIED`: encode/decode, PLI and loss/statistics tests pass | Public-network loss benchmark is absent |
| WebRTC data channels, RFC 8831 | Ordered DTLS/SCTP carries the mandatory hybrid handshake and AES-GCM application records with interactive/normal/bulk priority | `IMPLEMENTED_LOCAL_ONLY`: real loopback hybrid control and exact direct file transfer pass | Two-physical-device sustained control/transfer evidence remains external |
| TLS 1.2/1.3 and WSS | Nginx TLS proxy; `ClientWebSocket` validates public trust or one embedded development root | `IMPLEMENTED_LOCAL_ONLY`: WSS upgrade and development-root validation tests pass | Public CA, OCSP and certificate-rotation evidence is external |

## PeerOnQ signaling compatibility

PeerOnQ signaling is a bounded application JSON protocol over authenticated WSS; it is not an IETF
WebRTC signaling standard. `SignalingProtocol.CurrentVersion` is currently `3`, with an exact
supported range of 3-3. The client sends the version and bounded native capability manifest in
`hello`; the server checks version, capability dependencies and required server functions before
issuing a challenge. `registered` echoes the accepted endpoint capabilities, server capabilities and
the intersection of implemented optional features. Missing, pre-v3 or newer versions receive
`unsupported_version` plus received/minimum/maximum fields; a client rejects a mismatched response
instead of appearing partially connected.

Before creating a session, the server requires `security.hybrid-pq-v1` on both endpoints, then maps the immutable permission/access request to directional
viewer and host requirements and returns `capability_mismatch` with the missing side/capabilities.
Unknown well-formed message types return `unsupported_message` without closing the socket; malformed
JSON remains `malformed_message`. Canonical UTF-8 vectors live under `test-vectors/signaling-v3` and
are byte-compared by `CapabilityNegotiationTests`. The vector naming Linux proves shared-wire
portability only. Native Linux, Android and Apple viewer/controller projects now exist independently
of that vector; their implementation and unclosed physical-device gates are documented in
[CROSS_PLATFORM_CAPABILITIES](CROSS_PLATFORM_CAPABILITIES.md).

Collaboration protocol v2 uses `PNQ4` only inside authenticated `PNQE` records and is additionally bound to the exact
signaling session ID and permission generation. Pre-v3 signaling clients are intentionally
incompatible and are rejected by the signaling handshake before collaboration can half-connect.
Collaboration and remote-input framing remain version 2 inside a negotiated signaling-v3 session.
The hybrid handshake and key lifecycle are specified in [SECURE_TRANSPORT.md](SECURE_TRANSPORT.md).

Registration uses an ECDSA P-256 proof over a 256-bit server nonce. Production additionally requires
a short-lived Cloud attestation bound to the alias and public key. Session resume uses a 256-bit
random, short-lived, single-use rotating credential stored server-side only as SHA-256 and bound to
the original session/device/mode/participants. SDP, ICE candidates, credentials and tokens are not
written to normal logs or diagnostic exports.

Phase 7 extends the Cloud-signed attestation payload for **managed** devices with organization ID,
allowed connection-mode flags, minimum client version, approved relay regions, and the currently
representable hybrid-security requirement. Signaling validates those signed claims for both peers
and rejects managed cross-tenant or disallowed sessions before routing an offer. Unsupported required
hybrid security fails closed. The v1/accountless unmanaged path remains valid, so customer accounts
are not an activation requirement for direct LAN use. Policy claims are short-lived; every new
registration fetches the current attestation instead of trusting a cached grant.

## Path selection and recovery

With policy `all`, the media stack gathers the configured host, STUN and TURN candidates and reports
only the nominated pair: `DirectLan`, `DirectInternet`, or `Relayed`. `RelayOnly` is an explicit
diagnostic/policy mode. On a media or signaling interruption, PeerOnQ immediately disables input,
reauthenticates signaling when necessary, consumes/rotates the resume credential, and performs a
fresh ICE negotiation. That negotiation can choose a different viable candidate class.

Direct-to-relay or relay-to-direct behavior on a real changing ISP/NAT path remains
`EXTERNALLY_BLOCKED`; same-host synthetic state tests do not substitute for it. There is no proactive
periodic relay-to-direct probe after a stable relay session, so that optional optimization remains
`PLANNED` for the later Smart Connection phase.

## Historical local command evidence

```powershell
.\scripts\windows\peeronq-phase3-local.ps1 test -BindAddress 10.0.0.10
```

The Phase 3 2026-08-17 run passed signaling (67 passed, 1 normal opt-in skip), end-to-end (40 passed), live
TURN UDP relay video with a two-second interruption, live TURN TCP relay video, relay file-transfer
denial, invalid and expired credential rejection, TLS 1.3/DTLS 1.2 allocation, and the opt-in live
signaling restart test. This is real local-container evidence, not a public or two-device network
claim.

The later Phase 8 protocol-v3 gate passed 84 signaling tests with one normal opt-in Docker-restart
skip, the same 40 Windows end-to-end tests, the canonical byte vector and a zero-warning x64 WinUI
Release build. This is local Windows/WebSocket evidence, not third-party/native non-Windows interop.
