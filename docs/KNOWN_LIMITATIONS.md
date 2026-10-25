# PeerOnQ known limitations

Source facts reviewed on 2026-10-18. Dated observations below retain their original evidence scope;
absence from this list is not proof of production readiness. Release-specific changes and checks
are listed in the [release notes](../src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md).

## Current capability and release boundaries

- The WebRTC stack is SIPSorcery 8.0.14 (the last release without the upstream use restriction)
  with PeerOnQ patches. Its DTLS code needs Portable.BouncyCastle 1.9.0, which is no longer maintained
  and shares the Bouncy Castle code base affected by CVE-2024-29857, CVE-2024-30171 and
  CVE-2024-30172 (moderate). Only a peer that passed authenticated signaling and the session's ICE
  checks reaches the DTLS handshake. Replacing it requires a new DTLS layer.
- 4K streaming was deferred on 2026-10-18. Current streaming is capped at a 1080-pixel short edge with
  preserved aspect ratio/no upscaling; the picker offers Automatic/1080p and old higher preferences
  normalize to 1080p. Canonical 0.9.77 is a private unsigned candidate; public binary publication
  remains gated. Earlier 4K measurements below do not imply a current 4K offer or a passed target.
- The pre-cap release prerequisite added bounded MF callbacks and exact SIMD conversion, but
  native 4K latency remains open. After native VP8 reconfiguration, the fresh capture-inclusive
  landscape/portrait whole p95 was 224.02/102.57 ms; final-five p95 was 158.19/49.49 ms.
  The unchanged H.264 path also slowed in that run; no causal attribution is claimed.
  Sender tracing localizes material tails
  to codec calls; low transport/encryption times do not prove the complete pipeline meets
  100 ms. See [performance](PERFORMANCE.md). Version remains 0.9.76 and no
  new product patch is approved by these measurements.
- Two physical Windows devices were not available for the baseline validation. Capture color/channel correctness,
  tearing/banding, keyboard layouts, UIPI boundaries, emergency stop and real input/render latency
  remain externally blocked.
- Separate-ISP, mobile-hotspot, symmetric-NAT and UDP-blocked TURN fallback were not rerun. Local
  loopback/coturn evidence cannot close that gate.
- Public DNS/TLS, public MSI byte/content headers, multi-host failure, backup restoration, and
  production alert delivery require an external environment.
- No production Authenticode certificate, RFC3161 timestamp, signed hosted update, clean x64 VM
  lifecycle, or physical ARM64 install/launch has been executed yet.
- The unreleased Windows source now negotiates real MF hardware H.264 with VP8 compatibility and
  device-failure fallback. The four-test acceptance suite passed on RX 5600 XT (two actual hardware
  cases and two conversion/disposal checks), including encrypted 1080p,
  first-frame/resize/keyframe continuity. This does not prove another GPU, ARM64, physical second
  endpoint or sustained 4K performance. No Windows HEVC implementation is claimed. See the dated
  [performance notes](PERFORMANCE.md).
- Windows system sound is implemented with separate attended consent and default-off signed
  managed policy; no microphone capture is offered. Local encrypted delivery and silent WASAPI
  lifecycle checks do not prove audible physical two-endpoint A/V synchronization. The optional
  LocalService broker tracks signed-in user hosts only; it adds no logged-out desktop, UAC or
  Secure Desktop control. Clean-VM service/MSI lifecycle and physical audio acceptance remain open.
- The control-plane development topology is single-host. Redis-backed presence and signaling routing
  exist, but multi-host failover claims require their own shared-state and deployed-environment tests.
- `artifacts/peeronq` is intentionally offline/localStorage-only. It is not the cloud-backed native
  client and truthfully reports no API transport.
- `artifacts/api-server` remains a health-only Express skeleton, not the control-plane management API.
- Client source versions derive from canonical `0.9.77` in `Directory.Build.props`; Android and
  Apple bundle codes are `9077`. This does not waive
  package payload, checksum, signing or publication checks for an exact client release.
- SIPSorcery/SIPSorceryMedia, WiX UI terms and native libvpx/vpxmd provenance remain subject to
  exact-artifact review and external legal approval. See the
  [third-party license audit](THIRD_PARTY_LICENSE_AUDIT.md); the project's MIT license
  does not resolve third-party redistribution terms.
- The 2026-10-18 follow-up source pins maintained PeerOnQ.SIPSorcery 10.0.17.6 from upstream 10.0.17,
  and Abstractions 10.0.17. The TURN/TLS loop catches packet
  handler failures; its actual loop has a regression test. Server images now pin ASP.NET 10.0.13.
  The patched Node workspace audit has zero known findings; the unused mockup sandbox is excluded
  from workspace installation. These scan results do not clear native libvpx patch provenance,
  restricted licenses, physical-device or exact-artifact release gates. Existing published binaries
  were not rebuilt by this source task.
