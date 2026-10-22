# PeerOnQ 0.9.78 - private unsigned candidate

This synchronized full-server/client candidate includes the automatic website download selection
and the Windows dashboard simplification requested after 0.9.77. Directory.Build.props is the sole
product-version source. Windows, Portable Support, Linux, Android and Apple derive 0.9.78;
Android/Apple bundle codes advance to 9078. Older artifacts retain their original bytes/version.

## Changes since 0.9.77

- Windows Dashboard removes both Display quality and Target resolution dropdowns. Startup directly
  applies Automatic adaptive quality with an explicit P1080 target. This is a client behavior
  change, not merely a version rebuild. Sound requests, consent and session permissions retain
  their existing controls.
- The existing 1080-pixel short-edge capture policy retains aspect ratio and portrait orientation,
  never upscales a smaller source and independently caps the Windows capture path. A 3840x2160
  display sends 1920x1080; ultrawide long edges can exceed 1920. 4K mode remains deferred.
- The website automatically recommends x64 for Firefox/legacy Windows 64-bit compatibility
  information. Authoritative ARM64 Client Hints override the recommendation. Explicit ARM64
  survives missing/failed hints; a local 1.5-second deadline avoids indefinite checking. Manual
  selection remains an optional override and wins over delayed hints. Unknown/unsupported
  architectures remain unavailable. Reduced browser identities cannot prove every native CPU:
  the legacy x64 fallback is explicitly a recommendation, not verified native-CPU detection.
- Website-only builds now explicitly enable both server-owned Windows architecture links and
  clear inherited embedded ARM64 URLs. Their signature and client-authority boundaries remain.
  This full bundle supplies both exact checksum-verified installers and refreshed website metadata.
- Cloud, customer Portal and Admin have no additional behavior change in this patch. Their images
  are rebuilt with this same synchronized release. Server installation updates offered downloads;
  it does not automatically replace already-installed clients.

## Artifacts and validation boundary

Windows x64 and ARM64 installers use unsigned-public-pilot classification. Portable Support
x64/ARM64 use unsigned-development and do not install the optional service broker. Linux remains
a private unsigned attended viewer/controller preview; it has no hosting, file transfer,
clipboard, audio or unattended capability. Android/Apple receive derived source versions only;
no physical-device or distribution approval is implied.

Fresh source checks for this patch: public web tests 118/118, zero failed/skipped, TypeScript and
ESLint passed. The native UI contract passed after removing both selectors; the strict WinUI x64
source build passed with zero warnings/errors. Fresh installer payload/size/version/core checks,
Portable/Linux checks and extracted-bundle runtime acceptance are recorded with exact artifacts
and hashes in the accompanying VALIDATION.json. Require its explicit current pass.
Prior 0.9.77 media/SCTP/hardware measurements remain historical evidence, not fresh 0.9.78 runs.
Post-build acceptance and the whole-bundle hash are external to this immutable payload.

The fresh Windows pair passed full extracted-payload/ICE03 checks with zero warnings/errors;
both remain below 96 MiB. Packaging inputs: 56, zero drift. Exact embedded inputs:

| Architecture | Bytes | SHA256 |
| --- | ---: | --- |
| x64 | 85,442,560 | a675bde4f9875d9b2dcd141bdcf3aa0a803ee58adb119caa2f1de25fd1c4d115 |
| ARM64 | 77,930,496 | e80a252d2b7d9c9db1af6d1996b90291163b28a3cd8b91940b7fce17aea09e5b |

Fresh Linux validation passed 48/48 (22+14+12), zero failed/skipped, 194 source inputs/zero drift,
archive/core/notices and actual normal-close/startup-cancel checks. Its private preview archive is
54,103,727 bytes, SHA256 dabe9707ee7db52d064aa67d10ce234377b46751b2f849d40a05955793ac2c7c.
These local/container checks do not close the physical-device/publication gates below.

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
sha256sum --check peeronq-server-0.9.78.run.sha256
chmod 700 peeronq-server-0.9.78.run
sudo ./peeronq-server-0.9.78.run --env-file /etc/peeronq/peeronq.env --dry-run
sudo ./peeronq-server-0.9.78.run --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.78.run --status --env-file /etc/peeronq/peeronq.env
)
~~~

The checksum proves integrity, not publisher authenticity. This private candidate has no approved
detached server signature or Authenticode/timestamp approval. It is not ready for production or
public redistribution. Restricted dependency/license review, native source/build/security
provenance and exact-artifact SBOM, clean-machine MSI/SCM/LocalService lifecycle, physical ARM64,
two-endpoint media/audio/WAN and non-Windows capability/publication gates remain open.
No production installation was performed while preparing the bundle.

For an already installed candidate, after the applicable compatibility preconditions above,
restore the retained previously verified release through the existing installer rollback:

~~~bash
sudo ./peeronq-server-0.9.78.run --rollback --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.78.run --status --env-file /etc/peeronq/peeronq.env
~~~

Rollback does not downgrade installed clients. Retain the earlier release files, data and protected
configuration; preserve immutable 0.9.77 artifacts/evidence for comparison and recovery.
