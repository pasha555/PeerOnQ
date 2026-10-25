# Third-party license audit: media/security scope

Initial audit: 2026-08-18. Archive/source revalidation: 2026-09-27; source remediation: 2026-10-18;
license remediation: 2026-10-25. The release SBOM remains authoritative for a concrete artifact.
Historical archive hashes below describe the reviewed versions and must not be reused as evidence for
a new release.

## 2026-10-25 license remediation (current)

Every shipped SIPSorcery component is now under an OSI-approved license:

| Component | Version | Upstream commit | License file (upstream git, release commit) |
| --- | --- | --- | --- |
| SIPSorcery (maintained derivative `PeerOnQ.SIPSorcery` 8.0.14.1) | 8.0.14, released 2025-05-08 | `08d3a0271dded6aaebc3183f963bc820b099c552` | `LICENSE.md` SHA-256 `522136acbe9c1d7978dfe3b2e8590815b46e93ca4594b26d720b8fa38f7a382f`: BSD-3-Clause plus an informational DTLS & SRTP notice; no use restriction |
| SIPSorceryMedia.Abstractions | 8.0.10, released 2025-03-11 | `8a27b7f8ad4fcc9ab0c64e7c22e77d05d811c09c` | `LICENSE`: BSD-3-Clause |
| SIPSorceryMedia.Encoders | 8.0.7, released 2025-01-04 | `a12d06a05c4e4e9d588b7597e4ef1c1e82f6e445` | `LICENSE`: BSD-3-Clause (managed bindings only; the package DLL is not shipped) |
| libvpx (`vpxmd.dll`) | 1.17.0 | `6df3ec34557879fff673706f4a1d9fbd0f3a6f0e` | `LICENSE` SHA-256 `8267348d5af1262c11d1a08de2f5afc77457755f1ac658627dd9acf71011d615`: BSD-3-Clause; `PATENTS` grant shipped |
| Portable.BouncyCastle | 1.9.0 | tag `release-1.9.0` of bcgit/bc-csharp | `crypto/License.html`: Bouncy Castle License (MIT text) |

How the versions were chosen: SIPSorcery's `LICENSE.md` history shows the "Additional Restriction:
Boycott, Divestment, Sanctions" section added on 2025-05-18 (commit `004f72024151`). The `v8.0.14` and
`v8.0.15-pre` tags still have the earlier file; `v8.0.22` and every 10.x release have the restriction.
SIPSorceryMedia.Abstractions added the same section between `v8.0.10` and `v8.0.12`. SIPSorceryMedia.Encoders
has never changed its BSD-3-Clause `LICENSE`, but 10.0.4 requires Abstractions 8.0.12 or later, so 8.0.7
is used. NuGet packages up to SIPSorcery 10.0.12 contain no license file, only the `BSD-3-Clause`
expression, so the git history at the release commits is the evidence. No restricted license text was
removed or edited: PeerOnQ only uses releases that were published without the restriction.

The 8.0.14 license file's DTLS & SRTP notice says that parts of that code derive from Jitsi SRTP
(Apache-2.0), Minisip (LGPL) and RestComm media-core (AGPL-3.0), and that users should assume GPL-style
obligations such as source availability. These are OSI-approved licenses. PeerOnQ publishes the pinned
upstream archive and every patch (the complete corresponding source of the shipped assembly), and ships
the license file with every application.

Upstream advisories that affect 8.0.14 (GHSA-28gm-jrmw-xx93, GHSA-qmvg-569h-hqrh, GHSA-jwjp-4649-v8jp,
GHSA-mc26-5x96-8x52 and the IceTcpReceiver class GHSA-mwf8-6m4x-pgmm) are fixed by PeerOnQ patches
written from the published problem descriptions; `eng/sipsorcery/upstream.json` maps each advisory to its
patch. The SRTP AES-GCM advisory GHSA-vgjc-mh6q-fwcf does not apply: 8.0.14 has no AES-GCM SRTP. The
SIP-only advisories do not apply because PeerOnQ does not use SIPSorcery's SIP channels. 8.0.14 also
lacked TURN over TLS, which PeerOnQ implemented in its own patch.

## 2026-10-18 remediation (superseded by the 2026-10-25 license remediation)

