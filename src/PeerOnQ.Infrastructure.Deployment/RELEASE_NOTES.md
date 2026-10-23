# PeerOnQ 0.9.81 - private unsigned candidate

This synchronized full-server/client candidate replaces 0.9.80. Directory.Build.props is the sole
product-version source. Windows, Portable Support, Linux, Android and Apple derive 0.9.81;
Android/Apple bundle codes advance to 9081. Older artifacts retain their original bytes/version.

## Fixed in 0.9.81 (install on every client)

- Multiple monitors: the sharer publishes its display list right after the offer, before the
  viewer window exists, and that list was dropped. The viewer now keeps it, so the monitor
  picker, the arrows and "All displays" appear on every multi-monitor host.
- First picture: the host can finish its half of the post-quantum handshake first and send the
  first frame before the viewer can authenticate it; that frame was dropped, and a static remote
  desktop then sent nothing new until something changed. The viewer now asks for a key frame as
  soon as it can decrypt and again when its window opens; the host re-emits its unchanged screen
  for it. Frames decoded before the window opened are kept and shown.
- Viewer rendering: the WriteableBitmap presenter is the default again. The Direct3D swap chain
  was verified only on single-GPU desktops; it is opt-in with `PEERONQ_VIEWER_RENDERER=gpu`.
- Quality: the adaptive ladder no longer drops to a quarter-scale (480x270) desktop; half scale
  is the floor. Late frames whose viewer renders promptly lower the frame rate, not resolution.
  Field logs showed 0.9.80 falling from 1080p to quarter scale within 15 s.
- Diagnostics: both sides log a media summary every 10 s (codec, size, frame rates, bitrate,
  RTT/loss/jitter, path, repairs) and every quality change with its policy reason.

## Fixed in 0.9.80

- 0.9.79 viewers crashed as soon as a session opened the viewer window (WER APPCRASH
  0xc000027b, stowed 0x802B000A): the host showed "connected" while the viewer showed nothing.
  The GPU swap chain panel set a Background property that WinUI rejects only at runtime. The
  viewer now opens, renders and keeps all scale modes with the GPU swap chain and the bitmap
  fallback; a real-window runtime gate now runs against the packaged client.
- Frames replaced while ICE and the hybrid post-quantum session negotiate no longer cause a
  quality downgrade (30 to 20 fps) at connection.
- Do not install 0.9.79 or 0.9.80 clients. Replace them with 0.9.81 on both the viewing and the shared
  computer; the server upgrade alone does not update installed clients.

## Client changes since 0.9.78 (first built as 0.9.79)

These are client behavior changes, not a version-only rebuild. Both peers must run 0.9.81 for
the negotiated features; an older peer keeps its previous behavior with this release.

- 4K on 4K displays: a 4K-class source streams native 4K when the viewer has a monitor with a
  short edge of at least 1440 and hardware H.264 is usable; every other display streams a
  1080-pixel short edge. Congestion gives up 4K before frame rate. 4K stays at 30 fps.
- 1080p at 60 fps: Automatic quality may climb to a 60 fps rung (9 Mbps) only on a healthy link
  while hardware H.264 encodes a standard-tier source. Sessions start at 30 fps; software VP8,
  a 4K source or a lost hardware encoder stay at 30 fps.
- Freeze recovery: transient capture failures and graphics device loss restart capture instead of
  ending the session; hardware encoder failures retry up to three times before VP8; the viewer
  requests a key frame on RTP sequence gaps; the safety-net GOP is 5 s.
- GPU capture conversion: scaling and BT.601 NV12 conversion run on the GPU (1.4-4.1 ms instead
  of 38-51 ms CPU box scaling).
- RTP pacing: large frames (4K key frames) are spread at 2.5x the target bitrate after a
  16-packet burst, never beyond a 20 ms per-frame deadline, instead of one line-rate burst that
  overflowed router/socket queues and forced repeated key frames.
- NACK repair: a viewer that negotiates `a=x-peeronq-video-rtx:1` reorders packets, requests each
  missing one by RTCP generic NACK and receives an in-band retransmission (same SSRC, fresh
  sequence number, SRTP replay-safe, 35% bitrate budget). An unrepaired frame is skipped with a
  key-frame request. Older peers keep SIPSorcery's framer; its reassembly limit for older senders
  rises from 1 MiB to 8 MiB so large 4K key frames are no longer dropped.
- GPU viewer: the Windows viewer presents through a Direct3D 11 SwapChainPanel swap chain off the
  UI thread and keeps all four scale modes and pointer mapping. Any device/interop failure, or
  `PEERONQ_VIEWER_RENDERER=bitmap`, uses the previous WriteableBitmap path.
- All displays: a multi-monitor host offers a combined view; viewers step through displays with
  toolbar arrows or the local Ctrl+Alt+Shift+Left/Right shortcut (never forwarded).
- Two-way voice: Opus 48 kHz mono via managed Concentus 2.2.2 with Windows communications-mode
  echo cancellation. Consent is local on each device; nothing is captured or played until that
  device's user enables Voice. Voice uses its own lossy hybrid-PQ record context.
- Files prefer the direct P2P lane, pinned per transfer; the encrypted relay is used only when no
  direct path exists. Bulk traffic yields to measured queueing delay so input and video stay first.
- The hybrid post-quantum session (ML-KEM-768 + X25519, ML-DSA-65 + Ed25519, AES-256-GCM per
  direction and channel) is unchanged and now also protects voice records.

## Server, website, Portal and Admin

