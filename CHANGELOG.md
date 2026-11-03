# Changelog

All notable changes to PeerOnQ are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and version numbers follow
`Directory.Build.props`. The server and all clients share one version and should be upgraded
together. Detailed notes for each release, including upgrade steps, are in the
[release notes](src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md).

## 0.9.96 - 2026-10-28

### Fixed

- The remote picture no longer turns blurry while you work. When the remote computer could not
  encode every frame at 60 fps (software video on a laptop), adaptive quality lowered the bitrate
  again and again - down to about 2 Mbps within seconds - which blurred all text without making the
  encoder any faster. It now keeps the full-quality picture and lets the encoder skip frames it cannot
  finish; a lower bitrate is used only for network problems (packet loss), where it actually helps.
- On the same local network (direct LAN connection) the picture gets twice the bitrate, up to
  20 Mbps, so moving text and the periodic refresh frame stay sharp. Internet and relay sessions are
  unchanged, and network loss still lowers the bitrate.
- Computers without a hardware video encoder (CPU-only video) encode each frame about 15% faster on
  small CPUs (one encoder thread) and about 5% faster on larger ones, with identical picture quality:
  unchanged parts of the desktop are skipped cheaply and the video data is split so it can be packed
  and decoded on several threads.

### Changed

- GPU rendering in the viewer now scales the remote screen on the graphics card's video scaler into a
  pixel-aligned picture, and shows it pixel-for-pixel when the window is within 2% of the remote size.
  For the sharpest text when both screens have the same resolution, use Fullscreen (1:1).
- The "Send Ctrl+Alt+Del" tooltip now says plainly that the Windows sign-in screen it opens appears on
  the remote device only: PeerOnQ cannot show it or type on it yet.

## 0.9.95 - 2026-10-28

### Fixed

- The remote screen no longer freezes for good when the remote computer is locked. Previously, while
  the remote was on the Windows lock / sign-in screen the sharer could not read the screen, used up a
  small fixed number of capture restarts, and then stopped for the rest of the session - so after you
  unlocked the remote the viewer stayed connected but frozen with no control. Capture now keeps
  retrying at a steady pace for as long as the screen is unreadable and resumes by itself once the
  remote is unlocked; a genuinely lost graphics device still gives up as before.

## 0.9.94 - 2026-10-28

### Added

- Viewer "..." menu now has a "GPU rendering" switch. On most laptops two graphics chips make the
  viewer fall back to the standard (CPU) renderer, which cannot keep up at 60 fps and upscales the
  picture with blur; turning GPU rendering on draws the remote screen on the graphics card instead,
  for a smoother frame rate and sharper scaling. The choice is remembered per user, and any graphics
  failure still falls back to the standard renderer automatically.
- The session status line now ends with "GPU" or "CPU" so you can see which renderer is active.

## 0.9.93 - 2026-10-27

### Added

- Remote Ctrl+Alt+Del: with Full Control, the viewer "..." menu has a "Send Ctrl+Alt+Del" item that
  raises the sign-in / lock screen on the remote device, so you can reach it over the session. It
  needs the optional Windows session-broker feature installed on that device, and you still sign in
  with that device's own password or PIN — it never bypasses sign-in.

### Changed

- The optional Windows session broker now runs as LocalSystem (was LocalService) and enables the
  Windows "software Secure Attention Sequence" policy for services, so it can raise Ctrl+Alt+Del for
  a Full-Control viewer. It is still an optional feature and is removed cleanly on uninstall.

## 0.9.92 - 2026-10-27

### Fixed

- Sessions stayed near 30 fps. Windows power-throttles a background process, and the sharer's
  PeerOnQ window is usually in the background; the throttled hardware H.264 encoder took about
  30 ms per frame. PeerOnQ now opts out of power throttling, and the same encoder takes about 8 ms
  per 1080p frame and 12 ms per 1440p frame.

### Changed

- 60 fps minimum: every quality profile runs at 60 fps, and adaptive quality lowers the bitrate (and
  the resolution where the profile allows it) instead of the frame rate. A sharer without a hardware
  encoder on a small CPU keeps 60 fps by capturing a large desktop at half scale.

### Added

- Sharper still desktops: when the screen stops changing, the last frame is re-encoded for 2 to 4
  seconds (longer on a slower link) so text becomes crisp after scrolling.

## 0.9.91 - 2026-10-27

### Changed

- Hardware H.264 now holds its bitrate target. Some Windows encoders ignored a bitrate or frame-rate
  change mid-stream and sent up to three times the target, which caused the packet loss behind low
  picture quality. A quality change now starts a fresh encoder, prepared in the background and switched
  in at a key frame, so the picture does not stall.
- Adaptive quality steps down one level per network loss report instead of several, and a display
  switch keeps a level that the network set instead of starting high again.
- Peer-to-peer: when the router supports UPnP IGD or NAT-PMP, the Windows client asks it to forward the
  session's UDP ports for an hour (renewed while the session runs, removed when it ends) and offers that
  address to the peer. Sessions with a peer behind a strict NAT can then connect directly instead of
  through the relay. Routers behind another NAT or carrier-grade NAT are detected and skipped.

### Added

- Windows N and KN: setup adds Microsoft's Media Feature Pack when Media Foundation is missing, so
  hardware video works after the next restart. Until then, and if Windows cannot install it, PeerOnQ
  uses software video as before.

## 0.9.90 - 2026-10-25

### Fixed

- Upgrading the Windows client from 0.9.88 to 0.9.89 left `SIPSorcery.dll` and
  `SIPSorceryMedia.Abstractions.dll` missing, so sharing failed and the session ended with
  `not_session_participant`. The installer now always replaces both files together with the
  application. Fresh 0.9.89 installs were not affected; an affected 0.9.89 installation is repaired
  by installing 0.9.90 (or by Repair in Installed apps).

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
