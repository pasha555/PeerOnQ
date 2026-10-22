# PeerOnQ performance evidence

Historical Phase 10 baseline measured on 2026-08-18 from the 0.9.21 x64 unsigned-development Portable Support publish.
Host: Windows 10.0.26200, AMD Ryzen 7 3700X, 16 logical processors, 32 GiB RAM.

## Temporary 1080p product scope — 2026-10-18

The user explicitly deferred 4K. Canonical source now selects 0.9.77 for a private unsigned
candidate; public binary distribution remains gated. Streaming has a 1080-pixel short-edge cap
and retains aspect ratio/no upscaling. Automatic/old Native/1440p/2160p preferences normalize before
outgoing signaling and incoming capture; Windows FrameScaler independently enforces the cap.
The picker offers Automatic/1080p, and High Quality is 1080p/30 FPS/12 Mbps. Low-level 4K stress
fixtures remain diagnostics; they do not advertise 4K or supersede the failed measurements below.

The pre-broker-packaging strict build passed in 20.71 s with zero warnings/errors. All 19 post-cap projects completed:
1,310 total / 1,283 passed / zero failed / 27 explicit environment skips. Application 234/234,
EndToEnd 165/165, focused actual capture/resolution 62/62 and adaptive 40/40 passed. Web 228/228,
lint/typecheck/build passed. The actual 1080p hardware continuity row is 600.68 s / 17,946 frames /
29.88 FPS, with 538 hardware / zero CPU decoder samples; start/max/end private bytes were
453,816,320 / 453,816,320 / 319,676,416 (not a memory-plateau proof).
Actual SCTP disk transfers completed 1 GiB each in 72.1/60.5 s (primary/dedicated bulk), chunks
245,760/262,144, native QUIC false; exact source/destination SHA256 was
`f33f041361cbb11323df3f663a2a848a7fcd152214acf3fe7c5ead9082d43612`.
Separate 1 MiB co-load input tests passed unchanged 35 ms gates: dedicated baseline/bulk p95
17.1/18.3 ms and fallback 17.8/15.8 ms. These are local synthetic input sinks, not physical WAN.
An initial filename-based filter ran zero tests (not a pass); actual class filters then ran 62.
The native UI gate's obsolete 4K assertion was corrected and its actual rerun passed. A Portal
test's focus assertion now awaits the passive effect; final 228/228 passed, original failure kept.
The first Media attempt accidentally set the TURN-only fixture opt-in without TURN credentials;
two tests failed before file transport. That TRX/log/exit record is retained separately in
`failed-media-turn-configuration/`; after correcting only the runner environment to direct SCTP,
the whole Media project passed 315/316 with one explicit TURN skip. No file size, input threshold
or product source was changed by the fixture correction. Authoritative final 19 TRXs remain
under `whole/`; prior attempts do not inflate counts. All 27 primary skipped identities now
match actual supplemental Passed rows
(Linux 6, Cloud 16, Signaling 4, TURN 1), zero unmatched. Primary skips stay recorded; repeated
supplemental executions do not increase unique coverage. The exact reconciliation is
`app-updates/validation/2026-10-18-1080p-scope/whole/final-evidence.json`.

The additional actual 1 GiB native QUIC payload-contention test passed 1/1 with zero skips:
25.2 MiB/s payload and interactive p95 0.5 ms over 2,430 samples. This is a transport-payload
coexistence test, not a disk-file/SHA256 result; the SCTP disk results above remain separate.
The current Ubuntu 24.04 viewer package passed 48/48 (22 App, 14 Platform, 12 Infrastructure),
normal close/startup cancellation and all 194 input hashes with zero drift. Its private unsigned
54,105,863-byte archive is under `app-updates/linux/release-0.9.77-preview/`, SHA256
`2af4e49e5537f5a6839ac4af98277d244e9cf1c214890f6061abbb9a840ddd95`.
Current-source TURN UDP/TCP and signaling restart passed 3/3 without skips; both peers selected
Relayed paths. Four valid TURN authentication protocols and invalid/expired rejection passed.
The first context-preflight variable collision occurred before containers/tests and remains
preserved alongside the corrected functional run; no host CA or firewall changes were made.

The original private unsigned x64/ARM64 MSIs passed payload checks but exceeded the unchanged
96 MiB size gate: 112,365,568 / 103,153,664 bytes. Removing unused WinRT references only from
the broker still produced a failing 107,302,912-byte scratch MSI. Broker-only trimming then
produced an 85,422,080-byte scratch with payload/size checks passed and zero warnings; it is
not the final paired package set. Original artifacts remain unchanged. Broker publish
settings now exclude unused WinRT SDK references, trim its private runtime and make compiler/
linker warnings fatal; Windows TFM, multifile layout and security isolation remain unchanged.
The 41 functional inputs remain frozen, with seven broker-packaging inputs tracked separately.
The 1,310-test totals above are not a rerun of that packaging change. The post-packaging whole
strict build passed in 33.21 s with zero warnings/errors; ordinary broker tests passed 12/12.
The final trimmed broker passed six managed groups on shared .NET 10.0.13; actual self-contained
non-SCM apphost exit 2 was expected. SCM/LocalService/trimmed-worker DI are not established.
The final x64/ARM64 MSIs passed payload and existing 96 MiB size checks at 85,467,136 / 77,930,496
bytes; exact SHA256 values are recorded in [CURRENT_STATE](CURRENT_STATE.md). The separate private
unsigned Portable Support pair passed payload validation. The immutable full server bundle is
163,670,630 bytes, SHA256 `c6e1af61945dcb74bf8dfa125a3de645c6e764bddc14e9aa6ec48827507cd774`.

Actual extracted-bundle attempt 6 passed in 119.4647906 s / 104 commands / 5,964 harness assertions
(not test cases): nine non-root images, six ASP.NET 10.0.13 runtimes, restricted migrations twice,
delivered role/runtime permissions and default-false audio, actual Cloud health/401, three web
surfaces/six Portal SPA routes and full HTTP 200/hash checks of both MSIs plus version,
classification and served checksums. All 744 extracted inputs had zero drift; cleanup errors were
zero. Failed attempts 1–5, the cheap internal-only versus owned-frontend loopback diagnostic and
external Docker Desktop restart remain retained. Only fixture/network provisioning changed;
the .run/product bytes did not. No production installer execution is claimed.

Final writer/sealer succeeded: 1,180 curated proof files, 48 frozen source inputs / zero drift,
12 exact artifacts and the unchanged primary/supplemental identities above. The seal-summary
SHA256 is `a7d0a25671232e822e950c3ab3489961082c746df5fcac5189c5c79b3b35077f`; evidence-sums
SHA256 is `86444354ca840e9fa6342c5d7adf58a3868b15147134488a2aa1b1f66b8047a5`.
Both were copied byte-identically beside the server .run with candidate validation, INSTALL and
immutable release notes. Private/unsigned remains true, production-ready/published false; no
production deployment, source commit or GitHub push occurred.
These results do not establish production approval, physical-device parity or WAN performance.

## Encoder callback and exact SIMD checkpoint — 2026-10-18

Pre-cap decision: **hold the next product release**. At this checkpoint source was 0.9.76; no new
MSI/server patch was created, published or deployed. The 100 ms 4K observation is still unmet:
the latest capture-inclusive model measured whole p95 224.02/102.57 ms for landscape/portrait,
and the final current-source I420-only landscape case measured 105.28 ms. Functional test
success does not waive that performance condition. All measurements below retain their scope
and original failed windows; physical 4K display/WAN and release-signing gates remain open.

| Final current-source validation | Actual result |
| --- | --- |
| Whole strict build / 19 test projects | Zero warnings/errors; 1,246 passed, zero failed, 28 explicit environment skips out of 1,274 |
| Actual environment supplements | Ubuntu 48/48; Cloud Infrastructure 63/63; distributed signaling 3/3; audio 1/1; TURN UDP/TCP and restart 3/3, zero skips |
| Web | 228/228; workspace typecheck/build and lint passed |
| Legacy SCTP | Two 1 GiB disk transfers, 67.5/61.5 s, exact source/destination SHA256 |
| Hardware 1080p | 600.24 s, 17,987 frames, 29.97 FPS; 538 hardware / zero CPU decoder samples |
| Linux package | Private unsigned x64 viewer preview; 48/48, 194 inputs / zero source drift; physical capability gate open |

Final exact-identity reconciliation matched all 28 primary skipped cases to actual supplemental
Passed executions (28 covered, zero unmatched); primary skip records remain intact. Test display
name plus declared class/method identity were checked; repeated supplemental passes do not inflate
unique counts. All 14 frozen source hashes match, and Linux/E2E/Media outputs retain the exact .6
core. `whole-current-source/final-evidence.json` records the identities, actual result-row outcomes,
TRX hashes and scopes. xUnit's TRX summary may report `notExecuted=0` despite skipped result rows;
the actual rows and total-minus-executed establish the 28 skips, not that summary attribute alone.
Final sealing rechecked all 28 primary/supplemental TRX hashes, 14 source hashes and the final
Linux archive. The 504-file evidence inventory is `FINAL_SHA256SUMS.txt`, SHA256
`c7a08901d5d71ef5016dfcd2bb99abfbe0cfc41593e7207e8a0a4782efd5cfd9`;
`RELEASE_DECISION.json` explicitly records HOLD, unchanged 0.9.76 and no new patch/deployment.

The isolated final TURN fixture used the exact current .6 Media output and root-built Signaling
server without rebuilding product assemblies. UDP/TCP video each passed with both peers actually
`Relayed`; UDP pause/recovery also passed. Restart/re-authentication rejected stale server state.
Valid UDP/TCP/DTLS/TLS authentication passed, while invalid/expired credentials were rejected.
All three TRX rows passed without skips; owned containers were verified absent, environment
restored, and no host CA import/firewall change or historical key/state access occurred.
An initial recipe-writing parser error happened before execution and remains in preparation notes.
The single actual functional run passed; curated evidence is in `phase3-current-source/`.

Earlier diagnostic checkpoints and the final whole-suite measurements follow. They are retained
to explain selection and regressions, rather than presenting a favorable historical row as current.

Canonical source version remains 0.9.76. This checkpoint does not create or approve a release.
The encoder now receives bounded MF callbacks instead of polling with `Sleep(1)`; original
two-second input and 750 ms output deadlines, codec settings and fallback remain. The new queue
retains at most eight events, fences callbacks before native shutdown, and retains the callback
through failed/partial subscription cleanup. LowLatency and CommonLowLatency read back true;
RealTime is unsupported on this AMD MFT. No missing codec flag was assumed or forced.

I420 and NV12 packed-BGR conversion now use guarded AVX2 operations with the original scalar
fallback. Operation order, double versus integer arithmetic, rounding, clamping, output ownership
and the maximum four conversion workers remain unchanged. A private proof covered all 16,777,216
YUV triples per arithmetic plus narrow/padded layouts with exact bytes. The production exhaustive
tests also passed: VP8/converter16/16, combined native/callback/layout33/33; forced managed-intrinsic
fallback12/12 I420 and9/9 NV12. Whole strict CI/analyzer build passed in32.62 s, zero warnings/errors.

The original two production-coordinator theories were rerun unchanged, including authenticated
signaling/feedback, original acquisition times, observation windows and100 ms target:

| Native pixels / codec | First5s p95 | Entire-session p95 / FPS | Final5s p95 / FPS |
| --- | --- | --- | --- |
| 3840x2160 / AMD H.264 + D3D11 |126.49 ms, missed|93.94 ms /13.63|93.94 ms /18.80|
| 2160x3840 / VP8 |107.11 ms, missed|73.35 ms /19.40|60.97 ms /29.80|

All whole/final windows met100 ms; neither startup window did. Landscape30 FPS remains unmet.
Clock uncertainty maxima were0.41/0.45 ms. Landscape color conversion mean was4.26 ms versus
13.68 ms in the preceding whole-suite observation; encode mean remained44.19 ms, of which
38.97 ms is the composite native/copy/event-wait portion. These are headless, gray, local
coordinator tests with a validated output-buffer boundary, not physical display presentation,
moving-desktop quality or WAN evidence. Earlier failures below remain part of the record.

