# Performance and media evidence

## Product north star and competitive gate

PeerOnQ's standing product objective is to outperform AnyDesk in measured interactive remote-desktop
responsiveness and stability without weakening consent, authorization, encryption, visual fidelity,
or file integrity. "No latency" means no application-created queue growth, stale-frame playback, or
input/file head-of-line blocking; propagation, capture, codec and display time remain physical costs
and must be reported rather than hidden.

These are acceptance targets, not claims about the current build:

| Scenario | Required target |
| --- | --- |
| Direct LAN, 1080p interactive desktop | capture-to-present p95 <= 75 ms and authorized input-to-injection p95 <= 35 ms |
| Direct LAN, native 4K Quality profile | capture-to-present p95 <= 100 ms with no stale-frame queue growth |
| Active screen/control plus file transfer | input p95 inflation <= 10 ms, frame-age p95 inflation <= 20 ms, and no application stall >= 250 ms |
| High-capacity link with enough measured spare capacity for a >= 150 Mbps bulk budget | one GiB transfer <= 60 s while the interactive budgets above remain satisfied |
| Constrained/lossy link | file traffic degrades first; input remains authorized/responsive and video drops obsolete frames instead of increasing frame age without bound |
| Reliability soak | four continuous hours with zero unexpected disconnects, zero frozen-control intervals >= 1 s, and no unbounded queue or memory growth |
| Recovery | supported network interruption/path change returns to an interactive session within 3 s p95 when signaling and an authorized route remain available |

No release may claim that PeerOnQ is faster than AnyDesk until both products are measured on the
same two machines, display/workload, codec-quality class, network path/impairment, and run duration.
Record both exact versions and run at least ten trials; PeerOnQ must have lower p95 input latency and
frame age, no worse visual correctness, and no additional disconnect/freeze in that matrix. Preserve
raw results and artifact hashes. A single favorable run or unmatched quality setting is not evidence.

Required measurement work:

- export the implemented authenticated capture-to-present p50/p95/p99 frame-age samples together
  with clock uncertainty from both physical benchmark devices;
- use the implemented authenticated, sampled input marker/acknowledgement path to collect physical
  two-device input-to-successful-injection p50/p95/p99 without changing production authorization;
- record capture, encode, send, receive, decode, present and input queue depth/drop histograms;
- exercise direct, TURN, file-relay, bandwidth-limited, packet-loss, jitter, suspend/resume and path
  change cases with deterministic impairment profiles;
- treat every unexpected disconnect, >= 250 ms interactive stall, stale-frame buildup or unbounded
  queue as a release blocker and preserve a regression for each reproduced cause.

## Repeatable measurement

Use the exact signed or unsigned-development executable and record the artifact SHA-256, machine,
Windows build, CPU/GPU/driver, display resolution/refresh, network path, quality profile, and duration.
Run:

```powershell
.\scripts\windows\measure-phase5-performance.ps1 `
  -ExecutablePath .\dist\development\0.5.1\x64\app\PeerOnQ.exe `
  -DurationSeconds 30 `
  -OutputPath .\benchmarks\phase5-idle-x64.json