No server, Cloud, Signaling, Presence, Portal or Admin behavior change. Media stays peer-to-peer
after setup; servers handle presence, signaling and the existing TURN relay fallback. Images are
rebuilt with this synchronized release so offered downloads select 0.9.81. Server installation
updates offered downloads; it does not automatically replace already-installed clients.

New dependency: Concentus 2.2.2 (Opus BSD-3-Clause notice ships with release artifacts; its
optional native probe is disabled). The maintained PeerOnQ.SIPSorcery 10.0.17.6 core is unchanged.

## Artifacts and validation boundary

Windows x64 and ARM64 installers use unsigned-public-pilot classification. Portable Support
x64/ARM64 use unsigned-development and do not install the optional service broker. Linux remains
a private unsigned attended viewer/controller preview. Android/Apple receive derived source
versions only; no physical-device or distribution approval is implied.

Fresh validation for this patch: full .NET suite with the hardware video gate 1,416 total / 1,388
passed / 0 failed / 28 environment skips. A new production-configuration end-to-end test (real
Windows.Graphics.Capture, the app's media engine, real signaling) delivered 1080p frames with
hardware H.264 and with VP8 and retained the host's four display entries for the viewer; a
static-desktop test proved that a viewer key-frame request produces a fresh picture. The exact
packaged x64 client opened the real viewer window and sharing indicator with the bitmap default
and the opt-in GPU swap chain, rendered visible frames, cycled all scale modes and exited cleanly.
Public web tests 118/118, typecheck and lint, the native UI gate and five source release contracts
passed. All endpoints shared one machine: this is not physical two-device, hybrid-GPU laptop,
physical multi-monitor, physical-audio or WAN evidence.

The Windows pair was built from commit 5019875 with 517 frozen client/installer inputs and zero
drift. It passed full extracted-payload, ICE03 and the unchanged 96 MiB size gate with zero
warnings/errors and carries `ThirdParty/Concentus/LICENSE` plus the ten maintained SIPSorcery
notices. Exact embedded inputs and the Portable Support pair:

| Artifact | Bytes | SHA256 |
| --- | ---: | --- |
| x64 MSI | 85,499,904 | 0a9931501b2a48c5a12c4af0af47a4ad9dfc399a6e965793e576724741605971 |
| ARM64 MSI | 77,983,744 | cb008c5b2a50614732a7a3b024ca3ce3a8cc1e4fe77f81573076e1e823fee981 |
| x64 Portable Support ZIP | 101,597,274 | 94f6b9f9998121ed7afb9f4e389be8833522318d4e117912b3aa89cf8e1db50c |
| ARM64 Portable Support ZIP | 97,550,119 | 23a08b59743cb267de240c052c4f37a484d192a975edd83f1dfc229ab02801de |

The Linux preview is not embedded in this bundle; its status is recorded in the accompanying
VALIDATION.json. Post-build acceptance and the whole-bundle hash are external to this immutable
payload; require the explicit current pass in VALIDATION.json.

All existing security/compatibility fixes are retained: maintained PeerOnQ.SIPSorcery 10.0.17.6
provenance/notices, bounded authenticated transport, file integrity, protected optional LocalService
broker, default-off attended audio, Node 24 Docker pins and nested CR/download-header checks.
No encryption, authorization, licensing, quota, TLS, hash, package-size or release gate is relaxed.

## Configuration and coordinated rollback

Reuse /etc/peeronq/peeronq.env and the existing /opt/peeronq installation. Preserve SMTP settings,
TLS, customer registration/MFA, operator restrictions, data volumes and forward migrations.
An existing installation must not use --bootstrap.

No new migration is introduced after 0.9.77. The additive default-false system-audio policy column
remains. Cloud/Signaling must both understand audio flag 64 before enabling that policy. A rollback
to pre-audio parsers requires disabling the policy and refreshing device attestations, or waiting
their lifetime plus verifier clock skew; older parsers reject flag 64 even for non-audio sessions.
Keep the additive column/data and do not run a destructive database downgrade.

## Operator CLI

Copy the exact .run and trusted matching checksum sidecar to /home/peeronq. In Bash:

~~~bash
(
set -eu
cd /home/peeronq
sha256sum --check peeronq-server-0.9.81.run.sha256
chmod 700 peeronq-server-0.9.81.run
sudo ./peeronq-server-0.9.81.run --env-file /etc/peeronq/peeronq.env --dry-run
sudo ./peeronq-server-0.9.81.run --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.81.run --status --env-file /etc/peeronq/peeronq.env
)
~~~

The checksum proves integrity, not publisher authenticity. This private candidate has no approved
detached server signature or Authenticode/timestamp approval. It is not ready for production or
public redistribution. Restricted dependency/license review (including Concentus/Opus), native
source/build/security provenance and exact-artifact SBOM, clean-machine MSI/SCM/LocalService
lifecycle, physical ARM64, two-endpoint media/voice/multi-monitor/WAN and non-Windows
capability/publication gates remain open. No production installation was performed while
preparing the bundle.

For an already installed candidate, after the applicable compatibility preconditions above,
restore the retained previously verified release through the existing installer rollback:

~~~bash
sudo ./peeronq-server-0.9.81.run --rollback --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.81.run --status --env-file /etc/peeronq/peeronq.env
~~~

Rollback does not downgrade installed clients. Retain the earlier release files, data and protected
configuration; preserve immutable 0.9.78-0.9.80 artifacts/evidence for comparison and recovery.