Private callback A/B warm30 FPS mean improved36.765 to27.053 ms, but p95 only44.82 to41.90 ms.
Cold10 FPS callback initialization worsened234.22 to791.78 ms; that regression is retained and
is not waived by the successful steady-state windows. Private color benchmarks retain every
cold/warm/content/orientation row; output allocation is unchanged and no pooling benefit is claimed.

A separate faithful adaptive capture model then ran the production BGRA converter and capture
limiter before allocation, stamping acquisition before conversion. Its coordinator body differs
only in method name, capture factory and explicit fixture label, verified by a round-trip guard;
199 mapped source inputs had zero drift. Both functional cases passed, but all latency windows
missed the unchanged 100 ms target:

| Production-converter model | First 5 s p95 | Whole p95 | Final 5 s p95 / FPS | Capture conversion lifetime p95 |
| --- | --- | --- | --- | --- |
| Landscape H.264 | 158.78 ms | 120.72 ms | 110.44 ms / 9.80 | 35.47 ms |
| Portrait VP8 | 223.84 ms | 138.23 ms | 111.16 ms / 27.00 | 49.57 ms |

This model excludes WGC GPU readback and physical presentation. Source acquisition was not moved
past conversion. The 30 Hz native input is immutable; every accepted output owns fresh I420 bytes.
The source tick limiter runs before conversion/allocation. Allocation was 16.660/25.117 GB over
the respective observations. These results qualify the faster I420-only stress results above;
neither fixture is a substitute for physical desktop validation. A post-test PowerShell 5 report
parser failure was retained and recovered read-only; tests/build/process-exit checks had completed.
See `faithful-before-capture/` for the exact TRX, source/body guards and recovery classification.

The native BGRA converter was still two scalar image passes. A private bounded row-parallel
candidate preserved exact bytes in 138 layouts/content cases and reduced warm wall/p95 latency
in all eight 4K scenarios, but used about 1.5–3 times the process CPU and regressed several cold
calls. It is a pipeline candidate, not a CPU-saving claim; the full capture model decides adoption.

That row-parallel candidate was rejected after its capture-inclusive landscape whole p95 worsened
to 138.32 ms and portrait final FPS fell to 9.80. The original scalar implementation was restored.
A private scalar fusion passed exact comparisons but had only modest gains and one regression;
it was not adopted. Neither rejected experiment changes the target or erases its failed windows.

The adopted native capture path instead uses guarded, single-core AVX2 with the literal original
scalar fallback. Its private proof passed 4,303 cases, covering all 16,777,216 RGB triples plus
nonuniform 2x2 chroma, alpha, unaligned padding and vector/tail combinations. Private landscape/
portrait means fell from 28.753/28.488 to 8.486/8.784 ms, with process CPU 28.906/31.250 to
10.938/9.375 ms. Output allocation is identical. The production narrow suite passed 55/55 with
AVX2 and 55/55 in a separate intrinsics-disabled process; strict build had zero warnings/errors.

The identical 201-input capture-inclusive model still does not establish 100 ms acceptance:

| Adopted capture AVX2 model | First 5 s p95 | Whole p95 / FPS | Final 5 s p95 / FPS | Capture lifetime p95 |
| --- | --- | --- | --- | --- |
| Landscape H.264 | 131.29 ms | 113.47 ms / 13.94 | 141.08 ms / 16.40 | 17.27 ms |
| Portrait VP8 | 106.64 ms | 97.71 ms / 18.53 | 134.40 ms / 24.20 | 16.09 ms |

Only portrait whole-session p95 met 100 ms. Both startup/final windows and landscape whole missed.
The source, fixture, original body/windows and exact .6 dependency were frozen with zero drift.
These loops combine both endpoints in one process: fresh capture I420, decoded NV12/I420 and
presentation BGR total 49,766,400 managed bytes per matched frame, about 1.493 GB/s at 30 FPS.
Source audit found no redundant full-frame copy in the default Windows composition. Existing
ownership protects asynchronous consumers; pooling requires an explicit release/lifetime contract.
High allocation correlates with pauses, but these aggregates do not prove the cause of slow frames.
Sender pipeline p95 starts after capture conversion and ends after synchronous RTP submission;
subtracting lifetime encode means from that rolling p95 would not isolate queue wait.
Private bounded per-frame tracing then recorded the same authenticated frame sequence through
sender stages and the existing decoded/presented callbacks. Its 2,048 preallocated rows retain
no payload or hot-path strings; files are written after both peers stop. Four private diagnostic
source changes are declared separately from the unchanged fixture/clock/window arithmetic.
Both instrumented cases passed with zero drift in the 201 production inputs.

| Per-frame sender stage, p95 | Landscape | Portrait |
| --- | --- | --- |
| Pipeline entry to dequeue | 7.325 ms | 9.807 ms |
| Complete codec call | 54.217 ms | 54.513 ms |
| Application AEAD | 0.076 ms | 0.052 ms |
| Synchronous RTP submission | 0.418 ms | 0.382 ms |
| Cumulative GC pause during sender pipeline | 1.319 ms | 2.209 ms |

These observations localize material tails to codec service/wait, rather than normal sender
queueing, encryption or synchronous transport submission. They do not isolate pure GPU work.
For example, portrait sequence 445 took 152.948 ms in its codec call with 1.906 ms cumulative GC
pause; sequence 437 took 103.657 ms with zero measured GC pause. Native MF substages and VP8
reconfiguration/keyframe flags were then traced in a separate isolated run.
No latency target, source acquisition timestamp, quality setting or release gate was changed.

That second trace passed strict build and both functional cases with zero production-input drift.
MF steady initialization count stayed one; input/output PTS and durations matched. HaveOutput
wait dominated (mean 31.238 ms, p95 40.233 ms); NeedInput mean was 0.008 ms. Format/sample-copy/
zero/ProcessInput/output-read means were 4.531/2.684/1.131/3.451/0.188 ms. FPS changes also had
long ProcessInput calls, while separate frames showed copy/format tails; no single omitted
application sleep explains these observations. VP8 reinitialized 11 times across 407 calls;
initialization cost 31.566–78.068 ms, mean 45.782 ms. Removing these resets is a concrete
candidate, but noninitializing VP8 calls also reached 122.528 ms with zero measured GC pause.
Instrumented whole p95 was 118.59/104.86 ms. These are diagnostic local workloads, not new
production acceptance or evidence that reconfiguration alone closes the complete 4K target.

The selected VP8 fix uses native ConfigSet with a retained configuration and a fixed 90 kHz
timebase from initialization. Each accepted target change still requests the same recovery
keyframe; unsupported/failed reconfiguration recreates the codec with the desired targets.
The original quality, bitrate, dimensions and PLI tests passed unchanged, plus real-context
reconfiguration, exact fractional-duration/monotonic-PTS and six failure/recovery cases: 28/28,
zero skips, strict build zero warnings/errors. Actual Windows libvpx reports v1.9.0; the loaded
vpxmd DLL SHA256 is `9ef05f9448c23de0043c1b9da7d84fd3bc07d3a7aaeb57ce87ca68a52b8622c7`.
Seven actual ConfigSet calls retained one initialization across the tested FPS/bitrate sequence;
resize still required a second initialization. This identity check does not establish complete
native source/patch provenance. A first test-analyzer failure was corrected and its log retained.
The following fresh, uninstrumented capture-inclusive checkpoint retained the same fixture,
clock/windows, exact .6 core and original coordinator body guard. Strict build and both
functional cases passed; all 201 production inputs had zero drift. It did not close latency:

| After VP8 reconfiguration | First 5 s p95 / FPS | Whole p95 / FPS | Final 5 s p95 / FPS |
| --- | --- | --- | --- |
| Landscape H.264 | 131.55 ms / 14.20 | 224.02 ms / 10.34 | 158.19 ms / 10.40 |
| Portrait VP8 | 157.26 ms / 16.20 | 102.57 ms / 13.89 | 49.49 ms / 9.60 |

Only portrait's final window met 100 ms. Capture-conversion lifetime p95 was 54.08/15.89 ms;
sender pipeline-entry-to-send p95 was 105.22/20.70 ms. Allocations were 16.211/21.747 GB.
The H.264 path was unchanged by the sole VP8 source change, so this separate run does not
prove ConfigSet caused its landscape slowdown. It also does not erase the slow observation
or establish a 30 FPS result. No additional tuning, target relaxation or repeated-until-pass
selection followed. Exact logs and body/source guards are in `faithful-after-vp8-config/`.
The new release remains blocked on the measured 4K performance condition.

A final native-configuration guard then addressed a separate, proved compatibility hazard:
the generated encoder configuration occupies 384 bytes, while distro libvpx 1.14.0 has a larger
layout and copies its complete defaults before checking initialization ABI. The encoder now
rejects unknown versions before allocating a context/configuration or calling ConfigDefault.
The exact pinned Windows 1.9.0 path remains accepted. Final actual Windows VP8 tests passed
37/37, zero skips, strict build zero warnings/errors. Unsupported-to-supported retry preserves
desired targets and a fresh decodable keyframe. This guard is a layout check, not complete
native source/security provenance. Linux remains a viewer; no incompatible encoder probe ran.
The capture-inclusive checkpoint above predates only this initialization guard. Complete
current-source tests follow it. The final Linux checkpoint rebuilt the guarded source from 194
inputs with zero drift: Ubuntu 24.04 viewer/Cloud-client 48/48, zero skips, payload/ELF/manifest/
all SHA256/notices and actual normal/startup-cancel checks passed. The final private archive is
54,105,078 bytes, SHA256 `8a5792ef9d587352573512d872c7f9cb5ac956a5267724b30b50e7f7b15778bd`,
under `app-updates/linux/current-source-final-2026-10-18/`. libvpx identity was 1.14.0; no encoder
configuration was allocated or invoked. Linux hosting, physical interop and publication remain
unproved. Three earlier private source checkpoints remain separate, with their original hashes.

The final guarded source then passed the whole strict build in 32.00 s, zero warnings/errors.
All 19 projects completed: 1,274 total / 1,246 passed / zero failed / 28 explicit environment skips.
The actual audio opt-in subsequently passed 1/1 (two lifecycle iterations, seven captured frames
each, immediately zeroed, silent playback). Supplemental Linux/live-service coverage is recorded
separately; it must not remove the primary skip records or inflate unique test counts.

The current encrypted 1080p soak measured 600.24 s / 17,987 frames / 29.97 FPS; including setup,
18,108 frames over 605.99 s / 29.88 FPS. AMD hardware H.264 and D3D11 decoding stayed active:
538 hardware samples, zero CPU samples. Private bytes were 356,823,040 / 384,401,408 /
322,195,456 at start/max/end; these three aggregates do not prove a memory plateau.
Both actual 1 GiB SCTP disk transfers passed with SHA256
`f33f041361cbb11323df3f663a2a848a7fcd152214acf3fe7c5ead9082d43612` equal at source/destination:
primary 67.5 s / chunk 245,760, dedicated bulk 61.5 s / chunk 262,144, native QUIC false in both.
Separate strict 35 ms input gates passed: fallback baseline/bulk p95 16.7/15.7 ms,
dedicated 16.3/16.2 ms. This does not prove simultaneous 4K plus 1 GiB or WAN performance.

| Current original I420-only coordinator | First 5 s p95 / FPS | Whole p95 / FPS | Final 5 s p95 / FPS |
| --- | --- | --- | --- |
| Landscape H.264 | 123.65 ms / 15.20 | 105.28 ms / 13.35 | 102.23 ms / 15.00 |
| Portrait VP8 | 43.57 ms / 21.60 | 84.36 ms / 17.44 | 66.50 ms / 16.80 |

Landscape missed every 100 ms window; portrait met each window. The separate direct 4K row
measured landscape 99.08/105.17/111.16 ms and portrait 142.22/89.14/108.22 ms startup/whole/final.
All those tests passed their functional assertions; the unchanged 100 ms observation is separate.
They bypass BGRA acquisition/conversion and cannot supersede the capture-inclusive failures.
The transport 7/7 run included only a 64 MiB native-QUIC throughput row (25.2 MiB/s,
interactive p95 4.2 ms / 163 samples); it must not be relabeled as another 1 GiB transfer.

The isolated live-service supplement subsequently passed all 63 Cloud Infrastructure tests
and all three distributed-signaling tests, zero skips, against temporary authenticated
PostgreSQL/Redis. Exact actual TRX outcomes were checked in addition to counters; no build or
restore replaced the root-tested assemblies. Owned containers were verified absent and process
environment restored. An earlier preflight stopped before tests because Docker reported
`--tmpfs` under `HostConfig.Tmpfs` rather than `Mounts`; its failed record/clean cleanup remain.
The corrected preflight verifies exact tmpfs keys/options and rejects persistent/host mounts.
Local TURN/restart supplementation is separate from these passed database checks.

