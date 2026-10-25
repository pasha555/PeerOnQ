# Changelog

All notable changes to PeerOnQ are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and version numbers follow
`Directory.Build.props`. The server and all clients share one version and should be upgraded
together. Detailed notes for each release, including upgrade steps, are in the
[release notes](src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md).

## 0.9.89 - 2026-10-25

### Changed

- The WebRTC stack is now a maintained derivative of SIPSorcery 8.0.14, the last release published
  under plain BSD-3-Clause, with SIPSorceryMedia.Abstractions 8.0.10 and SIPSorceryMedia.Encoders 8.0.7.
  Later SIPSorcery releases add a non-OSI use restriction to their license. Every shipped component is
  now under an OSI-approved license.
- `vpxmd.dll` (libvpx 1.17.0) is built from pinned source in CI for x64 and ARM64 instead of using the
  prebuilt binary from SIPSorceryMedia.Encoders; libvpx's LICENSE and PATENTS ship with the app.
- The Windows installers no longer carry the unused Windows App SDK AI, ML (ONNX Runtime, DirectML) and
  Widgets binaries (about 44 MB smaller).
- Tag builds publish the installers as a GitHub pre-release with SHA-256 checksums until code signing
  through SignPath Foundation is in place.

### Added

- TURN over TLS (`turns:`, port 5349) in the maintained stack, with full certificate validation
  against the TURN host name and the system trust store.

### Security

- A malformed UDP, STUN, RTP or RTCP packet is dropped instead of ending the media session, and an ICMP
  "connection reset" no longer stops receiving (GHSA-28gm-jrmw-xx93 class).
- SCTP: a zero-length chunk can no longer loop the parser, oversized SACK reports are rejected, and the
  data-channel receive loop survives malformed packets (GHSA-qmvg-569h-hqrh, GHSA-jwjp-4649-v8jp).
- DTLS handshakes run on their own thread instead of holding thread-pool workers
  (GHSA-mc26-5x96-8x52); an oversized datagram no longer aborts the handshake, and closing a session
  ends a pending handshake at once.
- SRTP/SRTCP replay protection and rollover-counter handling follow RFC 3711: unauthenticated packets
  can no longer change receiver state, and packets after a sequence-number rollover decrypt correctly.
- The ICE-over-TCP receiver no longer closes on a single malformed packet (GHSA-mwf8-6m4x-pgmm class).

## 0.9.88 - 2026-10-25

### Added

- Frame rate follows the viewer's display. Automatic quality runs at 60 fps with a 10 Mbps ceiling
  and can rise to the display's refresh rate, up to 120 fps, on a healthy link with hardware H.264.
- Either side can turn on its microphone during a session, and both sides show sound, microphone
  and talking indicators.
- Network Doctor sends real STUN and TURN (UDP, TCP and TLS) probes, measures latency without DNS
  time, and reports results in plain language.
- A Windows release pipeline that signs installers through SignPath, with a published
  [code signing policy](CODE_SIGNING_POLICY.md). The signing program application is pending.

### Changed

- Sound is automatic: system sound starts when the session connects and the host can stop it.
  Where an organization policy forbids audio, sound is removed from the request instead of the
  session being refused.
- The Connect panel no longer shows the support-invitation switch or the system-sound request;
  `peeronq://support` links still work.
- A device that is already sharing its screen declines new sharing requests as busy.
- Session heartbeats start only after the cloud has accepted the session as connected.
- VP8 safety-net key frames are sent every 5 seconds and their size is bounded, which removes
  periodic bursts on Wi-Fi links.

### Fixed

- Sharing could freeze, and closing the session could hang, when a VP8 encoder call stalled. Each
  encoder now runs on its own thread with a stall watchdog, and every teardown step is time-bounded.
- Windows N editions without the Media Feature Pack share and view through software video.
- A resumed session with connected media no longer restarts ICE, and remote input keeps working
  after the sharer restarts ICE.
- Log messages for codec negotiation, request timeouts and software rendering are consistent.

## 0.9.87 - 2026-10-24

### Fixed

- Sessions can be resumed after the signaling service restarts.
- Closing a session releases its TURN connections.
- Quality changes report their reason, and jitter is measured against the real frame interval.
- A device can no longer connect to itself.
- Server upgrades keep the previous release online until the new one is healthy.

## 0.9.86 - 2026-10-24

### Fixed

- A signaling outage no longer ends a session whose peer-to-peer media is still connected.
- Quality is lowered only after viewer-side signals persist.
- Session telemetry recovers events the cloud has not yet recorded.
- Faster server redeployment.

## 0.9.85 - 2026-10-24

### Fixed

- An unanswered permission dialog could crash the app.
- Only one permission dialog is shown at a time.
- Session telemetry uses the correct device identifiers.
- The Direct3D 11 viewer is the default on machines with one hardware GPU.
- Messages to a peer that went offline are held and retried.

## 0.9.84 - 2026-10-24

### Fixed

- Presence reconnects, stalled viewers, ICE candidates from virtual network adapters, the quality
  ladder and diagnostics.

## 0.9.83 - 2026-10-24

### Added

- Each remote monitor can be shown in its own window.

## 0.9.82 - 2026-10-24

### Fixed

- Presence reconnect loop, heartbeat limits and the TURN relay address.

## 0.9.81 - 2026-10-24

### Fixed

- Multiple-monitor selection, time to first picture, the quality floor and log detail.

Earlier versions are described in the [release notes](src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md).
