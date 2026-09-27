# Phase 10 performance report

Measured on 2026-08-18 from the 0.9.21 x64 unsigned-development Portable Support publish.
Host: Windows 10.0.26200, AMD Ryzen 7 3700X, 16 logical processors, 32 GiB RAM.

## Measured evidence

| Measurement | Run 1 | Run 2 |
| --- | ---: | ---: |
| Main-window startup | 820.453 ms | 631.850 ms |
| Idle CPU median | 0% | 0% |
| Idle CPU p95 | 2% | 3% |
| Working set median | 195.098 MiB | 195.289 MiB |
| Working set p95 | 196.938 MiB | 196.844 MiB |
| Private memory p95 | 131.469 MiB | 131.027 MiB |
| Dedicated GPU memory p95 | 57.980 MiB | 57.980 MiB |

Each idle run sampled the real WinUI process for five seconds. The run is a startup/idle baseline,
not an active remote-session benchmark. Four CPU/memory samples were collected per run.

The deterministic connection-policy suite passed 21/21. It covers unknown/invalid telemetry,
packet-loss, RTT, jitter, encoder-backpressure, bandwidth-constrained, stable-headroom and bounded
2/8/16 MiB transfer-allocation decisions. Application, Signaling, Domain and End-to-End suites also
passed; see the Phase 10 completion report for exact counts.

## Isolated viewer hot-path measurements (2026-08-23)

These are deterministic local microbenchmarks, not a physical two-device or end-to-end result:

- The original byte-at-a-time 1920x1080 BGR24-to-BGRA32 expansion measured 3.507 ms/frame. The
  bounded packed-pixel implementation measured 1.490 ms/frame (2.35x faster), with byte-identical
  output on the local x64 device.
- The unchanged VP8 screen encoder measured 3.82-4.55 ms mean and about 5.5 ms p95 for the synthetic
  1080p desktop workload at the 60 FPS/12 Mbps Performance target. This fits the local 16.67 ms
  encode budget, so the user-reported LAN delay did not justify a speculative FPS or codec-quality
  downgrade.
- The sharer now wakes its capacity-one latest-frame encoder queue when a capture frame arrives.
  When the secure transport is already ready, this removes the former 0-16.67 ms (Performance) or
  0-33.33 ms (Automatic) polling wait before encoding. This is a code-path bound, not a claimed
  end-to-end latency measurement.

## Local bulk-transport contention measurements (2026-08-24)

These are real localhost transport runs on the development machine, not two-device LAN/Internet
claims. The production 256 KiB file record size and the same adaptive bulk pacer were used.

- The active SIPSorcery compatibility path moved 8 MiB over its dedicated SCTP association in
  26.5 seconds (309.5 KiB/s). The legacy shared association did not finish that payload inside its
  three-minute test timeout. This proves that increasing only the policy cap cannot meet the target.
- Unpaced managed QUIC cleared the throughput target but raised same-connection mouse p95 to
  265.6 ms, proving that transport replacement without a bulk reserve is also insufficient.
- After retaining one bounded Windows timer quantum of earned pacer credit and using the measured
  post-media/input remainder, three 64 MiB QUIC/TLS runs measured 25.2-25.3 MiB/s with mouse p95
  0.4-2.6 ms.
- A full 1 GiB QUIC/TLS run completed in 40 seconds at 25.3 MiB/s while 2,592 concurrent mouse
  samples measured 0.4 ms p95. The regression gate requires at least 17.1 MiB/s and at most 35 ms
  input p95. It is an equivalent transport-core acceptance result; physical disk, Wi-Fi/router,
  NAT/firewall, screen encoding and two-laptop contention remain to be measured.
- The production WebRTC estimator can expose only 43.2 Mbps while a 36 Mbps 4K target is active,
  leaving a 5,144 Kbps initial file allocation after reserves. A real 128 MiB managed-QUIC run with
  receiver-confirmed adaptive goodput escaped that ceiling, reached the defensive 1 Gbps probe,
  averaged 28.0 MiB/s, and kept 289 concurrent mouse samples at 8.3 ms p95. This proves the feedback
  path and bounded probing on localhost; it does not prove a physical link can sustain 1 Gbps.