Evidence: ignored `app-updates/validation/2026-10-18-release-performance-gate/`, including exact
source hashes, strict build, production TRX, private raw results and narrow rollback sources.
An initial command omitted the hardware opt-in and recorded skips (`combined-native` and
`combined-4k-stress`); those files are not hardware performance evidence. The corrected
`hardware-native`33/33 and `hardware-4k-stress`2/2 contain the actual executions.

## Latest .6 SCTP/UHD/Linux validation — 2026-10-18

The .5 observations below identified two additional defects. Its first RTCP packet compared
arbitrary RTP/arrival clock phase with zero; actual-package controls passed only three of nine.
One first-transit assignment passed all nine ordinary cases, preserving real jitter and natural
timestamp rollover, consistent with [RFC 3550 section 6.4.1](https://www.rfc-editor.org/rfc/rfc3550.html#section-6.4.1).
Maintained .6 preserves six earlier patches and adds this seventh; two clean packages are
byte-identical, builder 22/22 and compatibility 7/7 pass with all 6,674 API entries unchanged.
Plain solution restore and the whole strict Release CI/analyzer build passed with zero warnings/
errors (77.31 seconds). Windows x64/Media.Tests/EndToEnd.Tests hashes match exact .6 and all ten
Windows notices validate. After final native normalization the strict whole build passed again
in 19.24 seconds, zero warnings/errors. The pre-D3D whole suite passed 1,179/1,206, zero failures
and 27 explicit environment skips across 19 projects. Nine durable packaged RTCP regressions
passed. The fresh 41-project managed vulnerability inventory reports zero vulnerable entries.

Actual native twelve-format metadata also proved the sustained bitrate excess was filler, not
an IDR-only burst. Dynamic 20 FPS/3,000 kbps produced 56,256 bytes: 54,316 filler, 1,934 picture
and six AUD; dynamic 15 FPS/2,000 kbps produced 50,006 bytes with 48,065 filler. Mean bitrate
readback matched the request and rate-control mode was CBR zero. Fresh reconstruction at
20 FPS/3,000 kbps produced the expected 18,756 bytes. The unchanged twelve-output/seven-fresh-
recovery checks passed in that metadata-only run. A conservative public-encoder filter now
removes only fully recognized valid Annex B type-12/F0/NRI0 filler before encryption, preserving
all other bytes and uncertain inputs. The separate pure-parser/actual raw-versus-filtered pixel
checks below include actual native output; the initial 17 pure-parser passes alone did not prove
removal on this driver. The completed observations below leave 100 ms performance acceptance open.

Earlier Ubuntu .6 validation passed App 22, platform 14 and scoped Cloud 12: 48/48, zero
skipped. Retained notice-refreshed private artifact:
`app-updates/linux/transport-10.0.17.6/peeronq-0.9.76-linux-x64-unsigned-preview.tar.gz`,
54,101,175 bytes; SHA256
`f0a15154f39e7dbd26e6315beea5caa45eddccb2b9fe125534725d82854ea00d`.
Exact .6 core, ten notices, safe payload/per-file integrity and normal self-contained launch/close
passed. The actual final extracted archive also passed cancellation while its pinned keyring helper
was pending: app exit zero within ten seconds, helper terminated, UID 10001, shared .NET lookup off.
All three validation phases exited zero and all processes stopped before Windows timing resumed.
One of 192 inputs drifted: root updated `THIRD_PARTY_NOTICES.md` during the snapshot. The original
diagnostic archive/snapshot are retained; that exact notice was refreshed, per-file checksums/archive
regenerated and integrity/normal-close/startup-cancel rerun. All 191 other source hashes and every
other payload content hash match. Neither zero drift nor a compiled-code rebuild is claimed.
The earlier .5 archive below is retained historically. The final shared-contract rebuild is recorded below.

The first .6 three-case native/coordinator run passed functionally, but every real native-output
line reported zero removed filler bytes. Raw/filtered pixel equality was therefore vacuous and
does not prove this optimization works on the actual AMD stream. The 17 pure parser passes do
not override that finding. Actual leading/trailing zero and NAL/RBSP metadata were then classified.
A subsequent whole-suite attempt was explicitly
interrupted after the no-op finding (exit 1, partial results retained); it is not a full-suite pass.
No threshold was widened and no passing performance claim follows from that incomplete attempt.

The metadata-only classification found the actual AMD sample's NAL12 body was entirely FF bytes,
without the standard RBSP stop bit (for example 54,691 FF, zero stop-bit/other/trailing-zero bytes).
The strict parser was correctly conservative. A separately named trusted-native-sample entry point
now normalizes only that exact nonempty F0/NRI0 all-FF padding at the authoritative sample EOF,
still requiring known units and a VCL picture. Generic strict parsing and all 17 malformed/identity
cases are unchanged; ten additional native-boundary cases passed. This is an explicit local-driver
compatibility exception, not a remote-input validation rule or standards-valid-RBSP claim.

The exact native twelve-format test then passed with a hard assertion that bytes were removed.
All 12 actual frames removed padding: dynamic 20 FPS/3,000 kbps IDR 56,302→1,606 bytes;
interframe 56,256→1,940; 15 FPS/2,000 kbps 50,006→1,941; resized 1080p 12,506→531.
Public encoder and raw-baseline filter counts agreed. Raw-versus-filtered decoded pixels matched
12/12, independent fresh recovery 7/7; public decode 12/12/fresh 7/7 and matched current types
12/12 passed. Original native PTS/durations remained monotonic, with three dynamic changes,
zero fallbacks and three initializations across resize/return. Strict build zero warnings/errors;
27 parser cases plus this actual-native case passed 28/28. This proves removal and unchanged
pictures, not a universal compression ratio or 100 ms end-to-end result.

The normalized .6 paired-coordinator follow-up passed 2/2 functionally, with actual authenticated
feedback and no signaling errors. Every unchanged 100 ms observation window still missed:

| Dimensions / codec | First 5 s frames/ages; fps; p95 | Whole frames/ages; seconds; fps; p95 | Final actual 5 s frames/ages; fps; p95 |
| --- | --- | --- | --- |
| 3840x2160 / AMD H.264 | 74/74; 14.60; 140.29 ms | 349/349; 30.42; 11.44; 129.30 ms | 55/55; 10.80; 122.33 ms |
| 2160x3840 / VP8 | 75/72; 14.80; 167.32 ms | 528/525; 30.94; 17.03; 115.26 ms | 86/86; 17.00; 137.98 ms |

Clock maxima were 0.42/0.67 ms. Landscape initialized once; portrait retained native dimensions
through software VP8. Quality messages received were 74/81; final target ten FPS, reason
`viewer_frame_age`. The former 4,990 ms false jitter did not recur; sampled jitter maxima were
27.64/23.78 ms and RTT 18.30/12.82 ms, with zero packet loss, capture queue <=1 and sampled
pending ThreadPool work zero. Actual landscape encoded bitrate finished at 20.66 kbps for this
gray scene, mean 284.32 encoded bytes/frame, versus the earlier padding-dominated ~2,400 kbps.
That demonstrates removed waste for this content, not a general compression/Internet guarantee.

Mean landscape native encode/software H.264 decode/color times were 38.26/27.02/13.37 ms.
Whole private bytes start/max/end were 681,639,936/707,510,272/476,106,752 and
969,773,056/1,096,826,880/658,640,896. Cumulative allocation was
24,916,261,144/31,856,329,568 bytes (not live heap), GC0/1/2 279/272/271 and 411/383/383.
All seven source hashes and exact .6 runtime hash were unchanged pre/post. Pixels/native UHD
and feedback are functional; sustained 100 ms, physical 4K capture/display and 30 FPS remain
unproved. Software decode and allocation pressure are material measured costs.

The later full-suite .6 run retained both coordinator cases and passed all 112 EndToEnd tests.
Landscape first/whole/final-five p95 was 140.64/118.59/120.74 ms, all above 100 ms;
whole 348 frames/348 authenticated ages, 31.13 seconds, 11.15 FPS. Portrait first/whole/final-five
p95 was 75.03/87.03/57.28 ms, all below 100 ms in this later run; whole 519 frames/512 ages,
31.17 seconds, 16.62 FPS. Clock maxima were 0.33/4.31 ms. These later portrait passes do not erase
the earlier all-window misses or establish sustained physical-screen performance. The same
whole suite passed strict input baseline/bulk p95 15.638/17.319 ms fallback and 15.740/16.235 ms
dedicated; the separate final 1 GiB/input run follows. Hardware 1080p encrypted delivery passed 120 frames/5.69 seconds,
21.08 observed FPS. This short check is separate from ten-minute continuity acceptance.

The final produced .6 disk-transfer gate passed both 1 GiB SCTP rows, 164.57 seconds wall:
legacy-primary 84.2 seconds with 245,760-byte chunks, negotiated bulk 64.5 seconds with
262,144-byte chunks. Each copied exactly 1,073,741,824 bytes and matched full source/destination
SHA256 `f33f041361cbb11323df3f663a2a848a7fcd152214acf3fe7c5ead9082d43612`.
Native QUIC was false; no TURN or chunk override was set. Exact .6 core hash was unchanged
before/after. TRX recorded two executed/two passed and normal successful completion; the
Windows PowerShell process wrapper returned a null ExitCode, so no numeric process exit is
invented. These are local disk/loopback observations, not guaranteed Internet throughput.

The immediately following unchanged strict input gate passed 2/2 (18.64 seconds wall).
Dedicated baseline/active-bulk event-to-injection p95 was 15.996/16.780 ms, maximum
16.057/18.180 ms, inflation +0.784 ms. Primary fallback p95 was 17.383/16.991 ms, maximum
25.296/17.893 ms, inflation -0.392 ms. Each scenario validated three authenticated ACKs.
Both rows reached the original 35 ms baseline/bulk, <=10 ms inflation, <250 ms bulk maximum,
route-count, <=65,536-byte buffered input, signed clock, reverse-pressure expiry and continuing
video assertions. Exact route/buffer counts were not printed; only their original assertions are
claimed. Exact .6 core remained unchanged. This does not establish that the earlier intermittent
54.853 ms dedicated-input failure's underlying cause has been fixed.

A private optional D3D11 decoder proof (no product changes) passed exact visible pixels 12/12
and independent fresh recovery 7/7, including 1920x1088/pitch-2048 NV12 repacked to visible1080p.
Ten UHD CPU/D3D total min/mean/max were 35.986/48.958/77.277 and
9.556/33.698/108.821 ms; the eight UHD rows excluding first/resize-return cold rows were
35.986/43.756/52.985 versus 9.556/16.374/26.745 ms. All12 means were45.259/33.741 ms.
The initially sampled owned-PID VideoDecode activity was zero: DXGI output alone does not prove
hardware decoding. Three private API-signature compile failures and a null-ExitCode runner error
are preserved; the native harness printed PASS but the wrapper exited1. This is promising private
readback evidence alone did not justify production adoption. The subsequent bounded decode-only
proof disposed the encoder before either phase and exited zero. CPU: 313 exact frames/12.002 s,
all min/mean/max29.352/35.652/76.519 ms, cold35.833 ms. D3D11:401 exact frames/12.012 s,
all9.212/27.125/85.515 ms, cold85.515 ms, warm9.212/26.979/44.036 ms.
Owned-PID AMD `Video Codec 0` activity was zero in all11 CPU samples, versus12 D3D samples
mean41.353%, maximum47.339%, minimum5.197%; `Video Decode 1` stayed zero. Thus the original
counter label missed this driver's actual engine. Output was NV12, BindFlags.Decoder,
Usage.Default, CPUAccess.None; hardware H264 VLD configurations11. Readback mean23.377 ms
dominates the owned copy2.953 ms. This is23.9% mean decoder/readback benefit, slower cold start,
not a100 ms session result. Subsequent production integration/fallback results follow below.

After the shared decoder-acceleration contract/statistics changed, the Linux closure was rebuilt
and revalidated from a fresh 192-input snapshot: all48 actual Ubuntu tests passed again, zero
skipped, exact .6 core/ten notices/per-file archive integrity and self-contained normal close plus
pending-startup cancellation passed. All192 input hashes matched after completion, zero drift.
The final delivered private archive is
`app-updates/linux/final-transport-10.0.17.6/peeronq-0.9.76-linux-x64-unsigned-preview.tar.gz`,
54,101,107 bytes; SHA256
`f1704c67e2b93967950491fdd12a8fa4c908cb2d226a8256dfbbe80ab968e27a`.
Normal phase/cancel exit0,121.54/8.92 seconds; all processes/containers stopped. Root independently
verified the archive SHA and sidecar. Earlier archives remain untouched; no publication is implied.

The production optional-D3D integration then passed a strict focused build, zero warnings/errors,
and35/35 cases: seven initial factory/manager/readback/midstream/teardown/actual encrypted PLI
recoveries, native twelve-format CPU/D3D pixel equality/fresh recovery, and27 filler cases.
The received-H264 hardware flag activates only after valid hardware-device decoder output,
clears on reset/fallback, and defaults false for existing codec implementations. Independent
read-only review found a conditional delayed-output recovery latch issue; it was corrected to
accept references once the complete recovery IDR was consumed, without weakening keyframe
validation. The new fault tests were executed only after the isolated Linux run exited.

The adopted D3D11 path passed both real production-coordinator observations, source/core hashes
unchanged before/after; strict final whole-solution build then passed in33.21 seconds, zero
warnings/errors. No extra encoding/device queries or timing collectors remain in the decoder.

| Dimensions / codec | First5s frames/ages; fps; p95 | Whole frames/ages; seconds; fps; p95 | Final actual5s frames/ages; fps; p95 |
| --- | --- | --- | --- |
| 3840x2160 / AMD H.264 + D3D11 | 72/72;14.20;127.09ms | 334/334;30.25;11.01;122.26ms | 50/50;9.80;126.94ms |
| 2160x3840 / VP8 | 75/73;14.80;167.62ms | 480/478;30.39;15.76;117.57ms | 95/95;18.80;108.57ms |

Every unchanged100ms window missed. Decoder name was `Microsoft H264 Video Decoder MFT
(D3D11 output)` for landscape; native decode mean18.87ms versus the earlier CPU27.02ms.
Native encode40.21ms, total encode45.34ms and color14.41ms remain material. Clock maxima
0.47/0.54ms, real quality messages80/75, signaling errors0, packet loss0, capture queue<=1.
Landscape whole private bytes start/max/end1,153,527,808/1,153,527,808/858,267,648;
portrait959,660,032/1,057,861,632/660,008,960. Allocations24,389,589,472/29,906,569,840
bytes, GC0/1/2 294/287/287 and389/363/363. Sampled RTT maxima8.82/16.48ms,
jitter28.70/20.11ms; latest-worker replacements2/12. These headless gray-scene observations
prove native pixels and feedback, not physical4K, sustained30FPS or universal sub100ms latency.
### Final combined run

The final unchanged source passed the full19-project run, exit0:1,213 total,1,186 passed,
zero failed and27 explicit environment skips. EndToEnd119/119 passed in13m13s. The hardware
and WASAPI opt-ins were enabled, hardware soak600 seconds, and only the file-size override
was set to1,073,741,824 bytes. No live TURN/native QUIC/chunk override was set. Real WebRTC
and hardware collections disable parallelization; no other build/container job ran in this window.

Both actual authorized SCTP disk-transfer rows passed, including awaited disposal and cleanup:

| Lane | Bytes | Transfer seconds | Chunk bytes | Native QUIC | Source/destination SHA256 |
| --- | --- | --- | --- | --- | --- |
| Legacy primary | 1,073,741,824 | 66.1 | 245,760 | false | f33f041361cbb11323df3f663a2a848a7fcd152214acf3fe7c5ead9082d43612 |
| Negotiated bulk | 1,073,741,824 | 62.3 | 262,144 | false | f33f041361cbb11323df3f663a2a848a7fcd152214acf3fe7c5ead9082d43612 |

Unchanged strict input baseline/bulk p95 was17.550/15.915ms dedicated,16.351/16.133ms
primary fallback, each n20+20. Their maxima were17.597/16.871 and17.658/17.115ms.
Both reached the original35ms, inflation, route, buffer, signed-clock, reverse-pressure expiry
and video-continuity assertions. These are fake-sink event-to-injection measurements using the
original1MiB bulk-pressure fixture, separate from the1GiB disk tests; neither physical input
latency nor simultaneous4K+1GiB throughput is claimed. Earlier54.853ms failure remains retained.

Encrypted synthetic1080p H.264 continuity passed600.43 seconds:17,994 decoded frames,
29.97 observed FPS, private bytes start/max/end480,104,448/480,104,448/400,412,672.
Hardware decoder was true atstart/end and throughout537 periodic checks; CPU samples0.
Every check also retained connected peers, native1080p/H.264 hardware encoder, fresh frames
and no software-encoder switch. Full first-to-last was606.18s/18,115 frames/29.88FPS.
This is a local1080p hardware continuity result, not a physical4K/ten-minute WAN session.

The same final run retained both actual production-coordinator native-UHD observations:

| Dimensions / codec | First5s frames/ages; fps; p95 | Whole frames/ages; seconds; fps; p95 | Final actual5s frames/ages; fps; p95 |
| --- | --- | --- | --- |
| 3840x2160 / AMD H.264 + D3D11 | 71/71;14.00;151.22ms | 334/334;30.37;10.96;116.82ms | 55/55;10.80;119.02ms |
| 2160x3840 / VP8 | 69/66;13.60;231.83ms | 436/433;30.88;14.09;130.00ms | 55/55;10.80;102.51ms |

All unchanged100ms windows missed. Clock maxima0.48/0.55ms; actual received/usable quality
messages75/74 and82/81, signaling errors0. Landscape native encode/decode/color means
39.85/19.45/13.68ms; one hardware encoder initialization. Whole private bytes start/max/end
1,240,866,816/1,240,866,816/886,116,352 and940,883,968/1,219,768,320/909,631,488.
Cumulative allocations24,485,272,672/28,423,364,632 bytes, GC0/1/2 303/295/295 and
354/332/331. Native pixels/feedback work; reliable100ms/30FPS4K acceptance remains open.

All eight adopted source hashes matched after completion. The exact .6 core matched the actual
solution x64 output (`bin/x64/Release`) and both active test outputs. An initial recursive check
looked in retained historical `bin/Release` publish trees instead; that verifier-scope error and
correction are recorded without changing or relabelling those old binaries. The final Linux
archive hash still matched. Final TRX/JSON/log evidence is retained privately under
`app-updates/validation/2026-10-18-sctp-4k-linux-final/`; no public download, MSI or server
deployment occurred, and no production-ready/AnyDesk-parity claim follows.

## Earlier .5 SCTP, native 4K and Linux checkpoint — 2026-10-18

Latest completed private transport candidate K transferred exactly 1,073,741,824 disk bytes over
legacy-primary SCTP in 74.7 seconds (81.776 seconds wall), with equal source/destination SHA256
`f33f041361cbb11323df3f663a2a848a7fcd152214acf3fe7c5ead9082d43612` and completion/disposal
assertions passed. It recorded 1,443,825 UDP receive posts, maximum one owner, zero overlaps,
FAST/T3/recovery entries, receiver drops or selected transport/filter errors. The unchanged
240 KiB application stride and existing buffers/anti-replay checks were retained. This is a
local private-candidate result. OS-global receive errors were zero; the global no-port/discard
counter increased by two during the run/teardown, so not every OS discard counter was zero.
The exact clean produced .5 core subsequently completed the legacy-primary 1 GiB row in
76.8 seconds with that same final disk SHA256 and 245,760-byte chunks, without native QUIC.
The negotiated bulk SCTP row also passed, in 57.5 seconds with 262,144-byte chunks and the same
disk SHA256. Both rows executed without native QUIC; their whole test stage took 146.80 seconds.
The separate strict input stage passed primary fallback but failed dedicated baseline latency;
this is not erased by the earlier whole-suite pass. Earlier incomplete
observations remain below; they are not counted as passes.

| Final .5 input scenario, n=20 each | No-bulk p95 / observed max | Active-bulk p95 / observed max | Result |
| --- | --- | --- | --- |
| Primary fallback | 17.698 / 20.151 ms | 17.120 / 19.105 ms | Passed all unchanged gates |
| Dedicated input | 54.853 / 68.470 ms | 28.096 / 54.817 ms | Failed 35 ms no-bulk baseline |

Dedicated bulk inflation was -26.757 ms; it was not the failing condition. Its no-bulk
send-return-to-delivery p95 was 54.688 ms, compared with 0.159 ms secure creation, 0.093 ms
decryption and 0.008 ms permission validation. The failed row did not reach later route/buffer/
inflation/max assertions, so its observed values are not equivalent to an all-gates pass.
Its baseline authenticated acknowledgments were 4/4; active-bulk 3/5 (two unavailable).
Fallback acknowledgments were 3/3 in each phase. Printed p99 equals the observed maximum for
these 20-sample distributions. This is a synthetic sink, not physical Windows input-to-photon.
The exact earlier whole-suite .5 distributions passed dedicated baseline/bulk p95
15.836/16.643 ms, observed maxima 16.126/16.977 ms, with delivery-stage p95 0.375/0.319 ms.
The later failure is therefore variable, not proof of a fixed 25/50 ms transport delay. Source
inspection confirmed SCTP send and DTLS receive use dedicated threads, not lifetime-blocking
ThreadPool workers. A private numeric diagnostic's first wrapper had an invalid sleep argument
and generated artificial load; it was stopped and its measurements are invalid. It is retained
separately and cannot support a performance or recovery claim.

The corrected direct CLI was 1/2, not 2/2: dedicated passed, but primary fallback failed a later
`TrueException`. Its over-restrictive diagnostic catch omitted the assertion message/stack, so
the exact later condition cannot be reconstructed; it is not an acceptance pass. A subsequent
single original VSTest run with only deferred numeric GC/pool snapshots passed 2/2: fallback
baseline/bulk p95 16.047/17.401 ms (max 17.428/18.776), dedicated 16.190/16.068 ms
(max 16.420/16.263). All unchanged route/buffer/inflation/max assertions were reached.
Dedicated's 40 measured commands had zero GC-pause delta, pending work 0–2 and nine pool threads;
maximum begin/injection observation skew was 6.8/10.2 microseconds. One fallback tail coincided
with 2.697 ms GC pause. No >35 ms tail reproduced: neither causality nor a production fix for
the earlier dedicated failure follows. No pacing, timestamp, warmup, threshold or runtime setting
was changed by these test-only observations.

The final .5 whole-solution run passed: 1,167 total, 1,140 passed, zero failed, 27 environment
skips across 19 projects. Hardware video and system audio were enabled; opt-in large-file/soak
flags were absent. Linux's six Windows-host skips are covered by the separate actual Ubuntu
checks below. Other external-service/relay skips retain their limits. Strict Release CI/analyzer
build and plain restore passed with zero warnings/errors. The 41-project managed NuGet inventory
reported zero vulnerable entries; the original upstream advisory identity passed its separate
high/critical gate. These are source/private-package results, not deployment approval.

The isolated final-.4 legacy-primary baseline actually completed 67,108,877 bytes in 71.7 seconds,
913.6 KiB/s, with equal disk SHA256
`ec689dabef9ebe1053a048c8bc9ff8a0a4f5c33fb26343ecf5d8a34087869227`.
This is a slow completed 64 MiB+13 case, not the earlier failed 1 GiB case. It recorded 3,175 fast
and 40 timeout retransmissions, maximum send count 133, and no sampled zero receiver credit.

Two early private experiments failed to solve the issue without changing the dependency pin/feed/cache. A fast-once
experiment alone was aborted incomplete after 65.17 seconds near 3.93 MB. Adding the RFC fast-loss
congestion-window assignment was also incomplete at 90.6 seconds, with only 983,040 destination
bytes. Its trace reached 2,074,591 bytes (1.98 MiB) in flight against a 262,144-byte receiver window, zero credit
and 3,066 out-of-window drops. Neither experiment is a passed transfer or an adopted fix.
The original sender's new-DATA gate did not enforce receiver credit; retained receiver state and
dictionary-ordered retransmission were checked with ordinary valid traffic.

A private credit/congestion-window-per-chunk candidate (C, still the .4 receiver/recovery code)
passed the same 64 MiB+13 disk case in 5.9 seconds, 11,046.8 KiB/s, with the same final SHA256.
It recorded zero drops/retransmits, maximum send count one, and 113,045,504-byte peak working set.
Its 1 GiB follow-up was deliberately aborted incomplete after 196.61 seconds, last sampled
794,050,560 destination bytes; no final SHA/disposal completion is claimed. It had no window
drops, but 34 timeout retransmissions and a congestion window stuck at one MTU. The existing
growth test uses flight after ACK removal; strict per-chunk bounds expose its failure to grow.
C was an intermediate candidate, not the final artifact.

The growth/probe candidate D remained incomplete at 57.46 seconds and 17,694,720 bytes, with
806 fast and 47 timeout retransmissions. Candidate E corrected per-chunk fast eligibility,
fast-loss congestion response, lowest-TSN retransmission order and recovery exit accounting;
its live run then exposed the remaining receiver-credit defect. It was aborted incomplete at
112.08 seconds, 13,762,560 bytes, with 27 out-of-window drops. At one ordinary-traffic sample,
sender flight was only 6,000 bytes while pending TSN span reached 222 against the receiver's
existing 218-slot limit; 217 forward markers and retained fragments still advertised almost
the entire receive window. These incomplete runs are not throughput passes.

Candidate F reports free receive credit from retained unique payload bytes and the existing TSN
tracking span, using the larger reservation without double-counting payload/markers. Twenty-four
ordinary sender/receiver cases passed, including full-sized messages, duplicate accounting,
already-delivered other-stream tracking, zero-window recovery and actual ACK/loss response.
Its private legacy-primary 64 MiB+13 disk case passed in 4.6 seconds, 14,136.5 KiB/s, with the
same SHA256 and no native QUIC, drops or fast/timeout retransmissions. Its 1 GiB follow-up
failed at 24.01 seconds with an unhandled `ArgumentException`: ordering the live missing-chunk
`ConcurrentDictionary` let its LINQ count/copy operations race a valid incoming SACK. This was
introduced by private E/F ordering, not the released .4 source. An atomic dictionary snapshot
and a normal concurrent-ACK regression were required before adoption. No .5 package had
been built at that intermediate checkpoint. Buffers and
byte quotas were not enlarged.

The following private combined candidate J passed 30 ordinary sender/receiver cases, including
atomic concurrent missing-chunk snapshots, earliest due retransmission, zero/insufficient-positive
window probes, and receiver slot credit without the MTU-division remainder. Outstanding small
chunks reserve at least one MTU of receiver credit; cwnd still counts actual payload, and a fitting
short final fragment is admitted with no flight. Its 64 MiB+13 case completed in 5.3 seconds with
the same SHA256 and zero drops/retransmits. However, its 1 GiB case was aborted incomplete at
240.23 seconds, last received 177,930,240 bytes (169.7 MiB), with no final SHA/disposal success.
There were 8,440 fast / 210 timeout retransmissions and 5,469 recovery entries, no out-of-window
or stream drops, and one old-data drop. A near-full reassembly state persists. This failed large
case blocked adoption/package production at that checkpoint; the 64 MiB pass did not close the issue.

Bounded grouped/lifecycle diagnostics established that the missing fragment was still retained
by the sender, at the same cumulative-ACK position on both peers, but did not reach receiver
`OnDataChunk` after its FAST attempt. IPv4/IPv6 OS-global receive-error/discard deltas were zero.
The next controlled admission test forced the existing ordinary-bool race: original UDP code
posted two receives sharing its single buffer, while the gated candidate admitted one; both
close paths released ownership. A no-barrier live run then recorded 37,441 overlapping ownership
reservations among 84,659 receive-post attempts, maximum seven owners of one buffer at 24.23
seconds, with zero post/completion/send errors or ICE/no-DTLS filter drops. Counter release occurs
before normal re-arm, so serial callback chaining is not counted as overlap. This isolates a
pre-SCTP receive-buffer race. The gated candidate's 64 MiB case completed in 5.9 seconds with
the same SHA256, zero overlaps and maximum one receive owner; its completed 1 GiB result is
recorded above. Diagnostic hooks remain private and do not change buffers, anti-replay, packet
checks or encryption; the final maintained patch contains only the admission/close discipline.

Eight real VP8 frames per orientation measured native decode/conversion means of 24.98/70.98 ms
at 3840x2160 and 28.38/80.13 ms at 2160x3840. The conversion blocked the receive callback.
The focused replacement keeps authentication/reference decoding ordered, converts independent
decoded planes on a worker with one active plus one newest pending frame, and preserves frame
timestamps. Every one of 16,777,216 Y/U/V combinations matched the existing color arithmetic;
packed rows also satisfy the public frame-length contract for widths not divisible by four.
Slow conversion and slow presentation regressions cover admission and close/resize fencing.
One serialized, short comparison passed the prior 4K case functionally but still missed the
100 ms policy target: capture-to-present p95 949.7 ms, clock uncertainty 0.9 ms, encode/render
6.0/6.5 fps, 16 encoded/rendered frames and 18 capture drops. Capture-to-encode p95 was 818.8 ms
and decode-to-present 96.5 ms. This is not a clean speed improvement over the earlier single run;
the sample is short and cold/GC-sensitive. Eight-frame conversion means in that later process
were old/optimized 213.47/44.42 ms landscape and 213.03/46.50 ms portrait, with large outliers.
Fresh-process warmed stage measurements are pending; no sustained 30 fps claim follows.

Real hybrid-encrypted gray-scene UHD cases passed dimensions, protected timing and bounded queue
assertions: AMD hardware H.264 landscape delivered 91 frames at 13.16 fps, capture-to-present
p95 142.34 ms, clock uncertainty 0.38 ms; portrait VP8 fallback delivered 90 frames at 9.54 fps,
p95 195.51 ms, uncertainty 0.56 ms. Pending depth was zero in each, with 15 replaced decoded
portrait frames. These synthetic presentation callbacks do not measure a physical display.
Both remain slower than the 100 ms policy target; performance work continues.

A later private .4 run moved hardware NV12 conversion onto the same bounded worker and avoided
zero-initializing four buffers that are fully overwritten before exposure. Strict pixels/layout/
worker checks passed 37 cases, actual MF copy/crop pixels four, and isolated codec stages three.
Fresh 90-frame runs remained functional but missed 100 ms: landscape 15.39 fps / 105.89 ms p95;
portrait 18.28 fps / 118.67 ms. Separate 30-second observations retained every missed target:

| Orientation / codec | First 5 s samples / p95 | Whole samples / p95 / fps | Last 5 s samples / p95 / fps |
| --- | --- | --- | --- |
| 3840x2160 / AMD H.264 | 76 / 185.25 ms | 530 / 126.81 ms / 17.37 | 80 / 120.41 ms / 15.85 |
| 2160x3840 / VP8 | 83 / 130.68 ms | 564 / 104.50 ms / 18.27 | 129 / 149.88 ms / 25.74 |

Durations were 30.45/30.98 seconds; maximum clock uncertainty was 0.42/0.49 ms. Portrait had
567 delivered frames but 564 valid ages; the whole-run age count is not a frame count. Landscape
initialized hardware 11 times, three within the first five seconds, so cold startup alone does
not explain the misses. Private bytes start/max/end were 451,661,824 / 722,374,656 / 550,367,232
and 554,622,976 / 902,987,776 / 817,377,280. Allocated bytes were 38,414,684,496 / 33,287,722,256;
Gen2 collection counts were 397/393. These are allocation-pressure observations, not memory
bounds, a clean unloaded benchmark, physical capture/display measurements or final .5 results.

An isolated actual-AMD-MFT comparison then exercised 12 access units at UHD 30->20->15->30 fps,
bitrate changes, 1080p resize and UHD return. Both original/candidate builds had zero warnings/
errors; each decoded 12/12 outputs with seven exact fresh-decoder recovery checks. The candidate
required advertised dynamic-format support and accepted matched current input/output types,
requested native sample durations and monotonically increasing timestamps. Three dynamic changes
succeeded; hardware initializations fell from six to three (the remaining size changes still
reconstruct). The three FPS-transition encode times were 205.49/261.17/245.17 ms versus
41.37/53.65/46.53 ms. Unsupported or partially failed changes reconstruct using the existing
validated path. This is narrow driver evidence; integration and sustained measurements of the
production change are recorded below; other vendors are unverified.

The production dynamic-rate regression also passed 12/12 current-type/output checks and seven
fresh-decoder pixel recoveries, with three dynamic changes, zero rejected fallbacks and three
initializations. A subsequent private .4 landscape observation ran 30.99 seconds with only one
initialization and 621 converted frames at 20.01 fps. This fixes repeated rate reconstruction,
but does not meet 100 ms: startup/whole/last-five-second p95 were 176.75/151.50/139.19 ms,
with 103/621/102 valid ages and maximum clock uncertainty 0.44 ms. Private bytes start/max/end
were 546,082,816 / 752,029,696 / 635,150,336; allocation was 43,557,333,712 bytes and Gen2 count
422. Capture-to-encode p95 was 85.35 ms; decode-to-present 20.74 ms. Cadence increased while
whole-run p95 worsened versus the preceding 126.81 ms observation; no latency-improvement claim
follows. A bounded encoder-only scratch conversion change was then verified, as recorded next.

That scratch change passed seven actual MF/layout/native-copy/resize/disposal/allocation cases.
Four warmed UHD encodes allocated 69,704 bytes on the calling thread, versus 49,766,400 bytes
for the old four temporary NV12 arrays alone. Native decoding retained correct pixels after
immediate managed-buffer clearing; the single scratch resized exactly three times across
UHD->1080p->UHD and was cleared/released on disposal. The twelve-frame MF stage allocated
449,890,736 bytes versus 599,133,584 in the prior stage (about 25% less), with Gen0/1/2 counts
2/2/2 versus 6/6/6. This verifies allocation reduction, not sustained 100 ms performance.

### Final .5 direct codec/presentation observations

The main EndToEnd run passed 83/83, including 14 hardware cases. The exact produced .5 core
was hash-verified. Four warmed UHD encodes allocated 75,264 calling-thread bytes in this run,
versus 49,766,400 bytes for the old four temporary NV12 arrays alone. Native pixel recovery,
scratch resize/clearing and disposal passed. The AMD encoder remained hardware accelerated;
the installed Microsoft H.264 decoder is software. Neither GPU decoding nor physical 4K
capture/display was established; this host's physical display is 1920x1080.

| Synthetic dimensions / codec | First 5 s frames/ages; fps; p95 | Whole frames/ages; fps; p95 | Last 5 s frames/ages; fps; p95 |
| --- | --- | --- | --- |
| 3840x2160 / AMD H.264 | 101/101; 20.04; 127.61 ms | 605/605; 20.06; 134.75 ms | 101/101; 20.02; 125.29 ms |
| 2160x3840 / VP8 | 100/98; 19.82; 100.39 ms | 662/660; 21.93; 127.67 ms | 87/87; 17.25; 217.06 ms |

Whole durations were 30.11/30.14 seconds; maximum clock uncertainty 0.46/0.36 ms. All six
windows missed the unchanged 100 ms target. Landscape initialized once; portrait used VP8
because the installed hardware encoder's dimension limit excludes that orientation. Whole-run
private bytes start/max/end were 605,831,168/785,674,240/707,981,312 and
848,445,440/888,664,064/862,556,160. Cumulative allocation was 34,908,602,336/36,518,489,552
bytes, not live heap size. Gen0/1/2 counts were 392/308/308 and 481/452/452.

These direct fixtures validate encrypted codec delivery and headless presentation timing, but
do not instantiate production SessionCoordinators or send viewer quality back over signaling.
Their sender therefore cannot adapt from the measured remote frame age. Final target FPS
20/15 can follow local encoder pressure; it does not prove production ignores the 100 ms target.
A separate paired-coordinator observation uses actual registration, attended acceptance and
authenticated `session.quality`; it must be reported separately without erasing these misses.

The first such observation completed both 30-second sessions and their body assertions, but
both tests failed cleanup: the new fixture disposed its capture after the owning coordinator had
already disposed it. These are failed tests, not acceptance passes. The ownership fix removes
the new fixture's duplicate disposal. Real feedback arrived (77/80 messages): landscape
`viewer_frame_age` drove target FPS to ten; portrait later recovered to twenty. Whole-run p95
was 683.11/162.91 ms, with final viewer rolling p95 1,209.51/76.75 ms and maximum clock
uncertainty 0.39/0.48 ms. Landscape therefore exposed a remaining delay rather than a universal
100 ms improvement. Those first fixture windows used the last rendered frame as their endpoint;
the corrected observation anchors final windows to the actual observation end and discloses
the final presentation gap. No physical-display latency is measured.

One `malformed_message` was also observed: the server's finite quality-reason allowlist omitted
the current policy's `awaiting_network_feedback` and `viewer_video_stall` literals. A focused
actual registered/accepted relay regression failed both exact reasons before the fix (two
ten-second timeouts), then passed 2/2 after adding only those literals. Each case also rejects an
unknown reason and out-of-range latency. Strict CI/analyzer build passed, and the whole signaling
suite passed 138 with zero failures/four external skips. Numeric validation, session membership/
ownership and finite unknown-value rejection remain active; no protocol field/version was changed.

The corrected coordinated stage observations then passed 2/2 (strict build zero warnings/errors),
with no signaling errors and real accepted-session quality feedback:

| Synthetic dimensions / codec | First 5 s frames/ages; p95 | Whole frames/ages; fps; p95 | Final actual 5 s frames/ages; fps; p95 |
| --- | --- | --- | --- |
| 3840x2160 / AMD H.264 | 77/77; 158.62 ms | 332/332; 10.83; 140.11 ms | 50/50; 9.80; 118.76 ms |
| 2160x3840 / VP8 | 78/75; 124.41 ms | 490/487; 15.83; 92.11 ms | 133/133; 26.40; 75.52 ms |

Whole durations were 30.56/30.89 seconds. The final window is anchored to observation end;
last presentation gaps were 76.76/118.48 ms. Clock uncertainty stayed <=0.43/0.46 ms. Landscape
still misses 100 ms; portrait meets whole/final windows but misses startup. Real quality feedback
kept landscape at ten FPS for frame age; portrait recovered to a thirty-FPS target. Native hardware
encode averaged 38.84 ms, software H.264 decode 26.45 ms and color conversion 13.96 ms; individual
maxima 257.48/109.52/64.10 ms. These independent stage means/maxima are not an end-to-end sum.
Sampled pending ThreadPool work was always zero in landscape and capture queue <=1; no sustained
pool backlog was proved. Whole private bytes start/max/end were
699,256,832/699,256,832/449,814,528 and 831,324,160/883,716,096/718,229,504.

Metadata exposed two remaining issues. The hardware encoder produced sustained ~3x requested
CBR after rate changes (273 frames at ten FPS/800 kbps averaged 30,006.84 bytes, measured ~2,400.48
kbps). A separate existing twelve-frame native-codec probe checked NAL/config metadata before
any proposed correction. Portrait also reported 4,990.84 ms jitter at ~0.18 ms loopback RTT with
zero packet loss. The app's 90-kHz conversion is correct; upstream reception initialization leaves
the first transit origin at zero. Valid-packet regressions preceded the seventh maintained patch;
the completed .6 evidence is above. These observations are not actual LAN congestion.

Two deterministic authenticated clock-recovery tests failed before and passed after using the
injected clock for waits and retrying after one second while no <=50 ms estimate exists. A usable
estimate retains the existing one-minute refresh. Measurement uncertainty bounds and acceptance
assertions were not widened. Automatic capture now retains UHD in either orientation, without
upscaling smaller sources; actual encoded area selects the existing 75/100 ms adaptation targets.

### Linux client package

Ubuntu 24.04 with Xvfb, actual D-Bus/Secret Service and distro libvpx9 has loaded the Linux decoder
and delivered encrypted WebRTC frames with correct dimensions, including 3840x2160. The initial
two color failures came from ideal-RGB fixture expectations; reference pixels were calibrated
against the existing Windows decoder, with the same tolerance of eight and original ideal/I420
values retained. A coherent fresh-output .4 diagnostic passed App 21/21, Linux platform 14/14
and scoped shared Cloud-client 12/12, no skips. Actual keyring, X11 repaint/normal close and
proof/attestation composition ran. An earlier cached-output snapshot compile failed despite
matching source hashes; the fresh output tree fixed that orchestration issue.

Final explicit linux-x64 validation passed App 22/22, platform 14/14 and scoped shared Cloud
12/12: 48 passed, zero failed/skipped, 191 source inputs with zero drift. The actual keyring-backed
hybrid identity signed/verified, rejected modified transcript/signature and persisted its fingerprint;
this is not a complete physical hybrid-session interoperability test. A Windows-cache/RID-ambiguous
attempt first hit MSB3026/out-of-memory copying an irrelevant ARM64 PDB. A fresh container-local
official cache with explicit linux-x64 corrected the orchestration; warnings remained errors.

Private self-contained archive: `app-updates/linux/peeronq-0.9.76-linux-x64-unsigned-preview.tar.gz`,
53,986,198 bytes; SHA256
`fae6ed9b9644f085698a3c40b6e5e92f4eeca45a0bf744704834fcb5b6773b3f`.
Archive per-file integrity, safe paths, ELF x64/version, nine notices, exact .5 core and absence of
Windows `vpxmd.dll` passed. The extracted archive launched/provisioned/closed normally as UID 10001
with shared .NET lookup disabled. Startup cancellation of the pinned pending `secret-tool` helper
also passed: exit zero within ten seconds and helper termination. The first PATH-wrapper attempt
did not intercept the fixed helper path and is not counted as cancellation evidence.

This is an unsigned, unpublished attended viewer/controller preview. Read
[the launch instructions](../packaging/linux/README.md) for Ubuntu prerequisites, unlocked Secret
Service and complete endpoint configuration. No Linux hosting, file transfer, clipboard, unattended
access, audio or IME text composition is claimed. Container execution does not establish physical
Linux/Windows-host interoperability, accessibility, reconnect, ten-minute physical acceptance or
public production enrollment/signing.

## Unreleased remediation checkpoint — 2026-10-18

Canonical source version remains 0.9.76; these measurements are not published MSI/deployment
evidence or an AnyDesk A/B benchmark. Runtime: verified .NET 10.0.13 / SDK 10.0.402.

The first source-remediation full suite exposed the unchanged 35 ms WebRTC input gate again:
primary fallback no-bulk/active-bulk p95 = 33.948/37.709 ms, dedicated = 38.683/36.038 ms,
20 samples per scenario. The sink is synthetic, not Windows injection or input-to-photon delay.
An external original-source 10.0.17 baseline measured 36.529/35.289 and 35.854/35.351 ms.
An experimental receive-wakeup patch that also changed Close semantics did not complete the
actual media test before its 198-second diagnostic limit. Later stage tracing reached the test's
three-minute file-completion wait before disposal, so this is not evidence of a teardown deadlock.
Seven queue checks alone are insufficient; no product dependency or repository output was replaced.
A narrower candidate preserved Close semantics. Its completed unloaded three-round comparison
used 114 identical common files per variant, with only SIPSorcery DLL/PDB different. Original
passed 1/6 cases and failed five unchanged 35 ms gates; candidate passed 6/6. Ranges of twelve
scenario p95 values were 34.073–38.931 ms versus 17.018–18.485 ms, not pooled p95. Both candidate
lane variants completed SHA-verified 1 MiB transfers and disposal in all three rounds; no run
timed out. The user approved maintained-derivative adoption afterward. Produced-package SDK402
validation is recorded separately below; these comparison numbers used the SDK303 experiment.
Stage/error tracing confirmed `bulk_receive_queue_overflow` during the file-completion wait:
the dedicated bulk receiver dropped a record when its eight-slot queue filled. The production
receiver now waits for bounded capacity with session cancellation instead of dropping ordered
records. Both new regressions failed before the fix and passed afterward (32-record FIFO with a
slow consumer; shutdown releases a blocked writer). This is distinct from the dependency wakeup
change and does not itself establish that the 35 ms gate passes.

The subsequent actual 1 GiB disk test exposed another independent defect in the legacy primary
fallback: its 256 KiB data-channel limit was exceeded by the default 256 KiB payload plus JSON,
framing and 54-byte PNQE overhead. Small 180,000-byte tests had not exercised a full chunk. The
official baseline was stopped after the deterministic send failure was confirmed; it is not a
passed transfer or throughput measurement. Outbound primary payloads now cap at 240 KiB and freeze
that stride per transfer; inbound 256 KiB compatibility is retained. The ordinary file test now
uses 1 MiB+13 bytes and fails immediately on an explicit transfer failure. Later native-QUIC disk
results are recorded below; they do not clear the legacy SCTP compatibility path.

After that chunk fix, the produced 10.0.17.1 package (DLL `ea38ed83...`) did write data in the
actual 1 GiB primary-fallback test, but progress decayed and repeatedly stopped near 60 MiB;
it failed the 15-minute bound near 64 MiB. Managed stacks showed idle DTLS receivers rather than an application
callback blocking a receiver. A separate actual-receiver check proved that duplicate TSN history
was never cleared: 10,000 gap/duplicate/recovery cycles grew its SCTP acknowledgment packet to
40,036 bytes. The exact receiver with one clear after taking the report snapshot stayed at
36 bytes, with identical cumulative acknowledgment progress. This fixes a concrete lifetime-state
bug described by [RFC 9260 section 3.3.4](https://www.rfc-editor.org/rfc/rfc9260.html#section-3.3.4);
source evidence alone does not establish causality for the live stall. Revision 10.0.17.2 pins
that second one-line patch, preserves both changed sources in its package/PDB, and passes both
durable actual-package regressions. The .2 1 GiB run was aborted after 709.62 seconds with
82,329,600 destination bytes; a .3 32 KiB-chunk/64 MiB experiment was aborted after 300.79 seconds
with 63,995,904 bytes. Neither completed or proved final SHA/disposal. The latter showed bursts
of new SCTP chunks alternating with long retransmit-only periods (2,763 fast and 41 timeout
retransmits); the cause of that remaining progress decay is unproved. No failed/aborted run is
counted as a throughput pass.

Read-only follow-up found 47,089 new chunks in the first 10.06 seconds, then no new chunks at
10-40 seconds with only three timeout retransmissions. This is burst/recovery starvation rather
than a measured steady application rate. Upstream sends the current DATA packet's SACK before
calling application code, but subsequent SACK parsing shares the receive thread. The legacy
callback waits through decryption/disk handling; its delivery-feedback method returns immediately,
and it bypasses the dedicated eight-slot bulk queue. Neither a cyclic reverse-send deadlock nor
one causal sender defect was proved. Current window/flight/RTO and out-of-window drops were not
captured, so zero selected exception counters cannot establish a healthy transport.

Removing the artificial 1 Gbps bulk ceiling initially exposed queue growth: the 128 MiB native
QUIC adaptive test delivered 29.5 MiB/s with input p95 399.5 ms (261 samples); an accept-loop
dispatch correction alone still measured 32.1 MiB/s / 421.9 ms (230 samples). Receipt-count-based
EWMA/probe growth was treating short delivery bursts as sustained capacity. The revised estimator
weights elapsed time, grows at most once per 100 ms observation window and retains immediate
congestion backoff; pacing credit is bounded by the existing QoS byte window. The subsequent
unchanged seven-test transport suite passed: 128 MiB at 19.9 MiB/s, input p95 0.8 ms across
396 samples, learned budget 1,022,292 kbps. This is local QUIC payload delivery, not disk transfer
or WAN throughput. Numeric capacity bounds remain Int32 kbps, without the former 1 Gbps quota.

Actual Windows capture on the 1920×1080 display passed all four capture integration checks,
including a legacy 720p request and continued frame production after adaptive downscaling. Sources
below 1080 retain their native dimensions; the scaler does not manufacture detail by upscaling.

On AMD Radeon RX 5600 XT / driver 32.0.21045.5002, the four-test codec/hardware acceptance suite
passed. Two cases actually exercise the GPU/MF path, selecting `AMDh264Encoder` and
`Microsoft H264 Video Decoder MFT`; two are conversion/disposal checks. All eight synthetic frames decoded
immediately, including the first frame, resize and forced recovery keyframes. The real PNQE-
encrypted WebRTC path presented synthetic 1920×1080 with the actual hardware flag true. The
final initialization/resize sample had encode min/mean/max 6.00/64.37/254.52 ms; eight frames
including startup do not establish sustained FPS, end-to-end latency or another GPU vendor's
behavior. Unsupported MF dimensions fall back to VP8; physical second-endpoint, ARM64 hardware,
long-session and separate-network acceptance remain open.

### Final produced-package and default-path checks

The serial final-source solution run (hardware/audio opt-ins enabled) executed 1,065 tests:
1,043 passed, one failed, 21 environment-gated skips. Its unchanged 35 ms input gate passed both
lane variants; primary fallback no-bulk/active-bulk p95 was 17.062/18.284 ms and dedicated was
16.908/15.627 ms, 20 samples per scenario, synthetic sink. A separate 4K/30 fps VP8 case delivered
its first valid frame but failed to obtain a bounded authenticated capture-to-present sample
within 10 seconds. That failure is retained; it is not a clean whole-suite pass or a physical
latency measurement. The short final .4 encrypted AMD H.264 check decoded 120 1080p frames in
5.91 seconds (20.30 observed delivery fps), with actual hardware encoder/decoder selection.
One exact-case diagnostic 4K rerun passed in 4.474 seconds: 62 encoded/34 rendered frames,
clock uncertainty 5.1 ms, capture-to-present p95 1,739.4 ms, first/last local frame ages
235.2/1,734.4 ms, encode/render cadence 16.5/8.5 fps. It does not erase the whole-suite failure.
Source review found a one-minute clock retry gap after startup timeout/unusable samples; the
observed original failure branch remains unproved. Only bounded failure diagnostics were added;
production behavior, the 50 ms clock-uncertainty bound and the 10-second test assertion are unchanged.

The final .4 encrypted hardware session also passed a 600.36-second local continuity soak:
16,938 decoded synthetic 1920x1080 frames during the soak, 28.21 observed delivery fps; the whole
session delivered 17,059 frames in 606.31 seconds (28.14 fps). Every one-second check retained
Connected state, H.264 negotiation, actual hardware encoding, correct dimensions and a decoded
frame within the preceding 10 seconds; no software fallback was observed. Process private bytes
start/max/end were 160,808,960 / 255,135,744 / 158,928,896. These are observations, not a proven
memory bound, physical motion/quality acceptance, A/V synchronization or WAN latency. An overlapping
controller build encountered locked test DLLs and failed; it did not replace the running soak's
loaded assemblies. The long test passed. The subsequent complete corrected controller passed
serially with exit 0: local HTTPS health, STUN, authenticated UDP/TCP/DTLS/TLS allocations,
invalid/expired credential rejection, UDP/TCP relay video, both file-offer rejection cases and
signaling restart/re-authentication. Test credentials now have distinct per-session quota subjects,
matching production issuance; server quotas and file-path restrictions are unchanged.

Maintained 10.0.17.4 was built twice from clean pinned source with identical nupkg/core hashes
(see DEPENDENCIES.md). The actual package passed seven compatibility checks with the 6,674-entry
API unchanged, plus three ordinary SCTP regressions. The gap-counter hardening has code review
and ordinary-packet coverage; no hostile-boundary runtime test was executed.

Two actual FileTransferService disk transfers with the final .4 core passed at 1,073,741,824 bytes
each. Source/destination SHA256 was
`f33f041361cbb11323df3f663a2a848a7fcd152214acf3fe7c5ead9082d43612` in both cases. Unlike the
earlier deliberately bound-NIC diagnostic (11.6/9.1 seconds), these peers had no NIC bind or chunk
override: ICE nominated local `::` / remote `10.0.0.10`, and production route resolution proved
DirectLan. Both peers were native-ready and used their native transfer membership; SCTP bulk
records/fragments stayed zero. With optional SCTP bulk negotiation off/on, effective chunk size
was 245,760/262,144 bytes, transfer time 20.6/9.9 seconds and measured payload rate
50,939.5/105,601.9 KiB/s (49.75/103.13 MiB/s). Whole-case times were 36.795/17.512 seconds.
These are local functional results under mixed host load, not WAN/two-device/unloaded benchmarks.
The file-only native path is intentionally uncapped and has zero adaptive feedback samples;
adaptive coexistence with media/input is a separate transport test.

That separate unloaded final QUIC payload gate passed at the actual 1 GiB setting: 25.2 MiB/s,
interactive p95 0.8 ms across 2,540 samples, 211,944 kbps bulk budget, approximately 40 seconds.
Its unchanged requirements are at least 1 GiB/minute and interactive p95 at most 35 ms. This
test measures transport payload/coexistence, not disk SHA verification or WAN speed. The earlier
128 MiB adaptive-estimator measurement and full disk-service results retain their separate scopes.

The wildcard-route fix uses the OS-selected local address, matching family and an exact active-NIC
match before applying the existing private-address policy. It fails closed on route/NIC errors;
relay precedence and explicit-IP behavior are unchanged. A single cached nomination expires after
one second. All 28 deterministic resolver checks passed. This closes the default-LAN fast-path
selection defect, not the remaining legacy SCTP recovery starvation described above.

## Physical sharpness investigation - 2026-09-28 (Phase 0 pending)

Baseline: current `origin/main` `e831413d5d62303c8c66f9dc2ecdf256e1e14345`, canonical
0.9.73. Physical viewer/sharer/server versions, display dimensions/DPI and selected path are
**unverified**; the operator has been asked to supply them. No PeerOnQ App/Agent process was
running on this workstation when checked. Existing 0.9.73 matching client packages are available,
but their existence is not evidence that either physical endpoint was upgraded. The task explicitly
requires version verification before media-code edits. No runtime correction or version bump was
made in this investigation; the following findings do not prove the screenshot's root cause.

An isolated temporary console probe referenced the current Media project and linked the unchanged
FrameScaler source. A manual clock exercised the actual AdaptiveQualityController and
MediaStatisticsCollector, without transport or a physical screen:

- Begin with healthy feedback, then record ONE 80 ms decode-to-render sample. Re-evaluate once
  per simulated second with zero encoder queue drops and an 8 ms RTT.
- At t=2 s the controller reaches L3, downscale 2, reason `viewer_render_latency`. The same sample
  remains in the five-second percentile window; these are not three independently slow frames.
- At t=6 s the sample has expired and render p95 is zero. With fresh network reports, full recovery
  occurs at t=20 s. If network reports are unavailable from t=6 onward, the controller remains
  L3 at t=60 s with `awaiting_network_feedback`. This reproduces the missing-feedback HOLD case
  in source; it does not establish that RTCP was missing in the user's physical session.
- The unchanged scaler maps 2560x1440 and 3840x2160 Automatic requests to 1920x1080. Explicit
  1440p/2160p requests retain the supported source size and never upscale a smaller source.
- ActualSizeInDips preserves 3840x2160 physical pixels mathematically at 100/125/150/200% DPI.
  This checks geometry, not XAML interpolation, UI dispatch, GPU presentation or physical sharpness.

An additional synthetic probe used repeated glyph-like edges, 1 px stripes, colored icon blocks,
window borders and checkerboards. It ran the real BGRA-to-I420 scaler, VP8 encoder and decoder at
1080p/1440p/4K, Automatic/native size, and 4/12/36 Mbps targets: 18 cases, 12 static input frames
per case. The 4K Automatic path eliminated the source 1 px stripe contrast (0.0) at every bitrate;
native 4K retained contrast 217.0/221.0/219.0 on the decoded 0-255 scale. Raising bitrate cannot
recover spatial detail already removed by capture downscaling. Post-scaler luma PSNR ranged
24.30-26.18 dB and is not a source-resolution readability score. Native 4K/4 Mbps emitted 7/12
frames; every other case emitted 12/12. The benchmark was unpaced, cold-start-inclusive and used
a dense synthetic pattern. Its short timing distributions are not steady-state FPS or a measured
reason to change production thresholds, force 4K, replace the renderer or add an idle keyframe boost.

Targeted existing-source validation: Media adaptation/VP8/RTCP 60 passed, FrameScaler 24 passed,
pointer/DPI geometry 10 passed; zero failures/skips in these selected tests. The previous unrelated
full-suite latency failures have not been reclassified. No live TURN, physical two-device test,
actual viewport/UI-render timing, source-capture screenshot comparison or production test ran.
The expanded quality diagnostic, policy correction, regression gates and release remain pending
the required endpoint/version evidence. Do not describe this audit as a blur fix.

## Measured evidence

| Measurement | Run 1 | Run 2 |
| --- | ---: | ---: |
| Main-window startup | 820.453 ms | 631.850 ms |
| Idle CPU median | 0% | 0% |
| Idle CPU p95 | 2% | 3% |
| Working set median | 195.098 MiB | 195.289 MiB |
| Working set p95 | 196.938 MiB | 196.844 MiB |
| Private memory p95 | 131.469 MiB | 131.027 MiB |
| Dedicated GPU memory p95 | 57.980 MiB | 57.980 MiB |

Each idle run sampled the real WinUI process for five seconds. The run is a startup/idle baseline,
not an active remote-session benchmark. Four CPU/memory samples were collected per run.

The deterministic connection-policy suite passed 21/21. It covers unknown/invalid telemetry,
packet-loss, RTT, jitter, encoder-backpressure, bandwidth-constrained, stable-headroom and bounded
2/8/16 MiB transfer-allocation decisions. Application, Signaling, Domain and End-to-End suites also
passed; see the Phase 10 completion report for exact counts.

## Isolated viewer hot-path measurements (2026-08-23)

These are deterministic local microbenchmarks, not a physical two-device or end-to-end result:

- The original byte-at-a-time 1920x1080 BGR24-to-BGRA32 expansion measured 3.507 ms/frame. The
  bounded packed-pixel implementation measured 1.490 ms/frame (2.35x faster), with byte-identical
  output on the local x64 device.
- The unchanged VP8 screen encoder measured 3.82-4.55 ms mean and about 5.5 ms p95 for the synthetic
  1080p desktop workload at the 60 FPS/12 Mbps Performance target. This fits the local 16.67 ms
  encode budget, so the user-reported LAN delay did not justify a speculative FPS or codec-quality
  downgrade.
- The sharer now wakes its capacity-one latest-frame encoder queue when a capture frame arrives.
  When the secure transport is already ready, this removes the former 0-16.67 ms (Performance) or
  0-33.33 ms (Automatic) polling wait before encoding. This is a code-path bound, not a claimed
  end-to-end latency measurement.

## Local bulk-transport contention measurements (2026-08-24)

These are real localhost transport runs on the development machine, not two-device LAN/Internet
claims. The production 256 KiB file record size and the same adaptive bulk pacer were used.

- The active SIPSorcery compatibility path moved 8 MiB over its dedicated SCTP association in
  26.5 seconds (309.5 KiB/s). The legacy shared association did not finish that payload inside its
  three-minute test timeout. This proves that increasing only the policy cap cannot meet the target.
- Unpaced managed QUIC cleared the throughput target but raised same-connection mouse p95 to
  265.6 ms, proving that transport replacement without a bulk reserve is also insufficient.
- After retaining one bounded Windows timer quantum of earned pacer credit and using the measured
  post-media/input remainder, three 64 MiB QUIC/TLS runs measured 25.2-25.3 MiB/s with mouse p95
  0.4-2.6 ms.
- A full 1 GiB QUIC/TLS run completed in 40 seconds at 25.3 MiB/s while 2,592 concurrent mouse
  samples measured 0.4 ms p95. The regression gate requires at least 17.1 MiB/s and at most 35 ms
  input p95. It is an equivalent transport-core acceptance result; physical disk, Wi-Fi/router,
  NAT/firewall, screen encoding and two-laptop contention remain to be measured.
- The production WebRTC estimator can expose only 43.2 Mbps while a 36 Mbps 4K target is active,
  leaving a 5,144 Kbps initial file allocation after reserves. A real 128 MiB managed-QUIC run with
  receiver-confirmed adaptive goodput escaped that ceiling, reached the defensive 1 Gbps probe,
  averaged 28.0 MiB/s, and kept 289 concurrent mouse samples at 8.3 ms p95. This proves the feedback
  path and bounded probing on localhost; it does not prove a physical link can sustain 1 Gbps.
- A real VP8/WebRTC Full Control loopback reversed the contention direction: the sharer sent the
  file while viewer input travelled back to the sharer. At the media boundary, fresh viewer input
  p95/clock uncertainty appeared in sharer statistics, selected the 64 KiB/128 Kbps emergency bulk
  allocation, stepped adaptive video down on sustained pressure, expired when stale, and
  screen/input/file completion continued. One recorded run measured 34.7 ms baseline input p95,
  31.9 ms during the reverse-direction transfer and 4.8 ms maximum capture-to-decode latency.
  Separate coordinator and signaling integrations prove
  viewer publication plus authenticated relay/validation. Together these prove bidirectional
  feedback wiring locally; they do not replace the physical two-device acceptance matrix below.
- The Full Control isolation acceptance initially failed because SDP exposed only the primary and
  file peers. New peers now negotiate three independent ICE/DTLS transports: primary video/hybrid
  handshake, 64 KiB-bounded input SCTP, and bulk SCTP (or native QUIC for eligible LAN files).
  Authenticated input record counters proved mouse/keyboard used the input peer. The 21-second real
  VP8/input/file contention run passed its <=35 ms input p95, <=10 ms inflation, <250 ms individual
  stall, advancing-video and unfinished-concurrent-file gates. A separate real peer test proved the
  primary input fallback when the additive lane is not echoed. The Full Control acceptance also
  closes an established input peer at runtime, then proves the next mouse event arrives on the
  primary secure channel while both primary peers stay connected and VP8 presentation advances.
  These are localhost transport isolation results, not physical Wi-Fi/router results.
- The feedback publisher period is now an explicit 250 ms code-path bound instead of one second.
  A scripted failed-first-send regression completed the failure plus successful next-tick retry in
  652 ms on the development host. The four-message/second per-session cadence shares a 20 msg/s
  per-client budget; above five concurrent sessions it stretches automatically, retaining five
  messages/second below the server's default 25 msg/s limit for ICE/session control. This is a
  scheduler/path bound, not a physical-network latency claim.
- A fresh 0-decode/0-render viewer report was reproduced as a false `stable_headroom` decision even
  while the sharer kept encoding. The new two-window stall detector first failed its regression,
  then passed with bounded quality reduction and a coalesced sender key-frame request. The real
  VP8/WebRTC acceptance continued frames after injected zero-presentation feedback at 320x240, and
  the complete 320x240/1080p/4K theory passed 3/3 in 10.2 seconds. This proves the local recovery
  path, not physical-network stall recovery.

## Input latency investigation — 2026-09-27

**Result: the unchanged 35 ms p95 acceptance gate remains open.** No production transport,
decoder, timer/power policy, dependency version, security boundary or latency threshold was changed
by this investigation. Test instrumentation and the primary-input fallback case are retained.

Exact test:
`PeerOnQ.Media.Tests.WebRtcLoopbackTests.Interactive_input_remains_responsive_while_bulk_transfer_is_backpressured`.
The original Release fact first failed with p95 **35.8 ms** and send-call p95 **0.3 ms**.
That original output did not include p50/p99; they were not reconstructed.

Reproduction command (the final test is a theory with dedicated/fallback cases):

```powershell
dotnet test tests/PeerOnQ.Media.Tests/PeerOnQ.Media.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~Interactive_input_remains_responsive_while_bulk_transfer_is_backpressured --logger 'console;verbosity=detailed' --nologo
```

Configuration: Windows 10.0.26200 x64, Ryzen 7 3700X, .NET 10.0.12 testhost, Release, real
same-process WebRTC/ICE/SCTP/DTLS, mandatory authenticated hybrid session protection, 320x240
synthetic video with a 30 fps target, dedicated bulk peer, 1 MiB file and 1,024-byte chunks.
Each input-lane case uses five warm-up events, then 20 events without bulk and 20 with an active
backpressured file transfer. Percentiles use nearest rank; with n=20, p99 is the maximum, not a
large-sample tail estimate. The developer host's existing idle services remained running; this is
not dedicated benchmark hardware or a two-machine network experiment.

The fixture calls `SendPointerMoveAsync` and timestamps the callback inside **FakeRemoteInputSink**.
It does not sample a physical mouse, WinUI's input queue, Windows `SendInput`, or input-to-photon
latency. Real Windows injection remains **UNMEASURED / physical-device gate open**.

### Comparable input-to-fake-injection distributions

All entries below are milliseconds in **p50 / p95 / p99** order, n=20 per cell. The baseline uses
the corrected final probe with timer activation disabled. The candidate uses the same probe and
an OS timer lease active only while an input-authorized session's primary peer is connected.

| Input lane / load | Baseline, optimized decoder | Timer candidate, optimized decoder | Original decoder isolation, no timer |
| --- | --- | --- | --- |
| Dedicated / no bulk | 33.138 / 34.709 / 39.105 | 33.249 / 34.624 / 38.755 | 33.284 / 34.754 / 39.833 |
| Dedicated / active bulk | 33.069 / 38.328 / 39.140 | 33.249 / 34.926 / 41.212 | 33.245 / 35.578 / 35.957 |
| Primary fallback / no bulk | 32.483 / 37.467 / 38.147 | 25.750 / 26.326 / 26.852 | 33.111 / 35.567 / 39.733 |
| Primary fallback / active bulk | 33.263 / 36.620 / 40.498 | 33.321 / 38.011 / 39.503 | 33.279 / 34.286 / 34.618 |
| Complete acceptance cases | **0 passed / 2 failed** | **1 passed / 1 failed** | **0 passed / 2 failed** |

The original-decoder isolation temporarily substituted SIPSorcery's `VpxVideoEncoder.DecodeVideo`
for the optimized decoder, preserving the rest of the pipeline, then restored the file in `finally`.
Its total input timings remain valid; the earlier probe revision's unfiltered telemetry/ack stage
details are not used as the corrected stage baseline. Failure with both decoders rules out the
optimized decoder as the sole cause. This 320x240 comparison does not measure 4K decoding.

### Pipeline ownership

Corrected baseline, dedicated input during active bulk; milliseconds, n=20 except acknowledgements.
Reflection proxies observe existing abstraction boundaries and forward real calls unchanged.
Secure input routing excludes clock telemetry; plaintext values are not recorded. These boundaries
include probe overhead and cannot expose private transport internals independently.

| Requested boundary/stage | p50 | p95 | p99 | Interpretation |
| --- | ---: | ---: | ---: | --- |
| Local event | — | — | — | Monotonic test timestamp at the sender API; physical/WinUI event unavailable |
| Coalescing/pacing and command creation | 0.024 | 0.041 | 0.056 | Event to collaboration SendAsync |
| Secure-record creation | 0.043 | 0.077 | 0.078 | Binding, serialization, send gate and AEAD to media call |
| Send admission | 0.013 | 0.020 | 0.022 | Media SendDataAsync call to return, not a socket-write hook |
| SCTP/DTLS/OS scheduling through receiver delivery | 32.879 | 38.183 | 38.959 | Send return to raw DataMessageReceived; individual transport stages are not separable here |
| Receiver delivery | — | — | — | Timestamped by the prior row; includes media lane validation/copy before the callback |
| Secure validation/decryption | 0.085 | 0.115 | 0.116 | Raw ciphertext callback to bound, decoded command |
| Permission/focus/rate checks | 0.003 | 0.005 | 0.005 | Decoded command to sink entry |
| Fake sink injection | 0.001 | 0.001 | 0.002 | **Not Windows SendInput** |
| Authenticated acknowledgement return | 33.241 | 39.281 | 39.281 | Injection to accepted ReportInputLatency; n=6 requested, 6 validated, 0 unavailable |
| Final event-to-injection | 33.069 | **38.328** | 39.140 | **35 ms gate failed** |

The probe waits up to two seconds for sampled acknowledgements and reports requested, validated and
unavailable counts. It never assumes every event requests an ack: production samples at most once
per 100 ms. Concurrent receiver delivery can precede send-call return; any negative boundary overlap
is explicitly counted, not discarded. This baseline had zero overlaps. Stage percentiles must not
be added; acknowledgement return is subsequent work, not part of event-to-injection latency.

The excess is localized to **post-admission transport/delivery**, not crypto, pacing or input
permission checks. These same-process monotonic intervals are not network RTT. Exact attribution
among socket delivery, SCTP, DTLS polling and OS scheduling requires deeper transport tracing;
the boundary measurements do not pretend to supply that unavailable split.

### Supported mitigation tested and rejected

The pinned SIPSorcery DTLS receiver enqueues incoming records, then polls an empty queue using
`Thread.Sleep(25)`. The inspected 10.0.16 and upstream source retain the same implementation;
there is no supported poll-interval setting or transport factory. See the exact commit/blob and
API/security/license analysis in the [dependency audit](competitive/THIRD_PARTY_LICENSE_AUDIT.md).

A balanced Windows `timeBeginPeriod(1)` experiment initially passed 2/2 cases with input p95
26.053–26.411 ms. A production-shaped, permission-scoped connected-session lease then produced
the **1 pass / 1 failure** candidate above. A repeat with native-call tracing failed **both** cases
(dedicated baseline p95 36.158 ms, fallback bulk p95 35.963 ms), despite successful begin calls and
no end call before cleanup. Acquiring earlier passed 2/2 but still had fallback p95 34.506 ms;
no evidence of timer-resolution caching in SCTP/DTLS initialization was found.

A separate diagnostic sleeper during a subsequent lease run measured actual `Sleep(25)` at
p50/p95/p99 **25.526 / 32.591 / 51.805 ms** during fallback bulk (n=28). That run's input cases
passed 2/2, illustrating why a passing short sample cannot establish a reliable correction.
The sleeper is a separate thread, not an internal DTLS trace. The lease's ten deterministic
ownership tests passed during the experiment; they do not prove latency. The temporary lease,
ownership tests, tracing and decoder substitution were all removed. **There is no adopted fix and
therefore no successful production “after” distribution.**

Microsoft documents that [timer requests](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)
can improve wait precision at a power cost and may be ignored for occluded Windows 11 applications.
[Sleep](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-sleep) also depends on
scheduling after the wait expires. No process power-policy override, registry change or real-time
priority was introduced.

The smallest transport-level candidate is an upstream event-driven receive wakeup retaining
timeouts, ordering, cancellation and DTLS behavior. A maintained fork would need security backports,
license review and unchanged ICE/TURN/SDP/SCTP/hybrid/focus/reconnect compatibility gates. Existing
native QUIC file transport is not a drop-in input lane or a WAN/TURN replacement. Work stops before
a fork or major migration, as requested; the audit records the bounded alternatives.

### Final retained-source validation

After removing the candidate, the strict Release solution build succeeded with zero warnings and
errors. The complete solution regression returned **846 passed / 1 failed / 5 skipped** across
18 test assemblies. Media was **138 passed / 1 failed / 1 skipped**; Application was **160 passed /
0 failed / 0 skipped**. The extra Media case is the new primary-input fallback acceptance case.

The unchanged dedicated-input acceptance failed during bulk at p95 **35.057 ms**. Final solution-run
event-to-fake-injection values (p50 / p95 / p99, n=20 each) were:

| Scenario | Final retained-source measurement (ms) | 35 ms stage gate |
| --- | --- | --- |
| Dedicated / no bulk | 32.941 / 34.630 / 37.824 | Passed |
| Dedicated / active bulk | 33.264 / 35.057 / 39.792 | **Failed** |
| Primary fallback / no bulk | 30.868 / 32.202 / 32.476 | Passed |
| Primary fallback / active bulk | 31.077 / 31.671 / 32.285 | Passed |

These are an additional full-suite run, not an improvement attributed to a production change.
Five skips retain their real environment scope: three need an explicitly isolated
`PEERONQ_TEST_REDIS` endpoint; signaling restart and live TURN require the configured Phase 3 test
controller/credentials. Those environments were not supplied to this run; the existing development
Docker stack is not automatically a disposable test environment. No skip is counted as a pass.

## Not measured

- Active capture, encode, decode and render CPU/GPU.
- 720p/1080p/1440p/4K quality under 5–1000 Mbps shaped links.
- 15/30/60 FPS stability, true end-to-end frame latency and input latency.
- Direct versus TURN-relayed throughput, loss and jitter comparisons.
- Physical two-device file-transfer throughput and interactive/frame-age latency during contention.
- Physical-device hardware-encoder behavior and long-duration resource growth.

These missing measurements are external acceptance blockers. They must not be inferred from unit
tests, idle process data, UI telemetry, or codec capability labels.
