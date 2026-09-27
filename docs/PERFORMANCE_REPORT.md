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

## Not measured

- Active capture, encode, decode and render CPU/GPU.
- 720p/1080p/1440p/4K quality under 5–1000 Mbps shaped links.
- 15/30/60 FPS stability, true end-to-end frame latency and input latency.
- Direct versus TURN-relayed throughput, loss and jitter comparisons.
- Physical two-device file-transfer throughput and interactive/frame-age latency during contention.
- Physical-device hardware-encoder behavior and long-duration resource growth.

These missing measurements are external acceptance blockers. They must not be inferred from unit
tests, idle process data, UI telemetry, or codec capability labels.
