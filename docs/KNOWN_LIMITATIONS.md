# PeerOnQ known limitations

Verified or bounded at R0; absence from this list is not proof of production readiness.

- Two physical Windows devices were not available for R0. Capture color/channel correctness,
  tearing/banding, keyboard layouts, UIPI boundaries, emergency stop and real input/render latency
  remain externally blocked.
- Separate-ISP, mobile-hotspot, symmetric-NAT and UDP-blocked TURN fallback were not rerun. Local
  loopback/coturn evidence cannot close that gate.
- Public DNS/TLS, public MSI byte/content headers, multi-host failure, backup restoration, and
  production alert delivery require an external environment.
- A first-time Phase 3 local CA trust operation can require an interactive Windows security
  confirmation. The certificate was generated and its Compose configuration passed in R0, but the
  headless runner could not approve that OS prompt; Phase 3 trusted-HTTPS runtime is not claimed.
- No production Authenticode certificate, RFC3161 timestamp, signed hosted update, clean x64 VM
  lifecycle, or physical ARM64 install/launch was executed in R0.
- The negotiated media codec is software VP8. Hardware H.264/HEVC capability is probed but no
  production hardware encoder/decoder path is selected. GPU reset recovery, dirty-region encoding,
  and occlusion/minimize throttling remain open.
- The Phase 6 development topology is single-host. Some real-time state remains intentionally
  single-node; horizontal routing/failover claims require shared-state tests.
- `artifacts/peeronq` is intentionally offline/localStorage-only. It is not the cloud-backed native
  client and truthfully reports no API transport.
- `artifacts/api-server` remains a health-only Express skeleton, not the Phase 6 management API.
- Product/installer/API/workspace versions are not unified. No release should be cut until the P1
  version-source task is complete.
- The release pipeline emits SPDX 2.2 while the current SPDX standard is 3.0. Hosted provenance and
  current-format BOM validation were not run.
- Root formatting check exposes existing style debt and a Prettier parser failure on the WiX source.
  It was not hidden or mass-formatted in R0.
- Vite reports a product chunk above 500 KiB and vendored UI sourcemap warnings. The build succeeds.
- Node 25 emits a Vitest local-storage warning; the documented target is Node 24 LTS.
- WCAG 2.2 AA manual keyboard, screen-reader, high-contrast and zoom verification is incomplete.
- The in-app browser controller could not initialize because its host omitted required sandbox
  metadata. R0 used independent HTTPS and container health probes and does not claim a visual
  browser pass from that tool.
- No independent penetration test, privacy audit, standards certification, or competitive benchmark
  has been completed.
- No Linux desktop, macOS, Android or iOS/iPadOS native client project exists. Shared signaling-v3
  capability/vector tests are not a native build, package, runtime, physical-device or store gate.
