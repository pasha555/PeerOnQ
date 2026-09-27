# Hybrid secure transport and high-throughput file data plane

## Scope and threat model

Every native session uses an authenticated application record layer in addition to WebRTC
DTLS/SRTP. It protects against a passive signaling/TURN observer, ciphertext modification, replay,
cross-session injection, protocol downgrade, and later compromise of a long-term device signing key.
The signaling service routes public handshake material but never receives an ephemeral private key,
shared secret, traffic key, plaintext video frame, or plaintext collaboration message.

The protocol does not protect a device whose operating system/process is already compromised. The
existing ECDSA P-256 device key remains the directory trust anchor, so compromise of that key can
authorize a replacement hybrid identity. Independent cryptographic review and two-device hostile-
network evidence remain release gates.

## Components and providers

| Purpose | Algorithm/provider |
| --- | --- |
| Hybrid key establishment | ephemeral ML-KEM-768 + ephemeral X25519 (Bouncy Castle 2.6.2) |
| Hybrid peer authentication | ML-DSA-65 + Ed25519 (Bouncy Castle 2.6.2) |
| Existing directory binding | ECDSA P-256 signature over the hybrid public identity |
| Key derivation | HKDF-SHA-512 |
| Record protection | AES-256-GCM with authenticated session/channel/epoch/sequence context |
| File integrity | per-chunk SHA-256 plus final whole-file SHA-256 |

The Windows capability manifest advertises `security.hybrid-pq-v1` through the managed Bouncy Castle
provider on every supported Windows build. Session capability negotiation still requires the exact
hybrid suite on both participants; legacy peers remain rejected before session creation. Private
identity material stays in the existing DPAPI-protected store, and existing standardized PKCS#8
ML-DSA-65 keys remain interoperable with the managed provider.

## Handshake state machine

```mermaid
sequenceDiagram
    participant V as Viewer
    participant S as Sharer
    V->>S: ClientHello (nonce, ephemeral X25519, bound hybrid identity)
    S->>V: ServerHello (nonce, X25519, ephemeral ML-KEM-768, dual transcript signatures)
    V->>S: ClientKey (ML-KEM ciphertext, dual transcript signatures, viewer key confirmation)
    S->>V: ServerFinish (sharer key confirmation)
    V->>S: ClientFinish (viewer transcript confirmation)
    S->>V: ServerAck (sharer transcript confirmation)
    Note over V,S: SECURE; install record keys and allow application traffic
```

The wire framing is `PNQH`, version 1, strict bounded JSON with unmapped fields rejected. Exact wire
frames are length-prefixed into a SHA-512 transcript. Session ID, version and all selected suites are
checked exactly. Any unexpected state transition, malformed frame, identity mismatch, signature
failure, key-confirmation failure, timeout, or pre-`SECURE` application frame fails closed.

Ephemeral X25519 and ML-KEM private material is discarded after establishment. This gives handshake
forward secrecy against later long-term identity-key compromise. Epoch rekeying occurs after 1 GiB,
1,000,000 records, 30 minutes, or reconnect. Epoch keys derive from the in-memory session master;
this is not a continuous post-compromise-secure double ratchet.

## Key schedule and record layer

The 32-byte ML-KEM secret and 32-byte X25519 secret are concatenated only inside the key schedule.
HKDF-SHA-512 uses the transcript hash as salt and a version/session/suite binding as context. It
derives independent viewer/sharer authentication keys and independent traffic states by:

- direction: viewer-to-sharer or sharer-to-viewer;
- channel: control, encoded video, input, clipboard, file transfer, or optional timing telemetry;
- transfer ID for every file transfer;
- key epoch.

`PNQE` records authenticate the protocol version, channel, epoch, sequence, transfer ID, ciphertext
length, and signaling session ID. Each derived key has its own four-byte nonce prefix and monotonic
64-bit sequence. Ordered channels require the next exact sequence; video may skip lost records but
may start from the first authenticated sequence that survives RTP loss, and never accepts a
duplicate or older record. Late/replayed or otherwise unauthenticated video records return no
plaintext, are dropped, and request a recovery key frame. One lossy/truncated video frame therefore
cannot terminate the session, while a sustained consecutive verification failure still terminates
fail-closed. Ordered-channel replay or AEAD authentication failure remains immediately terminal.
Keys and failed plaintext buffers are zeroed when their lifecycle ends.

## Negotiated frame-age telemetry

New media peers advertise the exact SDP attribute `a=x-peeronq-video-frame-timing:1`; the sharer
enables it only when the viewer echoes that attribute in its answer. Unknown SDP attributes are
ignored by older peers, so a missing echo keeps the legacy raw VP8 payload and sends no timing
traffic. The strict version-1 hybrid handshake JSON is unchanged.