```

The script measures window-observed startup, normalized process CPU, working/private memory and GPU
process memory where Windows counters exist. It leaves unsupported measurements null with a reason.
For an active session, export media diagnostics and supply `-MediaDiagnosticsPath`; record transfer
throughput and reconnect duration from the real transfer/reconnect run alongside the file. Never infer
or guess missing values.

## Implemented resource controls

- Two-frame Windows capture pool and bounded latest-frame queue prevent unbounded latency/memory.
- Production capture buffer ownership transfers to the media queue; the encoder uses the underlying
  I420 array when safe, avoiding two redundant full-frame copies.
- Raw-frame pacing bounds work before encoding. The desktop VP8 encoder applies the selected CBR
  target after loading libvpx defaults and uses libvpx's reference-safe frame dropping; PeerOnQ
  never drops an already encoded VP8 access unit from the prediction chain.
- Bitrate, frame-rate, or capture-dimension changes recreate the encoder and make the first frame a
  key frame. Automatic key frames are capped at a two-second interval for bounded recovery.
- If VP8 decoding fails or yields no image, the connected viewer sends RTCP PLI at most once per
  500 ms. The sharer coalesces those requests onto its single encode loop and forces the next RTP
  payload to be a key frame; a new connection and ICE restart force the same recovery boundary.
- Adaptive mode uses real RTCP packet loss, RTT/jitter and local queue drops to reduce FPS first, then
  capture resolution, and recovers gradually.
- The viewer returns its decode/render FPS, decode-to-render p95, capture-to-present p95 and clock
  uncertainty over the existing authenticated session-quality channel. The same additive message
  returns input-to-injection p95 and its clock uncertainty, so a sharer sending a file in the
  opposite direction sees the viewer's control pressure. Fresh reliable input p95 above 35 ms
  throttles that sender's WebRTC/native bulk allocation immediately; if it remains high until the
  next one-second adaptive evaluation, video FPS/bitrate also steps down. Fresh reliable frame age
  above 75 ms (1080p) or 100 ms (4K), decode-to-render p95 above 50 ms, or render FPS below 75% of
  decode FPS feeds the same central adaptive policy. Feedback expires after five seconds, which
  covers the bounded 3.2-second cadence at 64 concurrent sessions plus delivery jitter, so an
  old peer or interrupted telemetry cannot pin the sharer at reduced quality. A failed signaling
  send drops only that one sample; the same bounded publisher retries on its next 250 ms tick
  and survives signaling registration gaps without touching the media connection.
- When the sharer encoded frames but fresh viewer feedback reports both decode and render FPS as
  zero for two consecutive one-second evaluations, the central policy declares
  `viewer_video_stall`, steps production down, and requests a coalesced key frame every evaluation
  until presentation resumes. A single zero window, stale/missing feedback, an old peer, or an idle
  sender does not trigger recovery.
- Every decoded frame carries its process-local monotonic timestamp through viewer latest-frame
  coalescing. Only the frame that successfully reaches `WriteableBitmap.Invalidate` contributes to
  decode-to-render p50/p95/p99, preventing a later decoded frame from corrupting an earlier frame's
  UI-latency sample. Capture-to-encode exposes the same bounded percentile distribution.
- New peers negotiate `x-peeronq-video-frame-timing:1` in SDP, refresh five encrypted NTP-style
  clock probes, and authenticate the capture timestamp and sequence inside each protected video
  frame. Successful presentation reports capture-to-present p50/p95/p99 only while the selected
  clock sample has at most 50 ms uncertainty. Failure or an older peer disables this optional
  measurement without ending or delaying the session. Estimates refresh each minute and expire
  after two minutes so a long session never reports stale clock evidence.
- Full Control peers separately negotiate `x-peeronq-input-ack:1`. At most one authorized command
  per 100 ms carries a timestamp, with no more than 32 outstanding samples. The host acknowledges
  only after successful OS input injection; the viewer combines that authenticated result with the
  bounded peer-clock estimate to report input-to-injection p50/p95/p99. Measurement timeout or send
  failure never disables working control, and an older/unnegotiated peer receives neither the new
  command field nor acknowledgement type.
- When input p95 exceeds 35 ms with clock uncertainty at most 10 ms, native/direct file pacing
  immediately drops to the conservative bulk tier with a 64 KiB window while preserving at least
  1 Mbps for interactive traffic and the current media reserve. The pressure expires after five seconds
  without a fresh input sample so idle transfers recover instead of remaining permanently slow.
- Full Control peers negotiate `x-peeronq-input-data-lane:1`; authenticated Input records use a
  dedicated `RTCPeerConnection`/SCTP association with a 64 KiB sender-queue bound. Screen RTP and the
  mandatory hybrid handshake remain on the primary peer. File-capable peers independently negotiate
  `x-peeronq-bulk-data-lane:1` on a third peer, so SIPSorcery's association-wide sender FIFO cannot
  place video or file work ahead of mouse/keyboard. Missing/closed input negotiation retains the
  authorized primary-channel fallback; it never ends screen share or grants input permission.
- File-capable new peers also negotiate `x-peeronq-native-bulk:1`. On a selected direct-LAN ICE
  path, the viewer and sharer exchange session-ephemeral TLS certificate pins and one listener port
  only inside the already authenticated PNQE channel, then keep each newly started transfer on a
  dedicated QUIC/TLS 1.3 connection. Screen RTP and the primary mouse/keyboard/control association
  remain physically separate. Failure, an older peer, a non-LAN path, or unavailable MsQuic keeps
  that transfer on the existing relay/dedicated-SCTP compatibility path without ending screen share.
- Native receivers coalesce cumulative delivery receipts after async file writes complete. The
  authenticated sender accepts only monotonic bytes that do not exceed locally scheduled payload,
  then raises the native file probe by at most 25% per confirmed window. Unknown connection health
  remains capped at 150 Mbps; Good/Excellent feedback may reach the defensive 1 Gbps bound. Input
  p95 pressure, Fair/Poor health, loss/RTT/jitter thresholds, invalid metrics, or one second without
  fresh delivery immediately returns pacing to the conservative post-media/input allocation.
- File-relay completion keeps heartbeat failure deferred for one heartbeat window after the
  callback returns, allowing the already queued PONG to be consumed before liveness is evaluated.
  This closes the completion-edge race without weakening the normal disconnect deadline.
- Negotiated file relay uses its own TLS-socket backpressure and bypasses the media-path token
  bucket. Its independent file send/receive gates preserve record order without holding the
  dedicated input or primary video paths, while unknown/poor media estimates cannot impose the
  direct-path 1,024/256 Kbps fallback on an otherwise faster relay connection.
- Direct bulk records are fragmented before SCTP admission. With interactive permission, both the
  fragment size and the pre-existing sender queue budget independently represent at most five
  milliseconds of the current bulk allocation. The file token bucket starts with one current record
  and retains at most one 16 ms Windows timer quantum of earned scheduler credit; this prevents
  coarse timer wakes from turning a configured 150 Mbps budget into a materially lower payload rate
  without allowing an unlimited queue. The refill rate, not the credit bound, controls sustained
  throughput. Unknown-capacity sessions keep the finite 150 Mbps File Transfer provisional ceiling;
  a trustworthy measured link may use the larger remainder after input and active-media reserves,
  up to the defensive 1 Gbps policy bound.
- Display resize recreates the two-buffer capture pool; multi-monitor switching reuses one active
  capture session. D3D11 falls back to WARP when hardware device creation fails.
- The actual negotiated codec is VP8 through SIPSorcery's software encoder/decoder. Media Foundation
  probes H.264/HEVC hardware capability, but PeerOnQ does not select it because no production hardware
  codec wrapper exists. Hardware acceleration must not be claimed.

## Profiles and targets

| Profile | Behavior | Target, not a guarantee |
| --- | --- | --- |
| Automatic/Balanced | Network-driven bounded FPS/resolution/bitrate | Office 1080p up to 30 FPS when end-to-end capacity permits |
| Performance | Higher FPS/bitrate within queue and capture limits | Up to 60 FPS only after capture/codec/display/network support is measured |
| Quality | Native/30 fps target, desktop VP8 mode, 24 Mbps ceiling | Text/detail on capable LAN links |
| Low bandwidth | Reduced FPS then resolution; bounded bitrate | Preserve readable interaction under constrained links |

The viewer sends its selection during session setup as one exact built-in value: `automatic`,
`performance`, `balanced`, or `quality`. A missing field from an older client remains Automatic;
malformed values fail closed. The sharer snapshots the validated profile before permission handling
and uses that immutable snapshot for capture and encoding, while adaptation may still reduce it when
measured network conditions require.

Diagnostics now report authenticated input-to-successful-injection p50/p95/p99 together with
clock uncertainty, plus exact per-frame local capture-to-encode and decode-to-render p50/p95/p99
and negotiated capture-to-present p50/p95/p99. They also expose native-QUIC negotiation/readiness,
current native file budget, receiver-confirmed goodput and accepted feedback sample count,
dedicated input/bulk negotiation/readiness, primary/input/bulk SCTP buffered bytes, input-lane record
count, adaptive fragment/queue budgets and completed-record/fragment counts. The explicitly
consented advanced diagnostics ZIP allowlists the same numeric latency, lane, queue, dimension and
goodput evidence while continuing to exclude network addresses and user content.
Loopback proves protocol, injection-boundary and traffic-isolation mechanics but is not physical
latency evidence; competitive claims still require the two-device matrix above. GPU-reset
automatic recovery, dirty-region encoding, occlusion/minimized-window throttling, and production
hardware encode/decode remain explicit engineering gates rather than claimed features.