- Earlier large legacy SCTP runs failed their bounds, including a 15-minute 1 GiB case near
  64 MiB. The follow-up proved concurrent UDP receives shared one buffer; a single-owner private
  candidate completed 1 GiB in 74.7 seconds with equal SHA256, no retransmissions and existing
  buffers/anti-replay checks. Exact produced .5 passed both actual 1 GiB SCTP lanes in 76.8/57.5
  seconds with equal disk SHA256 and no native QUIC. Separate strict input passed primary
  fallback but failed dedicated input; a later unchanged-gate .5 VSTest passed both with runtime
  snapshots, without proving the earlier cause fixed. Earlier isolated produced .6 passed both 1 GiB SCTP
  rows in 84.2/64.5 seconds, equal full source/destination SHA256, no QUIC/TURN/chunk override.
  That isolated unchanged strict input gate passed both routes (dedicated baseline/bulk p95
  15.996/16.780 ms, fallback 17.383/16.991 ms). The prior intermittent dedicated-input failure
  remains retained without a proved causal fix. The final combined run passed both 1 GiB lanes
  in66.1/62.3s and strict dedicated/fallback baseline/bulk p95 17.550/15.915 and16.351/16.133ms.
  Local passes are
  not WAN claims. The earlier .4 default-unbound native-QUIC
  disk tests passed 2/2: 1 GiB with equal SHA256 in 20.6 and 9.9 seconds, with native selection
  verified on both peers. The wildcard-candidate route correction therefore has actual unbound
  path evidence. These are local functional measurements, not Internet or physical two-endpoint
  performance claims; native QUIC remains gated to proved private LAN paths.
- WCAG 2.2 AA manual keyboard, screen-reader, high-contrast and zoom verification is incomplete.
- No independent penetration test, privacy audit, standards certification, or competitive benchmark
  has been completed.
- The earlier source solution run had one failure among 1,065 tests: 4K/30 fps VP8 delivered a valid
  frame but produced no bounded authenticated capture-to-present sample within 10 seconds. One
  exact-case diagnostic rerun passed, with clock uncertainty 5.1 ms but capture-to-present p95
  1,739.4 ms and render cadence 8.5 fps. That does not erase the initial failure or prove acceptable
  sustained 4K performance. The follow-up now retries the authenticated clock after one second
  while no bounded estimate exists, with two deterministic recovery regressions. Automatic
  capture retains native UHD in both orientations. The latest .5 whole suite passes 1,140 of
  1,167 with zero failures and 27 environment skips. The direct .5 30-second observations still
  miss 100 ms: landscape 20.06 fps / 134.75 ms p95; portrait 21.93 fps / 127.67 ms. This fixture
  has no production quality-feedback return path and cannot establish remote-age adaptation.
  The separate corrected .5 paired-coordinator observation passes functionally with actual
  accepted-session signaling: landscape whole/final p95 140.11/118.76 ms misses, portrait
  92.11/75.52 ms meets those windows but misses startup. Current .6 fixes falsely initialized
  RTCP jitter; native H.264 filler removal preserves all 12 tested decoded pictures. The .6
  pre-D3D checkpoint passed 1,179/1,206, zero failures and 27 explicit environment skips. Both normalized
  coordinator cases still missed every 100 ms window; a later whole-suite landscape p95 was
  118.59 ms, final-five 120.74 ms. Those checkpoints used software H.264 decoding. The optional
  D3D11 path/CPU fallback passed35 focused tests plus two real coordinator observations. Native
  landscape decode mean improved27.02 to18.87ms, but adopted-path whole/final-five p95 remained
  122.26/126.94ms landscape and117.57/108.57ms portrait. Every100ms window missed in that run.
  Final combined suite passed1,186/1,213, zero failures/27 environment skips; its whole/final-five
  p95 remained116.82/119.02ms landscape,130.00/102.51ms portrait, every100ms window missed.
  Its600.43s encrypted1080p soak passed29.97FPS with537 GPU/zero CPU samples; this is not a
  ten-minute4K or physical two-device test. Headless synthetic
  buffers do not establish physical 4K capture/display or sustained 30 fps; see the dated report.
- Native Linux, Android and Apple (macOS/iOS/iPadOS) viewer/controller projects exist. They do not
  advertise hosting, local capture, input injection, file/clipboard or unattended capabilities.
  The Linux follow-up uses fixed-system-path distro libvpx9, real Secret Service identity and
  Cloud attestation composition. Final .6 Ubuntu 24.04 linux-x64 validation passed App 22,
  platform 14 and scoped shared Cloud 12 (48/48, no skips), including UHD decode, real keyring,
  hybrid signatures and X11 repaint. The private self-contained archive passes integrity/notices,
  exact-core/ten-notice, actual launch/normal-close and startup-cancel checks. The final archive is
  under `app-updates/linux/final-transport-10.0.17.6/`; the final shared-contract rebuild passed
  all 48 checks and exact-archive lifecycle checks again, all 192 source inputs unchanged after
  completion. Earlier legal-notice drift/correction remains recorded historically. It is unsigned/unpublished;
  no Linux hosting, file transfer, audio or Unicode IME is implemented.
  Physical-device, native accessibility, ten-minute session and production signing/distribution
  gates remain open; Apple also needs actual Mac/Xcode compilation. See
  [CROSS_PLATFORM_CAPABILITIES](CROSS_PLATFORM_CAPABILITIES.md).
- An earlier 2026-09-27 media investigation recorded one failure of the unchanged 35 ms input-to-injection
  p95 gate (about 37–39 ms) and one live-TURN skip. A short synthetic same-process 4K run rendered
  about 15.5 fps; it does not establish sustained 4K/30 fps, physical input-to-photon delay or
  AnyDesk/TeamViewer parity. Later runs must report their actual measurements separately.
- The subsequent 2026-09-27 investigation reproduced dedicated and primary-fallback latency
  failures with both optimized and original decoder isolation. A timer-precision candidate was
  removed after inconsistent results; the 35 ms gate was open at that historical checkpoint. The
  final .4 source passes both synthetic lane gates (scenario p95 15.627-18.284 ms); physical
  input-to-photon latency remains unverified. Detailed stage timings,
  sample counts and migration options are summarized in [PERFORMANCE](PERFORMANCE.md).
  Its fake input sink does not measure actual Windows injection or a physical viewer event.