After `SECURE`, the viewer sends five bounded NTP-style `PNQT` probes on a dedicated Telemetry
record context. These probes are inside normal `PNQE` AES-256-GCM records, use an independent
direction/channel key and ordered sequence, and never enter application message dispatch. The
lowest-uncertainty valid sample estimates remote-minus-local UTC offset. The batch refreshes once
per minute and estimates expire after two minutes rather than producing stale long-session data.
Timeout, an unrecognized
authenticated timing message, or lack of negotiation disables frame-age measurement without
disconnecting the session; record authentication/replay failures retain the existing fail-closed
behavior.

When negotiated, the sender wraps encoded VP8 in a versioned `PNQV` envelope containing capture
Unix microseconds and capture sequence before video record protection. The metadata is therefore
authenticated and encrypted with the frame. The viewer carries it through the exact decoded-frame
and latest-frame UI slots and records capture-to-present latency only after successful presentation
and only when clock uncertainty is at most 50 ms. Sanitized diagnostics expose p50/p95/p99 and the
uncertainty, not candidate addresses or screen contents.

## Negotiated input-to-injection telemetry

Only peers with immutable `ControlInput` permission may advertise and echo the exact SDP attribute
`a=x-peeronq-input-ack:1`. A missing echo preserves input protocol v2 exactly: the nullable command
timestamp is omitted from JSON and no acknowledgement type is sent to an older peer. Negotiation
does not add permission or bypass the existing focus generation, reconnect generation, monotonic
sequence, authorization, validation, or revocation checks.

The viewer samples at most one input command per 100 ms and bounds pending samples to 32. The
timestamp travels inside the existing independent encrypted Input record context. The host emits an
encrypted `input.ack` only after the authorized command passes validation and the OS input sink
reports successful injection. The acknowledgement binds the acknowledged input sequence, original
viewer timestamp and host injection timestamp. The viewer accepts only an exact outstanding match
and reports input-to-successful-injection latency when peer-clock uncertainty is bounded.

Sampling timeout, malformed optional measurement values, or acknowledgement delivery failure drops
only the sample and never disables authorized control. Record authentication, replay, invalid input
state and authorization failures keep their existing fail-closed behavior. Sanitized diagnostics
expose only bounded p50/p95/p99 and clock uncertainty; no key, content, or raw input history is
exported. High reliable input p95 feeds the file pacer so bulk traffic degrades before control, and
the pressure expires after five seconds without a fresh sample.

## Negotiated dedicated input WebRTC transport

Only an immutable `ControlInput` grant creates and advertises the dedicated input transport. The
primary SDP carries the exact `a=x-peeronq-input-data-lane:1` capability plus one bounded base64url
secondary offer/answer; trickled ICE candidates use the `peeronq-input:` routing prefix. The
secondary connection accepts only the ordered `peeronq.input.v1` channel and only protected PNQE
records whose authenticated routing context is `Input`.

The separate ICE/DTLS/SCTP connection prevents video RTP and compatibility file SCTP queues from
creating association-level head-of-line blocking for mouse/keyboard. Its sender admission is bounded
to 64 KiB, and diagnostics expose only negotiated/ready state, buffered bytes and a record count. If
the peer omits the capability, the lane has not opened yet, or the optional connection closes, input
uses the existing permission-gated primary channel; this fallback does not change media state or
grant a capability. Input authorization, focus ownership, AEAD/replay checks and successful-injection
acknowledgement remain unchanged.

## Negotiated dedicated bulk WebRTC transport

Only peers with immutable `FileTransfer` permission create and advertise the dedicated direct-file
transport. The primary SDP carries the exact `a=x-peeronq-bulk-data-lane:1` capability plus one
bounded base64url secondary offer/answer; trickled candidates are explicitly routed to that peer
connection. The secondary connection accepts only the ordered `peeronq.bulk.v1` channel with the
existing `peeronq.hybrid-pq.v1` protocol. A duplicate, unknown, unauthorized, or wrong-connection
channel is rejected.

The bulk peer connection creates a physically separate SCTP association. Closing or failing that
bulk path does not change the primary media or dedicated input connection state. If either peer omits the exact
capability or secondary description, the sender retains the legacy primary-channel file path; this
compatibility decision never adds `FileTransfer` permission. The offerer holds bounded secondary
ICE candidates until the answer echoes the capability, so an older peer never receives a candidate
mid that it cannot route.

`PNQB` fragments contain only bounded transport metadata: record ID, total length and exact offset.
That metadata is treated as untrusted. Reassembly permits one ordered record of at most one MiB,
rejects gaps, record switches, invalid lengths and expired partial records, and releases no
application plaintext. The completed bytes must still be a FileTransfer `PNQE` routing context and
then pass the unchanged session/direction/channel/transfer/epoch/sequence AES-256-GCM verification
before normal file dispatch. A FileTransfer record on the negotiated primary lane, a non-file record
on the bulk lane, or a non-Input record on the input lane fails with
`secure_data_lane_context_mismatch`.

## Negotiated same-LAN native bulk

