# PeerOnQ current state

## Current source contract — 2026-10-24

Source facts below were checked against the current checkout. They describe implementation, not
production approval or a fresh execution of the historical tests farther down this page.

- `Directory.Build.props` defines the canonical server/Windows client version as `0.9.80`. Linux, Android
  and Apple client versions derive from it; Android and Apple bundle codes are `9080`. A source
  version does not establish that a matching signed package has been built or published.
- 0.9.79 media: 4K tier on 4K-class sources, an earned 1080p@60 hardware rung, RTP pacing,
  negotiated NACK repair (`a=x-peeronq-video-rtx:1`), GPU SwapChainPanel viewer presentation,
  all-displays view, two-way Opus voice and P2P-first files. See the 2026-10-23/24 AI_CHANGELOG
  entries and [the performance report](PERFORMANCE_REPORT.md) for scope and limits.
- `SignalingProtocol` in `src/PeerOnQ.Transport/Protocol/SignalingMessages.cs` accepts exactly v3:
  minimum, current and maximum are all `3`. Missing/pre-v3/newer versions fail compatibility checks.
- Windows supports permission-gated attended `ViewOnly`, `FullControl`, `FileTransferOnly` and
  valid custom scopes. `Phase1SessionScope` retains its historical name but no longer restricts
  every session to view-only. Full Control includes screen, input and file permissions; text
  clipboard is separate. Input still requires accepted scope and a fresh focus acknowledgment.
- Native projects exist for Windows (WinUI), Linux (Avalonia), Android (.NET Android) and Apple
  (shared iOS/iPadOS UIKit and Mac Catalyst). Non-Windows clients are attended viewer/controller
  previews; their capability profiles omit hosting, capture, input injection, file/clipboard and
  unattended access. See [the capability/evidence matrix](CROSS_PLATFORM_CAPABILITIES.md).
- `PeerOnQ.slnx` includes Windows/Linux apps and the platform-neutral Android/Apple layers and
  tests. Android and Apple app builds are separate platform-workload gates; a solution build alone
  does not prove they compile or run.
- The offline product prototype is separate from the API-backed Admin and customer Portal SPAs
  and the .NET Cloud/Presence/Signaling/Downloads services. Node 24 is the workspace runtime target.
- Runtime, release-license, physical-device and performance gates remain evidence-bound. See
  [known limitations](KNOWN_LIMITATIONS.md), [PHASE5](../PHASE5.md) and dated
  [AI_CHANGELOG](../AI_CHANGELOG.md) results; no production approval follows from this inventory.

## Full 0.9.80 patch candidate — 2026-10-24

Fixes the 0.9.79 viewer crash and the connection-time quality downgrade; carries all 0.9.79
features. Windows pair from commit 6cb3c98 (516 frozen inputs, zero drift, zero warnings, full
payload/ICE03/size gate): x64 MSI 85,499,904 bytes
`255a47bb8054f31f32c9e3cdd150c9d92fe0d96a868c92d8d063f0c1d2f63bfb`, ARM64 MSI 77,983,744 bytes
`8df5f6b8a82b2b074a05b52880f8e228fe433d6416165573bbd6a713e37da82c`. The exact packaged x64 app
passed the real viewer-window runtime gate for GPU and bitmap renderers. Server bundle
`dist/private-candidate/0.9.80/server/peeronq-server-0.9.80.run`, 163,800,827 bytes,
`3557b8f1240a54b9655cdf02e18295b779eb1f6f3a7968ee110f6001276d05f9`; extracted-bundle Docker
acceptance passed (6,080 assertions, zero cleanup errors); local website publication passed.
Final verifier `VALIDATION.json` SHA256
`3f16b2c782950440a46b0dc9b52881ab670dcdacb8c7523380f02683c841a917`. The Linux preview was not
rebuilt for 0.9.80. Unsigned; production gates remain open.

## Full 0.9.79 patch candidate — 2026-10-24 (clients withdrawn)

The 0.9.79 viewer window crashes at runtime (SwapChainPanel `Background`, WER 0xc000027b /
0x802B000A) as soon as a session opens it; the operator saw this after deploying the bundle.
Do not install 0.9.79 clients; 0.9.80 supersedes them. The record below is kept as history.

One synchronized server/client release with canonical version 0.9.79 and derived mobile bundle
codes 9079. Client behavior changed (not a version-only rebuild): 4K tier on 4K-class sources,
earned 1080p@60 hardware rung, RTP pacing, negotiated NACK repair, GPU SwapChainPanel viewer,
all-displays view, two-way Opus voice, P2P-first files and queueing-delay bulk QoS. Server, Cloud,
Signaling, Presence, Portal and Admin behavior is unchanged; their images are rebuilt with this
release. Source commits: f8f7a19 (features) and 9dd6c37 (Concentus/Opus notice in every payload).

Fresh validation: Debug solution build 0 warnings/0 errors; full suite with the hardware video
gate 1,411 total / 1,383 passed / 0 failed / 28 environment skips; public web 118/118, typecheck
and lint; native UI gate; five source release contracts. The first MSI pair (from f8f7a19)
lacked `ThirdParty/Concentus/LICENSE`; it is retained only under
`dist/private-candidate/0.9.79-attempt-1-superseded/` and was never published.

The attempt-2 Windows pair (commit 9dd6c37) passed full extracted-payload, ICE03 and the 96 MiB
size gate with zero warnings/errors; 514 frozen client/installer inputs had zero drift. Portable
Support passed payload validation (586/582 inner files, ten maintained notices, no broker).

| Artifact | Bytes | SHA256 |
| --- | --- | --- |
| 0.9.79 x64 MSI | 85,483,520 | `e3c9fbef7200582d19aa3542898e3c1280d47e205287528bfa6a58da71c6d230` |
| 0.9.79 ARM64 MSI | 77,975,552 | `84b65db00ee054042c2d620a309637c047a4dd643600ec5e33c47b3935eee5dc` |
| 0.9.79 x64 Portable Support ZIP | 101,591,064 | `6bda779f1ca86f2a9eb356b91962a5d52a40e5414eb961a7c4f533ebc1e9888c` |
| 0.9.79 ARM64 Portable Support ZIP | 97,543,917 | `91d2e14a13ced5e0f27718158734ec16237a90191ae649ab61304a152adbf7be` |
| `dist/private-candidate/0.9.79/server/peeronq-server-0.9.79.run` | 163,772,206 | `49c14af08e928a0c6f51db22caa0e558dac936344223c6dcd62ed48cbb6cc03e` |

The server bundle embeds that exact pair; release notes SHA256
`9e62e1cc0bc796213e301915160c151d1851d4bce59896c68ed5566276cc13b2`. Actual extracted-bundle
acceptance passed with `runtimeExecuted=true`: 286.1 s, 106 commands, 6,073 harness assertions
(not test cases), 759 extracted files / 168,890,208 bytes, zero cleanup errors, no named volume
and no production environment. Controlled local website publication passed: the pair and
SHA256SUMS were copied, the managed preview restarted, the actual Vite metadata selected 0.9.79 /
`unsigned-public-pilot`, and both MSI URLs returned HTTP 200 with matching bytes and hashes.
The private Linux preview was rebuilt on Ubuntu 24.04 / SDK 10.0.402 from 204 source inputs with
zero drift; App 22, Platform 14 and Infrastructure 12 tests passed (48/48, zero skips), normal
close and startup cancellation exited 0 as UID 10001, and the archive carries ten SIPSorcery
notices plus `ThirdParty/Concentus/LICENSE`:
`app-updates/linux/release-0.9.79-preview/peeronq-0.9.79-linux-x64-unsigned-preview.tar.gz`,
54,130,344 bytes, SHA256 `30290e252f2f4ef2008597b96fb0f7d209b00948864535dc7d57346167c7abcf`.
It remains unpublished pending its physical-Linux gate.
The final exact-artifact verifier passed: `VALIDATION.json` (identical in the evidence directory
and `dist/private-candidate/0.9.79/server/`) has SHA256
`8436e68639b27f8d997125e285b677962794bb545cb724a9dd9dd71e9f91573c` and decides
`PRIVATE_UNSIGNED_CANDIDATE_VALIDATED_RELEASE_GATES_OPEN` (`productionReady=false`).
Evidence: `app-updates/validation/2026-10-24-patch-0.9.79/`. Remote production is untouched;
signing, distribution/native provenance, physical two-device/multi-monitor/voice/WAN,
SCM/LocalService and production approval gates remain open.