- A real VP8/WebRTC Full Control loopback reversed the contention direction: the sharer sent the
  file while viewer input travelled back to the sharer. At the media boundary, fresh viewer input
  p95/clock uncertainty appeared in sharer statistics, selected the 64 KiB/128 Kbps emergency bulk
  allocation, stepped adaptive video down on sustained pressure, expired when stale, and
  screen/input/file completion continued. One recorded run measured 34.7 ms baseline input p95,
  31.9 ms during the reverse-direction transfer and 4.8 ms maximum capture-to-decode latency.
  Separate coordinator and signaling integrations prove
  viewer publication plus authenticated relay/validation. Together these prove bidirectional
  feedback wiring locally; they do not replace the physical two-device acceptance matrix below.
- The Full Control isolation acceptance initially failed because SDP exposed only the primary and
  file peers. New peers now negotiate three independent ICE/DTLS transports: primary video/hybrid
  handshake, 64 KiB-bounded input SCTP, and bulk SCTP (or native QUIC for eligible LAN files).
  Authenticated input record counters proved mouse/keyboard used the input peer. The 21-second real
  VP8/input/file contention run passed its <=35 ms input p95, <=10 ms inflation, <250 ms individual
  stall, advancing-video and unfinished-concurrent-file gates. A separate real peer test proved the
  primary input fallback when the additive lane is not echoed. The Full Control acceptance also
  closes an established input peer at runtime, then proves the next mouse event arrives on the
  primary secure channel while both primary peers stay connected and VP8 presentation advances.
  These are localhost transport isolation results, not physical Wi-Fi/router results.
- The feedback publisher period is now an explicit 250 ms code-path bound instead of one second.
  A scripted failed-first-send regression completed the failure plus successful next-tick retry in
  652 ms on the development host. The four-message/second per-session cadence shares a 20 msg/s
  per-client budget; above five concurrent sessions it stretches automatically, retaining five
  messages/second below the server's default 25 msg/s limit for ICE/session control. This is a
  scheduler/path bound, not a physical-network latency claim.
- A fresh 0-decode/0-render viewer report was reproduced as a false `stable_headroom` decision even
  while the sharer kept encoding. The new two-window stall detector first failed its regression,
  then passed with bounded quality reduction and a coalesced sender key-frame request. The real
  VP8/WebRTC acceptance continued frames after injected zero-presentation feedback at 320x240, and
  the complete 320x240/1080p/4K theory passed 3/3 in 10.2 seconds. This proves the local recovery
  path, not physical-network stall recovery.

## Input latency investigation — 2026-09-27

**Result: the unchanged 35 ms p95 acceptance gate remains open.** No production transport,
decoder, timer/power policy, dependency version, security boundary or latency threshold was changed
by this investigation. Test instrumentation and the primary-input fallback case are retained.

Exact test:
`PeerOnQ.Media.Tests.WebRtcLoopbackTests.Interactive_input_remains_responsive_while_bulk_transfer_is_backpressured`.
The original Release fact first failed with p95 **35.8 ms** and send-call p95 **0.3 ms**.
That original output did not include p50/p99; they were not reconstructed.

Reproduction command (the final test is a theory with dedicated/fallback cases):

```powershell
dotnet test tests/PeerOnQ.Media.Tests/PeerOnQ.Media.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~Interactive_input_remains_responsive_while_bulk_transfer_is_backpressured --logger 'console;verbosity=detailed' --nologo
```

Configuration: Windows 10.0.26200 x64, Ryzen 7 3700X, .NET 10.0.12 testhost, Release, real
same-process WebRTC/ICE/SCTP/DTLS, mandatory authenticated hybrid session protection, 320x240
synthetic video with a 30 fps target, dedicated bulk peer, 1 MiB file and 1,024-byte chunks.
Each input-lane case uses five warm-up events, then 20 events without bulk and 20 with an active
backpressured file transfer. Percentiles use nearest rank; with n=20, p99 is the maximum, not a
large-sample tail estimate. The developer host's existing idle services remained running; this is
not dedicated benchmark hardware or a two-machine network experiment.

The fixture calls `SendPointerMoveAsync` and timestamps the callback inside **FakeRemoteInputSink**.
It does not sample a physical mouse, WinUI's input queue, Windows `SendInput`, or input-to-photon
latency. Real Windows injection remains **UNMEASURED / physical-device gate open**.