New file-capable peers advertise the additive `x-peeronq-native-bulk:1` SDP capability. SDP only
negotiates support; it is not trusted for native transport authentication. After the hybrid PNQE
session is secure and ICE selected `DirectLan`, the viewer sends its session-ephemeral TLS
certificate SHA-256 pin inside the encrypted Telemetry record context. The sharer answers inside
that same protected context with its pin and one bounded UDP listener port. The connector reuses
only the already selected ICE peer IP, preventing an authenticated peer from supplying an arbitrary
destination address. Mutual certificate pinning then protects a separate TLS 1.3 QUIC connection.

Every new transfer chooses native QUIC only if both ends completed that negotiation; its offer,
chunks, controls and completion stay on the same path. A transfer never changes transport midway,
which preserves strict per-transfer AEAD sequence ordering. Video and mouse/keyboard stay on their
separate WebRTC peer connections.
An older peer, non-LAN path, missing MsQuic, certificate/port failure, or firewall rejection leaves
the screen session active and uses file relay/dedicated SCTP for transfers that have not started.
The PNQE FileTransfer records remain end-to-end encrypted inside QUIC, so TLS is defense in depth
rather than a replacement for hybrid identity, permission, replay or file-integrity validation.

After a native chunk passes PNQE authentication, per-chunk integrity checks and the asynchronous
destination write, the receiver coalesces a cumulative `TransferReceipt`. It travels only on the
already pinned native file path and is consumed by the transport rather than dispatched as
application data. The sender accepts only positive monotonic delivery that does not exceed bytes it
scheduled for that transfer. The receipt contains only transfer ID and byte count: no path, name,
content, address, certificate pin or key. Missing/malformed/stale feedback cannot elevate capacity;
receipt send failure is optional telemetry and never converts a successful disk write into a screen
session failure.

## Negotiated file relay

When both authenticated endpoints advertise and negotiate `file.relay.v1`, file offers, control
frames and chunks travel as bounded opaque binary records over the existing TLS signaling socket.
The signaling service validates the binary envelope, immutable `FileTransfer` permission, session
state, and both live connection owners before forwarding it to the session peer. It never decrypts,
parses, or logs file names, contents, or record keys. The hybrid AES-256-GCM record layer accepts
only the FileTransfer channel from this route, preserving direction, session, transfer-ID, epoch,
and replay binding. The relay's TLS socket supplies bounded backpressure and does not inherit the
direct media path's adaptive token bucket. Per-channel secure send serialization preserves file
record order without holding input, while video, handshake, clipboard and input remain on their
WebRTC paths.

If either peer did not negotiate the feature, the compatibility fallback accepts file records only
on a nominated `DirectLan` or `DirectInternet` ICE path; `Relayed` and `UnknownNegotiating` fail
with `direct_p2p_required`.

Default byte quotas are `long.MaxValue`, so PeerOnQ adds no product file-size ceiling. Files are
streamed in ordered 256 KiB chunks and use persistent sequential destination streams. During live
screen sharing, direct and relay chunks use the same media-aware token bucket: input/security and
the adaptive video target are reserved first, and file traffic receives the measured remainder.
Until a usable estimate exists, finite Remote Control/Balanced/File Transfer ceilings of
50/100/150 Mbps prevent an unlimited relay burst; file-only sessions remain uncapped. A trustworthy
capacity measurement may allocate the larger post-media/input remainder up to a defensive 1 Gbps
bound. WebSocket/TCP or QUIC backpressure still follows receiver capacity while interactive input
remains on its separate WebRTC path.
The receiver processes each relay record through authenticated decrypt and serialized destination
write completion before reading the next record, so a slow disk reduces file throughput instead of
allowing an unbounded in-memory queue to interrupt the remote session. Input, clipboard, and file
records have independent ordered receive lanes after the authenticated channel context is validated;
final whole-file hashing or malware scanning can hold only the file lane. Video protection remains
on the independent RTP pipeline.
Two to eight outbound workers (CPU-dependent, maximum 32 by explicit configuration) allow multiple
transfers to use available disk/network capacity without unbounded queues or memory. Actual speed is
limited by disk, CPU, adaptive media reserve/pacing, transport congestion control, and the two
network paths.

Existing resume offsets, pause/resume/cancel, collision handling, per-chunk and final digests,
malware policy, reparse/path traversal checks, partial files and same-volume final rename remain in
force. A per-transfer AEAD context is destroyed only after the terminal exchange completes, avoiding
premature key deletion before the completion acknowledgement.

## Operational verification

Automated gates cover a real hybrid handshake over linked media endpoints, wrong pinned identity,
downgrade/truncation, ciphertext tamper, replay, wrong transfer context, epoch rekey, channel/transfer
key separation, pre-secure data rejection, relay denial, real WebRTC input/data flow, exact file
transfer, encrypted clock synchronization, old-peer timing/input fallback, authenticated real-WebRTC
capture-to-present and input-to-injection telemetry, input during active bulk transfer, two-client
signaling E2E, and zero-warning WinUI Release build. Physical WAN/LAN,
resume after cable/Wi-Fi loss, high-volume throughput, and independent review remain external gates.