## Full 0.9.78 patch candidate — 2026-10-18

This is one synchronized server/client release, using canonical version 0.9.78 and derived mobile
bundle codes 9078. It includes the native dashboard selector removal: startup directly applies
Automatic quality/P1080. The temporary 1080p short-edge cap, aspect ratio and no-upscaling rules
remain intact. The public website automatically recommends x64 from explicit Firefox/legacy
64-bit signatures; authoritative ARM64 hints take precedence, failed/partial hints preserve
explicit ARM64, checking has a local deadline and manual override stays optional. The website-only
builder now declares ARM64 availability and clears inherited embedded ARM64 metadata. A reduced
browser signature is a recommendation, not proof of native CPU architecture. No protocol,
encryption, permissions or media acceptance threshold changed for this release.

Fresh 0.9.78 validation: public web tests 118/118 in 12 files, zero failed/skipped; typecheck and
lint passed. The Ubuntu 24.04 / SDK 10.0.402 Linux viewer was genuinely rebuilt from 194 coherent
source inputs, with zero drift before and after execution. All 48 tests passed (App 22, Platform
14, Infrastructure 12), with zero skips. Inner checksums, canonical manifest, x64 ELF, exact .6
core and ten notices passed; actual self-contained normal close and startup cancellation exited
0 as UID 10001. Both owned containers were verified absent. Distro libvpx identity was queried
without invoking its unsupported encoder configuration API; this does not add Linux hosting.

The fresh private unsigned Linux archive is
`app-updates/linux/release-0.9.78-preview/peeronq-0.9.78-linux-x64-unsigned-preview.tar.gz`,
54,103,727 bytes, SHA256 `dabe9707ee7db52d064aa67d10ce234377b46751b2f849d40a05955793ac2c7c`.
The earlier 0.9.77 archive remains byte-identical. Fresh logs, TRXs, source stamps and lifecycle/
cleanup evidence are under `app-updates/validation/2026-10-18-patch-0.9.78/`.

The matching private unsigned Windows x64/ARM64 MSI pair now passes full payload, ICE03 and the
unchanged 96 MiB size gate, with zero warnings/errors. The paired build took 412.88 s; all 56
frozen inputs remained unchanged. These are freshly built 0.9.78 binaries, not renamed packages:

| Artifact | Bytes | SHA256 |
| --- | --- | --- |
| 0.9.78 x64 MSI | 85,442,560 | `a675bde4f9875d9b2dcd141bdcf3aa0a803ee58adb119caa2f1de25fd1c4d115` |
| 0.9.78 ARM64 MSI | 77,930,496 | `e80a252d2b7d9c9db1af6d1996b90291163b28a3cd8b91940b7fce17aea09e5b` |
| 0.9.78 x64 Portable Support ZIP | 101,531,460 | `7f2d943d1ba8ebbc0e3e699e7f1d3675224907b21902dc3531c86cc37c29db5b` |
| 0.9.78 ARM64 Portable Support ZIP | 97,484,296 | `23cf2fc642789f211ae3cbe0b4f3939f745cde30f6f41d54c0e071ff1a6756a2` |
| `dist/private-candidate/0.9.78/server/peeronq-server-0.9.78.run` | 163,633,566 | `41dd381e56d518ada6539181889ab681ffb6628af206b479c852412a0a42567d` |

The separate private `development-unsigned` Portable Support pair passed current payload
verification: 585/581 inner files, exact checksums/version/architecture, .6 core/ten notices,
ephemeral user-temp profile manifests and no product broker payload. Both retain the compiled
public deployment endpoints with LAN development false. These checks did not launch, install or
connect the applications; endpoint metadata is not production approval. Archives are under
`dist/private-candidate/0.9.78/portable/` and the 56 packaging inputs have zero before/after drift.

The full server bundle build passed in 23.7013412 s. Its release notes are frozen with SHA256
`c512ec57faf4b0970fd977f477517d4f81afd7a133e5d7691d425bc6d3d36d3e`;
the server artifact and notes are immutable. Actual extracted-bundle acceptance attempt 1 passed
with `runtimeExecuted=true`: 356.3283211 s, 106 commands, 5,964 harness assertions (not test cases)
and nine images. All 744 extracted inputs / 168,614,423 bytes retained their exact hashes.
Cleanup errors were zero; no named volume or production environment was used. This is isolated
runtime evidence, not a production installer execution.

Controlled local website publication passed: the exact validated pair and matching SHA256SUMS
were copied to local public downloads, the managed preview restarted, and actual Vite metadata
selected 0.9.78 / `unsigned-public-pilot` through the local source, not embedded or tracked
metadata. Both complete MSI HTTP 200 responses matched expected byte counts and SHA256 hashes
within bounded 60-second streams. Older versioned files were preserved and the previous checksum
file backed up. Proof is under the new release's `local-website/` evidence directory. This is local
publication only; the remote production website was not changed.

The fresh package, isolated server runtime and controlled local publication checks have passed.
The final exact-artifact verifier exited 0: six binaries/six companions, 56 frozen source inputs
with zero drift, fresh web 118/118 and Linux 48/48, actual server acceptance and zero cleanup
errors. `app-updates/validation/2026-10-18-patch-0.9.78/VALIDATION.json` is byte-identical to
`dist/private-candidate/0.9.78/server/VALIDATION.json` (7,401 bytes), SHA256
`a76c79c4acfb5aaa6985fa7ca6fad6f52d10b267a2732098bf61f5ea8338a001`.
Its decision is a validated private unsigned candidate with release gates open:
`privateUnsigned=true`, `productionReady=false`, `productionDeployment=false` and
`localControlledPreviewPublished=true`.
The 1,310-test, 600-second hardware and SCTP results recorded below are previous baseline
evidence, not fresh 0.9.78 executions. Remote production remains untouched; unsigned candidates
and the Linux preview do not establish signing, distribution/native provenance, physical/WAN,
SCM/LocalService or production approval. No source commit or push is part of this candidate work.

## Website automatic-download follow-up — 2026-10-18

Public HTTPS probes observed `peeronq.com` offering 0.9.77 / `unsigned-pilot`. Both actual MSI
downloads returned HTTP200, with exact x64 `274e35dd...45f6` and ARM64 `add7653f...158a`
hashes recorded in the candidate evidence below. This read-only observation is not production
approval or a deployment performed by this follow-up.

