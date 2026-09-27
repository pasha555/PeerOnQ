# PeerOnQ Phase 6.5 gap analysis

Status values are evidence-based: `implemented`, `partial`, `missing`, or `blocked`.

| Requirement | Status | Existing evidence | Smallest safe next change |
| --- | --- | --- | --- |
| Bounded newest-frame pipeline | implemented | Production queue capacity is one; render queue coalesces to one pending frame | Preserve and add queue-depth evidence to benchmarks |
| Actual capture/encode/decode/render FPS | implemented | `MediaStatisticsCollector` uses real sliding-window events and exports separate dimension provenance | Add physical-session benchmark distributions |
| 720p and 1080p | implemented | Real WGC 720p scaling plus real WebRTC 1080p loopback tests | Add sustained-duration acceptance thresholds |
| 1440p | partial | Explicit non-upscaling 1440p option, signaling round-trip and scaler regression exist | Run sustained physical capture/encode/decode/render validation on capable hardware |
| Native 4K | partial | Real synthetic codec/WebRTC and native scaler coverage at low FPS | Physical 4K capture/render and 30/60 FPS evidence are blocked by hardware |
| Hardware acceleration | missing | Media Foundation capability probe exists; selector truthfully chooses software VP8 | Do not advertise hardware; implement only after codec/license/resource-lifecycle review |
| Desktop change optimization | missing | WGC pacing and encoder key-frame controls exist | Prefer supported dirty-region metadata; benchmark before adding full-frame analysis |
| Content modes | partial | Automatic, Office, Balanced, Performance, High Quality and Low Bandwidth have explicit bounded policies | Automatic local content classification remains missing |
| Adaptation hysteresis | implemented | Four healthy samples recover one rung; resolution changes have a ten-second dwell with oscillation regression | Measure behavior under controlled impairment |
| Direct route preference | implemented | Standard ICE ranking and nominated-pair classification | Preserve; never classify from candidate discovery alone |
| Relay subtype and region scoring | missing | One authenticated ICE configuration can carry one relay region label | Introduce bounded multi-region probes only when the server contract supplies candidates |
| Secure network-change recovery | implemented | Network watcher, PONG deadlines, typed reconnect, input release, reauthentication and fresh ICE | Add physical Wi-Fi/Ethernet/sleep evidence |
| Connection pre-flight | partial | Bounded on-demand Network Doctor checks interface, DNS, API, signaling, UDP, TCP RTT, region, protocol and clock skew | Add approved-session STUN/TURN and packet-loss probes without delaying every connection |
| Large streaming transfer | implemented | File stream/chunk protocol, bounded buffers and no default byte cap | Measure 1/10/100 GB; do not replace SCTP before data shows a bottleneck |
| Verified resume | implemented | Stable transfer identity, exact offsets, partial file and per-chunk/final SHA-256 | Add process-restart contract only if persistence ownership is designed |
| Interactive traffic priority | implemented | Separate interactive/normal/bulk send lanes; bulk yields to foreground waiters | Record input latency during measured transfer |
| Structured sanitized logs | implemented | JSON formatter, bounded rolling files, sensitive-name redaction and content sanitizer | Normalize event/correlation fields in the session timeline |
| Session timeline | implemented | Bounded real coordinator events persist after local release and are included in connection details/support bundles | Add admin projection only when privacy policy authorizes it |
| Network Doctor | partial | Deterministic bounded UI and sanitized technical report work without AI | STUN/TURN and packet loss remain session-scoped/not configured in standalone mode |
| Full session post-quantum protection claim | implemented with external gate | Application record layer protects all current session data channels | Keep mandatory downgrade rejection; obtain independent review |
| Dependency/license gate | blocked | Exact runtime SBOM tooling exists | Resolve SIPSorcery embedded additional restriction and native libvpx notices before distribution |
| Real two-device/Internet/impairment/soak evidence | blocked | Loopback and local coturn evidence exists | Requires controlled external hardware/topology and cannot be simulated in documentation |

## Priority order

1. Preserve baseline and truthful diagnostics.
2. Add missing dimension/encoder/decoder provenance and oscillation controls.
3. Add deterministic Network Doctor and real session timeline.
4. Re-measure before considering a hardware codec, dirty-region engine, QUIC transfer path or
   multi-region routing protocol change.
5. Close physical-device, license and independent-security gates before a production claim.