`Directory.Packages.props` now pins maintained PeerOnQ.SIPSorcery 10.0.17.6 (upstream 10.0.17),
Abstractions 10.0.17 and BouncyCastle 2.7.0.
The [official 10.0.17 source](https://github.com/sipsorcery-org/sipsorcery/tree/08c2ed0ad72c423ca06950dee2e842488a219884)
catches each TURN/TLS packet callback failure inside `StartTlsReadLoop`. PeerOnQ's regression invokes
that actual loop with three framed packets and a first-packet exception and verifies all three
dispatches. This source/test evidence is separate from an advisory page's patched-version field.
The embedded additional license restrictions remain, as do the WiX decision and native libvpx
source/build/security/notice gates. A dependency upgrade does not approve redistribution.

The official release retains the 25 ms empty-queue DTLS poll. A TEMP-only controlled comparison
used identical common test files and changed only the original-source versus patched SIPSorcery
DLL/PDB. The unchanged 35 ms gate passed 1/6 original cases and 6/6 patched cases; the ranges of
twelve scenario p95 values were 34.073–38.931 ms and 17.018–18.485 ms respectively, not pooled
percentiles. Every patched case also completed and verified its 1 MiB transfer and returned from
disposal. These are synthetic same-host input sinks, not physical Windows injection/WAN evidence.

The maintainers approved maintenance of the derivative on 2026-10-18. Source now selects
`PeerOnQ.SIPSorcery` 10.0.17.6, built by the pinned SDK-only builder/local-feed flow documented in
[DEPENDENCIES](../DEPENDENCIES.md). This decision satisfies the maintenance-decision requirement
below; it does not waive license, security, physical acceptance or release gates. Final SDK402
package-consumer evidence must identify its new binary hash rather than claim the SDK303 TEMP
comparison DLL was shipped. The second pinned patch clears reported duplicate history per SACK,
following [RFC 9260 section 3.3.4](https://www.rfc-editor.org/rfc/rfc9260.html#section-3.3.4).
The third patch widens the gap-loop counter to prevent ushort wraparound. The fourth/fifth patches
correct bounded sender/receiver flow control; the sixth serializes UDP receive-buffer admission
and close/re-arm ownership. The seventh initializes RTCP transit from the first packet, avoiding
arbitrary clock phase in jitter. All seven raw patches and five final changed source files retain exact
hash provenance. Normal SCTP/UDP recovery is tested; no hostile-boundary runtime probe was executed.
These changes retain close/cancellation semantics, public API, cryptography and wire framing. Original upstream advisory identity
is audited separately because the derivative package ID cannot inherit advisory matching.

## Historical package/license evidence — 2026-09-27

The production pnpm license inventory, repository vulnerability scan and secret scan passed in
the historical 2026-08-18 audit. Those results are not rerun results or current security clearance.
Neither that audit nor this revalidation establishes an approved exact-artifact SPDX/native
inventory: that gate requires a concrete MSI plus matching payload and resolution of the findings.

| Component | Version | Package-declared/embedded terms | Current decision |
| --- | --- | --- | --- |
| BouncyCastle.Cryptography | 2.6.2 | MIT | Compatible for current use; retain notice and security review |
| Vortice.Direct3D11 / Vortice.DXGI | 3.8.3 | MIT | Compatible; Microsoft platform/API terms remain separate |
| Serilog | 4.4.0 | Apache-2.0 | Compatible with notice obligations |
| SIPSorceryMedia.Encoders | 10.0.4 | NuSpec `BSD-3-Clause`; package embeds x64/x86 `vpxmd.dll` | Native source/build, security patch status and notice bundle remain unverified |
| SIPSorcery / SIPSorceryMedia.Abstractions | 10.0.15 | Package uses an embedded `LICENSE.md`: BSD-3 text plus an additional field-of-use/geographic restriction | **Distribution blocker:** not plain BSD-3/OSI terms; legal/maintainer resolution or a reviewed replacement is required |
| WiX UI extension binary | 6.0.1 | Embedded Open Source Maintenance Fee Agreement; source is described as MS-RL | Review revenue-generating use of official binaries and payment/exemption; the agreement preserves separate source/redistribution rights |
| Microsoft Windows App SDK / SDK tools | pinned centrally | Microsoft package/platform terms | Redistribution must follow Microsoft runtime terms |

## Exact restored package evidence

All four restored `.nupkg.metadata` files identify `https://api.nuget.org/v3/index.json` as their
source. The license files for SIPSorcery, Abstractions and WiX match their archive entries exactly.
The table records SHA-256 of the inspected archives, not a new signature or reproducible-build
attestation. No package-signature validation or complete vulnerability scan was run by this
documentation revalidation.

| Package | NuSpec repository commit | Restored archive SHA-256 |
| --- | --- | --- |
| SIPSorcery 10.0.15 | `6106ff9c7b0e1f4513f73f90375a2f0592cc208c` | `7de078bf88144feab0d72a8c8521e699ff2d52bb46b3bb13e18098a9b150dd52` |
| SIPSorceryMedia.Abstractions 10.0.15 | `6106ff9c7b0e1f4513f73f90375a2f0592cc208c` | `3130a5a4d94084f674160a1b533240a8dbb8a237e086dd86450211c23a65f725` |
| SIPSorceryMedia.Encoders 10.0.4 | **Not provided** | `c6c80f2eb5268bd93bb7d0f1e176e01c7f0d31fac5e96e499fdab4c91023ebdd` |
| WixToolset.UI.wixext 6.0.1 | `b05d563c634b53f5a3fad2e68aa36b0fa38db617` | `dd0c408c9ccb504c87dbb4e9daa4df3d08b8b194af593da58c51c1ac12a31fc0` |

SIPSorcery and Abstractions contain the same `LICENSE.md` bytes. Section 2 of the
[pinned source license](https://github.com/sipsorcery-org/sipsorcery/blob/6106ff9c7b0e1f4513f73f90375a2f0592cc208c/LICENSE.md)
adds geographic/use restrictions to the BSD text; it is not plain BSD-3-Clause. Its FFmpeg section
is explicitly about SIPSorceryMedia.FFmpeg and is not evidence that the inspected Encoders package
contains FFmpeg. The final complete runtime inventory remains necessary.

WiX's restored agreement matches the [pinned OSMFEULA](https://github.com/wixtoolset/wix/blob/b05d563c634b53f5a3fad2e68aa36b0fa38db617/OSMFEULA.txt).
It applies the fee to revenue-generating use of official binary releases and describes exemptions;
it also preserves underlying OSI-license rights, self-compiled binaries and redistribution subject
to those terms. Record the release owner's applicability/payment/exemption decision instead of
claiming that commercial redistribution is categorically prohibited or automatically approved.
Current [maintainer guidance](https://docs.firegiant.com/wix/osmf/) supplements rather than replaces
the exact 6.0.1 agreement. This review supplies evidence, not qualified legal approval.

Release owner decision (2026-10-25): PeerOnQ is a non-revenue open-source project. It sells no
licenses or subscriptions, and the installers are distributed free of charge, so its use
of the official WiX 6.0.1 binaries is not revenue-generating use and the Open Source Maintenance
Fee does not apply. The WiX UI extension contributes only installer dialog resources to the MSI; it
adds no executable code to the installed application. If PeerOnQ ever becomes revenue-generating,
the fee must be paid or the WiX binaries rebuilt from their MS-RL source before the next release.

## Native libvpx provenance (historical; resolved 2026-10-25)

Since 2026-10-25 PeerOnQ builds `vpxmd.dll` from pinned libvpx 1.17.0 source in CI (see
[DEPENDENCIES](../DEPENDENCIES.md)); the findings below describe the prebuilt binary it replaced.

The Encoders archive contains `build/x64/vpxmd.dll` and `build/x86/vpxmd.dll`, but no separate
libvpx `LICENSE`, `PATENTS`, copyright or notice entry. Both binaries contain the string `v1.9.0`.
They are byte-identical by Git blob identity to the checked-in binaries at Encoders source commit
`f9d98a04452a690a0e295b8482cc653adc997896`; this links the package bytes to that repository, not
to a reproducible libvpx build or its security patch set.

| Native archive entry | SHA-256 |
| --- | --- |
| `build/x64/vpxmd.dll` | `9ef05f9448c23de0043c1b9da7d84fd3bc07d3a7aaeb57ce87ca68a52b8622c7` |
| `build/x86/vpxmd.dll` | `7fd9cc7cd8c98347e92daa4ec76274160731ec9075885a67fbd7ebf93176f1c6` |

The upstream [build steps](https://github.com/sipsorcery-org/SIPSorceryMedia.Encoders/blob/f9d98a04452a690a0e295b8482cc653adc997896/lib/libvpx_build_steps.txt)
clone libvpx without pinning a commit and describe VS16 x64/x86 builds with manual project edits.
A [libvpx notice](https://github.com/sipsorcery-org/SIPSorceryMedia.Encoders/blob/f9d98a04452a690a0e295b8482cc653adc997896/lib/libvpx-LICENSE)
exists upstream but is missing from the package. Archive the exact source revision, patches,
toolchain/build options, architecture-specific hashes, applicable copyright/license and patent
terms with each release; copying a current notice alone does not prove binary provenance.

The official [libvpx 1.13.1 changelog](https://github.com/webmproject/libvpx/blob/v1.13.1/CHANGELOG)
records VP8/VP9 security fixes, including CVE-2023-5217. An embedded `v1.9.0` string does not prove
whether fixes were backported, so the current binaries cannot be cleared on managed NuGet audit
results alone. Security patch verification remains open. Source projects for Linux and Apple do
not establish the provenance of their separately supplied native runtime libraries either.

## Current SIPSorcery security review

Official advisories checked on 2026-09-27 now include the following. An empty patched-version
field means no patched release was listed at review time; it does not establish that no upstream
work exists. Package-level findings and PeerOnQ code-path applicability are separate decisions.

| Official advisory | Affected / patched version | PeerOnQ relevance |
| --- | --- | --- |
| [GHSA-6848-qmp4-652w](https://github.com/sipsorcery-org/sipsorcery/security/advisories/GHSA-6848-qmp4-652w), high, published 2026-09-20 | 10.0.9 through 10.0.16; no patched version listed | Malformed relayed traffic can terminate the TURN-over-TLS ICE receive loop. `WebRtcMediaSession` permits `turns:` servers, so this is a relevant release security blocker; no exploitation was tested here. |
| [GHSA-j5j8-rhcm-7fp9](https://github.com/sipsorcery-org/sipsorcery/security/advisories/GHSA-j5j8-rhcm-7fp9), medium, published 2026-09-20 | Through 10.0.16; no patched version listed | SIP-client WebSocket receive-loop denial of service. No `SIPClientWebSocketChannel` use was found in the inspected Media/Transport sources; this is not PeerOnQ's `ClientWebSocket` signaling path. Record applicability rather than suppressing the package finding globally. |
| [GHSA-h6x7-h3p4-ff4p](https://github.com/sipsorcery-org/sipsorcery/security/advisories/GHSA-h6x7-h3p4-ff4p), high | Through 10.0.15; fixed in 10.0.16 | SIPTLSChannel certificate-validation default. No use of that class was found in the inspected Media/Transport sources; this does not authorize weakening any PeerOnQ certificate validation. |

Downgrading for different license metadata is not a safe automatic remedy: upstream records
[SCTP SACK parsing](https://github.com/sipsorcery-org/sipsorcery/security/advisories/GHSA-jwjp-4649-v8jp),
[ICE-over-TCP](https://github.com/sipsorcery-org/sipsorcery/security/advisories/GHSA-mwf8-6m4x-pgmm) and
[TURN-server](https://github.com/sipsorcery-org/sipsorcery/security/advisories/GHSA-pfvm-w89x-94jw)
denial-of-service findings in versions through 10.0.13, patched in 10.0.14. PeerOnQ pins 10.0.15,
but that does not clear the newer findings above. Retain fresh package/native vulnerability review
and scoped applicability evidence at release time.

## Separate transport migration analysis — no implementation or approval

NuGet still lists 10.0.16 as the latest SIPSorcery/Abstractions release on 2026-09-27. Its NuSpec
points to `4ca86773993a875706d8a7a4c1e0108e3a885ebf`; its
[license](https://github.com/sipsorcery-org/sipsorcery/blob/4ca86773993a875706d8a7a4c1e0108e3a885ebf/LICENSE.md)
retains the additional restrictions. Its net10.0 dependency group also raises
BouncyCastle.Cryptography to 2.7.0, Abstractions to 10.0.16 and logging abstractions to 10.0.11;
a one-line transport bump is not the complete compatibility review.

The pinned 10.0.15, 10.0.16 and examined upstream `master` DTLS transport files share Git blob
`f1c300ef2401fa0cfe2aacbed863431f48c7eb7e`. In that
[implementation](https://github.com/sipsorcery-org/sipsorcery/blob/4ca86773993a875706d8a7a4c1e0108e3a885ebf/src/SIPSorcery/net/DtlsSrtp/DtlsSrtpTransport.cs),
`WriteToRecvStream` enqueues data while `Receive` sleeps for 25 ms when its queue is empty.
`TimeoutMilliseconds` configures the DTLS peer timeout, not this poll interval. The receive/enqueue
methods are non-virtual; [RTCPeerConnection](https://github.com/sipsorcery-org/sipsorcery/blob/4ca86773993a875706d8a7a4c1e0108e3a885ebf/src/SIPSorcery/net/WebRTC/RTCPeerConnection.cs)
constructs a concrete private DTLS handle without a public transport factory in the inspected
constructor. This source comparison does not substitute for current input-pipeline measurements.

| Option | Technical and release implications |
| --- | --- |
| Existing supported settings / 10.0.16 upgrade | No supported poll-interval or transport-injection API found. The new release retains the poll and license terms, and is still in the current TURN-over-TLS advisory range; it is not a demonstrated solution. |
| Windows timer precision during interactive sessions | A supported, balanced `timeBeginPeriod(1)` experiment improved some runs, but the session-scoped candidate still failed the unchanged gate in repeated runs. It was removed rather than shipping an unreliable power-cost tradeoff. See the [performance notes](PERFORMANCE.md); no Windows power policy was overridden. |
| Upstream event-driven receive correction | Smallest transport-level candidate: wake the receiver when data arrives while retaining bounded waits, packet ordering, shutdown/cancellation and DTLS behavior. Requires upstream implementation/release review and unchanged latency/security/interop gates; no patch is claimed here. |
| Maintained fork | A narrow event-driven patch is technically possible but introduces ownership of security backports, builds and distribution. It does not remove the upstream license terms. Stop for an explicit architecture/maintenance decision before implementing. |
| Existing native-QUIC file transport adapted for input | Direct-path file transport is not a drop-in WAN/TURN input lane. Requires authenticated capability negotiation, ordered input/ack handling, focus generations, reconnect and primary WebRTC fallback. No such migration or compatibility pass is claimed. |
| Different WebRTC/native codec dependency | No safe drop-in replacement was established by this review. Inspect exact license, security history, ABI/API and runtime provenance before selecting one; do not downgrade or substitute merely to clear documentation. |

Any selected replacement/correction must preserve ICE/STUN/TURN (UDP/TCP/TLS), SDP, DTLS-SRTP,
SCTP data channels, the dedicated input lane and old-peer primary fallback, media/reconnect behavior,
permission/focus generations and mandatory PeerOnQ hybrid session protection. Measure current/original
decoder and dedicated/fallback input with/without bulk traffic, retaining the existing 35 ms gate.
No fork, transport replacement, protocol change or relaxed threshold was implemented by this audit.

## Findings retained from the initial audit

1. The restored SIPSorcery 10.0.15 package cannot be recorded simply as BSD-3 because its actual
   license file adds a use restriction. Downgrading is not an acceptable automatic fix: earlier
   versions were replaced for security reasons and a critical media dependency needs compatibility
   tests. No public release should proceed until this is resolved.
2. `SIPSorceryMedia.Encoders` embeds native `vpxmd.dll` without a separate libvpx notice in the
   archive. The repository-to-binary match above narrows provenance but does not supply the exact
   libvpx source/build, security patch or release-notice evidence.
3. No GPL/FFmpeg binary was found in the inspected current media package. That is not a statement
   about every transitive/runtime artifact; the final SBOM/native inventory remains mandatory.
4. PeerOnQ's own license remains MIT. Third-party terms are not replaced by PeerOnQ's MIT grant.

## Required release gate

- regenerate SPDX/dependency/native-binary evidence for the exact final runtime tree;
- run vulnerability, secret and license scans;
- reject unknown/`NOASSERTION` items rather than guessing;
- retain all required notices and source/offer obligations;
- obtain qualified legal/release-owner review for the restricted package terms and WiX agreement
  applicability/payment/exemption; approval cannot be generated by this codebase;
- resolve the relevant current upstream security findings and native patch/provenance uncertainty;
- do not publish while the distribution blocker is unresolved.

The dependency entry point is `DEPENDENCIES.md`; the release-facing orientation notice is
`THIRD_PARTY_NOTICES.md`. PeerOnQ's `LICENSE` remains unchanged.