### Comparable input-to-fake-injection distributions

All entries below are milliseconds in **p50 / p95 / p99** order, n=20 per cell. The baseline uses
the corrected final probe with timer activation disabled. The candidate uses the same probe and
an OS timer lease active only while an input-authorized session's primary peer is connected.

| Input lane / load | Baseline, optimized decoder | Timer candidate, optimized decoder | Original decoder isolation, no timer |
| --- | --- | --- | --- |
| Dedicated / no bulk | 33.138 / 34.709 / 39.105 | 33.249 / 34.624 / 38.755 | 33.284 / 34.754 / 39.833 |
| Dedicated / active bulk | 33.069 / 38.328 / 39.140 | 33.249 / 34.926 / 41.212 | 33.245 / 35.578 / 35.957 |
| Primary fallback / no bulk | 32.483 / 37.467 / 38.147 | 25.750 / 26.326 / 26.852 | 33.111 / 35.567 / 39.733 |
| Primary fallback / active bulk | 33.263 / 36.620 / 40.498 | 33.321 / 38.011 / 39.503 | 33.279 / 34.286 / 34.618 |
| Complete acceptance cases | **0 passed / 2 failed** | **1 passed / 1 failed** | **0 passed / 2 failed** |

The original-decoder isolation temporarily substituted SIPSorcery's `VpxVideoEncoder.DecodeVideo`
for the optimized decoder, preserving the rest of the pipeline, then restored the file in `finally`.
Its total input timings remain valid; the earlier probe revision's unfiltered telemetry/ack stage
details are not used as the corrected stage baseline. Failure with both decoders rules out the
optimized decoder as the sole cause. This 320x240 comparison does not measure 4K decoding.

### Pipeline ownership

Corrected baseline, dedicated input during active bulk; milliseconds, n=20 except acknowledgements.
Reflection proxies observe existing abstraction boundaries and forward real calls unchanged.
Secure input routing excludes clock telemetry; plaintext values are not recorded. These boundaries
include probe overhead and cannot expose private transport internals independently.

| Requested boundary/stage | p50 | p95 | p99 | Interpretation |
| --- | ---: | ---: | ---: | --- |
| Local event | — | — | — | Monotonic test timestamp at the sender API; physical/WinUI event unavailable |
| Coalescing/pacing and command creation | 0.024 | 0.041 | 0.056 | Event to collaboration SendAsync |
| Secure-record creation | 0.043 | 0.077 | 0.078 | Binding, serialization, send gate and AEAD to media call |
| Send admission | 0.013 | 0.020 | 0.022 | Media SendDataAsync call to return, not a socket-write hook |
| SCTP/DTLS/OS scheduling through receiver delivery | 32.879 | 38.183 | 38.959 | Send return to raw DataMessageReceived; individual transport stages are not separable here |
| Receiver delivery | — | — | — | Timestamped by the prior row; includes media lane validation/copy before the callback |
| Secure validation/decryption | 0.085 | 0.115 | 0.116 | Raw ciphertext callback to bound, decoded command |
| Permission/focus/rate checks | 0.003 | 0.005 | 0.005 | Decoded command to sink entry |
| Fake sink injection | 0.001 | 0.001 | 0.002 | **Not Windows SendInput** |
| Authenticated acknowledgement return | 33.241 | 39.281 | 39.281 | Injection to accepted ReportInputLatency; n=6 requested, 6 validated, 0 unavailable |
| Final event-to-injection | 33.069 | **38.328** | 39.140 | **35 ms gate failed** |

The probe waits up to two seconds for sampled acknowledgements and reports requested, validated and
unavailable counts. It never assumes every event requests an ack: production samples at most once
per 100 ms. Concurrent receiver delivery can precede send-call return; any negative boundary overlap
is explicitly counted, not discarded. This baseline had zero overlaps. Stage percentiles must not
be added; acknowledgement return is subsequent work, not part of event-to-injection latency.

The excess is localized to **post-admission transport/delivery**, not crypto, pacing or input
permission checks. These same-process monotonic intervals are not network RTT. Exact attribution
among socket delivery, SCTP, DTLS polling and OS scheduling requires deeper transport tracing;
the boundary measurements do not pretend to supply that unavailable split.

