# PeerOnQ known limitations

Source facts reviewed on 2026-09-27. Dated observations below retain their original evidence scope;
absence from this list is not proof of production readiness. See [CURRENT_STATE](CURRENT_STATE.md)
for the current source contract and [AI_CHANGELOG](../AI_CHANGELOG.md) for executed checks.

## Current capability and release boundaries

- Two physical Windows devices were not available for R0. Capture color/channel correctness,
  tearing/banding, keyboard layouts, UIPI boundaries, emergency stop and real input/render latency
  remain externally blocked.
- Separate-ISP, mobile-hotspot, symmetric-NAT and UDP-blocked TURN fallback were not rerun. Local
  loopback/coturn evidence cannot close that gate.
- Public DNS/TLS, public MSI byte/content headers, multi-host failure, backup restoration, and
  production alert delivery require an external environment.
- No production Authenticode certificate, RFC3161 timestamp, signed hosted update, clean x64 VM
  lifecycle, or physical ARM64 install/launch was executed in R0.
- Windows desktop media uses software VP8. Hardware H.264/HEVC capability probes do not select a
  production Windows hardware codec. Android's separate MediaCodec viewer path is not a Windows
  hardware-acceleration claim. Physical GPU/driver and sustained 4K performance remain unverified.
- The Phase 6 development topology is single-host. Redis-backed presence and signaling routing
  exist, but multi-host failover claims require their own shared-state and deployed-environment tests.
- `artifacts/peeronq` is intentionally offline/localStorage-only. It is not the cloud-backed native
  client and truthfully reports no API transport.
- `artifacts/api-server` remains a health-only Express skeleton, not the Phase 6 management API.
- Client source versions now derive from canonical `0.9.66` in `Directory.Build.props`; Android and
  Apple bundle codes are `9066`. API/workspace versions are separate contracts. This does not waive
  package payload, checksum, signing or publication checks for an exact client release.
- SIPSorcery/SIPSorceryMedia, WiX UI terms and native libvpx/vpxmd provenance remain subject to
  exact-artifact review and external legal approval. See the
  [third-party license audit](competitive/THIRD_PARTY_LICENSE_AUDIT.md); the project's MIT license
  does not resolve third-party redistribution terms.
- SIPSorcery 10.0.15 is in the affected range of `GHSA-6848-qmp4-652w` (TURN-over-TLS receive-loop
  denial of service); the reviewed 10.0.16 release is also affected. Native libvpx patch provenance
  is unverified. The Node workspace audit separately reports 9 existing findings (5 high,
  4 moderate). No package was upgraded merely to suppress these findings.
- WCAG 2.2 AA manual keyboard, screen-reader, high-contrast and zoom verification is incomplete.
- No independent penetration test, privacy audit, standards certification, or competitive benchmark
  has been completed.
- Native Linux, Android and Apple (macOS/iOS/iPadOS) viewer/controller projects exist. They do not
  advertise hosting, local capture, input injection, file/clipboard or unattended capabilities.
  Physical-device, native accessibility, ten-minute session and production signing/distribution
  gates remain open; Apple also needs actual Mac/Xcode compilation. See
  [CROSS_PLATFORM_CAPABILITIES](CROSS_PLATFORM_CAPABILITIES.md).
- An earlier 2026-09-27 media investigation recorded one failure of the unchanged 35 ms input-to-injection
  p95 gate (about 37–39 ms) and one live-TURN skip. A short synthetic same-process 4K run rendered
  about 15.5 fps; it does not establish sustained 4K/30 fps, physical input-to-photon delay or
  AnyDesk/TeamViewer parity. Later runs must report their actual measurements separately.
- The subsequent 2026-09-27 investigation reproduced dedicated and primary-fallback latency
  failures with both optimized and original decoder isolation. A timer-precision candidate was
  removed after inconsistent results; the 35 ms gate remains open. Detailed stage timings,
  sample counts and migration options are in [PERFORMANCE_REPORT](PERFORMANCE_REPORT.md).
  Its fake input sink does not measure actual Windows injection or a physical viewer event.

## Historical R0 environment observations — 2026-08-12

These observations are retained for traceability, not asserted as failures in the current checkout:

- Initial local-CA trust required an interactive Windows prompt the R0 headless runner could not
  approve. Later local trusted-HTTPS/TURN evidence is recorded in [PHASE3](../PHASE3.md).
- R0 emitted SPDX 2.2; hosted provenance and newer-format BOM validation were not run.
- Root formatting exposed style debt and a Prettier parser failure on the WiX source; R0 did not
  hide or mass-format it.
- Vite reported a product chunk above 500 KiB and vendored UI sourcemap warnings while building.
- The then-installed Node 25 emitted a Vitest local-storage warning; the supported target is Node 24.
- The in-app browser controller lacked required host sandbox metadata. R0 used independent HTTPS
  and container health probes and claimed no visual browser pass from that tool.
