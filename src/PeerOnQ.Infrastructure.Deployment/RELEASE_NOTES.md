# PeerOnQ 0.9.79 - private unsigned candidate

This synchronized full-server/client candidate carries the Windows media, presentation and
collaboration work requested after 0.9.78. Directory.Build.props is the sole product-version
source. Windows, Portable Support, Linux, Android and Apple derive 0.9.79; Android/Apple bundle
codes advance to 9079. Older artifacts retain their original bytes/version.

## Client changes since 0.9.78

These are client behavior changes, not a version-only rebuild. Both peers must run 0.9.79 for
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
rebuilt with this synchronized release so offered downloads select 0.9.79. Server installation
updates offered downloads; it does not automatically replace already-installed clients.

New dependency: Concentus 2.2.2 (Opus BSD-3-Clause notice ships with release artifacts; its
optional native probe is disabled). The maintained PeerOnQ.SIPSorcery 10.0.17.6 core is unchanged.

## Artifacts and validation boundary

Windows x64 and ARM64 installers use unsigned-public-pilot classification. Portable Support
x64/ARM64 use unsigned-development and do not install the optional service broker. Linux remains
a private unsigned attended viewer/controller preview. Android/Apple receive derived source
versions only; no physical-device or distribution approval is implied.

Fresh validation for this patch: .NET solution build with zero warnings/errors; full suite with
the hardware video gate enabled 1,411 total / 1,383 passed / 0 failed / 28 environment skips
(EndToEnd 190/191 including hardware H.264 sessions through the pacer and NACK assembler, Media
365/366). Real loopback NACK repair recovered every dropped packet (5/5, 7/7, 8/8). Public web
tests 118/118, typecheck and lint passed; the native UI gate and five source release contracts
passed. All endpoints shared one machine: this is not physical two-device, multi-monitor,
physical-audio or WAN evidence.

The Windows pair was built from commit 9dd6c37 with 514 frozen client/installer inputs and zero
drift. It passed full extracted-payload, ICE03 and the unchanged 96 MiB size gate with zero
warnings/errors, and carries `ThirdParty/Concentus/LICENSE` plus the ten maintained SIPSorcery
notices. An earlier pair from f8f7a19 lacked the Concentus notice; it was superseded and never
published. Exact embedded inputs and the Portable Support pair:

| Artifact | Bytes | SHA256 |
| --- | ---: | --- |
| x64 MSI | 85,483,520 | e3c9fbef7200582d19aa3542898e3c1280d47e205287528bfa6a58da71c6d230 |
| ARM64 MSI | 77,975,552 | 84b65db00ee054042c2d620a309637c047a4dd643600ec5e33c47b3935eee5dc |
| x64 Portable Support ZIP | 101,591,064 | 6bda779f1ca86f2a9eb356b91962a5d52a40e5414eb961a7c4f533ebc1e9888c |
| ARM64 Portable Support ZIP | 97,543,917 | 91d2e14a13ced5e0f27718158734ec16237a90191ae649ab61304a152adbf7be |

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
sha256sum --check peeronq-server-0.9.79.run.sha256
chmod 700 peeronq-server-0.9.79.run
sudo ./peeronq-server-0.9.79.run --env-file /etc/peeronq/peeronq.env --dry-run
sudo ./peeronq-server-0.9.79.run --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.79.run --status --env-file /etc/peeronq/peeronq.env
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
sudo ./peeronq-server-0.9.79.run --rollback --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.79.run --status --env-file /etc/peeronq/peeronq.env
~~~

Rollback does not downgrade installed clients. Retain the earlier release files, data and protected
configuration; preserve immutable 0.9.78 artifacts/evidence for comparison and recovery.