The browser selector now automatically recommends the available client. Firefox/legacy 64-bit
Windows information can recommend x64 without requiring the architecture dropdown; authoritative
ARM64 Client Hints win. Failed/partial hints retain explicit ARM64, a 1.5-second local deadline
prevents indefinite checking, and manual override remains optional. Unknown unsupported devices
remain unavailable. A reduced UA cannot establish every native CPU; the x64 fallback is labeled a
recommendation ([Microsoft UA-CH guidance](https://learn.microsoft.com/en-us/microsoft-edge/web-platform/how-to-detect-win11)).

Public tests 118/118, typecheck/lint and website-builder invariant pass; production web build
13.70 s. This is a version-neutral website change: no native installer rebuild, client selection
by version, database change or new product release. `dist/website/review-auto-client-detection-2026-10-18/`
contains a 236,568-byte private unsigned review ZIP, its checksums and `VALIDATION.json`.
All 15 archive entries match the build inventory; no installer is included. Existing trusted P-256
website signing/publication access is required before Admin activation. This updated UI has not
been deployed by this task; the prior candidate evidence below remains immutable.

## Temporary 1080p scope — 2026-10-18

The user deferred 4K. Streaming now caps the short edge at 1080 pixels: 3840x2160 displays send
1920x1080, rotated displays send 1080x1920, aspect ratio is retained and smaller sources are not
upscaled. Ultrawide long edges may exceed 1920. Both session directions normalize old
Automatic/Native/1440p/2160p preferences to P1080; an older host also receives an explicit 1080p
request in Automatic quality mode. The Windows capture scaler independently applies the
cap. The wire enum values remain compatible. The native dashboard now has no display-quality or
target-resolution selectors; startup directly applies Automatic quality/P1080. The LAN-only 4K
startup override is removed, and the shared High Quality profile requests P1080/30 FPS/12 Mbps.
Public page copy reflects this scope. Low-level 4K codec diagnostics remain separate; their
failed performance observations below are historical evidence, not current product capability.

The subsequent dashboard-selector removal passed the strict WinUI x64 build in 53.49 s with
zero warnings/errors and the updated native UI gate. Previously packaged 0.9.77 artifacts below
remain immutable and do not contain this later source edit; no new MSI/publication is claimed.

The pre-broker-packaging strict post-cap build passed in 20.71 s with zero warnings/errors. All 19 projects completed:
1,310 total / 1,283 passed / zero failed / 27 explicit environment skips. Application 234/234,
EndToEnd 165/165, focused resolution/capture 62/62 and adaptive 40/40 passed. Web 228/228 and
lint/typecheck/build passed. Hardware 1080p continuity measured 600.68 s / 17,946 frames / 29.88 FPS,
538 GPU and zero CPU samples. Actual SCTP 1 GiB transfers completed in 72.1/60.5 s with equal
full disk SHA256; separate unchanged 35 ms input gates passed. Current-source environment
supplements now cover all 27 exact primary skipped identities: Linux 6, Cloud 16, Signaling 4
and TURN 1, with zero unmatched. The primary skip rows remain intact; supplemental passes do
not inflate the primary totals. The additional native QUIC 1 GiB payload-contention gate passed
1/1 with zero skips: 25.2 MiB/s and interactive p95 0.5 ms across 2,430 samples. This is transport
payload evidence, not a disk-file SHA256 proof.

The current Ubuntu 24.04 viewer closure passed 48/48 (App 22, Platform 14, Infrastructure 12),
normal close and startup cancellation, with all 194 source inputs unchanged. The private unsigned
54,105,863-byte archive is `app-updates/linux/release-0.9.77-preview/peeronq-0.9.77-linux-x64-unsigned-preview.tar.gz`,
SHA256 `2af4e49e5537f5a6839ac4af98277d244e9cf1c214890f6061abbb9a840ddd95`.
Current-source TURN UDP/TCP and signaling restart/re-authentication passed 3/3 with zero skips;
both media peers selected Relayed paths. Valid UDP/TCP/DTLS/TLS authentication passed and
invalid/expired credentials were rejected. Owned containers were removed without host CA or
firewall changes; the initial read-only context-preflight variable collision is retained separately.
The first filename-based resolution filter matched no tests and is retained separately; the
correct actual class filters ran all 62 tests. The native UI gate initially retained an obsolete
4K assertion, was corrected to the new contract and passed. A Portal test now awaits its actual
focus effect; all frontend cases passed. The first Media fixture mistakenly selected TURN without
credentials; two failures occurred before transfer. Corrected direct SCTP runs passed; failed
TRX/logs remain separately under `failed-media-turn-configuration`, outside authoritative totals.
Final package/supplement evidence is under ignored `app-updates/validation/2026-10-18-1080p-scope/`.
0.9.77 is a private unsigned candidate, not a published replacement for verified 0.9.76 downloads.
The original private x64/ARM64 MSI pair passed payload checks but failed the existing 96 MiB
size gate at 112,365,568 / 103,153,664 bytes; those artifacts are preserved unchanged. A broker-only
scratch without unused WinRT references still failed at 107,302,912 bytes. The trimmed-broker
scratch passed payload/size checks at 85,422,080 bytes with zero warnings; this is not a final pair.
The broker project now excludes unused WinRT SDK references, enables trimming and treats compiler/
linker warnings as errors, retaining its Windows TFM, multifile private runtime and security boundary.
The 41 frozen functional source inputs are retained alongside seven broker-packaging inputs; the
1,310-test totals above have not been rerun for this packaging change. The post-packaging strict
whole build passed in 33.21 s with zero warnings/errors; ordinary Service tests passed 12/12.
The actual trimmed broker passed six managed check groups on shared .NET 10.0.13; its self-contained
apphost returned the expected non-SCM exit 2. This does not prove SCM/LocalService or trimmed-worker DI.
The final Windows pair passed payload checks and the unchanged 96 MiB size gate. The separate
private unsigned Portable Support pair also passed payload validation. Final private artifacts:

| Artifact | Bytes | SHA256 |
| --- | --- | --- |
| x64 MSI in `dist/private-candidate/0.9.77/windows-size-fixed/` | 85,467,136 | `274e35dd45c5af373fa2f817e69419d6f209c97fd61a8f62c679e41ab23645f6` |
| ARM64 MSI in the same directory | 77,930,496 | `add7653fad166ea299dfd900a578ad49f13db48f3b4501aaf3a32281a1f9158a` |
| `dist/private-candidate/0.9.77/server/peeronq-server-0.9.77.run` | 163,670,630 | `c6e1af61945dcb74bf8dfa125a3de645c6e764bddc14e9aa6ec48827507cd774` |

Extracted-bundle acceptance attempt 6 passed in 119.4647906 s: 104 commands and 5,964 harness
assertions, not additional test cases. All nine images built and their non-root runtime users were
verified, with six ASP.NET runtimes at 10.0.13. Restricted migrations ran twice; delivered roles/permissions and default-off audio
passed. Actual Cloud readiness/liveness/401, all three web surfaces, six Portal SPA routes and
both complete MSI HTTP 200/hash/version/classification/checksum checks passed. All 744 extracted
inputs remained unchanged and cleanup errors were zero. Failed attempts 1–5, the isolated
dual-network diagnostic and external Docker Desktop restart remain preserved; fixture-only
changes did not change product bytes. This was not a production installer execution.

The final read-only seal verified 1,180 curated proofs, all 48 source inputs with zero drift,
the 19 primary TRXs with exact supplemental skip coverage, and 12 artifacts (six binaries/six
companions). `seal-summary.json` SHA256 is
`a7d0a25671232e822e950c3ab3489961082c746df5fcac5189c5c79b3b35077f`;
`EVIDENCE_SHA256SUMS.txt` SHA256 is
`86444354ca840e9fa6342c5d7adf58a3868b15147134488a2aa1b1f66b8047a5`.
Both records were copied byte-identically beside the .run, with `CANDIDATE_VALIDATION.json`,
`INSTALL-0.9.77.sh` and the unmodified 4,326-byte release notes. The decision remains
`privateUnsigned=true`, `productionReady=false`, `published=false`; no production deployment,
source commit or GitHub push occurred. Package completion does not establish physical-device parity.
Signing, distribution/native provenance and physical/WAN release gates remain unchanged.

## Historical pre-cap release performance checkpoint — 2026-10-18

**The next release is held.** Canonical source remains 0.9.76; no new patch/MSI/version or
publication/deployment was created. The unchanged 100 ms 4K condition is still unmet. The latest
capture-inclusive model measured whole p95 224.02/102.57 ms and final p95 158.19/49.49 ms for
landscape/portrait. The final current-source I420-only coordinator measured whole/final
105.28/102.23 ms landscape and 84.36/66.50 ms portrait. Functional assertions passed, but these
measurements do not meet every performance window or prove physical 4K/30 FPS or WAN behavior.
Both endpoints share one process in the diagnostic model; WGC readback/physical presentation
are excluded. Full scopes, earlier failed observations and rejected candidates remain in
[PERFORMANCE_REPORT](PERFORMANCE_REPORT.md) and ignored
`app-updates/validation/2026-10-18-release-performance-gate/`.

Selected changes: bounded MF event callbacks retain original deadlines and fence shutdown;
exact-arithmetic AVX2 speeds capture/I420/NV12 conversion with the original scalar fallback;
native VP8 ConfigSet retains codec state and a fixed 90 kHz clock while preserving keyframes,
resize and reset recovery. A version guard rejects unsupported native encoder layouts before
configuration allocation/defaults. No dependency, quality, timing threshold or protocol change
was introduced by this checkpoint. The rejected parallel converter is removed; cold callback
initialization regression 791.78 ms is retained as evidence, not waived by warm measurements.

The final strict whole build passed in 32.00 s with zero warnings/errors. All 19 test projects
completed: 1,274 total / 1,246 passed / zero failed / 28 explicit environment skips. Actual
current-source supplements passed without skips: Ubuntu 48/48, Cloud Infrastructure 63/63,
distributed signaling 3/3, audio 1/1, TURN UDP/TCP plus signaling restart 3/3. The TURN fixture
verified actual Relayed paths, UDP outage recovery, four authentication protocols and rejection
of invalid/expired credentials. All owned service containers were verified absent and environment
restored; no host CA or firewall change was used. Web 228/228, typecheck/build/lint passed.
SIMD production tests passed with intrinsics enabled and disabled; final Windows VP8 37/37.
Exact-identity reconciliation covers all 28 primary skips with actual supplemental Passed rows,
zero unmatched; the primary skips are not erased or counted twice. All 14 frozen source hashes
match and current Linux/E2E/Media dependency cores match .6. The final identity/metric evidence is
`whole-current-source/final-evidence.json` under the ignored validation directory above.

Current hardware 1080p continuity passed 600.24 s / 17,987 frames / 29.97 FPS, with D3D11 hardware
decode active in all 538 samples and zero CPU samples. Both actual SCTP lanes copied 1 GiB with
equal full disk SHA256 in 67.5/61.5 s, native QUIC false. Separate unchanged 35 ms input gates
passed; this does not prove simultaneous 4K plus 1 GiB or physical WAN performance.

The final guarded source was rebuilt on Ubuntu 24.04: 48/48 viewer/Cloud-client tests, zero skips,
194 inputs with zero drift; package/ELF/checksums/notices, actual normal close and startup cancel
passed. The private unsigned 54,105,078-byte x64 viewer preview is under
`app-updates/linux/current-source-final-2026-10-18/`, SHA256
`8a5792ef9d587352573512d872c7f9cb5ac956a5267724b30b50e7f7b15778bd`.
Only distro libvpx 1.14.0 identity APIs were probed; no incompatible encoder configuration was
allocated or invoked. Linux hosting/file transfer/audio are unsupported; physical Linux/Windows
interop and publication remain open. Earlier private archives retain their original hashes.

## Latest SCTP/UHD/Linux follow-up — 2026-10-18

Current source selects maintained `PeerOnQ.SIPSorcery` 10.0.17.6: seven patches, five final
embedded sources and ten notices. The seventh patch seeds first-packet RTCP transit; actual
phase/spacing cases failed six of nine on .5 and passed nine of nine on the candidate, preserving
real jitter and timestamp rollovers. Two clean .6 packages are byte-identical; builder 22/22 and
actual-package compatibility 7/7 passed, exported API 6,674 entries unchanged. Exact package/core/
PDB hashes are in [DEPENDENCIES](../DEPENDENCIES.md). An initial private API harness omitted an
existing dependency; that incomplete setup is retained separately, not an API mismatch/pass.

Actual AMD output also proved ~3x excess CBR padding after dynamic FPS changes despite correct
read-back bitrate/types. The public encoder now removes complete non-reference Annex B filler
before encryption. Generic parsing remains strict; a separate trusted-native-sample path recognizes
the observed AMD all-FF EOF padding without a standard stop bit. All picture/parameter/reference bytes and ambiguous input
remain intact. The exact .6 solution restored and passed the whole Release CI/analyzer/
warnings-as-errors build with zero warnings/errors (77.31 seconds, final normalization rebuild
19.24 seconds). Windows x64 and both main
media test cores match .6; all ten Windows notices validate. Durable packaged jitter 9/9 and
pure filler parser 17/17 pass. Actual Ubuntu .6 tests passed 48/48 with zero skips. The final
54,101,107-byte private Linux archive and sidecar are under `app-updates/linux/final-transport-10.0.17.6/`;
SHA256 `f1704c67e2b93967950491fdd12a8fa4c908cb2d226a8256dfbbe80ab968e27a`.
Integrity/exact-core/ten-notice, extracted self-contained launch/normal-close and pending-startup
cancel checks passed. This final rebuild contains the new shared default-false decoder acceleration
contract/statistics and rechecked all 192 inputs with zero drift. Earlier .6 notice-only drift and
its corrected archive remain historical evidence in the performance report. The normalized pre-D3D
whole suite passed 1,179/1,206 with
zero failures and 27 explicit environment skips; all 112 EndToEnd tests passed. Final .6 SCTP
copied 1 GiB on both lanes in 84.2/64.5 seconds with equal disk SHA256 and no native QUIC.
Unchanged 35 ms strict input passed both routes: dedicated baseline/bulk p95 15.996/16.780 ms,
fallback 17.383/16.991 ms. The earlier intermittent failure is retained; its cause is not claimed
fixed. Final continuity results follow below. The fresh 41-project managed NuGet inventory reports zero vulnerable
entries. The completed .5 measurements below remain a historical checkpoint,
not acceptance of the .6 source. Canonical product version remains 0.9.76; no publication/deploy.

The first .6 actual native/coordinator three-case run passed functionally but removed zero filler
bytes on the real driver. That raw/filtered pixel comparison is not removal proof. The broad
suite was stopped when this was found; its partial results are not a full pass. The exact parser
rejection was AMD's missing RBSP stop bit. The narrow native-only normalization then passed
27 parser cases and actual native raw/filtered exact-pixel comparison 12/12, independent fresh
recovery 7/7, with real padding removed on all 12 frames. One interframe shrank 56,256→1,940
bytes without changing pixels. The subsequently completed full suite is the final normalized source.

Normalized .6 actual paired-coordinator UHD observations passed 2/2 functionally, no signaling
errors, native pixels retained. Whole/final-five p95: landscape 129.30/122.33 ms; portrait
115.26/137.98 ms. All 100 ms windows missed. Real feedback drives the FPS target; false multi-
second startup jitter did not recur. Gray-scene landscape payload finished at 20.66 kbps after
padding removal, but software H.264 decode still averages 27.02 ms. No universal latency/GPU-
decoder/physical-screen claim follows. The later full suite still missed landscape 100 ms:
whole/final-five p95 118.59/120.74 ms. Large-file/continuity checks run separately; earlier .5,
no-op .6 and normalized .6 observations remain retained.

Current Windows source also has optional hardware-device D3D11 H.264 output, synchronous bounded
NV12 readback and CPU fallback. First validated output enables the hardware flag; reset/failure
clears it. Unsupported creation/manager and initial/midstream readback/teardown faults pass seven
actual regressions, including encrypted PLI recovery with the session still connected. The normal
delayed-output recovery latch accepts references after the complete recovery keyframe is consumed.
Together with native twelve-format CPU/D3D exact pixels/fresh recovery and 27 filler cases, the
focused gate passed 35/35. The default-false shared decoder contract preserves older implementations;
Linux was rebuilt/retested from this shared closure. Adopted-path coordinator cases passed2/2,
but whole/final-five p95 still misses100ms: landscape122.26/126.94ms, portrait117.57/108.57ms.
Native landscape decode mean improved27.02 to18.87ms; native encoder40.21/color14.41ms
remain material. The strict final solution build passed33.21s with zero warnings/errors; secret
scan1212 files/zero findings and diff check passed. Final combined19-project run exited zero:
1,213 total,1,186 passed,zero failed,27 explicit environment skips; EndToEnd119/119.
Both1GiB SCTP rows passed66.1/62.3s, equal full disk SHA256; dedicated/fallback input baseline/
bulk p95 17.550/15.915 and16.351/16.133ms passed unchanged35ms gates. These input fixtures
use their original1MiB bulk pressure, separate from the1GiB disk-transfer fixtures.
The encrypted1080p soak passed600.43s,17,994 frames/29.97FPS, GPU/CPU samples537/0 and
hardware true atstart/end; private bytes480,104,448/480,104,448/400,412,672 start/max/end.
Final native-UHD whole/final-five p95 remains116.82/119.02ms landscape and130.00/102.51ms
portrait; all100ms windows missed. Current actual x64/test consumers match exact .6 and eight
adopted sources did not drift. Physical4K, WAN, physical Linux-to-Windows-host interop and
signing gates stay open. Canonical version remains0.9.76; public Windows packages were not changed.

## Earlier .5 SCTP, native UHD and Linux checkpoint — 2026-10-18

- That checkpoint selected maintained `PeerOnQ.SIPSorcery` 10.0.17.5: six reviewed patches,
  four final changed source files, nine retained notices and unchanged upstream license/API/wire
  identity. Two clean packages are byte-identical; builder 22/22, package SCTP/UDP 33/33 and
  compatibility 7/7 passed, with 6,674 exported API entries unchanged. Exact hashes are in
  [DEPENDENCIES](../DEPENDENCIES.md).
- The root cause of sustained legacy loss was concurrent UDP receives sharing one buffer.
  Controlled/live evidence found up to seven owners; the gated private candidate retained one
  owner and completed a SHA256-verified 1 GiB in 74.7 seconds, with no retransmissions. Buffers,
  anti-replay, packet validation and encryption were not weakened. Exact produced-package
  1 GiB acceptance passed both SCTP lanes: legacy 76.8 seconds / 240 KiB and negotiated bulk
  57.5 seconds / 256 KiB, each with the same final SHA256 and native QUIC disabled. Separate
  strict input passed primary fallback but failed dedicated input; investigation is retained.
  Failed earlier candidates remain in the report.
- Plain solution restore and the whole Release CI/analyzer/warnings-as-errors build passed with
  zero warnings/errors. Windows x64 and both main media test outputs contain the exact .5 core;
  all nine Windows notices validate. Managed NuGet inventory: 41 projects, zero vulnerable entries;
  the original upstream identity separately passed its high/critical advisory gate.
  The main whole-suite run passed 1,140/1,167 with zero failures and 27 environment skips across
  19 projects. Hardware video/system audio were enabled. Six Linux skips are independently
  exercised on actual Ubuntu below; remaining external infrastructure/relay skips are not passes.
- Automatic capture retains native UHD in both orientations. Clock retries recover after one
  second while no bounded estimate exists; ordered native decode feeds a bounded latest-frame
  color/presentation worker. Hardware rate changes use validated matching dynamic media types
  or reconstruct explicitly, and one cleared encoder-owned NV12 scratch removes per-frame
  temporary allocation. Exact pixel, native-copy, resize, recovery and disposal checks passed.
  Final .5 direct 30-second UHD observations retained dimensions and missed the 100 ms target:
  landscape 20.06 fps / 134.75 ms p95; portrait 21.93 fps / 127.67 ms. That direct fixture omits
  production quality-feedback signaling, so it cannot establish remote-age adaptation behavior.
  A separate actual paired-coordinator/signaling observation is being recorded. These are
  synthetic headless output-buffer measurements; no physical 4K/30 fps claim follows.
- Paired production sessions exposed two current policy reasons rejected by the server:
  `awaiting_network_feedback` and `viewer_video_stall`. The finite allowlist now includes those
  exact literals. Registered/accepted relay cases failed 2/2 before and passed 2/2 after; arbitrary
  reasons/out-of-range latency remain rejected. Strict build and full signaling 138 passed /
  four external skips / zero failures. Existing session ownership/authorization checks remain.
- The Linux viewer/controller now uses distro libvpx9, real Secret Service/hybrid identity,
  proof-bound Cloud attestation and correct X11 repaint/cancellation. Actual Ubuntu linux-x64
  passed 48/48 (zero skips); 191 snapshot inputs had zero drift. The 53,986,198-byte private
  self-contained archive, nine notices and exact .5 core validate; actual extracted-payload launch,
  normal close and pending-startup cancellation passed. SHA256 and prerequisites are in
  [the performance report](PERFORMANCE_REPORT.md#linux-client-package) and
  [Linux README](../packaging/linux/README.md). Physical Linux/Windows-host interoperability,
  accessibility, ten-minute physical acceptance and production signing/publication remain open.

No product version was advanced or Windows release published; installed/published 0.9.76 MSI
files do not contain these source changes. Linux output is a private unsigned preview only.

## Earlier unreleased audit remediation checkpoint — 2026-10-18

These are source changes at canonical version `0.9.76`, not a published release or a
production deployment. Existing 0.9.76 downloads do not contain this remediation. Dated entries
below retain their original validation limits.

- Windows capture enforces a 1080-pixel short-edge floor when the physical source supports it,
  including legacy 720p requests and adaptive downscaling. Smaller sources stay native. Frame rate
  and bitrate can still adapt; a resolution floor does not guarantee bandwidth or physical latency.
- File byte quotas remain `long.MaxValue`; the separate artificial 1 Gbps capacity ceiling is
  removed. Authenticated receiver measurements, congestion response, control/media reservations,
  bounded buffers, path validation and integrity checks remain active.
- A full dedicated bulk queue applies cancellation-aware backpressure instead of losing an
  ordered encrypted record. File receive callbacks also wait through contention, preserving the
  queue bound. Frame pacing preserves deadline phase under capture jitter and uses a monotonic
  clock; deterministic 33 ms and 60 Hz sources now retain approximately 30 fps at the 30 fps target.
- Input ownership is scoped to its session. Unrelated view-only sessions cannot disable another
  controller; capture loss or failed monitor restart revokes that session's input and ends sharing.
- Optional `input.unicode.v1` negotiates encrypted bounded UTF-16 text from Android/Apple's
  short-text action to Windows. Windows physical-key/layout/IME behavior is a separate open gate.
- The native Cloud panel issues a two-minute ownership code; a customer Owner/Admin previews and
  confirms it in the portal. Codes bind current installation/identity, customer session and tenant,
  are consumed atomically, and reject replay or cross-tenant ownership. Native Refresh obtains
  updated policy and re-registers signaling; old unmanaged attestations retain their existing
  configured short lifetime rather than gaining synchronous revocation.
- Portal member/team lifecycle, owner transfer and organization deletion use existing authorization
  and explicit confirmations. MFA replacement requires current password and factor/recovery proof,
  with account/session/version-bound drafts. Rate limits now use the authenticated customer subject
  before authorization; separate customers behind one NAT no longer share a customer bucket.
- Server builds pin SDK 10.0.402 and ASP.NET runtime 10.0.13. The user-approved maintained
  `PeerOnQ.SIPSorcery` 10.0.17.4 builds from pinned upstream 10.0.17 source plus reviewed DTLS
  receive wakeup, per-SACK duplicate-history reset and bounded gap-counter patches; Abstractions remains 10.0.17. The original upstream advisory identity is
  checked independently. The actual TURN/TLS receive-loop regression verifies dispatch continues after a malformed
  packet handler throws. JavaScript dependency floors were patched with release-age checks intact;
  the excluded throwaway mockup sandbox is no longer installed as a workspace.
- New server bundles require matching verified x64/ARM64 Windows installers. Public download
  selection offers manual platform/architecture choice and fails closed when browser hints are
  uncertain. No installer or new product release was created by this source task.
- The user approved Vortice.MediaFoundation 3.8.3, NAudio.Wasapi 3.2.0 and WindowsServices 10.0.13
  on 2026-10-18. Windows hardware H.264 passed actual AMD GPU and encrypted 1080p checks; system
  loopback audio passed local endpoint and encrypted transport checks. Audio is default-off,
  requires separate attended consent, and managed sessions require signed opt-in on both endpoints.
  The optional LocalService broker passed 12 IPC/identity tests and both architecture publishes;
  clean-VM installation/reboot and physical two-endpoint media acceptance remain open. Restricted
  third-party licenses, native libvpx provenance, signing, physical-device acceptance and
  separate-network benchmarks remain open.

### Historical .4 dependency and build verification

The repo-local ignored SDK resolves ordinary `dotnet` and IDE-style design-time builds to
10.0.402 without changing the system SDK installation. Plain solution restore passed. The latest
whole-solution Release build with CI mode, current analyzers and warnings-as-errors passed with
zero warnings/errors; x64 App, Media.Tests and EndToEnd.Tests all contain the exact .4 core hash
recorded in [DEPENDENCIES](../DEPENDENCIES.md). Hash-verified copying fixes stale equal-size DLLs
with deterministic package timestamps; six regression cases passed. The unused `framer-motion`
catalog entry was removed, and the frozen pnpm workspace lock remains valid.

The final scan covered 41 .NET projects with zero known vulnerable entries; the original upstream
SIPSorcery advisory identity was independently checked. pnpm audit reported zero known findings,
and the working-tree secret scan passed for 1,181 source files. These are dated scanner results,
not a native-binary security certification. The maintained builder passed 16 self-tests, its exact
produced package passed seven compatibility checks, and two clean package builds were identical.
The final private Windows ARM64 WinUI self-contained publish passed with ARM64 PE files, all six
exact dependency notices and 159 unchanged source/build inputs, including the route correction.
Android's final isolated SDK303/workload rebuild passed with 116 unchanged inputs and both ABI
packaging checks; it does not establish an SDK402 Android workload or physical-device gate.

Default-unbound native QUIC file tests passed 2/2: each disk file was 1 GiB with equal source/
destination SHA256, actual native selection on both peers and zero SCTP bulk records. Transfer
times were 20.6/9.9 seconds on this host; see [PERFORMANCE_REPORT](PERFORMANCE_REPORT.md) for
scope and exact evidence. The OS-route/NIC resolver passed 28 deterministic checks. Legacy SCTP
large-file recovery starvation remains open, so no all-path throughput or AnyDesk parity is claimed.

The latest serial whole-solution run enabled actual hardware-video and system-audio tests:
1,065 total, 1,043 passed, one failed and 21 environment-gated skips. The sole failure was the
4K/30 fps VP8 case's authenticated capture-to-present measurement producing no bounded sample;
its first decoded frame had already passed dimensions/pixels/timestamps. Both unchanged 35 ms
input gates passed (four scenario p95 values 17.062/18.284/16.908/15.627 ms, 20 samples each;
synthetic injection sink). EndToEnd passed 64/64, Application 209/209 and transport 7/7. Separately,
the real PostgreSQL/Redis Cloud suite passed 63/63 and distributed signaling passed 3/3. The skipped
live-TURN/restart checks were exercised separately below. This whole run is not a clean pass;
diagnostic follow-up must retain that failed observation.
One diagnostic rerun of that exact 4K case passed, but measured capture-to-present p95 was
1,739.4 ms; neither that rerun nor a source retry-gap finding establishes the original cause.
The separate unloaded 1 GiB QUIC coexistence gate passed at 25.2 MiB/s with interactive p95
0.8 ms across 2,540 samples. Its 1 GiB/minute and 35 ms requirements were unchanged.

Live local acceptance reached authenticated UDP/TCP/DTLS/TLS TURN allocations and rejected
invalid/expired credentials, then passed both UDP/TCP relay-video cases (including UDP interruption).
The file harness incorrectly expected a direct bulk peer in RelayOnly mode and reused one quota
subject across two distinct sessions. Production credentials already bind device/session identity.
The test expectation and controller were corrected without changing quotas or file-path security.
Isolated fresh per-session credential runs passed both TURN file-offer rejection cases, and live
signaling restart/re-authentication passed. Earlier reused-credential runs timed out and remain
diagnostic evidence. The complete corrected-controller rerun was deferred until the soak finished:
one overlapping build attempt failed with MSB3027 locked test DLLs, an orchestration error rather
than a connection result. The final .4 hardware continuity run passed: 600.36 seconds,
16,938 synthetic 1080p frames at 28.21 fps, H.264/hardware/dimensions/Connected checks retained.
Process private bytes start/max/end were 160,808,960 / 255,135,744 / 158,928,896; this is measured
local behavior, not a memory bound or physical two-endpoint/WAN acceptance. The corrected full
controller subsequently passed serially (exit 0): Signaling 137/4 gated skips, EndToEnd 61/3
hardware/audio-gated skips (all 64 ran successfully in the main opt-in suite), both real TURN
video cases, both TURN file-offer rejection cases and restart/re-authentication. STUN, four
authenticated TURN listener variants, invalid/expired credential rejection and pinned local HTTPS
health checks passed in that same controller flow. This does not clear the main suite's separate
4K timing failure, legacy SCTP large-file behavior, physical/WAN or publication gates.

## Download presentation packaged as 0.9.76 - 2026-09-28

The operator requested a server patch for the committed website change. The full .run includes
the revised download hero: no amber/testing-only banner; existing classification and digital
signature status remain in Installer details. Customer/Admin auth, SMTP egress and native runtime
behavior are unchanged. Windows x64 and ARM64 were rebuilt solely for the common version stamp.

Both installer builds completed with zero warnings/errors and passed payload validation. The
restarted local :5555 preview selects 0.9.76 unsigned-public-pilot; both complete HTTP downloads
and the checksum document matched their packaged bytes. The server payload/header/archive paths,
canonical version, source UI and embedded x64 hash passed verification. Cloud API, Portal and
website Docker images built from the exact extracted bundle. Local HTTP probes checked the six
Portal routes, security headers, new website copy, portless Portal URL and exact embedded MSI.
Source and extracted merged Compose guards, generated header shell syntax and local TLS ingress
tests passed, including public Portal/API, operator CIDRs, private endpoints and unknown hosts.

Public website 86/86 and Portal 82/82 tests/typechecks passed. Canonical client/server and native UI
invariants passed; unchanged Release backend binaries passed Cloud 56/56, Admin 49/49 and deployment
21/21 with --no-build. The full media suite, physical devices and real-browser visual acceptance
were not rerun. Repository/staged secret scans and git diff checks passed.

Artifacts (immutable, unsigned public-pilot candidates; no detached server GPG signature):
- Server: dist/server/peeronq-server-0.9.76.run, SHA-256
  1bace95042d14cc1f00329bbb65f74973d2938991c0a7ad366ac6eef18652ceb.
- Windows x64: 33910917a74d273132e5c54f17e06466308af6e0c0511b229dcf83ff5ce66c97.
- Windows ARM64: b1d9d6e21b2c9d0f7436f1b5315c7a021211443979c9ba0b9bb1bfaaa5fa1e46.

Production installation/reachability of 0.9.76 remains an operator action; source publication and
local checks do not deploy it. Preserve the existing environment without --bootstrap or mail/MFA
overrides. Server installation does not update already installed clients. Rollback to the retained
0.9.75 restores the old notice and keeps that release's SMTP egress correction.

## Production customer sign-in confirmed by the operator - 2026-09-28

The operator received real password-reset email and completed the reset in production. Their
read-only account/audit query showed an active, unlocked account with successful password resets
but EmailVerified=false; previous verification deliveries had failed before mail was restored.
After requesting and completing the separate verification flow, the operator confirmed successful
Portal sign-in. Preserve SMTP settings, the separate verification requirement and the working
customer authentication flows. No credentials, tokens or account email are recorded here.
This confirms that account's recovery/verification/sign-in, not every recipient or a physical-client
performance/signing release gate. No production database changes were performed by the agent.

## Customer SMTP egress correction candidate - 2026-09-28

After enabling Resend, the operator confirmed an existing customer account but no message in
Resend Emails. Source inspection found Cloud API attached only to internal control/observability
networks. A local Docker reproduction could not obtain a Resend SMTP banner with internal-only
networking; attaching a dedicated outbound bridge returned `220 Resend SMTP Relay ESMTP`.
No credentials, AUTH or email submission were used in that probe.

Source 0.9.75 adds `customer-mail-egress` only to staging/production Cloud API. Shared internal
networks, public ingress, direct ports, cookie/CSRF/MFA policy and generic enumeration-safe mail
responses are unchanged. Development FileSink networking remains internal. This bridge supplies
outbound routing, not a provider allowlist. Existing Resend credentials/settings must be preserved.
Client packages advance only the common version; no native behavior or blur fix is included.

Merged Compose regressions and 21 existing deployment configuration tests passed; existing Cloud
56/56 and Admin 49/49 tests passed using unchanged Release binaries. Portal 82/82 and website 86/86
tests/typechecks passed. Both 0.9.75 Windows MSIs built and passed payload validation, then were
published to the restarted :5555 website with full HTTP GET hashes checked. The full server bundle
passed payload/header/source/version checks; Cloud API/Portal/website production images built
from that exact archive and the extracted Compose passed network/port regressions. Local HTTP
smoke tests verified the Portal routes/headers and exact offered x64 bytes. Server candidate:
`dist/server/peeronq-server-0.9.75.run`, SHA-256
`fbfb084e697f6c8ec9ef55bf5535d0a620c3da74b6d981a5dea4bb619db1cc4c`.
These are unsigned public-pilot packages, with no detached server GPG signature.
Local container STARTTLS to Resend also passed certificate-chain/hostname validation without AUTH
or mail submission. The operator's mail failure was on production; these local probes validate
the proposed fix only. At package validation time, production installation, SMTP authentication,
sender authorization and actual recipient delivery were untested. The later operator confirmation
above supplies account-level production evidence. Capabilities or a banner do not prove mail delivery.

## Operator mail configuration clarification - 2026-09-28

The operator clarified that Resend was already configured on an earlier release. This supersedes
the earlier report of having no SMTP service. A live read-only HTTPS capabilities request returned
Closed registration with registration/recovery/verification disabled; it did not inspect private
SMTP credentials or prove whether they can be recovered on the server.

The previously supplied `--disable-customer-mail` command explicitly clears SMTP host, username
and password in the protected environment. Do not repeat that flag for this installation. Restore
the existing Resend sender/API key from a protected backup, or provision a replacement sending key
in the same Resend account if the original is unavailable. Keep secrets off chat and Git.
Use Smtp, smtp.resend.com:587 with STARTTLS, username resend and a verified sender domain. Enable
Open registration with email verification only with real delivery configured. Validate the merged
production Compose configuration, then recreate only cloud-api to apply it. A same-version full
installer apply rejects replacement of the active release. Live mail acceptance remains pending.

## Customer auth navigation candidate - 2026-09-28

- Portal sign-in (`/`), registration (`/register`), password recovery (`/forgot-password`) and
  verification resend (`/resend-verification`) now have explicit client routes and real links.
  Direct navigation and history restore the matching form; first-field/success-heading focus
  follows navigation. `/verify-email` and `/reset-password` retain their real token endpoints.
- Successful registration/recovery/resend stays on its route with guidance and an explicit sign-in
  link. Existing backend capabilities, password rules and invitation tokens govern availability.
  Closed registration and unavailable email recovery are hidden and blocked on direct navigation.
- Auth endpoints, host-only cookies, CSRF, rate limits, verification, customer/Admin MFA, organization
  security and native accountless LAN behavior are unchanged. Native packages only advance their
  shared version; this patch is not a physical-desktop blur fix.
- The operator has no SMTP service. Production Open/InvitationOnly registration and recovery cannot
  work without real mail delivery; use Closed registration with mail Disabled until SMTP is configured.
  Navigation fixes do not provision SMTP or bypass the production startup requirement.

Local Portal typecheck/build and 82/82 tests, Cloud.Infrastructure 56/56 and Admin 49/49 passed.
Public-site typecheck/86 tests, deployment configuration/21 tests, Compose/bootstrap and native
UI/version invariants passed. Matching x64/ARM64 unsigned-public-pilot MSIs were built, payload-
validated and published to the restarted local :5555 website; both full HTTP GET checksums match.
The full 0.9.74 server bundle embeds that exact x64 package; checksum/payload/header checks and
production Portal/public-site Docker builds from its extracted source passed. Loopback HTTP probes
verified all six SPA auth routes/headers, deployed auth JavaScript and the offered client bytes.
Server candidate: `dist/server/peeronq-server-0.9.74.run`, SHA-256
`17d7587da5067fbb8cf7f6d7f70f6ac1e647c09511b5f82395fddd5f4be61626`.
The bundle has no detached GPG signature; do not infer signing approval or production installation.
Browser visual/pointer-hit acceptance was unavailable (no connected browser); DOM keyboard/history
tests passed. Live SMTP/mailbox acceptance and physical-device validation were not performed.

## Physical desktop sharpness investigation - 2026-09-28

The current 0.9.73 source was audited against origin/main. Physical viewer/sharer/server versions
and display/DPI measurements remain unverified; the requested Phase 0 gate blocks media edits
until the operator supplies them. A deterministic probe reproduced a single 80 ms render sample
reaching L3 at t=2 s and remaining there through t=60 s when RTCP feedback becomes unavailable,
despite sample expiry. With fresh network feedback it recovers at t=20 s. Automatic still caps
larger displays to 1080p; a real synthetic encode/decode probe shows that bitrate alone cannot
restore 1 px detail removed at that boundary. See PERFORMANCE_REPORT.md for scope and limits.
94 selected existing tests passed. No runtime changes, new release or physical blur-fix claim.

## Customer portal completion candidate - 2026-09-28

- Source 0.9.73 adds auth capabilities, verification resend, authenticated password change,
  server-driven registration/password/MFA UX and single-flight session refresh.
- Customer MFA defaults off while stored enrollment/recovery/organization requirements remain
  preserved. Admin MFA, native device identity, protocols and accountless LAN are unchanged.
- Installer upgrades preserve valid registration modes and accept explicit registration/MFA flags.
  Public Open registration requires verified email and configured SMTP outside development.
- Real local HTTPS/FileSink acceptance passed email/password/session, tenant/RBAC/policy,
  stored MFA policy transitions and actual rate-limit/lockout checks. This is not public SMTP proof.
- Self-service device claim remains blocked on a native short-lived ownership-proof exchange;
  no device-ID-only or raw-service-token UI was added.
- Live production email acceptance remains BLOCKED without approved mailbox/SMTP access.
  A local release candidate does not establish installation on the production host.

Candidate validation: Portal 58/58, Cloud.Infrastructure 56/56, public website 86/86;
114 real local HTTPS/FileSink requests passed. Full requested solution test execution had
885 passes, 4 failures and 5 skips; isolated Media retains two input-latency failures and one
live-TURN skip, while isolated Transport passes 7/7. Native/protocol code was not changed to
hide those results. Full build passed with no warnings/errors. Local :5555 serves and verifies
the matching 0.9.73 unsigned-public-pilot x64/ARM64 pair. The full server candidate is
`dist/server/peeronq-server-0.9.73.run`, SHA-256
`997841e6da699170e080c5949e19c59491c8050edfdfa29efce2a4031c28db1d`.
It has not been installed on the production host; live email and release-signing gates remain open.

## Operator deployment evidence — 2026-09-27

- The operator supplied the successful installer result: server 0.9.70 is healthy and active, with
  current release `/opt/peeronq/releases/0.9.70`. The log passed embedded Windows publication,
  proxy streaming/cache validation, public API/signaling routing and base website activation.
- Independent read-only HTTPS requests from the development workstation returned 200 for the
  public website, www, customer portal and embedded client version metadata. Public API liveness
  returned healthy and signaling readiness returned ready. DNS resolution and certificate
  validation succeeded without a TLS bypass or a forced local address.
- A complete public x64 MSI GET returned 200 and 78602240 bytes; SHA-256 matched the validated
  0.9.70 package: `d72f617c028ee3b435f3106fa6ce8a06420ed50c1a123f46655752a0a696a2bb`.
  Metadata reported 0.9.70 and the response retained `Cache-Control: no-store, max-age=0`.
- The operator supplied a single-host `/32` allowlist; Get-NetIPAddress confirmed that this
  workstation's active IPv4 address matches it. Admin 200 and Grafana/Prometheus 302 from this
  workstation are consistent with permitted operator access, not evidence of public exposure.
  A live denial check from outside that CIDR remains unverified: the separate web-tool attempt
  could not access those hosts but supplied no usable HTTP status. No allowlist change was needed.
- This confirms installation and the tested public endpoints, not release-signing approval,
  account authentication, TURN/media behavior or physical 4K/latency acceptance. Existing client
  installations do not upgrade automatically. No new patch or version bump was needed to record
  this successful deployment, and the immutable 0.9.70 bundle/release notes were not changed.

- A subsequent navigation check found the deployed 0.9.70 public JavaScript compiled with
  `https://portal.peeronq.com:8443`. Both Portal and Sign in used that value, inherited from the
  development Compose build arguments. Direct portless portal HTTPS returned 200 and the anonymous
  profile endpoint returned the expected 401; those checks did not validate an account login.
  Source release 0.9.71 fixes the inherited portal/download URLs. Its production activation and
  real-account sign-in still require operator verification; no public 8443 forwarding is needed.

- The long-session blur report exposed a separate client measurement bug: frame/render latency
  percentiles retained 120 samples without an age limit, and sparse new input samples prolonged
  earlier input spikes. A deterministic quiet-desktop reproduction reached quality rung 4 and
  stayed there after one transient delay. Source 0.9.72 expires individual latency samples after
  five seconds, retaining real congestion protection and fresh-network recovery requirements.
  This is a measured controller fix, not physical-session/4K validation. Both endpoint clients
  need updating; installing a server bundle alone cannot replace their running media code.

## Current investigation boundaries — 2026-09-27

- The unchanged 35 ms input-to-injection p95 gate was reproduced in Release on both dedicated
  input and primary fallback. The corrected probe localizes the excess to post-admission
  transport/delivery; a Windows timer-precision candidate was tested and removed after inconsistent
  results. No production latency fix, fork or transport migration was adopted. See the dated
  [pipeline measurements and options](PERFORMANCE_REPORT.md#input-latency-investigation--2026-09-27).
- The Node contract now requires 24.x (`engines`, `.nvmrc`, pnpm engine enforcement), and the
  catalog/lockfile use `@types/node` 24.19.0. Frontend validation used a verified Node 24.21.0 runtime;
  the host's default Node 25 installation was not replaced. Activate Node 24 before workspace commands.
- Exact restored dependency licenses/provenance and current advisories are recorded in the
  [release audit](competitive/THIRD_PARTY_LICENSE_AUDIT.md). The current TURN-over-TLS receive-loop
  advisory, native codec provenance and external legal review remain open. The separate
  [Node audit](../DEPENDENCIES.md) reports 9 existing findings (5 high, 4 moderate).

## Historical R0 snapshot — 2026-08-12

The following inventory, tool versions, phase decisions and test counts belong to branch
`feat/phase1-remote-view`, original snapshot commit
`ed8942e172367cf8c11d8968cb77005bae47bf54`. They are retained as historical evidence, not current
capability or validation claims. The pre-change worktree was clean.

### R0 repository shape

- 30 .NET projects in `PeerOnQ.slnx`: native Windows client, domain/application/infrastructure,
  media, Windows capture/input, transport, signaling, cloud/presence/admin/download/observability
  services, shared contracts, and 13 test projects.
- 10 pnpm workspaces. `artifacts/peeronq` is the offline product prototype,
  `artifacts/peeronq-admin` is the Admin SPA, and `artifacts/api-server` is a health-only skeleton.
  `artifacts/mockup-sandbox` is explicitly excluded from product commands.
- PostgreSQL, Redis, Nginx, coturn, OpenTelemetry Collector, Prometheus, Grafana, Loki, and Tempo
  are composed by the development/deployment definitions.
- WiX/MSBuild installer, update manifest, signing, SBOM, checksum, and release scripts exist. No
  official artifact was published during R0.

### R0 detected toolchain

| Tool | Detected |
|---|---:|
| Git | 2.53.0.windows.2 |
| Node.js | 25.2.1 (repository/container target is Node 24) |
| pnpm | 10.33.0 |
| .NET SDK | 10.0.302, pinned by `global.json` |
| Docker Engine | 29.7.2 |
| Docker Compose | 5.3.1 |
| PowerShell | 5.1.26100.8972 |
| WiX CLI | not on `PATH`; the project uses the pinned WiX SDK packages |

### R0 Phase 1-6 truth matrix

| Phase | Implementation at R0 | R0 evidence | R0 decision |
|---|---|---|---|
| 1 | Windows identity, permission flow, signaling, display capture and an attended view-only session pipeline; Phase 1 scope rejects input, file transfer, clipboard, and unattended access at UI/application/protocol/media boundaries | Release build and 455 .NET tests pass; the required two-physical-device visual run was not performed | `EXTERNALLY_BLOCKED` |
| 2 | R0 source contained later-phase input components, but that snapshot's supported Phase 1 runtime deliberately rejected `ControlInput`; no Phase 2 capability had been enabled | Phase 2 did not begin because the Phase 1 physical acceptance gate was blocked | `EXTERNALLY_BLOCKED` |
| 3 | TLS signaling, coturn REST credentials, ICE path reporting, reconnect and local controller | Targeted local TURN media checks pass, but the full local controller's first UDP relay-media test times out after 45 seconds; Phase 2 runtime is not accepted | `KNOWN_BROKEN` |
| 4 | Clipboard, file transfer, address book, groups, trusted devices, unattended policy and later full-control integration | Automated persistence/protocol coverage exists; public two-device collaboration/control cases are unverified | `PARTIAL / EXTERNALLY_BLOCKED` |
| 5 | Performance controls, installer/update verification, release builder, SBOM/checksum paths | Local build/tests exist; official signing, timestamping, hosted update and install/upgrade matrix require external inputs | `PARTIAL / EXTERNALLY_BLOCKED` |
| 6 | PostgreSQL/Redis cloud control plane, Presence, Admin, Downloads, observability and deployment topology | Code and local Compose topology exist; public staging, multi-host failover and signed-release ingestion are unverified | `PARTIAL / EXTERNALLY_BLOCKED` |

### R0 baseline results

- `pnpm install --frozen-lockfile`: pass.
- `dotnet restore PeerOnQ.slnx --locked-mode`: pass.
- Pre-change Release build: 30 projects, zero warnings/errors.
- Pre-change .NET tests: 452 passed, 2 intentionally skipped.
- Frontend: 67 tests passed across product/Admin; root typecheck and lint passed.
- Product/Admin/API builds passed independently. The original root build failed first on a required
  `BASE_PATH`, then on the throwaway sandbox's unrelated `PORT` requirement.
- `pnpm audit --audit-level high` and .NET vulnerable-package audit found no known vulnerabilities.
- Repository secret scan passed 841 files.
- Phase 6 development Compose config passed with the ignored local environment. Staging config
  correctly rejected missing staging-only identity inputs.

Machine-readable TRX and command logs were recorded under `artifacts/test-results/r0-baseline/`
and `artifacts/test-results/r0-final/`; generated reports are excluded from public source. See the
[R0 completion report](phase-reports/R0_COMPLETION_REPORT.md) for the original evidence.

### R0 version snapshot (superseded)

At R0 the native product and installer defaulted to `0.5.1` (`0.5.1-beta.1` informational), the
OpenAPI document was `0.1.0`, and workspace package manifests were mostly `0.0.0`. R0 recorded
release/version drift. The current client source contract above supersedes that diagnosis;
independent API/workspace package versions are not client-release versions.
