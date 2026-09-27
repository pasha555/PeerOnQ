# Native QUIC remote-desktop migration

Status date: 2026-08-24

Status: reliable QUIC is active for negotiated same-LAN file transfers; WebRTC uses separate media,
input and compatibility-file peer connections.

## Evidence before this change

The production composition still follows this path:

```text
Windows.Graphics.Capture/D3D11 surface
  -> GPU-to-CPU staging readback
  -> CPU BGRA-to-I420 conversion
  -> software libvpx VP8 encode
  -> SIPSorcery WebRTC RTP/ICE/DTLS
  -> software VP8 decode to BGR24
  -> CPU BGR24-to-BGRA conversion
  -> WinUI WriteableBitmap
```

Collaboration records use one primary ordered WebRTC data channel plus negotiated dedicated input
and compatibility-file SCTP associations. File traffic has bounded backpressure and adaptive
pacing. New peers on a selected direct-LAN ICE path may now move a newly started transfer to its own
reliable QUIC connection after authenticated negotiation.

The pre-change measurements in `docs/PERFORMANCE_REPORT.md` remain the baseline. On the local x64
host, synthetic 1080p60 VP8 encode measured 3.82-4.55 ms mean and about 5.5 ms p95; the optimized
viewer BGR24-to-BGRA conversion measured 1.490 ms/frame. Active two-device capture-to-render,
input-to-photon, impairment, relay, and file-contention latency have not been measured. Those values
must not be inferred from loopback tests.

## Versioned target contract

Native data-plane protocol v1 uses ALPN `peeronq-dp/1`. The channel contract is owned by
`RemoteSessionTransport.cs`:

| Priority | Channel | Delivery | Lane policy |
| --- | --- | --- | --- |
| P0 | Mouse | reliable ordered QUIC stream | one dedicated lane per direction |
| P0 | Keyboard | reliable ordered QUIC stream | one dedicated lane per direction |
| P1 | Control/authentication | reliable ordered QUIC stream | one dedicated lane per direction |
| P2 | Screen | QUIC DATAGRAM, latest useful frame | native MsQuic adapter required |
| P2 | Audio | QUIC DATAGRAM, latency-first | native MsQuic adapter required |
| P3 | Clipboard | reliable ordered QUIC stream | one dedicated lane per direction |
| P4 | File transfer | reliable ordered QUIC streams | parallel lanes allowed |
| P4 | Telemetry | reliable ordered QUIC stream | one dedicated lane per direction |

The reliable adapter is real `System.Net.Quic` traffic, backed by the platform MsQuic runtime on
Windows. It uses TLS 1.3, mutual certificates, an authenticated out-of-band SHA-256 certificate
pin, bounded message sizes, bounded per-channel receive queues, and separate unidirectional streams.
Parallel lanes are accepted only for file transfer. A protocol violation closes the connection.

The adapter intentionally reports `SupportsUnreliableDatagrams=false`. The installed .NET 10
`System.Net.Quic` API exposes streams but not QUIC DATAGRAM. Native MsQuic supports the unreliable
datagram extension, so the screen/audio implementation must use a reviewed native adapter rather
than pretending a reliable stream is a datagram. See the official
[System.Net.Quic connection API](https://learn.microsoft.com/en-us/dotnet/api/system.net.quic.quicconnection?view=net-10.0)
and [MsQuic datagram API overview](https://microsoft.github.io/msquic/msquicdocs/docs/API.html#datagrams).

## Security boundary

The QUIC certificate pin is transport authentication, not a replacement for the existing hybrid
ML-KEM-768 + X25519 and ML-DSA-65 + Ed25519 session identity. Ephemeral QUIC certificate pins and the
listener port are carried inside the established PNQE session rather than trusted from raw SDP. Existing
`SessionTrafficProtector` records remain end-to-end encrypted so a future forwarding relay cannot
decrypt content. No key or peer address is added to diagnostics.

## Migration gates

1. **Complete:** map current transport/capture/codec/render/input/file/security paths and preserve
   the measured baseline without claiming unmeasured end-to-end latency.
2. **Complete:** add the cross-platform channel contract, protocol-v1 framing, real QUIC listener,
   real two-peer reliable streams, parallel file lanes, bounded queues, and fail-closed certificate
   pinning tests.
3. **Next:** implement the native MsQuic DATAGRAM adapter for screen/audio, including bounded packet
   ownership, frame/fragment identifiers, obsolete-frame cancellation, loss feedback, and ARM64/x64
   ABI tests. No Windows capability is advertised before this works.
4. **Partial:** negotiate `x-peeronq-native-bulk:1`, exchange ephemeral certificate pins and the
   listener port inside PNQE, and reuse only the selected direct-LAN peer address. Concurrent IPv6/
   IPv4 server-reflexive candidates, NAT hole punching, path racing/migration and relay remain open.
5. **Partial:** route each newly started same-LAN file transfer to reliable QUIC while the adaptive
   pacer governs it. Authenticated receiver-confirmed disk delivery now lets the sender escape the
   WebRTC estimator ceiling with bounded 25% probes, one-second freshness expiry and immediate
   media/input-health backoff. Hybrid handshake and clipboard remain on the primary WebRTC peer;
   mouse/keyboard use their dedicated WebRTC peer with primary fallback; dedicated bulk SCTP/file
   relay remain per-transfer compatibility fallbacks.
6. Route video to QUIC DATAGRAM only after packet loss, stale-frame drop, keyframe recovery, and
   reconnection tests pass. Then remove WebRTC from the default engine composition.
7. Replace GPU readback/software VP8/CPU bitmap hot paths with measured WGC-or-DXGI GPU capture,
   hardware codec, hardware decode, and D3D rendering. Each unavoidable copy must be recorded.
8. Run physical two-device 1080p60/120, 1440p60, 4K60, shaped RTT/loss/bandwidth, reconnect, relay,
   and simultaneous-file benchmarks. Publish actual p50/p95 results and remaining bottlenecks.

## Current non-claims

- Native QUIC is selected only for negotiated, authenticated, newly started direct-LAN file
  transfers. A transfer never switches paths after its offer.
- QUIC DATAGRAM, STUN/hole punching for the QUIC socket, direct-Internet path racing/migration, and
  QUIC relay are not implemented.
- WebRTC remains the active media, dedicated input and non-LAN collaboration transport.
- Hardware encode/decode, GPU zero-copy, dirty regions, 90/120 FPS, audio, and GPU rendering are not
  implemented.
- The 1 GiB localhost run proves QUIC/TLS/pacing behavior (40 seconds, 25.3 MiB/s, 0.4 ms mouse p95
  across 2,592 samples); it is not a physical LAN or Internet performance result.
- Receiver-confirmed adaptive probing moved 128 MiB at 28.0 MiB/s from a production-like 5,144 Kbps
  starting budget while mouse p95 remained 8.3 ms; this is also localhost transport-core evidence,
  not a physical 4K/Wi-Fi/disk claim.
- No installer or development version was created for this migration slice.