### Supported mitigation tested and rejected

The pinned SIPSorcery DTLS receiver enqueues incoming records, then polls an empty queue using
`Thread.Sleep(25)`. The inspected 10.0.16 and upstream source retain the same implementation;
there is no supported poll-interval setting or transport factory. See the exact commit/blob and
API/security/license analysis in the [dependency audit](competitive/THIRD_PARTY_LICENSE_AUDIT.md).

A balanced Windows `timeBeginPeriod(1)` experiment initially passed 2/2 cases with input p95
26.053–26.411 ms. A production-shaped, permission-scoped connected-session lease then produced
the **1 pass / 1 failure** candidate above. A repeat with native-call tracing failed **both** cases
(dedicated baseline p95 36.158 ms, fallback bulk p95 35.963 ms), despite successful begin calls and
no end call before cleanup. Acquiring earlier passed 2/2 but still had fallback p95 34.506 ms;
no evidence of timer-resolution caching in SCTP/DTLS initialization was found.

A separate diagnostic sleeper during a subsequent lease run measured actual `Sleep(25)` at
p50/p95/p99 **25.526 / 32.591 / 51.805 ms** during fallback bulk (n=28). That run's input cases
passed 2/2, illustrating why a passing short sample cannot establish a reliable correction.
The sleeper is a separate thread, not an internal DTLS trace. The lease's ten deterministic
ownership tests passed during the experiment; they do not prove latency. The temporary lease,
ownership tests, tracing and decoder substitution were all removed. **There is no adopted fix and
therefore no successful production “after” distribution.**

Microsoft documents that [timer requests](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)
can improve wait precision at a power cost and may be ignored for occluded Windows 11 applications.
[Sleep](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-sleep) also depends on
scheduling after the wait expires. No process power-policy override, registry change or real-time
priority was introduced.

The smallest transport-level candidate is an upstream event-driven receive wakeup retaining
timeouts, ordering, cancellation and DTLS behavior. A maintained fork would need security backports,
license review and unchanged ICE/TURN/SDP/SCTP/hybrid/focus/reconnect compatibility gates. Existing
native QUIC file transport is not a drop-in input lane or a WAN/TURN replacement. Work stops before
a fork or major migration, as requested; the audit records the bounded alternatives.

### Final retained-source validation

After removing the candidate, the strict Release solution build succeeded with zero warnings and
errors. The complete solution regression returned **846 passed / 1 failed / 5 skipped** across
18 test assemblies. Media was **138 passed / 1 failed / 1 skipped**; Application was **160 passed /
0 failed / 0 skipped**. The extra Media case is the new primary-input fallback acceptance case.

The unchanged dedicated-input acceptance failed during bulk at p95 **35.057 ms**. Final solution-run
event-to-fake-injection values (p50 / p95 / p99, n=20 each) were:

| Scenario | Final retained-source measurement (ms) | 35 ms stage gate |
| --- | --- | --- |
| Dedicated / no bulk | 32.941 / 34.630 / 37.824 | Passed |
| Dedicated / active bulk | 33.264 / 35.057 / 39.792 | **Failed** |
| Primary fallback / no bulk | 30.868 / 32.202 / 32.476 | Passed |
| Primary fallback / active bulk | 31.077 / 31.671 / 32.285 | Passed |

These are an additional full-suite run, not an improvement attributed to a production change.
Five skips retain their real environment scope: three need an explicitly isolated
`PEERONQ_TEST_REDIS` endpoint; signaling restart and live TURN require the configured Phase 3 test
controller/credentials. Those environments were not supplied to this run; the existing development
Docker stack is not automatically a disposable test environment. No skip is counted as a pass.

## Not measured

- Active capture, encode, decode and render CPU/GPU.
- 720p/1080p/1440p/4K quality under 5–1000 Mbps shaped links.
- 15/30/60 FPS stability, true end-to-end frame latency and input latency.
- Direct versus TURN-relayed throughput, loss and jitter comparisons.
- Physical two-device file-transfer throughput and interactive/frame-age latency during contention.
- Physical-device hardware-encoder behavior and long-duration resource growth.

These missing measurements are external acceptance blockers. They must not be inferred from unit
tests, idle process data, UI telemetry, or codec capability labels.
