# AI Changelog

**Purpose:**

- Keep a short history of AI-made changes.
- Prevent agents from rediscovering the same context.
- Help future agents understand why something was changed.

**Rules:**

- Newest entry on top.
- One entry per task, not per file.
- Keep each entry short — this file is read at the start of every task.
- Use absolute dates (`YYYY-MM-DD`).

**Format:**

```text
## YYYY-MM-DD — <short task title>

Task:
Files changed:
Reason:
Validation:
Risk:
Rollback:
```

---

## Entries

## 2026-09-28 - Package the website presentation update as 0.9.76

Task: Deliver a full server patch for the operator's production installation, including the
previously committed removal of the amber pilot/testing warning.
Files inspected: Canonical props, server/public-pilot builders, installer/Compose/ingress guards,
website/Portal Docker inputs and metadata, release/state/maps and source tests.
Files changed: Directory.Build.props, RELEASE_NOTES, CURRENT_STATE and this log. No runtime change.
Changes: Advance the shared server/Windows version to 0.9.76 and Android/Apple codes to 9076; rebuild
and publish the classified x64/ARM64 pair, embed the exact x64 MSI in the full immutable server .run,
and provide matching release notes. Preserve working SMTP/Portal/Admin policy and native behavior.
Validation:
- Website typecheck and 86/86 tests, Portal typecheck and 82/82 tests passed. Canonical Windows/server
  and native UI/accessibility/open-source invariants passed. Unchanged Release binaries run with
  --no-build passed Cloud 56/56, Admin 49/49 and deployment 21/21; full solution/media not rerun.
- Both Windows architectures built with zero warnings/errors and passed installer payload checks.
  The restarted local :5555 website selects 0.9.76 unsigned-public-pilot; both full HTTP 200 downloads
  and SHA256SUMS bytes matched. An initial ad-hoc checksum-document string comparison failed due
  to Python newline normalization; the corrected raw-byte check and both MSI hash checks passed.
- Full server hash/payload/header/path/source/version/embedded-MSI checks passed (684 files).
  Generated header shell syntax and extracted/source merged Compose guards passed. The existing
  real local TLS ingress/installer fixture passed public Portal/API, operator/metrics isolation,
  unknown-host rejection, embedded download integrity/cache checks and HTTPS header checks.
- Cloud API, Portal and public-site Docker builds passed using the exact extracted payload.
  Loopback HTTP checked six Portal SPA routes/security headers and the web bundle's new disclosure,
  absent old warning, portless Portal link, 0.9.76 metadata and exact served x64 MSI hash.
- Repository secret scan, staged gitleaks and git diff --check passed. No production deployment,
  recipient email, real-browser visual or physical-device performance test was performed here.
Artifacts: Server SHA-256 1bace95042d14cc1f00329bbb65f74973d2938991c0a7ad366ac6eef18652ceb;
x64 33910917a74d273132e5c54f17e06466308af6e0c0511b229dcf83ff5ce66c97;
ARM64 b1d9d6e21b2c9d0f7436f1b5315c7a021211443979c9ba0b9bb1bfaaa5fa1e46.
Risk: Windows packages remain unsigned public pilots; no detached GPG signature was created.
Production activation and signing/physical acceptance remain separate. Server apply does not
upgrade installed clients; keep the operator's working Resend environment and registration policy.
Rollback: Use the retained verified 0.9.75 server release without deleting data/forward migrations;
its old website notice returns, while its SMTP egress correction and current protected env remain.

## 2026-09-28 - Simplify the public download presentation

Task: Remove the highlighted pilot warning from the production-facing landing page.
Files inspected: DownloadsPage/LandingPage, route tests, design system, release metadata contracts;
Admin bootstrap/deployment instructions for the operator's separate credential question.
Files changed: DownloadsPage, existing route tests, DESIGN_SYSTEM, CURRENT_STATE and this log.
Reason: The prominent amber pilot/testing notice dominated the primary download action.
Changes: Replace the warning and testing-only sentence with a collapsed native Installer details
disclosure containing the existing unsigned classification and factual digital-signature status.
Package selection, checksums, canonical version and signing gates are unchanged. No new package
or product version was published. Record the operator-confirmed production Portal recovery,
separate email verification and successful sign-in without recording personal data or secrets.
Validation: Public website typecheck, production build and all 86 tests passed. Existing embedded
download coverage now checks collapsed details and disclosure on click. Local :5555 served the
revised UI module over HTTP 200 and still selected 0.9.75 unsigned-public-pilot. Repository secret
scan and git diff --check passed. Browser discovery returned no available browser, so visual and
real-browser keyboard checks were not performed. Native/backend behavior was not changed/tested.
Risk: This source/UI change is not deployed to production by the commit. Installer signatures remain
unchanged; operating the server in production does not certify the downloadable Windows package.
Rollback: Revert this UI commit; no database/configuration migration is involved.

## 2026-09-28 - Restore production Cloud API SMTP egress (0.9.75)

Task: Diagnose the existing-account recovery success screen with no message in Resend Emails.
Files inspected: CustomerAccountService/CustomerMail/endpoints, production/staging/development
Compose networks, deployment regression scripts, state/maps/release tooling and Docker/Resend docs.
Files changed: Staging Compose, merged-model regression script, canonical version, project map,
deployment/current-state/release notes and this log. Auth, portal and native runtime code unchanged.
Reason: Cloud API inherited only internal control/observability networks, so enabling Smtp did not
create an outbound route. Recovery intentionally hides delivery failure to avoid account enumeration;
the existing account.mail_delivery_failed audit records that case. A generic response is not delivery.
Changes: Only staging/production Cloud API joins a dedicated customer-mail-egress bridge; shared
internal networks, private ports and other service attachments are preserved. The bridge supplies
outbound routing, not a SMTP provider/port firewall allowlist. Development FileSink is unchanged.
Preserve the operator's existing Resend/API key/sender; normal upgrade commands must not disable mail.
Validation:
- New Compose guard failed before the fix (no SMTP outbound route), then passed on real merged
  staging/production models; it checks exclusive membership, internal database/cache and shared
  networks, unchanged application/public/operator port boundaries and development isolation.
- Isolated local Docker probe using the pinned ASP.NET runtime failed to obtain an SMTP banner
  on an internal-only network; attaching an outbound bridge obtained 220 Resend SMTP Relay ESMTP.
  No AUTH, API key, email submission or real account data was used; all probe resources were removed.
- A separate container STARTTLS probe to smtp.resend.com:587 passed certificate-chain and hostname
  verification. This is outbound/TLS evidence from the development workstation, not production
  SMTP authentication or inbox delivery. The operator's failed mail attempt was on production.
- Bootstrap/installer shell contracts and canonical server/client/native UI invariants passed.
  Existing Release binaries run with --no-build: deployment 21/21, Cloud.Infrastructure 56/56,
  Admin 49/49. No backend/auth source changed, and the full solution/media suite was not rerun.
- Portal 82/82 and public website 86/86 tests and both typechecks passed. Matching x64/ARM64
  0.9.75 MSI builds passed with zero warnings/errors and payload validation. Local :5555 restarted,
  selected unsigned-public-pilot 0.9.75 and served full HTTP 200 bytes matching both SHA256SUMS.
- The full server checksum, archive paths, payload/header/source/version and embedded MSI checks
  passed; generated header shell syntax passed. Cloud API, Portal and website production Docker
  images built from the exact extracted bundle. That extracted Compose passed the full merged
  model guard; loopback HTTP checked six Portal routes/security headers and the website's exact
  x64 download/metadata/portless portal link. No deployment was performed on the production host.
- Server SHA-256: fbfb084e697f6c8ec9ef55bf5535d0a620c3da74b6d981a5dea4bb619db1cc4c.
  Windows x64: 9f60108c3d99fc8cebb4404a486a514a6e6478c13d04f90a99af64ff73ab9273;
  ARM64: 22eb50a67e0389264c125167cd85b7d7eb6fe2c3a5dfe132d21ecfebdd40e620.
  Server/clients are unsigned public-pilot candidates; no detached GPG signature was created.
  Physical performance/signing gates remain unchanged; client behavior is unchanged.
Risk: The production network/firewall, SMTP credentials/sender authorization and actual recipient
delivery are not proven by the local connectivity probe. No production install was performed here.
Rollback: Retained server release/volumes/forward migrations remain available; rollback restores the
old networking and can reintroduce the SMTP issue. Preserve current SMTP settings and never publish
application/database/cache ports as a workaround.

## 2026-09-28 - Preserve previously configured Resend SMTP

Task: Explain missing auth links after the operator clarified that Resend worked on an older release.
Files inspected: Portal capability gates, CustomerMail, installer policy/Compose/active-release
handling, deployment docs and official Resend SMTP documentation.
Files changed: docs/CURRENT_STATE.md and this log only; no new version or package.
Reason: Live HTTPS capabilities returned Closed/registration=false/recovery=false/verification=false.
The earlier suggested --disable-customer-mail flag explicitly clears SMTP host/username/password;
the old no-SMTP assumption is superseded. Future upgrades must preserve existing Resend settings.
Validation: Read-only production capabilities HTTP 200, source-verified STARTTLS/SMTP mapping and
configuration reload commands. No private server env was read and no live email was sent. Source
and test execution are unchanged; configuration recovery must be performed on the production host.
Risk: Original API key availability is unknown. Restore only mail settings from a protected backup
or use a replacement sending key in the existing Resend account; never paste secrets in chat/Git.
Rollback: Documentation-only change; no runtime or production data changed.

## 2026-09-28 - Explicit customer auth navigation (0.9.74)

Task: Fix ambiguous Sign in/Create account/Forgot password/Resend verification navigation.
Files inspected: Required repository maps/contracts/state, canonical version/release tooling;
Portal App/pages/auth/API/password fields/brand/styles/tests; existing customer endpoint/capability
contracts and production SPA fallback. No authentication or media protocol rewrite.
Files changed: Portal App/pages/uiStates/styles and auth tests; Directory.Build.props, project/route
maps, CURRENT_STATE, release notes and this log.
Reason: Local AuthMode buttons did not update browser history; successful recovery/resend/register
immediately switched back to login, hiding which flow had completed.
Changes: Real links/routes at /register, /forgot-password and /resend-verification; reactive token
routes retained. Route-scoped state, first-field/success-heading focus, explicit button types,
capability-gated direct entry, preserved invitation token, dedicated generic success screens and
safe existing-account/mail errors. Real API endpoints/auth provider are reused unchanged.
Security: Host-only __Host- cookies, CSRF/rate limits/email verification, customer MFA off, Admin MFA,
organization authorization and native LAN accountless access are unchanged. No overlay was added;
the existing authenticated navigation scrim is outside AuthFrame. SMTP was not provisioned: the
operator has no provider, so Closed registration/mail Disabled remain required until SMTP is ready.
Validation:
- Before the fix, all three new link-navigation tests failed against the state-button implementation.
- Node 24 Portal typecheck/build and 82/82 tests passed (24 new navigation cases); public website
  typecheck and 86/86 tests passed. DOM click/keyboard/history/direct-entry, success, capability,
  password/invitation, stale-request, /verify-email and /reset-password coverage passed.
- Cloud.Infrastructure 56/56 and Admin 49/49 passed. Deployment configuration 21/21 passed using
  the existing Release test binary with --no-build; merged Compose URL/private-port checks passed.
- Canonical client/server version and native UI/accessibility invariants passed; repository secret
  scan passed. Both Windows architectures built with zero warnings/errors and passed MSI payload
  validation. The restarted local :5555 website selects 0.9.74 unsigned-public-pilot; full GETs of
  both MSI URLs returned HTTP 200 and matched the published SHA256SUMS.txt.
- Full .run checksum/payload/header/version/archive-path checks and embedded x64 MSI checks passed;
  the generated header passed POSIX shell syntax validation. Portal and public-site production
  Docker images built from the exact extracted bundle. All six auth routes returned the SPA over
  loopback HTTP with security headers; deployed JS includes the new routes/success guidance.
  The bundled public site serves the exact 0.9.74 x64 bytes and portless portal link. An initial
  ad-hoc origin probe incorrectly expected the edge-only no-store header; after checking ownership,
  origin bytes and existing ingress/bootstrap cache-policy contracts were verified separately.
- New packages are unsigned public-pilot candidates; no server GPG signature was created. Native
  behavior is unchanged (shared version stamp only); server installation does not update clients.
- Server: dist/server/peeronq-server-0.9.74.run; SHA-256
  17d7587da5067fbb8cf7f6d7f70f6ac1e647c09511b5f82395fddd5f4be61626.
- Windows x64 SHA-256: 9a9a16e062d818433f8ca376f290da18c996d26b8fdbb5c9c938d23d5daa2e14;
  ARM64: 477d090835db532efa49c3ee2c5da252156df0e467a481196545b5453f9a6d19.
Risk: No connected browser was available, so visual/pointer-hit testing is not claimed. No live SMTP,
physical Windows/ARM64 session, production install or desktop sharpness acceptance was performed.
The full solution/media suite was not rerun; the previously documented media timing failures remain.
Rollback: Use the retained verified server release via --rollback without deleting data/forward
migrations. Revert the frontend change if needed; server rollback does not downgrade installed clients.

## 2026-09-28 - Physical sharpness baseline; endpoint-version gate pending

Task: Investigate the physical fullscreen blur without assuming the earlier latency fix solved it.
Files inspected: Required maps/state/performance/protocol docs; Windows capture/scaler, VP8 codecs,
WebRtcMediaSession/statistics/adaptation, connection policy, native viewer/main UI and relevant tests.
Files changed: docs/PERFORMANCE_REPORT.md, docs/CURRENT_STATE.md, AI_CHANGELOG.md only.
Reason: The user explicitly requires BOTH running client versions before media edits. Current
origin/main is e831413, source 0.9.73; physical viewer/sharer/server versions and monitor/DPI are
unknown and have been requested. Matching 0.9.73 MSI pair exists; installed versions are not inferred.
Validation: Temporary probe using current code reproduced ONE 80 ms render sample causing L3 at
t=2 s. Its latency expires at t=6; fresh network feedback recovers L0 at t=20, absent RTCP remains
L3 through t=60. 18 real synthetic scaler/VP8 encode/decode cases show 4K->Automatic1080p loses
1 px stripe detail even at higher bitrate. No physical source/capture/viewport or render timing claim.
Existing targeted tests: Media 60/60, FrameScaler 24/24, pointer/DPI 10/10. Runtime unchanged;
no full-suite rerun, version bump, MSI rebuild, server deployment or physical acceptance this task.
Risk: These source reproductions do not establish the user's physical root cause. Diagnostic UI,
evidence-backed correction and release remain pending Phase 0. Preserve all congestion/security
limits and the previously confirmed fixes; do not treat missing RTCP as healthy networking.
Rollback: Documentation-only entry can be reverted independently; no runtime/data change.


## 2026-09-28 - Customer email authentication and personal portal completion (0.9.73)

Task: Complete the existing customer architecture; preserve Admin/native identity boundaries.
Files changed: CustomerPortalOptions/Authentication/Endpoints, CustomerAccountService,
CustomerOrganizationService, CustomerMail, CustomerIdentityEntities, CloudApiApp; real Portal
forms/auth/API client/tests; bootstrap/installer/Compose and acceptance/contract tests; canonical
version, release notes, deployment/security/current-state and project/route/design maps.
Reason: Production Closed registration and installer mode resets were inconsistent with public
onboarding; UI lacked policy discovery, resend/confirmation/password-change and optional-MFA state.
Changes: Public capabilities, verification resend with old-token invalidation/cooldown, authenticated
password change/current-session preservation/other-session revocation, single-flight refresh,
customer-only MFA off with stored enrollment/recovery/policy retained, explicit registration/MFA
installer flags preserving valid upgrades, approved SMTP sender configuration. Native code/protocols,
Admin MFA, operator CIDRs, cookies/CSRF and accountless LAN are preserved. Device-claim UI remains
blocked on a native single-use ownership-proof exchange; no raw device-token or ID-only shortcut.
Validation:
- Exact requested `dotnet restore PeerOnQ.slnx` passed. Release solution build with
  `--no-restore -p:ContinuousIntegrationBuild=true -p:EnableNETAnalyzers=true -p:AnalysisLevel=latest
  -warnaserror` passed (0 warnings/errors); final affected API/tests rebuilt with analyzers.
- Exact requested `dotnet test PeerOnQ.slnx --configuration Release --no-restore --nologo` completed:
  885 passed, 4 failed, 5 skipped across 18 projects. Failures were unchanged real-media/video/input
  timing and QUIC interactive timing. Isolated Transport: 7/7 passed; isolated Media: 141 passed,
  2 existing input p95 failures (38.1/35.6 ms against 35 ms), 1 live-TURN skip. No threshold relaxed.
- Final Cloud.Infrastructure suite: 56/56 (21 CustomerPortalSecurity cases including SMTP failure).
  The full run also passed Admin 49/49, Cloud.Domain 14/14, Cloud.Application 19/19 and Observability
  35/35 (including 21 deployment tests). No failing/skipped result is reported as PASS.
- Node 24 Portal typecheck/build and 58/58 tests; public-site typecheck and 86/86 tests passed.
  Native UI, Windows/server version guards, merged Compose policy/URL/port matrix, Linux bootstrap
  upgrade contracts and trusted local Nginx TLS/public/private/unknown-host regressions passed.
- Current local HTTPS/FileSink harness executed 114 validated HTTP requests: Open/Closed/InvitationOnly,
  real invitation signup, verification/resend/expiry/replay, password reset/change, active/revoked
  sessions, CSRF, logout/relogin, profile/org/devices/history, tenant/RBAC/audit, actual lockout/rate
  limits, MFA enable/restart/disable/re-enable with stored enrollment/recovery/org policy retained.
- Fresh 0.9.73 x64/ARM64 MSI builds and installer payload checks passed. Preview restart initially
  rejected system Node 25; restarting with the existing Node 24 toolchain succeeded. Local :5555
  selects 0.9.73 unsigned-public-pilot; both full HTTP downloads returned 200 and matched SHA256SUMS.
- Full server bundle built and extracted: 79,258,256 bytes / 683 files; header/payload hash, safe
  archive paths, exact source/notes, canonical version and embedded x64 hash verified.
  Production web Docker image built successfully from that exact extracted bundle with portless
  portal/download HTTPS URLs and its embedded checksum-verified x64 MSI.
  Server SHA-256: 997841e6da699170e080c5949e19c59491c8050edfdfa29efce2a4031c28db1d.
  x64: fac3f3efe2940992d5b26eede0b8803ef53fbfd7270440922a78e5a9c1bdfc50.
  ARM64: 7adf8d55caaad541c168f6f58c1eefada180f709a9f6385ff9ff2cd37119a88b.
- Repository secret scan and diff whitespace check passed. Live SMTP/approved-mailbox acceptance
  remains BLOCKED. Browser visual/physical Windows/ARM64/4K/WAN and production installation were not
  performed; local API/DOM tests are not substitutes for those gates.
Risk: Customer MFA off is intentional; keep Admin MFA. Existing media timing gates remain red;
unsigned-pilot/signature and live-email gates mean this is not production approval.
Operator handoff: Always give the exact versioned server CLI and hash; do not assume a copied
`.run` has its `.sha256` or `.asc` sidecars. Server/client versions stay unified. Configure real SMTP
and verification before Open registration; never disable verification or broaden CIDRs to proceed.
Rollback: Retained server --rollback plus restoration of prior protected policy/mail configuration;
preserve data volumes/migrations. Server rollback does not downgrade installed clients.

## 2026-09-27 - Expire stale media pressure for quiet-desktop recovery (0.9.72)

Task:
- Investigate the user's later report that a connected desktop becomes blurred over time.
Files changed:
- MediaStatisticsCollector, PipelineTests, AdaptiveQualityTests, canonical version, release notes,
  PROJECT_MAP, CURRENT_STATE and this entry. The previously committed portal fix is retained.
Reason:
- Frame/render latency queues retained 120 values without time expiry; periodic feedback kept
  reporting a historic stall. Sparse input samples also refreshed the lifetime of older spikes.
  Three new tests failed before the fix, including a quiet desktop stuck at quality rung 4 after
  a single transient delay. Individual samples now expire after five seconds, with the same
  120-sample cap. Fresh slow frames still degrade and recovery still requires healthy network data.
Validation:
- Complete Media suite: 143 passed, 1 existing live-TURN test skipped without its external fixture.
  New coverage exercises expiry boundaries, sparse healthy samples, preserved totals, simulated
  one-minute recovery and ongoing genuine pressure. Native UI and all platform/server version
  guards passed for 0.9.72 (Android/Apple codes 9072).
- Cumulative portal checks in the preceding entry remain applicable; its pushed Quality run
  36344033758 passed production-ingress, all web images, web-workspace and secret-history. Its .NET
  job failed the two existing interactive-input p95 gates (36.9/37.1 ms versus 35 ms); the thresholds
  are unchanged. Local Media success does not establish remote CI or physical-device performance.
- Fresh 0.9.72 self-contained x64/ARM64 builds and MSI payload validation passed. The restarted
  website selects the new unsigned-public-pilot pair; both complete HTTP GETs returned 200 with
  matching checksums, and both application assemblies are 0.9.72.0.
- Built the immutable 79250908-byte server bundle with the exact matching x64 client; verified
  payload/header hashes, canonical version, release notes and shell syntax. Built its extracted
  website using the merged production arguments and verified HTTP-served portless portal/download
  URLs plus the complete embedded MSI hash. Source secret scan and git diff --check passed.
Risk:
- A deterministic controller reproduction is not proof of the exact cause on the user's devices.
  No physical two-device/4K/WAN, browser sign-in or new production deployment was tested. Update
  both endpoint clients: installing the server alone does not change their running media pipeline.
  Encryption, consent, protocol and accountless LAN behavior are unchanged; packages remain pilots.
Rollback:
- Restore a retained client/server release through the existing procedure; preserve data volumes.
  Reverting this measurement change can reintroduce persistent false quality pressure.

## 2026-09-27 - Correct public portal navigation in release 0.9.71

Task:
- Fix Portal/Sign in timing out on the public website's inherited development port.
Files changed:
- Staging Compose web build arguments, merged-Compose regression/Quality wiring, canonical version,
  deployment/release documentation, PROJECT_MAP, CURRENT_STATE and this entry; no auth/native changes.
Reason:
- The actual production JS compiled https://portal.peeronq.com:8443. Staging extends development,
  whose web build arguments appended its 8443 default; Dockerfile defaults did not override them.
  Explicit portless portal/download URLs fix both navigation entries without opening another port.
Validation:
- The real Compose regression fails against the original files and passes for the corrected
  staging/production merge with absent, 8443 and 443 bind settings; localhost development is retained.
- 21 deployment tests, 12 customer security tests, 86 public-site and 47 portal tests, both
  frontend typechecks, portal build, native UI and Windows/Linux/Android/Apple/server guards passed.
- Linux installer contracts and trusted-TLS Nginx boundary fixture passed; operator restrictions,
  same-origin portal API, public metrics denial, unknown hosts and security headers remain enforced.
- Built and payload-validated matching 0.9.71 x64/ARM64 MSIs. Restarted local preview selects the
  checksum-verified unsigned-public-pilot pair; both complete curl downloads returned 200 and matched
  SHA256SUMS; both app assemblies are 0.9.71.0. A slow PowerShell download was canceled, not counted.
- Built immutable 79253858-byte server bundle, verified payload/header/embedded MSI/version/notes,
  and built its extracted website with the actual merged production arguments. Strict checks on
  its HTTP-served JS found portless URLs, and the complete embedded MSI matched the x64 hash.
- Source secret scan (1106 files), workflow formatting and git diff --check passed. Public live
  portal HTTPS returned 200 and anonymous profile 401; these do not prove real-account sign-in.
Risk:
- No browser connection or real-account/physical-device test; operator must install and verify.
  Packages remain unsigned pilots, with existing release/performance gates unchanged. The separately
  reported long-session blur is not fixed by this version-only native rebuild.
Rollback:
- Use the installer's retained previous release; preserve volumes and forward-only migrations.
  Old immutable packages are unchanged; no DNS/NAT, cookies, CORS or operator CIDR changes.

## 2026-09-27 - Confirm the workstation matches the operator allowlist

Task:
- Resolve the pending CIDR clarification from the successful 0.9.70 deployment.
Files changed:
- docs/CURRENT_STATE.md and this entry.
Reason:
- The user supplied a single-host /32 CIDR; Get-NetIPAddress confirmed this workstation matches it.
  Its successful operator-host responses are consistent with allowed access.
Validation:
- Read-only local IPv4 inspection; no server configuration or package change.
Risk:
- Live denial from outside the allowed CIDR remains untested; do not infer universal reachability.
Rollback:
- Documentation only; preserve the existing operator allowlist and active 0.9.70 release.

## 2026-09-27 - Record successful operator deployment of 0.9.70

Task:
- Review the operator's latest output and remember the verified installation fixes.
Files changed:
- AGENTS.md, docs/CURRENT_STATE.md and this entry; no runtime or artifact changes.
Reason:
- The supplied output is successful, with 0.9.70 healthy/active and current pointing to its release.
  Preserve the Node build compatibility and nested download-header fixes as confirmed behavior.
Validation:
- Actual external HTTPS requests from this workstation passed certificate validation: website/www,
  portal, API liveness and signaling readiness returned 200; API was healthy and signaling ready.
- Public version metadata was 0.9.70; the complete x64 MSI returned 200, matched the validated
  package's SHA-256, and retained no-store. Detailed evidence is in CURRENT_STATE.
- Repository secret scan (1105 files) and git diff --check passed; no runtime tests were needed
  for this documentation-only change.
Risk:
- Operator CIDR isolation remains unverified from this possibly allowed workstation; requested the
  exact configured CIDR after Admin/Grafana/Prometheus responses of 200/302/302. A separate web-tool
  attempt was inaccessible without usable status, not a passed isolation test. No account-login,
  physical-session, performance or signing approval is inferred. No production mutation performed.
Rollback:
- Revert only this documentation commit if needed. Keep the active release and data volumes intact.

## 2026-09-27 - Fix installer download-header parsing for release 0.9.70

Task:
- Fix the operator's false cache-policy failure after healthy 0.9.69 startup.
Files changed:
- Linux installer, bootstrap contract and Nginx fixture tests, Quality workflow, Directory.Build.props,
  PROJECT_MAP, CURRENT_STATE, DEPLOYMENT, deployment RELEASE_NOTES and this entry.
Reason:
- Single quotes inside the proxy's single-quoted sh -ec command consumed the backslash in
  tr -d '\r'. The container deleted literal r characters, so Cache-Control was never recognized.
- Correct only that quoting; retain all publication/TLS/hash/version gates. Advance the shared
  server/client version to 0.9.70 and Android/Apple codes to 9070; native behavior is unchanged.
Validation:
- Added an executable regression using the actual nested installer command: it reproduced the
  operator's error before the fix and passed afterward. Missing/cacheable headers remain rejected.
- Linux bootstrap/Compose contract and real Nginx ingress tests passed in the pinned production
  image with locally trusted TLS. The complete original publication function also failed against
  that real proxy, while the corrected function passed. Added both checks to GitHub Quality.
- All 21 deployment tests, 86 public-site tests/typecheck, workflow formatting, native UI and all
  platform version/publication guards passed. The Linux contract ran in Linux containers; a Git Bash
  attempt stopped in the pre-existing TLS fixture before reaching the new regression.
- Fresh x64/ARM64 MSI builds and extracted-payload validation passed. App assemblies are 0.9.70.0;
  the restarted website selects 0.9.70 unsigned-public-pilot and both full HTTP downloads returned
  200 with matching SHA-256. Built dist/server/peeronq-server-0.9.70.run (79241730 bytes); verified
  680 safe payload files, the corrected installer header, canonical/embedded versions, matching
  x64 MSI, byte-exact release notes and payload/bundle hashes.
  Bundle SHA256: 05cc035783ef89481a2292cf1784e30744333a8df016942e2b25ec23f996b36f.
- Built the actual bundle's web image and verified its healthy isolated container serves the
  complete embedded MSI with the expected hash and version 0.9.70. Packaged installer bash -n,
  repository secret scan (1105 files) and git diff --check passed. Packages remain ignored.
Risk:
- No production rollout or physical-device validation. Existing signing and WebRTC latency release
  gates remain open; a packaging fix does not establish production approval.
Rollback:
- Preserve immutable older packages and retained releases/data volumes. Do not disable the check
  or manually activate a failed release. A source revert restores the known header parsing bug.

## 2026-09-27 - Remember server patch CLI handoff preference

Task:
- Include installation commands whenever handing the user a server patch.
Files changed:
- AGENTS.md and this entry.
Reason:
- The user copied the patch to the server and requested persistent, version-specific CLI guidance.
Validation:
- Checked upgrade, preflight and status flags against the existing installer and deployment guide.
Risk:
- Documentation only; this does not deploy the patch or remove existing signing/release gates.
Rollback:
- Revert this documentation change.

## 2026-09-27 - Fix production web Docker Node mismatch in release 0.9.69

Task:
- Fix the operator's 0.9.68 installation failure during the production stack build.
Files changed:
- Three web Dockerfiles, package.json, Directory.Build.props, deployment tests, Quality workflow,
  DEPENDENCIES, PROJECT_MAP, CURRENT_STATE, DEPLOYMENT, deployment RELEASE_NOTES and this entry.
Reason:
- Admin/Portal failed with ERR_PNPM_UNSUPPORTED_ENGINE: pinned Node 24.4.1 was below locked jsdom
  30.0.1's minimum. The operator's status still pointed to 0.6.28 after the attempted upgrade.
- Pin official Node 24.21.0 images by verified registry digest, retain Debian/Alpine variants,
  declare >=24.15.0 <25, and build all three actual production web images in CI. Preserve engineStrict,
  frozen installs and minimumReleaseAge. Advance server/client version to 0.9.69 and mobile codes 9069.
Validation:
- All three new regression cases failed on 24.4.1 before the fix; all 21 deployment tests then passed.
  Strict deployment test-project build passed with zero warnings/errors. The three complete Docker
  builds passed; isolated runtime containers were healthy and served /health/live as non-root nginx.
- Workspace typecheck, 169 frontend tests, workflow/package formatting, native UI and all platform
  version guards passed. Fresh x64/ARM64 MSI extraction matched publish payloads by SHA-256; app
  versions are 0.9.69.0. Local website restarted and selected 0.9.69 unsigned-public-pilot; both full
  HTTP downloads returned 200 and matched the published checksums.
- Built dist/server/peeronq-server-0.9.69.run (79253737 bytes), containing 680 safe payload files,
  byte-exact release notes and the matching validated x64 MSI. Verified canonical/embedded versions,
  payload/bundle hashes and installer header bash -n. Built the website image from the actual bundle
  payload; its isolated container passed health, served the complete MSI with its expected SHA-256
  and reported embedded version 0.9.69. Built download URL/cache-busting checks passed.
  Bundle SHA256: 93e7a2055744aa7852b1acfc090c02f2fbf0cde4e150d059d62aa0f3b0216dc2.
- Repository secret scan (1105 files) and git diff --check passed. Packages and checksums remain
  ignored; only source, tests and documentation are committed.
Risk:
- No production rollout or external DNS/TLS/physical-device validation. Existing GPG/Authenticode
  signing and WebRTC CI latency gates remain open; these are controlled pilot candidates. Full .NET
  and transport performance suites were not repeated for this Docker/build change.
Rollback:
- Restore a retained verified server release without removing data volumes. Reverting to the old
  Docker Node pin reintroduces the known build failure; previous immutable artifacts remain intact.

## 2026-09-27 - Build matching server and Windows client release 0.9.68

Task:
- Apply the shared-version policy to actual server and client packages, not only documentation.
Files changed:
- Directory.Build.props; server/development/release builders; MSI validator and server invariant;
  Quality workflow; PROJECT_MAP, CURRENT_STATE, DEPLOYMENT, deployment RELEASE_NOTES and this entry.
Reason:
- Advance the canonical version to 0.9.68 and Android/Apple codes to 9068. Reject a noncanonical
  server version before staging and compare the actual MSI ProductVersion with the release version.
  Run the server invariant in CI. Native behavior, endpoint metadata and protocols are unchanged.
Validation:
- Regression failed before the guard and passed afterward; actual 0.9.67 MSI rejected for 0.9.68.
  Windows/Linux/Android/Apple version checks, native UI guard, 18 deployment tests, 86 public-site
  tests, public-site typecheck/build and workflow formatting passed.
- Fresh x64/ARM64 self-contained builds and MSI administrative extraction/payload hash validation
  passed; both compiled app assemblies resolve to 0.9.68.0. Local website restarted with the matching
  unsigned-public-pilot pair. Both complete HTTP downloads returned 200 and matched SHA256SUMS.txt;
  both MSI ProductVersion values are 0.9.68.
- Built dist/server/peeronq-server-0.9.68.run (79241812 bytes), with byte-exact release notes and
  the validated x64 client. Verified 680 safe payload files, canonical source/embedded versions,
  MSI/payload/bundle hashes and installer header bash -n.
  Bundle SHA256: 9e8892705c78b38faa140a8b2b05ece4d2126eae0e4c9ae11182e818ac104fdd.
- Repository secret scan (1105 files) and git diff --check passed. Generated packages and checksums
  remain ignored; only source, tests and documentation are committed.
Risk:
- Controlled pilot candidate only: server GPG and client Authenticode signatures remain unavailable.
  The prior two GitHub WebRTC latency failures are not resolved by version alignment. No production
  deployment, DNS/TLS/reachability or physical-device testing; full .NET/Admin/portal/performance
  suites were not repeated for this packaging change. Installed devices do not update automatically.
Rollback:
- Revert this source change and restore the retained verified download pair, then restart the local
  preview. Retain immutable artifacts; production hosts and databases were not modified.

## 2026-09-27 - Record unified server and client release versions

Task:
- Record the user's requirement that server patches and clients use the same version and advance
  together in future releases, including changes limited to server/web behavior.
Files changed:
- AGENTS, PROJECT_MAP, docs/DEPLOYMENT, deployment RELEASE_NOTES and this entry.
Reason:
- Replace the independent server version policy with one canonical Directory.Build.props version.
  Require matching rebuilt client packages, full server bundle and release notes; update deployment
  examples to reuse that version. Historical 0.6.46/0.9.67 candidate remains explicitly noncompliant.
Validation:
- Documentation-only change; reviewed the canonical version source and build parameter contract.
  No packages rebuilt/renamed, version bump, runtime changes or production deployment in this task.
- git diff --check and the repository secret scan passed (1105 files); runtime suites were not rerun
  for this documentation-only change.
Risk:
- Existing signing and CI blockers remain. This records release policy; it does not add a new
  automatic server/client equality check to the builder or certify existing artifacts.
Rollback:
- Revert this documentation/instruction commit; no runtime or data rollback is needed.

## 2026-09-27 - Include server and client scope in requested patch deliverables

Task:
- Record the user's rule that a patch includes the full server upgrade and identifies any client
  changes because the operator intends to apply it to production.
Files changed:
- AGENTS, docs/DEPLOYMENT, PROJECT_MAP, deployment RELEASE_NOTES and this entry.
Reason:
- The earlier local 0.6.45 candidate embedded client 0.9.66. Built a new immutable 0.6.46 candidate
  with the validated canonical 0.9.67 x64 MSI. Release notes are included by the existing src payload
  selection and copied beside the candidate; no installer/proxy/authentication code was changed.
- Notes identify the separate server/client versions, Windows client changes, public/portal updates,
  signing classification, outstanding validation gates and the independent client installation step.
Validation:
- Existing server builder publication/version/MSI invariants and embedded x64 MSI validation passed.
- Verified 680 payload files, safe archive paths, exact embedded release notes, client 0.9.67 metadata,
  MSI SHA-256, payload SHA-256 and complete bundle checksum. Installer header passed bash -n.
- Bundle: 79242107 bytes; SHA256 32912908434770df34663a78f4d3f9a8fc33d6113a61391724e3fcc4c5912701.
- Repository secret scan and git diff --check passed. Generated .run/checksum/notes sidecars remain
  outside source control. No runtime changes warrant repeating frontend or .NET suites in this task.
Risk:
- NOT production approved: no trusted server GPG signature, unsigned pilot MSI and two known GitHub
  WebRTC latency failures. Existing client release/dependency limitations remain documented in notes.
  Production signing metadata was requested; no key, signature bypass or deployment was introduced.
Rollback:
- Revert these documentation/instruction changes. The candidate has not been installed; no production
  rollback or database operation was performed. Existing immutable bundles remain intact.

## 2026-09-27 - Correct cross-platform Quality workflow failures

Task:
- Diagnose the failed main Quality run 36334120630 from its actual GitHub job logs.
Files changed:
- .gitattributes, .github/workflows/quality.yml, public routeSeparation tests,
  ManagedQuicRemoteSessionTransportTests, pnpm workspace/lockfile, DEPENDENCIES, PROJECT_MAP and this entry.
Reason:
- Eight public download assertions inherited jsdom's Linux runner identity while expecting Windows
  links. Give the suite an explicit Windows device fixture; existing non-Windows/ARM64/unknown
  device cases still override it. Production device detection remains unchanged.
- Windows checkout converted two canonical signaling JSON vectors to CRLF, breaking byte-for-byte
  checks against LF wire output. Pin only those shared vectors to LF; do not normalize assertions.
- Native QUIC benchmarks overlapped other test collections and solution tests overlapped builds/
  test projects. Run the already-built solution tests serially and isolate native QUIC acceptance
  through the same xUnit nonparallel collection pattern already used for real WebRTC acceptance.
  Retain all performance assertions, including the 35 ms input gate and explicit 1 GiB transfer.
- The previously unreached web audit step also blocked on five high findings in existing fast-uri
  and js-yaml overrides. Advance only their patch floors to 3.1.6 and 4.3.2 after registry/license/
  release-age review; no new package or minimumReleaseAge exception is introduced.
Validation:
- Strict Release solution build passed with zero warnings/errors. Serial full solution run:
  875 passed, 0 failed, 5 existing environment-dependent skips (Redis/restart/live TURN).
- Node 24 workspace typecheck/lint and all 169 frontend tests passed (86 public, 36 admin, 47 portal).
- Fresh core.autocrlf=true checkout preserved both signaling vectors byte-for-byte with LF.
- Explicit 1 GiB QUIC test passed: 25.2 MiB/s payload, 0.4 ms interactive p95, 2716 samples.
- .NET dependency audit reported no high/critical findings. pnpm audit --audit-level high passed
  after remediation; four previously documented moderate findings remain (qs and Vitest/mocker).
- Full workspace build and OpenAPI generator smoke check passed with the patched build dependencies;
  checked-in generated API files are unchanged. Fallow, repository secret scan and diff checks passed.
- GitHub Quality run 36335866350 for d3b653b confirmed web-workspace and secret-history success.
  Windows: 873 passed, 2 failed, 5 skipped; signaling vectors and all 7 QUIC tests passed. The two
  WebRTC input cases still exceeded 35 ms at baseline p95 37.4/37.2 ms. CI is NOT fully green.
  The later GitHub 1 GiB/audit steps were skipped after that failure; their passing evidence above
  is local execution only. Reordering tests is not a fix for the existing DTLS polling limitation.
Risk:
- This fixes test inputs/execution, not the documented production transport latency limitation.
  An initial isolated input test still reproduced a 39.7 ms baseline before the complete serial run
  passed. Shared-runner timing is not a physical-device/WAN latency guarantee; thresholds stay strict.
- No native runtime behavior, protocol, client version or security policy changed.
Rollback:
- Revert this commit; no data or installer migration is involved.

## 2026-09-27 - Publish local Windows client 0.9.67 patch packages

Task:
- Package the recent native product-consistency changes as a patch over the installed 0.9.66 client.
Files changed:
- Directory.Build.props, localDownloadTelemetry.ts and its regression tests, PROJECT_MAP,
  docs/CURRENT_STATE and this entry. Generated installers/checksums remain outside source control.
Reason:
- Advanced the canonical client version to 0.9.67 and derived Android/Apple bundle codes to 9067.
  Retained the previous public-pilot endpoint/trust metadata and unsigned classification; no native
  protocol, account requirement, production-signing or automatic-update trust change was introduced.
- Full HTTP download verification uncovered an unhandled telemetry start rejection: DNS failure
  before the MSI response finished terminated Vite and reset the download. Handle this immediately,
  report the failure, and skip completion without a telemetry session. HTTPS validation is unchanged.
Validation:
- Built x64 and ARM64 public-pilot installers; administrative extraction and per-file payload
  verification passed for both. Both compiled client assemblies report 0.9.67.0.
- Published both MSIs and matching SHA256SUMS.txt to artifacts/peeronq/public/downloads; restarted
  the owned local website. Live metadata selects 0.9.67 unsigned-public-pilot. Both complete HTTP
  GETs returned 200 and matched local/manifest hashes despite the unavailable telemetry host.
- x64: 78602240 bytes; SHA256 f361e0efb727ae79c645460b8137d840778169b5301012a8e38dab69eeef88f7.
- ARM64: 74272768 bytes; SHA256 ae4b38e6cc6279ce68779f65a1591008a76ffff7a137461f6ec19dbe2fc6e4e2.
- New early-failure tests reproduced both failures before the fix. Node 24 public typecheck,
  lint, all 86 tests and production build passed. Native UI invariant and 106 Infrastructure tests
  passed. Fallow, repository secret scan and git diff --check passed.
Risk:
- Unsigned controlled-test packages only; manual MSI installation is needed to replace 0.9.66.
  No physical-device upgrade, ARM64 execution, two-device latency/4K or public production delivery
  was tested. Linux/Android/Apple packages remain unpublished; Verified Updates remains unconfigured.
Rollback:
- Revert this source commit; restore the previous validated pair and its SHA256SUMS, then restart
  the local website. Reverting Git alone does not downgrade installed clients or package files.

## 2026-09-27 - Remove eight Fallow dead-code findings

Task:
- Fix the editor's seven unreachable files and one unused export on main 51b911c.
Files changed:
- Removed retired public About/Downloads/Features/Help/Security page files, the old About implementation
  and PlatformCard; trimmed unused PublicMarketing helpers; localized the telemetry plugin factory;
  updated LandingPage FAQ, native UI disclosure validation, PROJECT_MAP and DESIGN_SYSTEM.
Reason:
- Marketing URLs already redirect to landing anchors. Their old implementations were unreachable;
  the telemetry factory is called only inside its own module. No Fallow suppressions were added.
- Preserved the retired About license/activation disclosure in the active FAQ and pointed the existing
  invariant at that live source. Active routes, download selection and telemetry behavior are unchanged.
Validation:
- VS Code's installed Fallow 3.30.0, uncached analysis: 8 findings before, 0 after. The older global
  2.88.3 CLI cannot read the existing config, so validation used the editor's matching binary.
- Node 24 public typecheck, lint, 84 tests and production build passed. Native UI invariant,
  repository secret scan and git diff --check passed. No new dependencies or analyzer exclusions.
Risk:
- Low-risk source cleanup; interactive browser and production deployment were not exercised.
Rollback:
- Revert this commit; no data, API, native-client or route migration is involved.

## 2026-09-27 - Align native, public and customer portal product language

Task:
- Align existing product surfaces on origin/main b7f3a86 without redesigning session behavior.
Files changed:
- Native MainWindow, permission/viewer labels, App metadata and CloudEndpointConfiguration;
  public preview/version/navigation helpers and landing copy; portal support/download guidance;
  installer version default, endpoint/UI/frontend regression tests, brand compatibility manifest,
  PROJECT_MAP, DESIGN_SYSTEM and this entry. No routes or API contracts changed.
Reason:
- Added optional Settings/About Open Account Portal navigation through the default system browser.
  Existing compiled metadata resolves the official HTTPS portal, rejects credentials/query/fragment,
  and permits HTTP only for explicit loopback Development metadata in Debug builds. Device enrollment
  remains separate from customer identity; LAN startup and native protocols/consent remain unchanged.
- Aligned View Only, Full Control, File Transfer, Remote Device ID and Verified Updates labels.
  Retained canonical logos, typography, theme resources and platform controls. Portal support now
  points to native Diagnostics/Verified Updates; MIT source and third-party licenses stay distinct.
- Removed stale native/preview version literals, including old locally stored preview device display.
  Native labels remain assembly-derived; download metadata is unchanged. WiX now defaults to the
  canonical client version and rejects an override mismatch before PrepareForBuild.
- Brand validation exposed historical original paths in the unchanged .gitleaksignore. Classified
  that exact file as historical compatibility evidence; no secret-scan suppression/rule was changed.
Validation:
- Windows x64 Debug and Release builds: passed, zero warnings/errors. Native UI invariant passed.
- Infrastructure tests: 106 passed (including 23 endpoint cases); installer property evaluation matched
  the canonical version and a deliberate mismatch was rejected before preparation.
- Node 24: public typecheck/lint/build and 84 tests passed; portal typecheck/build and 47 tests passed.
  Initial new-test selector/type errors and the temporarily removed GitHub link were corrected.
- Brand purity, repository secret scan and git diff --check passed. Browser enumeration returned no
  available browser, so no interactive browser or physical native/default-browser/session test ran.
Risk:
- Source/build validation only; no production deployment, DNS/TLS reachability, MSI publication,
  physical-device session, ARM64 runtime, 4K or latency acceptance is claimed. Client version unchanged.
Rollback:
- Revert this commit and rebuild the affected surfaces; no data or schema migration is involved.

## 2026-09-27 - Customer portal account UX and scoped organization forms

Task:
- Refine the real customer portal on origin/main 483af37 without changing API schemas or backend security.
Files changed:
- Portal App/auth/brand/pages/portalShell/workspacePages/uiStates/styles, new theme provider,
  auth/workspace/setup tests and new portalUx tests; PROJECT_MAP, ROUTES_MAP, DESIGN_SYSTEM and this entry.
Reason:
- Canonical logos and Overview already existed. Added independently loaded sign-in session/trust
  counts, verification/MFA status, scoped workspace summaries and a canonical /profile route with
  /account compatibility. Renamed Sign-in sessions, Trusted sign-in devices and Managed devices.
- Organization routes gate loading/error/no-selection states and remount forms on tenant switches.
  Policy labels/descriptions retain server values and field names; manager-only edits/invitations
  follow existing server roles. Added confirmation/error feedback for revocations and deletion,
  pending form feedback, branded reset/verification screens, shared auth theme and mobile focus/inert handling.
- Failed logout now reports failure instead of falsely clearing the signed-in UI. Same-origin
  credentials/CSRF, cookie design, rotation, MFA and customer/Admin identity boundaries are preserved.
  No JWT/credential persistence, new endpoints, dependency changes or client/package changes.
Validation:
- Node 24.21.0: portal typecheck, production build and all 45 tests across 6 files passed
  (auth 11, workspace 8, product UX 17, absent commercial routes 5, async states 3, API/CSRF 1).
- Canonical logo bytes match public assets; regression coverage includes partial failures, policy
  defaults/rejection/switching, profile alias, MFA, registration/invitation/reset/verification,
  logout failure/retry, confirmation, storage restrictions and operator-link isolation.
- git diff --check and repository secret scan passed (1,111 files). No shared runtime assets or
  configuration changed; public/native/backend builds were outside this frontend-only validation.
- Browser runtime returned no available browser. Dialog tests model native open/close in jsdom;
  actual modal containment and desktop/mobile light/dark visual review remain unverified.
Risk:
- Source/build tests do not certify production readiness. Live cookie/mail/MFA/tenant acceptance and
  public TLS/reachability were not executed; no production deployment was performed.
Rollback:
- Revert this portal UX commit. Backend routes, schemas, identity and policy defaults are unchanged.

## 2026-09-27 - Public website product narrative and primary download flow

Task:
- Refine the public product presentation on current main while preserving real customer Portal access.
Files changed:
- PublicLayout, LandingPage, DownloadsPage, Sidebar, routeSeparation tests; PROJECT_MAP, ROUTES_MAP,
  DESIGN_SYSTEM, FRONTEND_ARCHITECTURE and this record.
Reason:
- Current main already had real Portal/Sign in links and accurate native source-preview status.
  Bring the existing device-selected download into the hero beside Open Portal; add header/mobile
  download anchors, numbered access-mode rows, connection/recovery/diagnostics/update explanations,
  architecture/self-hosting links and clearer platform release status. Replace stale v0.5.1 with
  Offline UI preview. Preserve canonical release selection, classifications and unavailable states.
- Public navigation retains HTTPS-only portal links and no operator surfaces. Customer auth, cookies,
  API routes, native client behavior/version, publication and deployment configuration are unchanged.
Validation:
- Node 24.21.0: public workspace typecheck, lint, all 80 tests across 11 files, and production build passed.
- Targeted route/offline tests passed (54); keyboard menu traversal/Escape/focus, platform truth,
  single hero package action and preview label are covered. Existing URL/release/offline tests remain.
- Repository secret scan passed for 1,109 files; git diff --check passed.
- Browser runtime exposed no browser; no desktop/mobile screenshot or visual-browser pass is claimed.
Risk:
- Responsive and dark/light visual review remains outstanding; no production deployment or external
  DNS/TLS/reachability verification was performed. This is a website-only change, not a client release.
Rollback:
- Revert this website commit; release resolver, customer services and operator ingress are unchanged.

## 2026-09-27 - Public customer portal ingress and operator boundary validation

Task:
- Reconcile production customer Portal exposure with existing authentication and verify ingress isolation.
Files changed:
- Nginx default/template and validator; bootstrap help, env example, Phase 7 harness default;
  deployment/customer-security tests; DEPLOYMENT, PROJECT_MAP and this record. No API routes changed.
Reason:
- Main cdc8742 already removed the portal CIDR restriction, but deployment guidance, bootstrap help
  and the CIDR map still classified Portal as an operator-only host.
- Added missing HTTPS default-host rejection and blocked public metrics proxying through the existing
  API/Presence/Downloads catch-all routes. Website/Portal also reject metrics paths. Internal scrapes,
  three operator CIDR rules, Prometheus GET/HEAD restriction and all customer auth behavior remain.
- Documented public Portal DNS/TLS/operator acceptance; bootstrap/import/renewal already include Portal.
  The local Phase 7 harness now uses a named HTTPS origin compatible with the strict SNI boundary.
Validation:
- DeploymentConfigurationTests 18/18 and CustomerPortalSecurityTests 12/12 passed in Release.
  The new ingress regression first failed on the missing HTTPS default server before the fix.
- Pinned Nginx 1.28.3-alpine syntax and runtime fixture checks passed: trusted certificate verification,
  public apex/www/Portal, same-origin API path/method/Host forwarding and 401 propagation, security
  headers, forbidden operator access/spoofed X-Forwarded-For, read-only Prometheus, public metrics
  denial and unknown HTTP Host/TLS SNI rejection. No certificate-validation bypass remains in this test.
- Production Compose merge validated with disposable example inputs: only HTTPS/TURN public ports,
  internal data/telemetry networks, correct portal origin and preserved registration mode. Missing
  Portal host or Admin CIDR rejected. Bootstrap/production Compose contract (including TLS) passed.
- Phase 7 PowerShell syntax, git diff check and repository secret scan passed.
Risk:
- Local fixtures do not prove Internet DNS/TLS/reachability or real production account flows. No public
  DNS, live deployment, registration policy, credentials or installed packages changed. External
  operator rollout/acceptance remains required; publish portal.peeronq.com to the public TLS ingress.
Rollback:
- Revert this commit and redeploy the previous verified configuration; no schema/data rollback.
  The preceding commit already exposes Portal; reverting this change does not privatize it.

## 2026-09-27 - Unify public website and customer portal presentation

Task:
- Redesign the public website and real customer portal around native PeerOnQ terminology,
  MIT/open-source positioning and honest package/security capabilities.
Files changed:
- Public landing/layout/downloads, scoped tokens, portal URL guard, metadata and route tests;
  portal shell/auth presentation/styles/routes, workspace pages, brand assets and component tests;
  Nginx portal ingress and validation; project/route/design/architecture maps and this record.
Reason:
- Public navigation hid the real portal; its profile-first UX omitted organization remote history.
  Portal ingress incorrectly shared the operator CIDR allowlist.
- Added Hero, native access modes, portal entry, security, MIT source, downloads and FAQ. Download
  CTAs lead to one platform-matched package action; known unsigned releases are clearly labeled.
- Portal now opens Overview and includes Account, remote Sessions, Downloads and Support. Existing
  browser-session revocation remains separate. Organization loads expose failures; new workspace
  requests discard stale tenant responses. Mobile focus trapping ends when the viewport widens.
- Customer ingress is public; Admin/Grafana/Prometheus retain network restrictions. Customer
  cookies, CSRF, tenant authorization, device proof and the network-free public prototype remain.
Validation:
- Node 24: workspace lint and build (including whole-workspace typecheck) passed. Workspace tests
  passed; after review corrections, affected public/portal suites passed again: public 78/78,
  portal 21/21, unchanged Admin 36/36; 135 current frontend tests across 20 files.
- DeploymentConfigurationTests: 16/16 passed. Disposable pinned Nginx validator passed syntax and
  real HTTP checks: portal 200; Admin/Grafana/Prometheus 403; existing download routing checks pass.
- Local public/portal SPA routes and portal brand assets returned HTTP 200. These HTTP checks and
  mocked-API component tests do not constitute a live production login or visual acceptance test.
- Repository secret scan passed after a new test-only password was named explicitly as an example.
  Gitleaks staged-diff scan found no leaks. Independent portal review findings were fixed;
  git diff check passed.
Risk:
- Browser runtime had no connected browser; isolated headless Edge was rejected by automatic
  approval review (blocked by policy). Visual desktop/mobile acceptance remains unverified.
- Existing desktop installation identity and portal accounts share organization device records,
  but no native customer login/self-service device-linking flow exists; the UI states this limit.
- No production deployment, account registration policy, client version, MSI or release gates changed.
Rollback:
- Revert this website/portal/ingress commit; no database migration or package rollback is needed.

## 2026-09-27 - Reproduce input latency, reconcile source truth and enforce Node 24

Task:
- Investigate the unchanged 35 ms input gate; reconcile current documentation, Node tooling and
  exact dependency license/security evidence without claiming production approval.
Files changed:
- Media loopback test and new test-only InputLatencyProbe; Application test friend assembly;
  package.json, pnpm catalog/lock and .nvmrc; PROJECT_MAP, README, phase/current-state/capability/
  protocol/performance documents; dependency notices/audit and this record.
Reason:
- Release reproduced the original fact failure (35.8 ms p95). Boundary probes locate the excess
  after send admission, with dedicated/bulk baseline 38.328 ms p95 versus 38.183 ms in transport/
  delivery. Original decoder isolation also fails. The sink is fake, not Windows SendInput.
- A supported balanced Windows timer request initially passed, but its session-scoped candidate
  failed repeated checks; the candidate and temporary tests/traces were removed. No production
  transport fix or fork was adopted; thresholds, security and compatibility remain unchanged.
- Added the primary-input fallback acceptance case and honest ack counts/overlap reporting.
- Current docs now distinguish canonical 0.9.66, signaling v3 and native viewer/controller source
  from dated historical test/package evidence. No installer/version/publication was changed.
- No workspace needs Node 25 APIs; Node 24.x is enforced and exact reviewed @types/node 24.19.0
  replaces 25.x through normal pnpm resolution. minimumReleaseAge remains 1440 minutes.
- Exact package/native hashes and licenses, WiX applicability, libvpx provenance and current
  SIPSorcery advisories remain evidence-bound release blockers; legal approval is external.
Validation:
- dotnet restore PeerOnQ.slnx: passed. Strict Release solution build with ContinuousIntegrationBuild,
  EnableNETAnalyzers, AnalysisLevel=latest and -warnaserror: passed, 0 warnings/errors.
- dotnet test PeerOnQ.slnx --no-restore -c Release --nologo (detailed console logger):
  846 passed / 1 failed / 5 skipped across 18 assemblies. Media: 138/1/1; Application: 160/0/0.
  Dedicated bulk input p50/p95/p99: 33.264/35.057/39.792 ms, n=20; unchanged gate FAILED.
  Final fallback bulk: 31.077/31.671/32.285 ms, n=20. Full before/candidate/final matrices and
  stage timings are in docs/PERFORMANCE_REPORT.md. No discarded experiment counts enter totals.
- Three isolated-Redis tests and Phase 3 controller-owned Docker-restart/TURN tests stayed skipped;
  required test endpoints/credentials were not supplied. No physical-device test was executed.
- Client-version invariant and Phase 5 native UI scripts passed. Secret scan: 1103 files passed.
- With verified temporary Node 24.21.0/pnpm 10.33.0: pnpm install --lockfile-only and
  --frozen-lockfile, root typecheck/lint/test/build passed (PORT=23586, BASE_PATH=/ for build).
  Frontend: 122 passed / 0 failed / 0 skipped in 19 files. Host default Node 25 remains unchanged.
- pnpm audit --json: FAILED with 9 existing findings (5 high, 4 moderate), recorded in DEPENDENCIES.
  No unrelated unreviewed upgrades were made. Git whitespace/diff review passed.
Risk:
- Input acceptance, real Windows injection/physical-session/4K claims, dependency advisories,
  native provenance and release/legal/signing gates remain open. Use Node 24 in fresh shells.
Rollback:
- Revert this source/documentation commit normally; reinstall with the restored pnpm lockfile.

## 2026-09-27 - Open every development interface from the Windows launcher

Task:
- Make peeronq-start.bat open the website, Admin and all existing development interfaces.
Files changed:
- Start BAT; workspace, Phase 6 and preview controllers; deployment regression checks;
  README, PROJECT_MAP and this record.
Reason:
- The workspace already opened five interfaces after startup, but omitted the desktop UI preview.
  It now opens six distinct pages and waits for Prometheus readiness alongside the other services.
- Workspace/preview URLs now accept the same quoted custom ports as Phase 6 and reject invalid
  values instead of opening an unrelated default port. Startup errors remain visible on double-click.
- The preview runs hidden with stdout/stderr under ignored .peeronq-run; its tracked launcher exits
  if pnpm exits, allowing the existing readiness/ownership guard to report failure promptly.
Validation:
- PowerShell syntax checks and all 16 deployment-configuration tests passed.
- Both port readers passed 18 isolated cases covering defaults, quoted/plain ports and invalid
  values. Browser failure continuation was reviewed statically.
- Actual peeronq-start.bat completed with exit code 0 and launched all six browser URLs after
  readiness checks. All six returned HTTP 200 with normal redirects followed and TLS verification
  enabled. The tracked preview remained healthy after the launcher exited.
- Live testing caught an inherited-pipe stall in an initial logging implementation; moving log
  redirection inside the detached cmd process fixed it, and the complete launcher was rerun successfully.
- Working-tree secret scanning and git diff whitespace checks passed. No application version or
  installer was changed.
Risk:
- Full startup retains the existing Docker rebuild, migration and required local configuration
  behavior; browser pages open only after successful service startup. Admin authentication remains required.
Rollback:
- Revert this task's launcher/controller, regression-check and documentation changes together;
  local databases, certificates, credentials and client packages are unchanged by the source patch.

## 2026-09-27 - Publish the open-source repository on GitHub

Task:
- Publish PeerOnQ under the user's GitHub account and keep subsequent commits synchronized.
Files changed:
- .gitignore, .gitleaksignore, AGENTS.md, README.md, PROJECT_MAP.md and this record; generated packages, historical
  test reports and attached local input are removed from the public source index, retained on disk.
Reason:
- No GitHub origin was configured. Existing local history includes generated Linux packages and
  test outputs; a new source-only main preserves the local history without publishing those files.
- Record the public pasha555/PeerOnQ MIT repository and the user's standing commit/push instruction.
Validation:
- GitHub account ownership was verified through existing credential-manager authentication.
- The working-tree secret scan passed for 1,101 source files. Four Gitleaks source matches were
  independently compared with existing reviewed false positives: UI metric metadata, a class
  declaration, an empty example setting and a loopback-only TURN fixture. Only their exact public
  root-commit fingerprints are excluded; new findings remain blocking.
- Original local main was preserved as local-history/main-before-publication-20260927; the feature
  branch also retains its history. 538 generated package files, 31 test reports and one attached
  local input were removed from the public index without deleting working files.
- Gitleaks 8.30.1 scanned the complete public main history with no remaining findings; the scanner
  download was verified against its official release checksum. Staged whitespace validation passed.
- Published https://github.com/pasha555/PeerOnQ. Anonymous GitHub API verified public visibility,
  main as default branch and MIT license detection. git ls-remote confirmed the first pushed source
  head 7f3cc3e matched the local commit; main now tracks origin/main and origin is the default push remote.
- GitHub Quality and CodeQL checks started on push and were still pending/running at publication;
  their completion is not claimed. The publication record is committed and pushed afterward.
- No application behavior changes in this publication step; the preceding development change's
  known 35 ms latency gate failure remains documented and is not waived.
Risk:
- Public source can be cloned and forked. Publishing source is not a signed installer release or
  production deployment; no automatic deployment runs on a main-branch push.
Rollback:
- Preserve the local historical branch and working files; revert publication documentation or
  disconnect origin locally. Removing a public repository cannot recall existing copies.

## 2026-09-27 - Repair heartbeat starvation and development 4K latency

Task:
- Investigate blurred/disconnected 0.9.66 sessions and improve the development client's 4K path.
Files changed:
- Native startup/UI guard; signaling heartbeat/refresh and regression tests; shared connection
  policy; media adaptation, RTCP feedback, desktop VP8 decoder and tests; PROJECT_MAP/PHASE3; this record.
Reason:
- Slow cloud attestation was awaited inside the heartbeat loop, allowing an otherwise live socket
  to disappear from presence. Refresh now runs alongside heartbeats, bounded by the original token
  expiry; rejection, disposal and replacement remain fail closed.
- Stale RTCP loss/jitter could repeatedly degrade quality; reports now expire after ten seconds
  and reset on path changes. Missing feedback cannot trigger recovery or a bulk-capacity increase.
  Video latency decisions allow half the fresh path-minimum RTT, capped at 75 ms, while bulk still
  reacts to full input latency. Rising RTT does not raise that allowance.
- LAN development now explicitly requests High Quality/4K (30 fps target, 36 Mbps ceiling, no
  upscaling). Desktop VP8 color conversion uses bounded parallel workers for full-HD/4K frames,
  retaining decoder state and identical BGR pixels. Production retains Automatic as its default.
Validation:
- Application: 160 passed. Signaling: 125 passed, four opt-in Redis/Docker tests skipped. Focused
  media: 64 passed. Full media: 137 passed, one failed, one live-TURN test skipped (139 total).
- Unchanged 35 ms input-injection gate still fails: focused runs measured about 37-39 ms p95,
  with send admission about 0.2 ms. Release and original-decoder isolation also failed the gate.
  Pinned SIPSorcery 10.0.15 polls DTLS receive with Thread.Sleep(25); official 10.0.16/current
  upstream retain the same code and expose no supported receive-transport replacement hook.
- Isolated synthetic same-process 4K loopback capture-to-present p95 improved from approximately
  653-1076 ms before decoder optimization to 61.5 ms afterward (22.2 ms clock uncertainty).
  Render throughput in that short run was 15.5 fps; this does not establish sustained 4K/30 fps,
  physical display latency, WAN performance or parity with AnyDesk/TeamViewer.
- Final LAN-development Debug build passed with zero warnings/errors; native UI guard passed.
  Scoped whitespace verification and git diff checks passed.
- Defender initially quarantined the media test assembly; after the user reported disabling it,
  the tests above ran. No antivirus exclusions or security settings were changed by this task.
Risk:
- Installed logs show cloud_unavailable and target_offline during the reported reconnect failure;
  the reproduced heartbeat defect is a plausible contributor, not a proven complete production
  incident attribution. Two physical endpoints and live TURN remain unverified.
- The 35 ms input gate remains open and requires a reviewed upstream transport correction;
  no dependency/security change, version bump, installer publication or production deployment occurred.
Rollback:
- Revert this task's native startup, policy, media and heartbeat changes together with their tests
  and documentation; remove the added decoder/RTCP helpers and new regression test files.

## 2026-08-27 - Select the published app for the visiting device

Task:
- Make the single public download action offer the release for the platform used to open the page.
Files changed:
- Public download resolver/tests; frontend and deployment release URL configuration; website-patch
  neutrality guard; repository maps and documentation; this record.
Reason:
- Device detection already recognized Apple and mobile browsers, but release resolution returned a
  package only for Windows and unknown clients incorrectly fell back to Windows x64.
Validation:
- PeerOnQ frontend typecheck, lint and production build passed; all 75 frontend tests passed,
  including 47 route/download tests for Windows, macOS, iPhone, iPad, Android, Linux and unknown
  devices. The website-patch client trust-boundary invariant also passed.
- The local preview returned HTTP 200 and the development Compose file passed structural config
  validation without interpolation. In-app visual verification could not run because no browser
  instance was connected; full Compose interpolation stopped at the pre-existing required-secret/path
  gate on the empty `PEERONQ_BACKUP_DIR`. No deployment was started.
Risk:
- Non-Windows links are build-time HTTPS destinations and remain empty by default. Apple, Linux and
  Android previews are still unpublished until their existing signing, physical-device and release
  gates pass; this change does not turn preview artifacts into public releases.
Rollback:
- Restore the Windows-only resolver/tests and remove the five non-Windows URL inputs from the
  Docker/development configuration, documentation and website-patch environment guard.

## 2026-08-26 - Add registered-iPhone development signing

Task:
- Allow the Apple viewer to be development-signed for one explicitly registered personal iPhone
  without weakening or replacing the existing iOS distribution-signing path.
Files changed:
- Apple build and invariant scripts; Apple signing/security/capability repository documentation;
  this record.
Reason:
- A public/App Store package cannot be produced on the Windows workstation, but a Mac with Xcode and
  a Personal Team can create a short-lived development package for a registered physical device.
Validation:
- Git Bash syntax, the non-macOS fail-closed host gate and the Apple version/build-contract check
  passed at canonical 0.9.66/bundle code 9066; Apple platform tests passed 14/14.
- A real signed IPA was not produced: it still requires macOS, Xcode, the .NET Apple workload,
  reviewed arm64 libvpx and local Keychain/profile material.
Risk:
- Development provisioning is device-bound and short-lived. The resulting IPA is explicitly barred
  from website/App Store publication and does not satisfy physical-device or production release gates.
Rollback:
- Remove the `ios-development` target and its profile/device verification, restore the previous
  Apple script contract checks and documentation, and remove this entry.

## 2026-08-26 - Add Apple attended viewer source previews

Task:
- Add one native Apple viewer/controller codebase for macOS (Mac Catalyst), iPhone and iPad while
  preserving PeerOnQ consent, least-capability and canonical-version boundaries.
Files changed:
- Canonical Apple version metadata; Apple platform/app/test projects; Keychain/local stores; UIKit
  lifecycle, frame rendering and input UI; macOS-only signed build/version gates; architecture,
  security, capability, design-system, storage and repository maps; this record.
Reason:
- PeerOnQ had no Apple client. The existing VP8 transport also cannot be passed to an undocumented
  Apple VideoToolbox path, so Apple builds now require the already-used libvpx dependency as a
  reviewed architecture-matching static archive and fail closed when it is absent.
Validation:
- Apple platform tests passed 14/14 and the canonical version/build-contract gate passed at 0.9.66
  with bundle code 9066. The platform project and tests built warning-free on Windows.
- Native Apple app compilation was attempted and stopped at `NETSDK1147`: this workstation has only
  the Android workload, not iOS/Mac Catalyst, Xcode, static Apple libvpx or signing inputs. No `.app`,
  `.ipa`, physical-device, Developer ID, notarization or App Store claim is made.
Risk:
- UIKit/SecItem/static-link integration remains uncompiled until run on a real Mac with the matching
  .NET 10 Apple workload/Xcode. Direct/TURN interop, lifecycle, VoiceOver/Dynamic Type/rotation,
  ten-minute sessions, code signing and notarization/store review remain open release gates. The
  public website continues to expose no Apple download.
Rollback:
- Remove the Apple app/platform/test projects and Apple scripts/docs/storage entries, then remove the
  derived Apple version properties and solution registrations; Windows, Linux and Android behavior
  is otherwise unchanged.

## 2026-08-26 - Fix Android installation on BlueStacks Nougat 32

Task:
- Make the Android preview install and launch on the user's BlueStacks 5 Nougat 32 instance without
  weakening the existing 64-bit Android package.
Files changed:
- Android app minimum API/RID selection and API guard; Android package/version gates; packaging,
  repository and capability docs; this record; ignored domain-bound BlueStacks x86 APK/checksum/
  metadata under `app-updates/android/`.
Reason:
- BlueStacks reported Android 7.1.1/API 25 with only 32-bit x86/armeabi-v7a, while the first APK
  required API 26 and contained only arm64-v8a/x86_64. Its own log confirmed
  `INSTALL_FAILED_NO_MATCHING_ABIS`.
Validation:
- The dedicated x86 Release build and the unchanged default arm64/x86_64 Release build completed
  with analyzers/warnings-as-errors at 0 warnings/errors; Android platform tests passed 10/10 and
  the version/compatibility invariant passed for API 25 and both RID modes.
- The domain-bound x86 APK exposes package `io.peeronq.android`, version 0.9.66/code 9066, min API
  25, target API 36, x86-only native payload, INTERNET-only permission and no desktop VPX DLL. Its
  SHA-256 is `e32eab99bb91c76f289dd204b7b53ee17ebd316da28346c89fe6057737a9a97b`.
- BlueStacks installed the replacement with `status = 0`, registered `PeerOnQ` 0.9.66, launched
  `MainActivity`, reported it displayed and opened the application socket without a crash callback.
Risk:
- BlueStacks compatibility is development-only; production should use a supported arm64 Android
  release. The APK remains development-signed, and `signal.peeronq.com` DNS/TLS must be deployed
  before a live domain session can connect.
Rollback:
- Restore Android minimum API 26/default RID metadata and the unconditional API-26 autofill calls,
  remove the BlueStacks builder mode/compatibility docs, and delete the ignored x86 artifacts.

## 2026-08-26 - Build the domain-bound Android 0.9.66 preview APK

Task:
- Produce an installable Android APK bound to the canonical PeerOnQ signaling domain.
Files changed:
- This record; ignored APK, SHA-256 sidecar and truth metadata under `app-updates/android/`.
Reason:
- The earlier validation package used a loopback-only endpoint and was deliberately removed; a
  user-installable artifact must carry a stable domain endpoint instead of a machine-local URL.
Validation:
- Release build completed with 0 warnings/errors. The APK passed Android v2/v3 signature,
  checksum, package `io.peeronq.android`, version 0.9.66/code 9066, API 26/36,
  INTERNET-only permission, ARM64+x86_64 and no-desktop-vpx-payload checks.
- Assembly metadata and package truth metadata both identify `wss://signal.peeronq.com/ws`; APK
  SHA-256 is `bbbc0b2c3caee4ad3c31489951c7401f2c3fe87fb461b3e5519ddc3e10f70293`.
- The available OnePlus5 emulator could not complete ADB transfer: streamed installation closed
  the transport and non-streaming installation returned an ADB file-sync protocol fault before
  Android parsed the APK. Emulator launch is therefore not claimed as passed.
Risk:
- This is development-signed and not a store release. `signal.peeronq.com` did not resolve from the
  build workstation, so live connection remains blocked until its DNS A/AAAA record, TCP 443 and
  publicly trusted TLS certificate are deployed and physical-device interop is retested.
Rollback:
- Delete the three ignored `peeronq-0.9.66-android-development-signed-preview` artifacts from
  `app-updates/android/` and remove this record; source behavior is unchanged.

## 2026-08-26 - Add the native Android attended viewer/controller

Task:
- Add an Android client that can initiate attended view-only or explicitly approved control
  sessions while reusing the existing PeerOnQ identity, signaling, transport and media contracts.
Files changed:
- Canonical version/solution metadata; Android app, platform abstraction and tests; platform video
  sink support in Media; Android package/version validators; CI version gate; architecture,
  security, capability, design-system, packaging and repository maps; this record.
Reason:
- PeerOnQ had Windows and Linux clients but no Android surface. Reusing the desktop decoder or
  advertising host/file/clipboard/unattended capabilities would have been incorrect and unsafe on
  Android, so the new client is least-privilege and viewer/controller-only.
Validation:
- Android platform tests passed 10/10; Media tests passed 113/113 with one expected live-TURN
  environment skip; the full .NET solution and the Android Release target built with analyzers and
  warnings-as-errors at 0 warnings/errors.
- The Android version invariant passed at 0.9.66 (versionCode 9066). A development-signed validation
  APK passed SHA-256, Android v2/v3 signature, package/version, API 26/36, INTERNET-only permission,
  ARM64+x86_64 and no-desktop-vpx-payload checks, then was removed because its loopback signaling
  URL was test-only and unusable from a physical phone.
Risk:
- This is source-complete preview work, not a production/store release. Physical ARM64 launch,
  Keystore and MediaCodec behavior, TalkBack/dynamic-text/rotation, direct and TURN interop,
  ten-minute sessions, release signing and an Android-reachable signaling endpoint remain open.
Rollback:
- Remove the Android app/platform/test projects and Android scripts/docs entries, restore canonical
  metadata/solution/CI, and revert the optional platform video-sink integration in Media.

## 2026-08-26 - Bring the Linux viewer package to canonical client 0.9.66

Task:
- Deliver the existing native Linux viewer/controller as current-version x64 and ARM64 preview
  packages without inventing unsupported Linux host capabilities or publishing unverified binaries.
Files changed:
- Canonical build metadata; Linux app project, package builder and new version/package validators;
  quality workflow, engineering rules/project map, Linux packaging/capability docs and this record.
Reason:
- The real Avalonia Linux viewer already existed and passed its tests, but its project and builder
  independently hardcoded 0.9.21. Current source could therefore produce a stale-version Linux
  package even while every Windows surface used 0.9.66.
Validation:
- Canonical Linux version invariant passed at 0.9.66; both new shell validators passed syntax.
- Linux platform tests passed 8/8; Linux viewer tests passed 11/11; Release build completed with
  0 warnings/errors. The Windows canonical-version invariant and native app compatibility build
  remained green.
- Self-contained `linux-x64` and `linux-arm64` archives built and passed path-containment, manifest,
  entrypoint/README, per-file and archive SHA-256 validation. Archive hashes are recorded in
  `docs/CROSS_PLATFORM_CAPABILITIES.md`.
Risk:
- Viewer/controller only: Linux screen hosting, incoming sessions, unattended access, file transfer,
  clipboard, installer/update and signing remain unsupported. Physical Linux launch/keyring,
  Windows-host interop, accessibility and ten-minute-session gates remain open, so the artifacts are
  ignored unsigned previews and were not published to the website.
Rollback:
- Restore the Linux project's prior metadata/manifest and builder, remove the two Linux validators
  and their workflow/map/docs entries, and discard the ignored 0.9.66 preview archives.

## 2026-08-26 - Revalidate Phase 10 and 11 on client 0.9.66

Task:
- Confirm whether Phase 10 Smart Connection and Phase 11 support/multi-session/portable work still
  needed implementation, then validate the current source and record the current acceptance truth.
Files changed:
- Phase 10 and Phase 11 completion reports, world-class validation report and this record.
Reason:
- Both phases were already implemented, but their retained completion evidence still described the
  original 0.9.21 snapshot. Rewriting the stable paths would have duplicated working behavior and
  risked the user's confirmed fixes.
Validation:
- Application 160/160, Media 111/111 (+1 live-TURN environment skip), Signaling 120/120 (+4
  distributed/failover environment skips), Domain 64/64, Infrastructure 85/85, Transport 7/7 and
  End-to-End 49/49 passed; focused Phase 10 tests passed 40/40 and focused Phase 11 tests passed 5/5.
- Native App Debug build completed with 0 warnings/errors. Canonical 0.9.66 version and native UI
  contract checks passed. Unsigned 0.9.66 Portable Support x64/ARM64 packages built and passed
  manifest, per-file checksum, archive checksum and no-PeerOnQ-service-payload validation.
Risk:
- Documentation-only tracked change. The current result is a controlled single-node pilot pass, not
  a production/HA claim; physical/signing/soak/security gates and Phase 9 HA remain open.
Rollback:
- Remove the 2026-08-26 revalidation sections from the three reports and remove this entry; no
  runtime behavior or published package changes need rollback.

## 2026-08-26 - Restore the local Grafana entry path

Task:
- Make Grafana reachable from every Windows development entry point and prevent startup from
  reporting the stack ready while the canonical Grafana endpoint is unavailable.
Files changed:
- Development/staging Compose; Phase 6, workspace and preview controllers; deployment map/guide;
  Observability and preview regressions; this record.
Reason:
- Grafana enforced `grafana.dev.localhost` but its configured root URL omitted the development
  proxy's port. Direct `localhost:3000` links therefore redirected to unused HTTPS port 443 instead
  of the healthy proxy on 8443, while the Phase 6 controller checked only the direct health API.
Validation:
- The complete Phase 6 controller rebuild/start exited 0, synchronized the Grafana credential and
  passed the new canonical HTTPS Grafana health gate. The restarted preview retained the verified
  0.9.66 unsigned-public-pilot download selection.
- `http://localhost:3000/` now redirects to `https://grafana.dev.localhost:8443/`; the HTTPS health
  API returns 200 and the authenticated search API exposes the provisioned `PeerOnQ Operations`
  dashboard. The staging overlay retains its canonical HTTPS/443 root URL. Observability tests
  passed 30/30 and the preview route suite passed 40/40.
Risk:
- Low and development-scoped. The direct loopback port remains available for diagnostics, but all
  normal entry points now use the authenticated, CIDR-restricted TLS proxy host.
Rollback:
- Restore the prior Grafana root URL and controller links, remove the Grafana startup gate, then run
  `peeronq-phase6-dev.ps1 -Action start -NoBrowser` and restart the preview controller.

## 2026-08-26 - Unify Windows client version and publish website 0.9.66

Task:
- Prevent client builds, server embedding and local website downloads from selecting different
  Windows versions, and replace the stale local 0.9.57 website selection with the verified current
  0.9.66 x64/ARM64 public-pilot pair.
Files changed:
- Canonical build metadata; Windows release/publication/preview controllers and invariant test;
  Downloads link/telemetry regressions; engineering rules/maps/frontend contracts; ignored local
  0.9.66 website packages and checksum manifest.
Reason:
- Four build entry points still defaulted independently to 0.9.57. The preview chose the newest
  checksum pair only from `unsigned-development` filenames, while the newer 0.9.66 pair was correctly
  classified as `unsigned-public-pilot` and remained only under ignored release output. Updating the
  client therefore did not update or invalidate the website's older selection.
Validation:
- `PeerOnQWindowsClientVersion` now resolves to 0.9.66 in MSBuild. The canonical-version, server
  bundle and website-patch boundary tests passed; PowerShell parsing passed for every touched release
  controller.
- PeerOnQ web typecheck passed; all 68 web tests passed; production Vite build passed. The restarted
  preview reports `0.9.66 unsigned-public-pilot` and both versioned x64/ARM64 URLs return HTTP 200
  with lengths 78,577,664 and 74,252,288 bytes.
- Published SHA-256 remains x64
  `9fc63a6f800f60402d3617c269cd622360bac2da10dd7fcec8168739a85f94d4`; ARM64
  `c2216d37562f23f7890c4312044b67c25f9b84689c3168509bd7fe5944a0cfaf`.
Risk:
- The locally exposed 0.9.66 pair is an unsigned controlled public pilot, not a signed production
  release. Production activation and fresh-browser/two-device WAN acceptance remain operator gates.
Rollback:
- Restore `PeerOnQWindowsClientVersion` and the scoped release/preview changes, replace the ignored
  download pair and `SHA256SUMS.txt` with the prior verified set, then restart `peeronq-dev.ps1`.

## 2026-08-25 - Stop post-verification rollback in server 0.6.44

Task:
- Replace superseded server 0.6.42/0.6.43 after production proved that their embedded client was
  correct but activation rolled back to 0.6.28 during the final managed-TLS renewal refresh.
Files changed:
- Linux server installer and bootstrap/Compose regression; deployment map; this record; ignored
  `peeronq-server-0.6.44.run` release output.
Reason:
- A server with existing HTTP-01 renewal units and newly enabled Spaceship credentials entered the
  installer's existing-renewal branch. That branch attempted to atomically install the DNS hook
  below `/usr/local/libexec` without first creating and validating its parent directory, so the
  otherwise healthy deployment failed after web/client verification and rolled back automatically.
Validation:
- The regression first reproduced the missing-parent copy failure, then passed after the installer
  created the exact configured hook parent with root ownership and mode 0755 before atomic install.
- POSIX syntax, bootstrap/production Compose contract, semantic-version/containment, website-state,
  embedded-client, Spaceship hook, platform-agent, and PowerShell server-publication tests passed.
- `peeronq-server-0.6.44.run` checksum, header syntax, embedded metadata and extracted MSI invariant
  passed. Complete SHA-256:
  `90eec6ac5af0faeed3d8fd95ee133544ee9991c093f4aa0601e1e708dc2bcc2c`; payload SHA-256:
  `83f05bb4e3e65066959b8ebed35c9589747df926762f897bd6e89e4146836671`. It embeds client 0.9.66 x64
  SHA-256 `9fc63a6f800f60402d3617c269cd622360bac2da10dd7fcec8168739a85f94d4`.
Risk:
- This remains an unsigned controlled-pilot artifact; Admin production upgrade requires its enrolled
  detached GPG signature. Production activation and two-device WAN acceptance remain operator checks.
Rollback:
- If activation fails, the installer retains its bounded failure log and automatically restores the
  prior release. Do not redeploy superseded 0.6.42 or 0.6.43.

## 2026-08-25 - Prevent stale Windows downloads in client 0.9.66 and server 0.6.43

Task:
- Replace superseded server 0.6.42 after production could still return the previously cached 0.9.61
  MSI from the canonical same-name download URL.
Files changed:
- Public Downloads link and regression; production Nginx template/validation; Linux installer and
  bootstrap contract; deployment map; this record; ignored 0.9.66/0.6.43 release outputs.
Reason:
- Every embedded client used `/downloads/PeerOnQ-Windows-x64.msi`, while the edge explicitly allowed
  that response to remain cacheable. A browser or intermediate cache could therefore reuse 0.9.61
  after the server had embedded a newer byte sequence.
Validation:
- Embedded links now carry the numeric version query, and the Downloads page, MSI, checksum and
  release metadata return `Cache-Control: no-store, max-age=0`. The installer refuses activation
  unless the public version, streamed MSI SHA-256 and cache policy match the embedded release.
- Web typecheck/build passed; 66/66 web tests passed. Bootstrap/production contract and real container
  Nginx syntax/routing/header regression passed. x64/ARM64 MSI ICE03 and payload validation passed.
- Client 0.9.66 SHA-256: x64
  `9fc63a6f800f60402d3617c269cd622360bac2da10dd7fcec8168739a85f94d4`; ARM64
  `c2216d37562f23f7890c4312044b67c25f9b84689c3168509bd7fe5944a0cfaf`.
- `peeronq-server-0.6.43.run` complete SHA-256:
  `b011625e6a9ec47cc0f39497eca505da5233228d02e742db543b871b669de03d`; payload SHA-256:
  `fe943cce8e247b34fe6f9af55d1be898c2f58d7898a351fd5cc95b19e68116e5`. Exact embedded client,
  versioned link, no-store template and activation gates were independently extracted and checked.
Risk:
- These remain unsigned controlled-pilot artifacts; Admin production upgrade requires an enrolled
  detached GPG signature. Production activation and a fresh-browser download remain operator checks.
Rollback:
- Use the prior approved signed server bundle with `--rollback` and reinstall its approved client.
  Do not redeploy superseded 0.6.42 when testing this cache fix.

## 2026-08-25 - Publish WAN screen-stability client 0.9.65 and server 0.6.42

Task:
- Ship the pending WAN/TURN screen-sharing fixes in a new x64/ARM64 Windows client and embed the
  verified x64 client in a replacement Ubuntu server bundle so the production download page no
  longer serves 0.9.61.
Files changed:
- Desktop media defaults and TURN peer construction, their native UI/media regressions, this record,
  and ignored release outputs under `dist/public-pilot/0.9.65` and `dist/server`.
Reason:
- The deployed 0.9.61 client could start WAN screen sharing at 4K/36 Mbps, while Full Control's file
  permission could incorrectly remove TURN candidates from the shared screen/input peer. A skewed
  client clock could also discard fresh server-issued TURN credentials. The new client starts with
  the bounded adaptive profile, preserves TURN for mixed Full Control sessions, omits only the
  direct-only bulk peer in relay-only mode, keeps a newest-frame-only video queue, and retains
  receiver-stall PLI/keyframe recovery.
Validation:
- Native UI contract passed. Full Release Media suite: 111 passed, 1 live-TURN environment test
  skipped, 0 failed. Focused WAN/keyframe/TURN suite: 10/10 passed. Website typecheck and download
  source suite: 38/38 passed.
- x64 and ARM64 0.9.65 MSI builds passed WiX ICE03 and extracted-payload validation. SHA-256: x64
  `d182021109e589977684071659e112cd898a1da7316c48d711f5d32e9f5593ba`; ARM64
  `9145c2b03c6b3511496a09c5c8090fae42b38e52c0227320b31a17b8006d390b`.
- `peeronq-server-0.6.42.run` passed publication invariants, complete-file and embedded-payload
  checksums, exact embedded-MSI comparison, embedded version/release-type checks, and the Spaceship
  DNS hook User-Agent check. SHA-256:
  `a370d63aa93a09eee0d20f88476d70a45a1338bfc4f9e5c06182bafb6615b712`; embedded payload SHA-256:
  `03cb042b7dd7961bc298fd9f5fe5edb0cddd5eb9ad59c208dad3fe4cf0184740`.
Risk:
- The packages are unsigned controlled-pilot artifacts. Admin must reject the server `.run` without
  the enrolled offline GPG `.asc`; Windows may warn for the unsigned MSI. Real two-device WAN/TURN
  acceptance and production DNS/NAT remain operator checks.
Rollback:
- Reinstall the previously approved Windows client. For a successfully signed server upgrade, run
  the prior approved bundle with `--rollback`; installer transaction recovery remains unchanged.

## 2026-08-25 - Stabilize WAN media and repair internal TLS rollout in server 0.6.35

Task:
- Stop cross-network Full Control video from defaulting to an unsustainable 4K/36 Mbps profile,
  repair TLS SAN issuance/renewal for the internal operations hosts, support a rollback-safe one-time
  certificate import on an existing server, and publish one replacement controlled-pilot bundle with
  Windows client 0.9.64 embedded. Superseded 0.6.34 must not be deployed.
Files changed:
- Desktop quality/resolution defaults and native UI contract; production bootstrap, server installer,
  TLS renewal and bootstrap/Compose contract; deployment/project maps and operator procedure; this
  record.
Reason:
- Startup applied `High quality + 4K` even on WAN/TURN paths, whose lowest adaptive rung remained
  4K. The installed certificate omitted Portal/Grafana/Prometheus, standalone Certbot could collide
  with the running proxy on TCP 80, and an internal-only split-DNS conversion had no safe in-place
  certificate path. OpenSSL's `x509` hostname reporter can also return success for a mismatch, so all
  bootstrap/import/renewal hostname gates now use reliable `verify -verify_hostname` semantics.
  Admin's separate 403 remains the intentional source-CIDR gate and requires the protected environment
  value to match the direct management-client network.
Validation:
- Native UI contract and Release application build passed with zero warnings/errors. POSIX syntax,
  custom-environment TLS binding, failure-safe proxy handoff, renewal refresh/ordering, certificate
  transaction rollback, and the full bootstrap/production Compose contract passed. Real OpenSSL
  validation covered apex+wildcard and multi-certificate fullchains, missing SANs and mismatched keys.
  Server publication invariants, website-state rollback and scoped diff checks passed.
- Matching unsigned x64/ARM64 0.9.64 MSIs passed WiX/payload validation. SHA-256: x64
  `7c0b3c82286a8a533b7e7f678cc40ee3f3c2e35b0fc248970eaf58e079855dec`; ARM64
  `327b7a417c6961b992be275864263e99b11cbe551621b103e94c5b3e095ebc9b`.
- `peeronq-server-0.6.35.run` passed bundle/payload checksum, archive traversal, embedded-script,
  safe-media-default, version/release-type and exact embedded-MSI stream checks. SHA-256:
  `c6726464b0c90e1b92f959d6e639af420ca18f886797f58d60edd55b44ae2b5c`; embedded payload SHA-256
  `d2d374546aec889e7a87d12da93187b267057b9264c7793ef98f0f95935744c8`.
Risk:
- These are unsigned controlled-pilot artifacts; no detached `.asc` exists, so Admin must reject the
  server bundle until it is signed with the enrolled offline platform key. Production DNS/NAT and
  two-device WAN acceptance remain operator checks. HTTP-01 cannot validate names whose public A records
  point to RFC1918 addresses; the one-time root import requires a browser-trusted DNS-01 SAN/wildcard
  pair and never carries its private key through Admin. Do not broaden the internal Nginx CIDR to
  `0.0.0.0/0`.
Rollback:
- A failed upgrade restores the prior application, TLS pair, renewal state, proxy/TURN and website
  overlay automatically. After a successful signed upgrade, run the 0.6.35 bundle with `--rollback`;
  reinstall the prior approved Windows client only if the new Automatic defaults must also be rolled
  back.

## 2026-08-25 - Repair in-place server upgrade and publish 0.6.26 pilot bundle

Task:
- Fix the production upgrade failing at dependency reconciliation, retain existing data, include the
  complete Portal application, and create a newly validated Ubuntu `.run` bundle.
Files changed:
- Linux server installer and bootstrap/Compose contract regression; Windows server-bundle builder
  and invariant; deployment/project maps; this record. Desktop remote-control and transfer code was
  not changed.
Reason:
- 0.6.21 tried to replace a changed telemetry network while old application endpoints were still
  attached, so Docker failed before migrations and recovery restarted 0.6.6. Later full validation
  also exposed two independent gates: the builder omitted `artifacts/peeronq-portal`, and the 76 MiB
  MSI verifier copied into a 16 MiB container `/tmp`.
Validation:
- Shell syntax, bootstrap/production Compose contract, website-state transaction, server version,
  mandatory-MSI/Portal payload invariant and checksum checks passed. An isolated real 0.6.8 stack
  was upgraded through 0.6.26: old services were quiesced without volume deletion; dependency
  health gates, migrations, Portal/web/Admin/API/Signaling/TURN/observability, streamed MSI SHA-256,
  public routing and base-website activation all completed; `--status` reports 0.6.26 with every
  long-running service healthy and the post-install dry-run passes.
- The isolated host used a self-signed fixture, so the unmodified release correctly rejected it at
  strict public TLS verification. The completed integration used a test-only header copy that changed
  only that fixture check; the published bundle retains `--tlsv1.2` certificate validation. Embedded
  Windows x64 remains unchanged at 0.9.59 (`8bad346670b719eaf215c3fcb2cb6acc299c6a78cca3dad66413e18170d9b4da`).
- Pilot server SHA-256: `3c56658dbb28ff35dc874933ce339187d1ea361cf807be2706ce10640cb344dc`.
Risk:
- The bundle is an unsigned controlled pilot because no offline GPG release key was available; do not
  classify it as a signed production release. Upgrade briefly stops the old stack to release Docker
  networks, while named volumes are preserved. Forward migrations remain backward-compatible.
Rollback:
- On deployment failure the installer automatically restores the retained release. After activation,
  run `peeronq-server-0.6.26.run --rollback`; revert these focused installer/builder changes to restore
  the earlier behavior.

## 2026-08-24 - Connect Admin telemetry and publish 0.9.57 trust-recovery installers

Task:
- Make Admin downloads, online/open-session counts, infrastructure history, alerts, audit and logs
  reflect the running local services without changing desktop remote-control or transport code.
Files changed:
- Admin live metrics/health worker and overview copy; Downloads-aware Vite server middleware;
  Phase 3/Phase 6 observability, Redis/PostgreSQL permissions and controllers; focused tests/maps;
  Windows development package defaults and the local 0.9.57 website package pair.
Reason:
- The real 0.9.56 laptops used Phase 3 signaling while Admin read empty Phase 6 tables, local static
  MSI links bypassed Downloads telemetry, no worker produced service-health history, and missing
  observability DNS aliases dropped logs/metrics. Installation totals also hid historical
  unregistered rows. HA and missing-target handling could undercount or leave stale Healthy rows.
Validation:
- Admin API 27/27, Observability 29/29, Cloud Infrastructure 40/40, public preview 66/66 and Admin
  SPA 21/21 tests passed; both frontend typechecks passed. Development, HA, staging and production
  Compose render checks and all touched PowerShell parse checks passed.
- Live Prometheus reports all seven configured targets up, including the isolated Phase 3 target;
  Admin readiness is HTTP 200. All enabled Admin sidebar APIs return HTTP 200 for the Owner account;
  signed website publication remains intentionally disabled. Loki returned 48 recent signaling
  entries, OTel showed no DNS/export error, and PostgreSQL is recording six fresh Healthy service
  snapshots per interval. Live static MSI checks recorded 2 starts and 2 completions; audit and alert
  rows remain queryable.
- Matching x64/ARM64 0.9.57 MSIs passed Release publish, WiX/payload validation and pinned-root
  comparison. Website URLs return HTTP 200 and the restarted preview selects 0.9.57. SHA-256:
  x64 `a10e099066802cd1e8166ba65050ed147dcab287c7eb01a022a374425a3df360`; ARM64
  `3739b0227b2aac7c71ada31267e8258941785322315502a52b86abde991c218c`.
Risk:
- Historical local static downloads cannot be reconstructed because no access log existed; counts
  begin with real requests after this change. The privacy-limited unique estimate is not an exact
  device count; local Vite forwarding can merge multiple clients with the same browser family into
  one daily proxy/network bucket.
- During live controller validation, the old Phase 3 local CA was implicitly regenerated before the
  new fail-closed rotation guard existed; its private key was not recoverable. The guard now requires
  explicit `-RotateCertificate` and preserves backups. Existing 0.9.56 clients therefore cannot
  trust the current endpoint: install 0.9.57 on both laptops before online/session values can rise.
- Phase 3 log forwarding is deliberately asynchronous and drops while Phase 6 is offline. Desktop
  App/Application/Transport source was not changed.
Rollback:
- Revert this task and restart the managed preview/Phase 3/Phase 6 stacks. Reverting code cannot
  recover the lost old CA private key; keep 0.9.57 installed or explicitly rotate and rebuild another
  matched client pair.

## 2026-08-24 - Exit stale Admin shells when refresh authentication expires

Task:
- Fix every Admin section incorrectly showing `Permission denied` after the operator refresh
  session had expired.
Files changed:
- Admin SPA API-client refresh invalidation, AuthProvider state transition, focused auth regressions,
  and this record.
Reason:
- A protected request correctly returned 401 and its automatic refresh correctly failed closed, but
  the client cleared only its in-memory token. React stayed authenticated, so the shell remained
  visible and page loaders rendered the refresh 403 as a role denial even though Owner RBAC was
  valid.
Validation:
- The new expired-session regression failed against the old behavior, then passed. Admin SPA
  typecheck, all 21 tests, production build and Docker image build passed. Only `admin-ui` was
  recreated and became healthy. A fresh real Owner login returned HTTP 200 for session, overview,
  every sidebar resource, infrastructure metrics and administrator sessions; verification sessions
  were revoked/logged out afterward.
Risk:
- Expired or CSRF-invalid refresh sessions now return immediately to the secure sign-in screen.
  Genuine resource-level 403 responses still render `Permission denied`; Admin API RBAC, MFA,
  CSRF, token lifetime and desktop-client code are unchanged.
Rollback:
- Revert the Admin API-client callback, AuthProvider invalidation, both focused regressions and this
  entry, then rebuild/recreate only `admin-ui`; the stale authenticated shell behavior will return.

## 2026-08-24 - Publish 0.9.56 relay-throughput installers

Task:
- Package source commit `2e32582` as matching x64 and ARM64 LAN test installers with the restored
  uncapped negotiated file-relay path.
Files changed:
- Windows development/public-pilot/portable-support version defaults, Phase 3 build guidance,
  versioned website downloads/checksums, and this release record.
Reason:
- Both physical laptops must run the corrected transport; leaving either endpoint on 0.9.55 keeps
  the accidental 256/1,024 Kbps relay media-pacer cap active on that sender.
Validation:
- Matching x64 and ARM64 self-contained Release publishes completed with zero warnings/errors;
  both MSIs passed WiX ICE03, administrative extraction, runtime presence and exact publish-payload
  comparison. x64 is 78,585,856 bytes with SHA-256
  `2311f98e2e2c6dbb776460b74dc287d7a10d14c1aa9f6e297cb364c0b286de3f`; ARM64 is 74,276,864 bytes
  with SHA-256 `3f69295735109af9a6d33d7ed3106e90503d19178a9ad588b17c33e92a6aad04`.
  Both package URLs and `SHA256SUMS.txt` return HTTP 200. The restarted preview's checksum-verified
  startup selector selected 0.9.56; both payload assemblies report 0.9.56.0 and source `2e32582`.
Risk:
- These remain unsigned LAN-development installers pinned to
  `wss://signal.10.0.0.10.sslip.io:5443/ws`. The exact 1 GiB/41-second automated gate covers native
  QUIC; restored relay throughput still requires the user's two-laptop measurement. Fully close
  PeerOnQ and install x64 0.9.56 on both ordinary Intel/AMD laptops before testing.
Rollback:
- Restore the four 0.9.55 defaults and checksum file, restart the preview, reinstall the 0.9.55
  package pair, and revert source commit `2e32582` if the relay behavior must also be restored.

## 2026-08-24 - Restore fast negotiated file-relay throughput

Task:
- Restore the file-transfer speed that physical laptops had before the media-reservation work,
  without removing pacing from native QUIC or direct WebRTC file paths.
Files changed:
- Secure collaboration route dispatch, relay pacing regression coverage, transport/performance
  documentation, the project map, and this record.
Reason:
- Commit `99a3731` accidentally removed the established relay exception and made every relay chunk
  pass through the media token bucket. On the normal Windows relay fallback, Fair/Unknown and Poor
  health could therefore cap an otherwise fast connection at 1,024 or 256 Kbps. The earlier
  `7a2bc25` behavior intentionally relied on the relay socket's own bounded backpressure.
Validation:
- The relay-no-pacing, native-over-relay, and dedicated-WebRTC regressions passed 3/3; Application
  passed 160/160, Transport passed 7/7, and the three real-WebRTC bulk/control cases passed. The
  explicit 1 GiB native-QUIC contention gate still completed in 41 seconds with its simultaneous
  interactive-latency assertion passing; the full Release solution build completed with zero
  warnings and zero errors.
Risk:
- Relay throughput is again controlled by actual socket/server/network backpressure rather than the
  media estimate. File record ordering, bounded receiver backpressure, heartbeat protection and the
  separate input/video paths remain unchanged; physical two-laptop throughput is still the release
  acceptance test.
Rollback:
- Restore relay chunks to the media pacer, reinstate the paced-relay regression expectation, and
  revert the matching documentation lines.

## 2026-08-24 - Make the 1 GiB per minute contract a mandatory CI gate

Task:
- Prevent future file-transfer changes from silently dropping below 1 GiB/minute or starving the
  simultaneous mouse/keyboard lane.
Files changed:
- .NET quality workflow, project map, and this record.
Reason:
- The transport suite always checked the same throughput ratio with a 64 MiB sample, but the full
  1 GiB contention run required a manual environment flag and was therefore not mandatory in CI.
Validation:
- The explicit 1,073,741,824-byte native-QUIC run completed in 40 seconds while its interactive-lane
  p95 assertion passed; the normal Transport suite passed 7/7. Workflow diff and whitespace checks
  passed.
Risk:
- Windows CI gains about 40 seconds of runtime. The gate uses loopback native QUIC, so a physical
  network below roughly 143 Mbps cannot be made to satisfy the target by software alone.
Rollback:
- Remove only the dedicated quality-workflow step and matching map/changelog lines; the production
  transfer path and its existing scaled regression remain unchanged.

## 2026-08-24 - Publish 0.9.55 native-throughput installers

Task:
- Package source commit `21a8b59` as matching x64 and ARM64 LAN test installers containing the
  native-QUIC preference/adaptive-throughput fix and auto-dismissing completed progress.
Files changed:
- Windows development/public-pilot/portable-support version defaults, Phase 3 build guidance,
  versioned website downloads/checksums, and this release record.
Reason:
- Both transfer peers must run the corrected path selection; leaving either laptop on an older
  package can retain the slow relay-selected behavior.
Validation:
- Matching x64 and ARM64 self-contained Release publishes completed with zero warnings/errors;
  both MSIs passed WiX ICE03, administrative extraction, runtime presence and exact publish-payload
  comparison. x64 is 78,589,952 bytes with SHA-256
  `b10393f513ad71521162c990019b84809af8df090a205a97ef793267025f76f3`; ARM64 is 74,276,864 bytes
  with SHA-256 `66c9ca4afafdf488a1011b14a98a6ff8b17733b789af30f0704e9a2982d5cf25`.
  Both package URLs and `SHA256SUMS.txt` return HTTP 200. The restarted preview's checksum-verified
  startup selector selected 0.9.55; both payload assemblies report 0.9.55.0 and source `21a8b59`.
Risk:
- These remain unsigned LAN-development installers pinned to
  `wss://signal.10.0.0.10.sslip.io:5443/ws`. Close old PeerOnQ processes and install x64 0.9.55 on
  both ordinary Intel/AMD laptops before physical transfer verification.
Rollback:
- Restore the four 0.9.54 defaults and checksum file, restart the preview, reinstall the 0.9.54
  package pair, and revert source commit `21a8b59` if the transport behavior must also be restored.

## 2026-08-24 - Restore native LAN transfer speed and dismiss completed progress

Task:
- Prefer the negotiated native QUIC file lane over an available signaling relay on Direct LAN,
  keep its adaptive throughput estimator active, and hide the viewer's completed transfer status.
Files changed:
- Secure collaboration path selection, native-startup regression fakes/tests, viewer completion
  timer, project map, and this record.
Reason:
- An available file relay skipped the bounded native-startup wait and could lock the entire transfer
  to slower WebSocket signaling. Even after native was selected, the relay flag incorrectly disabled
  adaptive capacity tracking. Completed progress also stayed permanently in the command bar.
Validation:
- The new Direct-LAN relay/native regression passed; Application passed 160/160 and Transport passed
  7/7. The live QUIC contention gate moved 1 GiB in 40 seconds while its interactive-lane latency
  assertion passed. x64 and ARM64 Release WinUI builds completed with zero warnings/errors.
Risk:
- The first Direct-LAN offer can wait at most four seconds for native startup; if native is unavailable,
  the existing relay fallback is retained. Internet/relayed sessions do not receive that wait.
Rollback:
- Restore the relay short-circuit/adaptive-path conditions, remove the two-second viewer timer and
  its regression coverage, and revert the matching map/changelog lines.

## 2026-08-24 - Publish 0.9.54 reconnect and compact-progress installers

Task:
- Package source commit `39cd27c` as matching x64 and ARM64 LAN test installers containing the
  duplicate-instance/reconnect fix and the short top-toolbar 0-100 transfer indicator.
Files changed:
- Windows development/public-pilot/portable-support version defaults, Phase 3 build guidance,
  versioned website downloads/checksums, and this release record.
Reason:
- 0.9.53 could run two clients for one local identity, allowing connection replacement churn; the
  corrected code and requested compact progress placement require matching binaries on both laptops.
Validation:
- Matching x64 and ARM64 self-contained Release publishes completed with zero warnings/errors;
  both MSIs passed WiX ICE03, administrative extraction, runtime presence and exact publish-payload
  comparison. A packaged x64 Portable Support smoke test kept exactly one primary process and made
  the second launch redirect and exit cleanly with code 0.
- x64 is 78,594,048 bytes with SHA-256
  `ea9fd6633a9dacfe549e46ef9aa893fb3e4fd7f18075ee1034224f80f8fc1421`; ARM64 is 74,276,864 bytes
  with SHA-256 `4fef1809b213f59c2f4046caf2b6b8b9672bcdd879ac67b32f6b5106c6b20929`.
  Both package URLs and `SHA256SUMS.txt` return HTTP 200. The restarted preview's checksum-verified
  startup selector selected 0.9.54; both payload assemblies report 0.9.54.0 and source `39cd27c`.
Risk:
- These remain unsigned LAN-development installers pinned to
  `wss://signal.10.0.0.10.sslip.io:5443/ws`. Close both running 0.9.53 processes before installing;
  physical two-laptop transfer/view/input verification is still required.
Rollback:
- Restore the four 0.9.53 defaults and checksum file, restart the preview, and reinstall the 0.9.53
  package pair; source commit `39cd27c` remains independently revertible.

## 2026-08-24 - Stop duplicate-client reconnect loops and compact viewer progress

Task:
- Prevent repeated app launches from competing for the same device registration, make a displaced
  signaling connection terminal until explicit reconnect, and place a short 0-100 transfer status
  at the top immediately after the Fullscreen/file command.
Files changed:
- Windows app activation/main-window focus routing, signaling reconnect policy and regression
  tests, viewer XAML/progress source guard, project map, and this record.
Reason:
- Diagnostics showed two installed PeerOnQ processes repeatedly replacing each other's server
  registration, producing Connect/Disconnect/Connect churn and destabilizing active transfer/view
  sessions. The prior transfer indicator also consumed a full row instead of the requested compact
  toolbar area.
Validation:
- x64 Release WinUI build passed with zero warnings/errors; Signaling passed 120/120 with four
  environment-only skips, including the real duplicate-device replacement regression; Application
  passed 160/160; targeted WinUI/single-instance source contracts and `git diff --check` passed.
- The unchanged native QUIC throughput gate transferred 1 GiB in 41 seconds while its concurrent
  interactive-lane latency assertion also passed, retaining the 1 GiB/minute target.
Risk:
- Existing 0.9.53 processes must both be closed before installing/starting the corrected build;
  the single-instance guard prevents duplicate launches from that point onward. Physical two-device
  verification remains required for final visual and real-network confirmation.
Rollback:
- Revert this entry's app activation, signaling reconnect sentinel, toolbar layout, tests and map;
  file pacing/chunk/allocation code is unchanged and needs no rollback.

## 2026-08-24 - Publish 0.9.53 consolidated-viewer installers

Task:
- Package source commit `084aaf1` as matching x64 and ARM64 LAN test installers so the
  single-window viewer, toolbar file picker and fixed 0-100 transfer progress can be tested.
Files changed:
- Windows development/public-pilot/portable-support version defaults, Phase 3 build guidance,
  versioned website downloads/checksums, and this release record.
Reason:
- The viewer behavior was implemented and validated in source but version 0.9.52 did not contain
  it, so both laptops need matching binaries built from the new source commit.
Validation:
- Pinned LAN signaling was ready and proxy/signaling/TURN containers were healthy. Matching x64
  and ARM64 self-contained Release publishes completed with zero warnings/errors; both MSIs passed
  WiX ICE03, administrative extraction, runtime presence and exact publish-payload comparison.
- The x64 MSI is 78,594,048 bytes with SHA-256
  `360a27f2166b17831d4e3e21dfa40eb7c256ea6a17aaf7158242b5abc4f029b2`; ARM64 is 74,264,576 bytes
  with SHA-256 `ee3374b6284c8ecc0fb7eea1a96b649ab371fd102032c97f132632cec36ee903`.
  Both package URLs, `SHA256SUMS.txt`, and `/downloads` return HTTP 200. The restarted preview's
  checksum-verified startup selector selected version 0.9.53, and both payload assemblies report
  version 0.9.53.0.
Risk:
- These remain unsigned LAN-development installers pinned to
  `wss://signal.10.0.0.10.sslip.io:5443/ws`; physical two-laptop verification is still required for
  the new window lifecycle and viewer progress presentation.
Rollback:
- Restore the four 0.9.52 defaults and checksum file, restart the preview, and reinstall the 0.9.52
  package pair; source commit `084aaf1` remains independently revertible.

## 2026-08-24 - Consolidate the remote viewer and expose transfer progress

Task:
- Keep one visible window while viewing a remote laptop and move explicit file selection plus
  determinate progress into the viewer toolbar beside Fullscreen.
Files changed:
- WinUI main/viewer lifecycle and viewer transfer UI, native UI source guards, project map, and
  this record.
Reason:
- The dedicated viewer opened without hiding the main PeerOnQ window, and its existing drag/drop
  and clipboard transfer path had no visible picker or authoritative 0-100 progress surface.
Validation:
- The x64 Release WinUI build completed with zero warnings/errors; Application passed 160/160;
  targeted XAML, event-wiring, single-window and progress contracts passed; scoped formatter and
  `git diff --check` passed.
- The aggregate native UI script remains blocked before its assertions by the pre-existing missing
  `artifacts/peeronq/src/layouts/PortalLayout.tsx`; the changed viewer contracts were run separately.
Risk:
- Main-window hiding is limited to active viewer sessions and restores when the final viewer
  closes. File permission, offer, pacing, encryption, receive policy, input and video paths are
  unchanged; a physical two-laptop visual check remains appropriate.
Rollback:
- Revert this entry's main/viewer UI, native source guards and project-map note; the existing main
  window transfer page plus viewer drag/drop/paste behavior will remain available.

## 2026-08-24 - Publish 0.9.52 file-throughput recovery installers

Task:
- Package source commit `7c77b0a` as a matching two-architecture LAN test release after the real
  one-GiB throughput, input-latency and post-transfer liveness gates passed.
Files changed:
- Windows development/public-pilot/portable-support version defaults, Phase 3 build guidance,
  versioned website downloads/checksums, and this release record.
Reason:
- Version 0.9.51 can lock an early transfer to the slow SCTP fallback and freeze file completion;
  both laptops must run binaries containing the corrected native-startup and bulk-dispatch paths.
Validation:
- Pinned local signaling health was ready. Matching x64 and ARM64 self-contained Release publishes
  completed with zero warnings/errors; both MSIs passed WiX ICE03, administrative extraction,
  runtime presence and exact publish-payload comparison.
- The x64 MSI is 78,585,856 bytes with SHA-256
  `333deccbcd2ce29bb819a19ae37e864d6ec487747f546532cc92678a119996e2`; ARM64 is 74,268,672 bytes
  with SHA-256 `7d72e138e78f2856cdeec0c468406b682bd39fc770f773160bbb84214dd3f153`.
  Both package URLs, `SHA256SUMS.txt`, and `/downloads` return HTTP 200. The restarted preview's
  checksum-verified startup selector selected version 0.9.52.
Risk:
- These remain unsigned LAN-development installers pinned to
  `wss://signal.10.0.0.10.sslip.io:5443/ws`. Physical two-laptop Windows firewall/path acceptance
  still determines whether the 25.3 MiB/s native QUIC route is available.
Rollback:
- Restore the four 0.9.51 defaults and checksum file, restart the preview, and reinstall the 0.9.51
  packages; source fix commit `7c77b0a` remains independently revertible.

## 2026-08-24 - Restore high-throughput file startup and post-transfer liveness

Task:
- Keep capable same-LAN transfers on the native QUIC path, extend receiver-confirmed pacing to the
  isolated WebRTC fallback, and prevent bulk receive callbacks from freezing completion/control.
Files changed:
- Collaboration bulk selection/feedback/receipt scheduling, bounded WebRTC bulk dispatch, focused
  transport/media regressions, test media capabilities, and this record.
Reason:
- A transfer offered before asynchronous native negotiation completed was permanently locked to the
  much slower SIPSorcery SCTP fallback. That fallback ignored disk-confirmed capacity feedback and
  synchronously ran disk/completion work on the SCTP receive callback, causing low throughput and a
  post-100% session freeze.
Validation:
- Application passed 160/160, Media passed 109/109 with one opt-in TURN test skipped, and Transport
  passed 7/7. Real WebRTC bulk completion and full-control contention passed; the latter retained
  live video and input under file pressure.
- A real 1 GiB native QUIC contention run completed in 40 seconds at 25.3 MiB/s while 2,597
  simultaneous input samples held 0.6 ms p95, exceeding the 17.1 MiB/s one-GiB-per-minute gate.
Risk:
- The throughput gate is guaranteed only when authenticated native QUIC becomes available on a
  capable direct-LAN path. The bounded dedicated WebRTC fallback remains live and adaptive but is
  still limited by SIPSorcery SCTP performance; physical Windows firewall/path acceptance remains
  part of the two-laptop test.
Rollback:
- Revert this task's collaboration/media/test changes; the prior isolated input/video/bulk lanes and
  0.9.51 packages remain available.

## 2026-08-24 - Publish 0.9.51 isolated-transport test installers

Task:
- Package the committed screen/input/file isolation, adaptive reserve, runtime fallback and expanded
  diagnostics work as a new two-architecture LAN test version for physical verification.
Files changed:
- Windows development/public-pilot/portable-support version defaults, Phase 3 build guidance,
  versioned website downloads/checksums, and this release record.
Reason:
- The user needs matching current binaries on both laptops; the previously installed 0.9.41 and
  website 0.9.50 packages predate commit `99a3731`.
Validation:
- Pinned TLS health returned HTTP 200 for `signal.10.0.0.10.sslip.io:5443`. Matching x64 and ARM64
  self-contained Release publishes completed with zero warnings/errors. Both MSIs passed WiX ICE03,
  administrative extraction, runtime presence and exact publish-payload comparison.
- The x64 MSI is 78,606,336 bytes with SHA-256
  `31fa8807cc58bba6a3cbbefa4f1645fc4c67e5138aedae87c1df4c7d2a2ec8df`; ARM64 is 74,256,384 bytes
  with SHA-256 `d6ae8271681ca5360e1d24a2c061842699f0ebe9aed8195d223f21676f0a429e`.
  Both package URLs, `SHA256SUMS.txt`, and `/downloads` return HTTP 200. The restarted preview's
  checksum-verified selector and injected download source both select version 0.9.51.
Risk:
- These are unsigned LAN-development installers pinned to the scoped development root and
  `wss://signal.10.0.0.10.sslip.io:5443/ws`; they are not public production releases. Physical
  two-laptop latency, 4K, one-GiB transfer and interruption acceptance still belongs to the user run.
Rollback:
- Revert the four 0.9.51 defaults/release record and republish the checksum-verified 0.9.50 pair;
  source commit `99a3731` remains independently revertible.

## 2026-08-24 - Preserve contention evidence in advanced diagnostics

Task:
- Make an explicitly exported diagnostics ZIP sufficient to evaluate the implemented screen,
  input and file-transport latency/isolation gates without including user content or addresses.
Files changed:
- Native diagnostics request mapping, bounded bundle statistic allowlist, sanitization regression,
  and performance evidence documentation.
Reason:
- The existing archive exposed RTT/loss/FPS but omitted capture-to-present and input-to-injection
  latency, lane readiness/buffers, native goodput, dimensions and queue counters needed to diagnose
  the reported post-transfer freeze on two physical devices.
Validation:
- The allowlist regression failed first because the required fields were absent, then passed with
  frame-age p95, input p95, input-lane readiness and native goodput present while an unknown private
  field remained excluded. Infrastructure passed 80/80; the Windows app Debug build completed with
  zero warnings/errors; scoped formatter verification and `git diff --check` passed.
Risk:
- The additions are numeric/enum technical state only. Existing consent, bundle size/expiry,
  address-free allowlist, log sanitization and content exclusions remain unchanged.
Rollback:
- Remove the added request keys, allowlist names, regression and documentation; older basic ZIP
  manifests remain readable because the manifest schema and existing fields did not change.

## 2026-08-24 - Physically isolate Full Control input from video and file SCTP

Task:
- Keep mouse/keyboard on a network-level lane that cannot sit behind video RTP or file SCTP queues,
  while preserving authorized control with older peers and after optional-lane failure.
Files changed:
- WebRTC media negotiation/routing/statistics, media diagnostics, real peer-connection and contention
  regressions, secure-transport/performance maps and report.
Reason:
- File traffic already had dedicated SCTP/native QUIC, but protected Input records still shared the
  primary video peer connection. Application send gates alone could not prove association-level
  isolation from video transport work.
Validation:
- The real Full Control contract first failed because the input-lane SDP capability was absent. New
  peer negotiation, bounded codec/ICE routing, wrong-lane checks, dedicated record counters and old-
  peer primary fallback passed 4/4. The real VP8/input/file contention acceptance passed in 21 s
  with its <=35 ms input p95, <=10 ms inflation, <250 ms individual stall, advancing-video and
  concurrent-file gates. A runtime-failure acceptance closes the established input peer and proves
  primary fallback, connected media state and advancing VP8 frames. Application passed 159/159;
  Media passed 109/109 with one environment-owned live TURN test skipped; the full solution build
  completed with zero warnings/errors.
Risk:
- The auxiliary peer adds one ICE/DTLS/SCTP connection only when `ControlInput` is granted. Missing,
  unopened or closed negotiation uses the unchanged permission-gated primary fallback; it does not
  alter media state, hybrid protection, focus ownership or Windows injection authorization.
Rollback:
- Remove the additive input SDP/ICE peer, lane routing/statistics, regressions and docs; protected
  input records will return to the primary WebRTC data channel while the bulk paths remain intact.

## 2026-08-24 - Recover a fresh receiver zero-presentation stall

Task:
- Prevent a screen that stops decoding/rendering after file contention from being classified as a
  healthy session while the sharer continues to encode.
Files changed:
- Central connection policy, adaptive media controller, WebRTC key-frame recovery, focused unit/real
  peer-connection regressions, and performance maps/report.
Reason:
- Fresh viewer feedback with zero decode and render FPS did not match the existing ratio check and
  fell through to `stable_headroom`; the sender could keep producing without lowering load or
  forcing a decoder recovery key frame.
Validation:
- The focused regression first failed with the quality level still at zero, then passed by requiring
  two consecutive sender-active zero-presentation windows. Real VP8/WebRTC 320x240 recovery passed,
  and the complete 320x240/1080p/4K acceptance theory passed 3/3 in 10.2 seconds. Application passed
  159/159; Media passed 108/108 with one environment-owned live TURN test skipped; the full solution
  build completed with zero warnings/errors.
Risk:
- Detection requires fresh negotiated feedback and newly encoded sender frames, so one empty sample,
  old peers, stale telemetry and a sender with no newly encoded frames do not create repeated quality
  reductions.
Rollback:
- Remove the receiver-feedback/stall fields, the central `viewer_video_stall` branch, key-frame
  request and their focused tests/docs.

## 2026-08-24 - Bound reverse-direction QoS feedback to 250 ms

Task:
- Make remote input/file backoff react inside the interactive stall budget instead of waiting up to
  one second for viewer quality feedback.
Files changed:
- `SessionCoordinator` cadence contract/test, WebRTC feedback freshness contract/test, and
  performance maps/report.
Reason:
- The 1 s publisher period exceeded the 250 ms application-stall acceptance boundary before the
  sharer could see viewer input pressure and reduce reverse-direction bulk traffic.
Validation:
- The explicit 250 ms contract and failed-first-send retry passed in 876 ms. The real WebRTC
  input+active-file regression, including five-second feedback expiry, passed in 23 s. Application
  passed 159/159, Signaling passed 115/115 with four external Redis/Docker tests skipped, and the
  full solution build had zero warnings/errors.
Risk:
- Cadence is 4 msg/s per active session and shares a 20 msg/s client budget; above five concurrent
  sessions it stretches automatically below the server's default 25 msg/s limit. The 5 s freshness
  window covers the bounded 3.2 s cadence at 64 sessions plus delivery jitter. Video adaptation
  remains at one second; only feedback/file-backoff reaction becomes faster.
Rollback:
- Restore `QualityPublishInterval` to one second and feedback freshness to three seconds, then remove
  the cadence/freshness assertions and docs.

## 2026-08-24 - Keep adaptive feedback alive after a transient signaling failure

Task:
- Prevent one temporary quality-message send failure from disabling screen/input feedback for the
  remainder of an otherwise healthy remote session.
Files changed:
- `SessionCoordinator` quality loop, its scripted signaling fake/regression, and transport maps.
Reason:
- The exception boundary wrapped the entire periodic publisher. One failed send completed the task,
  so later registration recovery could leave adaptive file/video reserves without fresh feedback.
Validation:
- The regression first failed after one send attempt, then passed by observing a failed first tick
  followed by a successful second tick carrying the original input-pressure fields. Application
  passed 159/159 and the full solution build completed with zero warnings/errors.
Risk:
- Retry remains bounded to the existing 250 ms timer; cancellation still ends the loop and no
  media, permission or cryptographic behavior changes.
Rollback:
- Restore the publisher-wide exception boundary and remove the scripted failure regression/docs.

## 2026-08-24 - Propagate input pressure to the reverse-direction sender

Task:
- Ensure mouse/keyboard latency reserves apply when the remote sharer, rather than the viewer,
  sends a file during Full Control.
Files changed:
- Existing session-quality contract/wire/validation, coordinator, media feedback/policy, native
  capacity estimator, reverse-direction WebRTC acceptance and performance documentation.
Reason:
- Input-to-injection p95 was measured only on the viewer. A sharer sending bulk data could not see
  that pressure, so its SCTP/native pacer and adaptive video controller could not react. The
  coordinator also started quality publication only for sharers, leaving viewer presentation/input
  fields present on the wire but unsent by the real application flow.
Validation:
- Media passed 107/107 with one environment-owned live TURN skip; Application passed 159/159;
  Signaling passed 115/115 with four external Redis/Docker tests skipped. Real reverse-direction
  VP8/WebRTC input+file acceptance passed, and the full solution build had zero warnings/errors.
Risk:
- Fields are additive and zero for old peers; feedback is session-owned, range-validated and expires
  after five seconds. File backoff occurs immediately; video changes only on the adaptive loop.
Rollback:
- Remove the two additive input quality fields and remote-quality mappings; retain the earlier
  presentation-only feedback and local input pacing.

## 2026-08-24 - Feed viewer presentation pressure back to adaptive media

Task:
- Reduce perceived screen lag without changing the working input, file-lane or authorization paths.
Files changed:
- Application quality/media contracts and coordinator; WebRTC adaptive policy; signaling quality
  wire/validation and relay heartbeat; focused application/media/signaling regressions; performance maps.
Reason:
- Viewer decode/render and authenticated frame-age metrics were diagnostic-only, so the sharer did
  not reduce production when presentation fell behind. File completion also had a race between
  clearing its receive marker and consuming a PONG already queued behind the callback.
Validation:
- Media 105 passed (+1 live TURN skip), Application 158 passed; real WebRTC feedback acceptance 3/3,
  quality signaling relay 1/1, policy 4/4, and relay-completion heartbeat repeated 5/5.
Risk:
- Additive quality fields remain zero for old peers; receiver feedback expires after five seconds.
Rollback:
- Revert this entry with the presentation-feedback fields/policy tests and heartbeat completion grace.

## 2026-08-24 - Learn native file capacity from receiver-confirmed delivery

Task:

- Remove the production WebRTC estimate ceiling from negotiated LAN file throughput without letting
  file traffic create screen/input queueing or making optional feedback a disconnect dependency.

Changed:

- Added authenticated, native-QUIC-only cumulative delivery receipts after file bytes are written.
  Receipts are bounded/coalesced, never reach application handlers, never use WebRTC/relay, and
  cannot claim more than the sender scheduled for that transfer.
- Added a connection-scoped goodput estimator. It starts from the existing post-media/input budget,
  raises the file probe by at most 25% per confirmed window, caps unknown health at 150 Mbps and
  verified healthy links at 1 Gbps, and immediately returns to the conservative policy on input
  pressure, Fair/Poor health, loss/RTT/jitter thresholds, invalid metrics, or feedback older than
  one second.
- Added sanitized native bulk budget/goodput/sample diagnostics and regression coverage for disk
  delivery reporting, protocol bounds, forged/non-monotonic receipts, stale feedback, native-only
  dispatch, media/input backoff, and real managed-QUIC contention.

Validation:

- Real 128 MiB adaptive QUIC/TLS contention run started at the production-capped 5,144 Kbps budget,
  reached the bounded 1 Gbps probe, averaged 28.0 MiB/s, and kept 289 mouse samples at 8.3 ms p95.
  Application passed 157/157, Transport passed 7/7, and Media passed 101/101 with the one
  environment-owned live TURN test skipped. The full Release solution build completed with zero
  warnings/errors; scoped formatter and diff whitespace verification passed.

Risk:

- Loopback confirms implementation and queue isolation, not physical Wi-Fi/router/disk/4K behavior.
  Two-laptop shaped-link acceptance remains required; direct-Internet native QUIC is still outside
  this LAN-only optimization.

Rollback:

- Remove `TransferReceipt`, `NativeBulkCapacityEstimator`, file-delivery reporting, the three native
  diagnostics fields and their tests/docs. Native QUIC then returns to the conservative media-derived
  allocation while the existing transfer, screen and input paths remain intact.

## 2026-08-24 - Activate authenticated same-LAN QUIC file transport

Task:

- Preserve responsive screen/input during large transfers and make the one-GiB-per-minute target a
  measured regression gate rather than an assumed policy-cap result.

Changed:

- Measured the active production-sized WebRTC path: dedicated SCTP moved 8 MiB in 26.5 seconds
  (309.5 KiB/s), while the legacy association timed out after three minutes. This ruled out another
  rate-limit-only change.
- Added `x-peeronq-native-bulk:1` negotiation. On an ICE-selected direct-LAN path, both peers exchange
  ephemeral TLS certificate pins and one listener port inside authenticated PNQE, then keep every
  newly started file transfer on a mutually pinned QUIC/TLS 1.3 connection. Screen and input remain
  on WebRTC; older peers, non-LAN paths and failed QUIC startup retain relay/dedicated-SCTP fallback.
- Kept path choice stable for the full transfer so strict per-transfer AEAD ordering cannot race
  across transports. Added sanitized negotiated/ready diagnostics without exposing an address, pin,
  key or file content.
- Corrected coarse Windows timer-credit loss in the adaptive file pacer and separated the finite
  150 Mbps unknown-capacity ceiling from a defensive 1 Gbps measured-capacity bound, so verified
  spare bandwidth above 150 Mbps is no longer discarded after media/input reserves.

Validation:

- Native QUIC full-payload run passed: 1 GiB in 40 seconds (25.3 MiB/s) while 2,592 concurrent mouse
  samples measured 0.4 ms p95. Three 64 MiB repeats measured 25.2-25.3 MiB/s and 0.4-2.6 ms p95.
- Application tests passed 154/154; Transport tests passed 6/6; Media tests passed 101/101 with the
  one environment-owned live TURN test skipped. Real ephemeral-certificate mutual-pin QUIC and
  authenticated LAN file/WebRTC-input isolation tests passed.

Risk:

- Native QUIC is intentionally LAN-first. Direct-Internet NAT traversal, QUIC relay/path racing,
  native DATAGRAM video and physical two-laptop/Wi-Fi/router benchmarks remain acceptance work.
  Local firewall policy can reject the new UDP listener; that leaves unstarted transfers on the
  compatibility path and must not end screen share.

Rollback:

- Remove the native-bulk SDP/PNQE negotiation, factory injection, `ManagedQuicBulkTransportFactory`,
  native transfer selection/diagnostics and their tests; restore the 150 Mbps measured cap and 5 ms
  pacer credit. The existing dedicated-SCTP/file-relay path remains the compatibility implementation.

## 2026-08-24 - Physically isolate direct file transfer from media and control

Task:

- Preserve the recorded AnyDesk-class responsiveness objective by preventing direct file traffic
  from sharing the sender queue used by video, mouse and keyboard, without regressing working
  security, legacy interop or transfer integrity.

Changed:

- File-capable new peers negotiate a bounded secondary SDP and route direct bulk data through a
  dedicated `RTCPeerConnection`/SCTP association. The mandatory hybrid handshake, input, clipboard,
  timing and video remain on the primary connection; bulk failure cannot change its connection
  state. Missing negotiation retains the old primary-channel compatibility route.
- Added bounded `PNQB` fragmentation/reassembly around encrypted file records. Interactive sessions
  admit at most five milliseconds of the live bulk allocation per fragment/queue burst, while the
  existing token bucket preserves sustained refill throughput and reserves control/media first.
- Bound channels to their negotiated physical connection and secure routing context. Fragment
  metadata stays untrusted; only a complete FileTransfer `PNQE` record that passes the unchanged
  AEAD/session/direction/transfer/epoch/sequence checks reaches file dispatch.
- Added separate primary/bulk SCTP buffer diagnostics and regressions for exact SDP negotiation,
  malformed descriptors, legacy fallback, wrong-lane rejection, exact file hash, bounded
  fragmentation, and simultaneous video/input/file traffic.

Validation:

- New focused Media acceptance passed 13/13. The active-bulk real-WebRTC scenario requires idle and
  bulk-time input injection within 35 ms, no more than 10 ms bulk inflation, at least three advancing
  video frames, maximum capture-to-decode of 75 ms, an unfinished concurrent transfer, and final
  transfer completion. The final 20-sample run measured 32.3 ms idle p95, 32.2 ms bulk-time p95 and
  4.2 ms maximum capture-to-decode on local real WebRTC.
- Application passed 152/152. The latest full Media suite passed 100/100 with one explicit live-TURN
  credential test skipped. The x64 Release WinUI build passed with zero warnings and zero errors.
- All touched Application, Media and Media-test C# files passed scoped
  `dotnet format --verify-no-changes`.

Risk:

- Loopback proves queue separation and protocol behavior, not two-device LAN/WAN throughput,
  congestion, Wi-Fi contention or four-hour stability. The legacy primary-channel fallback cannot
  provide the new physical isolation. No AnyDesk superiority claim or new installer is published.

Rollback:

- Revert the secondary peer negotiation/candidate routing, bulk codec and pacing changes,
  diagnostics/tests/docs and this entry together; direct file records will return to the primary
  SCTP association and its compatibility behavior.

## 2026-08-24 - Measure real Full Control input latency and throttle bulk first

Task:

- Protect the standing AnyDesk-level responsiveness goal with authenticated input-to-successful-
  injection evidence and make file traffic react to measured control delay without changing working
  authorization or old-peer behavior.

Changed:

- Added permission-bound SDP negotiation for sampled input acknowledgements. At most one command per
  100 ms is measured, pending state is capped at 32, and the host acknowledges only after successful
  OS injection on the existing independently encrypted interactive lane.
- Added clock-corrected input-to-injection p50/p95/p99 and uncertainty to sanitized diagnostics.
  Measurement timeout/delivery failure is optional and never disables authorized Full Control;
  unnegotiated peers receive neither the optional command field nor acknowledgement.
- Fed reliable input p95 into the live file allocator. Above 35 ms, bulk drops to a 64 KiB window and
  conservative rate while retaining at least 1 Mbps interactive and the current media reserve. Five
  seconds without a fresh sample clears stale pressure so file speed recovers.
- Added deterministic unit and real-WebRTC regressions for successful injection-boundary timing,
  bounded sampling, old-peer fallback, optional failure, uncertain/stale samples, Full Control and
  input responsiveness while an 8 MiB bulk transfer remains active.

Validation:

- Application passed 152/152. Media passed 91/91 with one explicit opt-in live TURN test skipped.
- Real WebRTC Full Control, legacy fallback and input-during-active-bulk scenarios passed inside the
  Media suite. Release x64 WinUI build passed with zero warnings/errors.
- Changed C# files passed scoped `dotnet format --verify-no-changes`; `git diff --check` passed. The
  whole-solution format gate still reports two pre-existing whitespace errors in unchanged
  `src/PeerOnQ.Cloud.Api/CustomerOrganizationService.cs` lines 196-197.

Risk:

- Loopback proves protocol and isolation behavior, not competitive physical latency. Exact
  two-device LAN/WAN benchmarks and soak evidence remain required before an AnyDesk comparison.

Rollback:

- Revert input-ack SDP/protocol/session logic, input statistics/policy, associated tests/docs and
  this entry together; the prior input delivery and file allocator remain the compatibility path.

## 2026-08-24 - Measure authenticated cross-device capture-to-present frame age

Task:

- Turn the recorded low-latency objective into trustworthy runtime evidence without breaking older
  peers or making diagnostics a new disconnect path.

Changed:

- Added exact SDP echo negotiation for optional video timing while leaving strict handshake v1
  unchanged. Missing echo retains the legacy raw VP8 path.
- Added a dedicated encrypted Telemetry record context and five-sample NTP-style peer clock
  estimation. Timing timeout/malformed samples disable only measurement; AEAD/replay checks remain
  fail-closed and telemetry never enters input/clipboard/file dispatch.
- Added an authenticated capture timestamp/sequence envelope inside video protection, carried the
  exact frame through UI coalescing, and export capture-to-present p50/p95/p99 plus clock uncertainty
  only after successful presentation and within a 50 ms uncertainty bound.
- Made the existing parallel-transfer-worker regression deterministic by holding both first sends
  until both production workers report `Transferring`; no transfer runtime behavior changed.

Validation:

- Codec, percentile, clock-estimator, encrypted-transport and old-peer fallback regressions pass.
- Real WebRTC VP8 loopback passes at 320x240, 1080p and 4K and produces authenticated bounded
  capture-to-present samples without losing the existing secure media/input path.
- Application passed 145/145. Media passed 87/87 with one explicit opt-in live TURN test skipped.
  Scoped formatting and diff checks passed; the x64 Release WinUI build passed with zero warnings/errors.
- The previously timing-sensitive parallel-worker regression passed 11 consecutive isolated runs
  after its deterministic barrier replaced coalesced UI-event timing as the overlap oracle.

Risk:

- UTC clock estimation is observability, not scheduling. Physical two-device competitive evidence
  remains required; samples above the uncertainty bound are deliberately omitted.

Rollback:

- Revert SDP timing negotiation, Telemetry context/clock sync, video envelope/frame propagation,
  diagnostics/tests/docs and this entry together; legacy local latency fields remain available.

## 2026-08-24 - Attribute local render latency to the exact presented frame

Task:

- Make screen-latency evidence accurate before further codec/network optimization by measuring the
  frame that the viewer actually presents, including p50/p95/p99 rather than one ambiguous p95.

Changed:

- Found that `MediaStatisticsCollector` stored only the latest decode timestamp. If another frame
  decoded while the UI was drawing an earlier coalesced frame, render latency could be attributed to
  the wrong frame and appear artificially low.
- Added a process-local monotonic decode timestamp to `RemoteVideoFrame`, carried it through the
  capacity-one ViewerWindow pending slot, and reports it only after `WriteableBitmap.Invalidate`
  succeeds. Render dimensions now come from that same presented frame.
- Added bounded capture-to-encode and decode-to-render p50/p95/p99 distributions to
  `MediaStatistics` and sanitized diagnostics. Existing latency fields remain p95-compatible.

Validation:

- The new distribution regression failed before implementation because the collector had no exact
  render-frame overload or percentile fields, then passed 2/2 with the existing dimension test.
- Application passed 141/141. Media passed 83/83 with one explicit opt-in live TURN test skipped;
  real WebRTC loopback now verifies decoded frames carry their presentation correlation timestamp.
- Scoped formatting passed. The x64 Release WinUI build passed with zero warnings/errors.

Risk:

- These percentiles are accurate local pipeline segments, not yet a synchronized two-device
  capture-to-present claim. Backward-compatible negotiated capture timestamps and peer clock sync
  remain required before comparing end-to-end latency with another product.

Rollback:

- Revert the frame timestamp propagation, percentile fields/export, regression, performance/map
  documentation and this entry together; diagnostics return to the ambiguous last-decode p95.

## 2026-08-24 - Record the low-latency competitive performance north star

Task:

- Make PeerOnQ's standing objective explicit: outperform AnyDesk through measured responsiveness,
  stability and throughput while keeping screen, input and file traffic isolated.

Changed:

- Added measurable direct-LAN 1080p/4K frame-age and input-latency targets, concurrent-transfer
  inflation limits, a conditional one-GiB-per-minute target, four-hour reliability soak and bounded
  recovery gate to the existing performance evidence document.
- Defined a fair competitor protocol requiring identical machines, workload, quality class, network
  impairment, exact versions, artifact hashes and at least ten trials. No comparative claim is
  allowed from unmatched quality settings or a single favorable run.
- Recorded the missing instrumentation required for honest p50/p95/p99 end-to-end frame age and
  two-device input measurement. Existing security, consent and authorization remain non-negotiable.

Validation:

- Documentation-only change reviewed against the existing performance evidence and remote-desktop
  security/reliability contract. No code, package, version or installed application was changed.

Risk:

- These values are ambitious acceptance targets, not current-build claims. Hardware, codec and
  physical network limits still apply, and competitive wording remains blocked until the defined
  repeatable benchmark produces evidence.

Rollback:

- Revert the north-star section and this changelog entry; runtime behavior is unaffected.

## 2026-08-24 - Isolate input and screen from file completion and relay bulk traffic

Task:

- Fix mouse, keyboard and screen responsiveness during bulk transfer and after a transfer reaches
  100%, while keeping high throughput, file integrity and malware verification fail-closed.

Changed:

- Identified one shared collaboration receive gate held through synchronous relay dispatch. The
  final `TransferComplete` handler closes streams, hashes every complete file, scans it and moves it;
  direct input was queued behind that bounded but potentially slow storage work.
- Added independent ordered receive lanes for input, clipboard, and file records. The record header
  is read only as an untrusted scheduling hint; existing AEAD authentication, channel/transfer
  binding, permission checks, replay order, file backpressure, SHA-256 and malware policy remain
  authoritative and unchanged.
- File-transfer processing now rejects non-file collaboration messages before creating async work,
  so input bursts cannot accumulate no-op tasks behind a slow completion check.
- Kept video outside these collaboration lanes on its existing independently authenticated RTP
  path, so file completion cannot acquire a video or input receive gate.
- Confirmed capture and viewer queues retain only the latest pending video frame; old frames were not
  accumulating in the codec/UI pipeline. The remaining screen delay came from relay chunks
  explicitly bypassing the adaptive file pacer and filling the shared physical NIC/router queue.
- Direct and relay chunks now share the same media-aware token bucket. Live video and authenticated
  input/security capacity are reserved first; file traffic uses the measured remainder and is
  throttled first on poor health. Without a usable capacity estimate, finite 50/100/150 Mbps
  priority ceilings replace an unlimited burst. File Transfer Priority can still carry one GiB in
  under 60 seconds on a link with sufficient spare capacity; file-only sessions stay uncapped.

Validation:

- The new blocked-completion regression failed before the fix with an 840 ms input timeout and
  passed afterward while the file handler was still deliberately held. Relay backpressure,
  serialized file sending and encrypted completion passed with it 4/4. The relay pacing regression
  also failed before the fix because the second 16 KiB chunk completed inside 100 ms, then passed
  with input delivered while the file lane remained paced. The provisional-cap regression failed
  with an unlimited `0` rate before the fix and passed at 150,000 Kbps afterward.
- The complete Application suite passed 141/141. The complete Media suite passed 82/82 with one
  opt-in live TURN test skipped; it includes real WebRTC Full Control video/input and interactive
  input under bulk backpressure. Scoped formatting passed and the x64 Release WinUI build completed
  with zero warnings/errors.

Risk:

- Per-channel handlers may now run concurrently by design. Each channel remains ordered and bounded,
  session teardown waits for every lane, and malformed or unauthenticated records still fail closed.
- SIPSorcery exposes no transport-wide TWCC capacity estimate. The high provisional file ceiling is
  bounded and drops when media health worsens, but no software can guarantee simultaneous 4K and a
  one-GiB-per-minute transfer when the physical link lacks their combined capacity.

Rollback:

- Revert the per-channel receive gates, unauthenticated routing hint, relay chunk pacing,
  provisional bulk ceilings, regressions, transport docs, map update and this entry together; the
  shared completion/input gate and unlimited relay burst will return.

## 2026-08-24 - Compact the incoming connection dialog

Task:

- Replace the oversized, explanation-heavy remote-host consent popup with a smaller, cleaner
  Fluent dialog without changing its security decisions.

Changed:

- Reduced the content width from 520-580 px to 400-440 px and the scroll ceiling from 650 px to
  480 px. Removed the decorative hero and verbose protection card.
- Kept one concise requester card, compact icon-led View only / Full control rows, exact permission
  summaries, local request time, and a short auto-decline countdown. Selected rows use existing
  theme resources without layout-shifting border changes.
- Preserved the initially unselected radio scope, disabled approval until selection, exact grant
  button label, Decline default, Block action, timeout, and fail-closed permission result.

Validation:

- Compact-layout and explicit-scope static guards passed. The scoped formatter passed, permission
  boundary regressions passed 2/2, and the x64 Release WinUI build passed with zero warnings/errors.
- The aggregate native-UI script remains blocked before its popup assertions by the pre-existing
  missing `artifacts/peeronq/src/layouts/PortalLayout.tsx`; the popup guards ran independently.

Risk:

- Large system text can make the compact body scroll, but the requester, scope and timer remain in
  the same standard ContentDialog focus order. No signaling, permission, input, media, or transfer
  behavior changed.

Rollback:

- Revert the compact dialog layout, its static guards, map/design documentation and this entry
  together.

## 2026-08-24 - Keep Full Control alive with high-polling mice

Task:

- Fix a real two-laptop Full Control session where live video worked but controller mouse and
  keyboard stopped affecting the remote computer.

Changed:

- Compared controller diagnostic `324ea428...` with remote diagnostic `9ae87090...`. The session
  accepted Full Control and reached Windows input injection at 07:44:29 UTC, then a high-rate
  pointer burst at 07:51:34 UTC produced 1,410 `input_rate_rejected`, 2,000
  `input_focus_generation_rejected`, and later viewer `stale_focus` warnings.
- Pace/coalesce pointer motion to at most 240 commands per second. This remains well above display
  refresh rates while keeping high-polling mice below the unchanged 2,000-message receiver safety
  boundary; keyboard and mouse-button events remain immediate.
- Made an already failed input protocol state idempotent so one rejected burst cannot repeatedly
  reset the host boundary or create thousands of duplicate warnings. Full Control permission,
  Windows injection, video, file-transfer, bandwidth reserve and encryption behavior are unchanged.
- Advanced Windows development, public-pilot and portable-support defaults to 0.9.50.

Validation:

- The new 2,500-event high-polling regression failed before the fix with the same
  `input_rate_rejected` cascade and passed afterward. Pointer pacing, receiver rate-limit,
  coalescing and reconnect/focus regressions passed 4/4; the complete Application suite passed
  140/140.
- Real WebRTC Full Control and input during backpressured bulk transfer passed 2/2. The x64 WinUI
  0.9.50 Release build passed with zero warnings/errors.
- Built and payload/ICE03-validated matching x64 and ARM64 0.9.50 installers. Their SHA-256 values
  are x64 `ae26e206c6be3a76ac752d059f7b42b103b6c148241e4ee69a1ecc5820eb8fe0` and arm64
  `012051f583984c582c5082c7f71777c6a4410896ad4dbc4e1b2aaf6cd1c38a93`; both MSI URLs and the
  checksum manifest return HTTP 200, and the restarted preview selects checksum-verified 0.9.50.

Risk:

- The exact diagnostic failure now has deterministic regression coverage, but physical confirmation
  still requires both laptops to install 0.9.50 and start a fresh Full Control session.

Rollback:

- Revert pointer pacing, idempotent failure handling, its regression, version defaults, map update
  and this entry together; the earlier explicit-scope and pre-offer input-target fixes remain.

## 2026-08-24 - Modernize the remote-host consent dialog

Task:

- Make the incoming connection popup on the remote laptop more modern, colorful, and polished
  without weakening or changing the explicit attended-access decision.

Changed:

- Rebuilt the native WinUI consent hierarchy around Fluent requester, access, protection, and
  timeout cards with system icons, concise English copy, exact scope summaries, and a scroll-safe
  desktop layout.
- Added theme-aware trust navy and secure green semantic resources for Light, Dark, and High
  Contrast modes. The dialog resolves the active XAML-root palette before creating its controls.
- Preserved the initially unselected View only / Full control choice, disabled approval action
  until selection, safe Decline default, fail-closed timeout, and exact selected grant label.
  Media, signaling, input, file-transfer, heartbeat, and bandwidth implementations are unchanged.
- Advanced Windows development, public-pilot and portable-support defaults to 0.9.49.

Validation:

- The x64 WinUI 0.9.49 Release build passed with zero warnings/errors, the targeted consent UI
  guards passed, permission/granted-scope regressions passed 5/5, and the complete Application
  suite passed 139/139.
- Real WebRTC Full Control and input during backpressured bulk transfer passed 2/2; encrypted file
  completion retained the screen session 1/1. A temporary parallel-build DLL lock was eliminated
  by rerunning the affected test serially, where it passed.
- The aggregate native-UI script remains blocked before its assertions by the pre-existing missing
  `artifacts/peeronq/src/layouts/PortalLayout.tsx`; the targeted popup guards run independently.
- Built and payload/ICE03-validated matching x64 and ARM64 0.9.49 installers. Their SHA-256 values
  are x64 `a9070c755c6f83360725edcb2a142eb798cf0f6adb0823032aea6d9361c1ecb6` and arm64
  `2952fb0c64bdddd4ce7f8ecc05339795b065cd135034764a18d79b4e98db4c62`; both package URLs and
  the checksum manifest return HTTP 200, and the restarted local preview selects checksum-verified
  version 0.9.49.

Risk:

- Build and source guards verify the native layout, but the final visual appearance still requires
  a physical incoming request on the remote laptop after installing 0.9.49.

Rollback:

- Revert the consent layout, theme tokens, source guards, version defaults, map/docs update, and
  this entry together; the 0.9.48 explicit-scope fix remains the behavioral baseline.

## 2026-08-24 - Require an explicit attended access choice

Task:

- Fix attended Full Control sessions that connected as View Only despite the requester choosing
  Full Control on the viewer device.

Changed:

- Used the remote-host diagnostics bundle to prove the accepted session lacked `ControlInput`:
  capture started with `cursor=true`, no remote-input boundary event followed, and the permission
  dialog's accepted result produced the same scope as its preselected View Only choice.
- Removed the preselected scope from the host permission dialog. The host must now explicitly pick
  View only or Full control; the primary action stays disabled and names the exact selected grant.
- Let `RadioButtons` own its accessible item containers so its `SelectedIndex` is authoritative.
  Media, file-transfer, heartbeat, bandwidth and input-transport implementations are unchanged.
- Advanced Windows development, public-pilot and portable-support defaults to 0.9.48.

Validation:

- Permission/granted-scope regressions passed 5/5, the complete Application suite passed 139/139,
  the targeted permission-dialog source guard passed, and the x64 WinUI Release build completed
  with zero warnings/errors.
- Real WebRTC Full Control passed 1/1, interactive input under backpressured bulk transfer passed
  1/1, and encrypted file completion retained the screen session 1/1.
- The aggregate native-UI script remains blocked before its assertions by the pre-existing missing
  `artifacts/peeronq/src/layouts/PortalLayout.tsx`. The unchanged signaling heartbeat integration
  test failed twice with `Disconnected`; its deterministic helper passed and no signaling source
  changed in this task.
- Built and payload-validated matching x64 and ARM64 0.9.48 installers. Their SHA-256 values are
  x64 `c5d8a38900e90128413b10025b6a6d96080b28614e024c08d353a080904eeec3` and arm64
  `14d009e5acbe6138081b649c542f741f3e8e37f287f38a11ecfbcfd5ba794819`; both package URLs and
  the checksum manifest return HTTP 200, and the restarted local preview selects 0.9.48.

Risk:

- The diagnostics prove the reported session was granted View Only and the dialog can no longer
  accept an implicit scope. Physical two-device confirmation is still required after both endpoints
  install 0.9.48 and the host explicitly selects Full control.

Rollback:

- Revert the explicit permission choice, source guards, version defaults, map update and this entry
  together; the earlier transport, file-completion and input-target fixes remain independent.

## 2026-08-24 - Arm Full Control input before the viewer offer

Task:

- Stop Full Control from behaving like View Only when the viewer requests input focus immediately
  after its secure channel opens.

Changed:

- Bind the sharer's approved capture target to the Windows input sink before sending the SDP offer.
  Injection stays disabled until the authenticated focus request is accepted.
- Added a regression that proves the input target exists before the offer can reach the viewer.
- Advanced Windows development, public-pilot and portable-support defaults to 0.9.47 without
  changing file-transfer, screen-sharing, heartbeat or bandwidth-allocation behavior.

Validation:

- The new ordering regression failed before the fix and passed afterward; the complete Application
  suite passed 139/139. Real WebRTC Full Control and interactive-input-under-bulk-load checks passed.
- Screen-after-file-completion, relay heartbeat/rate/backpressure and 4K/interactive bandwidth
  preservation checks passed 9/9.
- Built and payload-validated matching x64 and ARM64 0.9.47 installers. Their SHA-256 values are
  x64 `9948473334cdf8a01c860202f9e545df7c0faa05e56e075235712c640661af8f` and arm64
  `d2018a8285283ca1be58d52fe3de797a088f2268a79f3595c6789ad8d766b1c5`; both package URLs and
  the checksum manifest return HTTP 200, and the restarted local preview selects 0.9.47.

Risk:

- Automated coverage validates the race and preserved traffic paths, but physical two-device input
  injection still requires installation on both endpoints. Windows UIPI and secure-desktop limits
  remain unchanged.

Rollback:

- Revert the pre-offer capture-target binding, regression, version defaults, map update and this
  entry together; the earlier 0.9.45 and 0.9.46 fixes remain independent.

## 2026-08-24 - Restore late-bound Full Control input

Task:

- Restore mouse and keyboard forwarding when a Full Control viewer window is created before the
  secure collaboration channel becomes available.

Changed:

- Bind the authorized `RemoteInputSession` to an existing viewer when collaboration arrives late,
  matching the established late file-transfer attachment path.
- Preserve the original session binding and permission-change subscription; reject an unexpected
  second input session rather than risking cross-session input forwarding.
- Added native UI source guards for the late input attachment and advanced Windows development,
  public-pilot and portable-support defaults to 0.9.46.

Validation:

- Application input/session tests passed 138/138 and the x64 WinUI build succeeded.
- Built and payload-validated matching x64 and ARM64 0.9.46 installers. Their SHA-256 values are
  x64 `a2cde8ead71b3b9f0a04bfba781b92abef8f57a6aa78bf1675244889e117be2d` and arm64
  `c1d2dfde7ad679c1ea8c78818235ef3bc804f8b7d78636805d1c6aa9a15d513c`; both package URLs and
  the checksum manifest return HTTP 200, and the restarted local preview selects checksum-verified
  0.9.46.

Risk:

- The fixed activation sequence is covered by build and collaboration tests, but Windows input
  injection still needs a physical two-device Full Control check. The existing native UI aggregate
  script is currently blocked before its assertions by a missing unrelated
  `artifacts/peeronq/src/layouts/PortalLayout.tsx` file.

Rollback:

- Revert the viewer late-attachment, UI source guard, version defaults, map update and this entry
  together; Full Control input will again require collaboration to predate viewer creation.

## 2026-08-24 - Preserve screen sharing through file-relay completion

Task:

- Prevent a screen-sharing session from disconnecting after a file transfer completes.

Changed:

- Keep the authenticated signaling heartbeat as the single liveness mechanism. Disabled the
  redundant ClientWebSocket PING timeout, whose PONG cannot be read while bounded file completion
  processing is validating the received file.
- Defer an application heartbeat failure while authenticated file completion receive work owns
  the shared WebSocket, then continue normal heartbeat processing when that bounded work ends.
- Added loopback and unit regressions that hold file completion work longer than the heartbeat
  window and verify signaling stays registered without entering reconnect.
- Advanced Windows development, public-pilot and portable-support defaults to 0.9.45.

Validation:

- Signaling targeted regression passed 2/2; full Signaling suite passed 115/115 with four normal
  opt-in Docker/distributed skips. Application passed 138/138.
- Built and payload-validated matching x64 and ARM64 0.9.45 installers. Their SHA-256 values are
  x64 `484207651f4612a98d8f200d8894160e537175f33dd3bc97fa4ef42861e55151` and arm64
  `04a67054a391ed2f55990cb8d17a1b99cae623db4e815d762c2c10146e9321d0`; both package URLs and
  the checksum manifest return HTTP 200, and the local preview selects checksum-verified 0.9.45.

Risk:

- This preserves the existing bounded file backpressure. A genuinely failed socket is still
  detected by the authenticated application heartbeat after relay work yields; physical
  two-device verification remains required.

Rollback:

- Revert the WebSocket heartbeat/relay accounting, regressions, version defaults, map update and
  this entry together; the redundant PING timeout will again risk closing screen sharing during
  slow file completion processing.

## 2026-08-24 - Remove media pacing from the separate file relay

Task:

- Restore LAN file-transfer throughput toward the 1 GiB/minute target without allowing file
  traffic to block interactive input or the direct media path.

Changed:

- Route negotiated authenticated file-relay chunks before the media-path pacer; the relay has its
  own WebSocket backpressure and no longer inherits the live-video 1 Mbit/s/256 Kbit/s fallback.
- Keep the direct P2P file path's bounded window, input preemption, media reservation and poor-link
  throttle. A stable 4K session with no available-bandwidth estimate now keeps those reservations
  without inventing a 1 Mbit/s direct-transfer cap.
- Advanced Windows development, public-pilot and portable-support defaults to 0.9.43.

Validation:

- Relay serialization/no-media-pacing regression passed 1/1.
- Transfer-allocation regression set passed 4/4, including 4K reservation, poor-network throttle,
  file-only behavior and the no-synthetic-cap case.
- Real authorized data-only file-transfer integration passed 1/1; the full Application suite
  passed 138/138.
- Built and payload-validated the matching x64 and ARM64 0.9.43 installers. Their SHA-256 values
  are x64 `57d5e20ef3a36bc68438c8ebe4be4225ef849cf6226121e4955db90271026913` and ARM64
  `024b7a38ae51f48ce0e296c866365c779f4bf1449b011a3cc5932d97f728548b`; both package URLs and
  the checksum manifest return HTTP 200, and the preview selects checksum-verified 0.9.43.

Risk:

- This removes an artificial application limit; actual throughput still depends on both endpoint
  NIC/Wi-Fi speed, storage, WSS relay path and any physical network contention. It does not claim
  a universal 1 GiB/minute guarantee before a two-device measurement.

Rollback:

- Revert the relay route ordering, stable-unmeasured allocation branch, regression tests, version
  defaults and this entry together; relay chunks will again receive media-path pacing.

## 2026-08-24 - Repair LAN signaling release configuration and publish 0.9.42

Task:

- Resolve the 0.9.41 desktop client showing `Disconnected` immediately after installation.

Changed:

- The Phase 3 LAN controller now derives certificate-covered, DNS-resolvable
  `signal.<LAN-IP>.sslip.io` and `turn.<LAN-IP>.sslip.io` hostnames instead of falling back to
  nonexistent `*.peeronq.com` or multi-label `.localhost` addresses.
- Website MSI publication now fails closed unless the build explicitly supplies a reachable LAN
  `wss://.../ws` endpoint and matching scoped development root through
  `-PublishLanWebsiteDownloads`.
- Advanced the Windows development, public-pilot, and portable-support defaults to 0.9.42 and
  published x64/ARM64 0.9.42 installers compiled for
  `wss://signal.10.0.0.10.sslip.io:5443/ws` with the matching pinned root certificate.

Validation:

- The corrected Phase 3 Docker stack is healthy at
  `https://signal.10.0.0.10.sslip.io:5443/health/ready`.
- Both 0.9.42 MSIs passed administrative payload validation. Their published SHA-256 values are
  x64 `d7400271ee0d84d1c7e5a092a0a0ad959faa02f9174ccda7d63ab1c0ab5070a5` and ARM64
  `7bcc2e649c2971fec74617bc4ab38d857c8b554ebc66b91b21ac5c559b71f1b9`; each package and the
  checksum manifest return HTTP 200 from the restarted local website preview.
- A live pinned-WSS two-client registration, signaling-container restart, automatic
  reauthentication and session re-establishment acceptance passed (1/1).

Risk:

- These are unsigned LAN-development installers, not public production releases. They only work
  while the trusted LAN can resolve and reach `10.0.0.10` on the configured signaling/TURN ports.
- Creating the required Windows Private/LocalSubnet inbound firewall rules still requires an
  Administrator PowerShell session; the controller reported that this session was not elevated.

Rollback:

- Stop the Phase 3 stack with the same `-BindAddress`, restore the prior controller/build-script
  behavior and republish the preceding verified development pair if this LAN deployment is
  withdrawn. No public DNS, certificate-store machine-wide trust, or production endpoint changed.

## 2026-08-24 - Publish 0.9.41 unsigned development installers

Task:

- Create and commit the next local Windows development version after the file-transfer stability,
  observability-startup and native QUIC migration work.

Changed:

- Advanced the development/default build version from 0.9.40 to 0.9.41 in the Windows builder,
  public-pilot builder, Portable Support builder and local development instruction.
- Built and payload-validated the matching x64 and ARM64 0.9.41 unsigned development MSIs, then
  published their matching `SHA256SUMS.txt` to the local website downloads directory.

Validation:

- `PeerOnQ-0.9.41-unsigned-development-x64.msi` SHA-256:
  `ccb3055c04f417d251025db7c5bd3a68d925dd86695140b105e7709b2ce5932c`.
- `PeerOnQ-0.9.41-unsigned-development-arm64.msi` SHA-256:
  `4142a57e3f08f769347c7102d5a1abaa5f095dfb09b051ec5a25617cabaf3c16`.
- Both installer payload validations passed. The local website preview was restarted, selected the
  checksum-verified 0.9.41 pair, and both download URLs returned HTTP 200.
- The Release solution build passed with zero warnings/errors. Full solution tests had one
  timing-sensitive Application reconnect assertion fail under concurrent test load; the exact test
  and the complete Application project rerun both passed (138/138). All other solution projects
  passed; four Docker-dependent signaling tests and one live-TURN media test were skipped.

Risk:

- These are unsigned development installers and must not be treated as a production release.
- The binary downloads and checksums are intentionally ignored by Git; this commit records the
  reproducible source, version defaults, tests and release evidence rather than 150+ MiB of MSI data.

Rollback:

- Re-publish the previous checksum-verified MSI pair, restore the four default version values, and
  revert this entry with the associated source changes if the development build must be withdrawn.

## 2026-08-24 - Start the native QUIC data-plane migration

Task:

- Inspect and baseline the active remote-desktop pipeline, then begin the production migration from
  WebRTC-primary transport to independent low-latency QUIC channels without advertising incomplete
  behavior or creating a new package version.

Changed:

- Added a platform-independent protocol-v1 channel contract for dedicated mouse, keyboard, control,
  screen, audio, clipboard, file and telemetry delivery/priority semantics.
- Added a real TLS 1.3 managed QUIC listener/connection over the platform MsQuic runtime with mutual
  certificate pinning, bounded protocol framing/queues, independent reliable streams and parallel
  file lanes. The adapter explicitly reports no DATAGRAM support because .NET 10 does not expose it.
- Added a dedicated transport test project and migration document. Windows does not advertise or
  select native QUIC yet; WebRTC remains active until native MsQuic DATAGRAM and routing gates pass.

Validation:

- `PeerOnQ.Transport` Release build passed with zero warnings/errors.
- Real two-peer QUIC loopback tests passed 4/4: mutual authentication and pin rejection, ordered
  dedicated channels, parallel file lanes/input independence, and delivery/size enforcement.
- Application tests passed 138/138 after one timing-sensitive parallel-transfer test was confirmed
  by an isolated rerun; the full .NET solution Release build passed with zero warnings/errors.

Risk:

- This is a source-only foundation, not a completed primary transport. QUIC DATAGRAM, P2P/STUN/path
  racing, relay, live session integration, hardware codec/GPU rendering and physical benchmarks are
  still explicit acceptance blockers.

Rollback:

- Remove `RemoteSessionTransport.cs`, `PeerOnQ.Transport/DataPlane`, the transport test project and
  solution entry, this migration document, and the related map/changelog rows.

## 2026-08-24 - Preserve remote control while file traffic uses spare bandwidth

Task:

- Diagnose the post-transfer screen freeze/disconnect from the supplied sanitized diagnostics and
  keep 4K video plus mouse/keyboard responsive while file transfer consumes only spare capacity.

Changed:

- Serialize protected records per input, clipboard, and file channel before assigning secure
  sequence numbers. Concurrent file sends can no longer reach relay transport out of order, while
  a blocked file write cannot hold the input lane and trigger `AuthenticationMismatch` teardown.
- Reserve adaptive video capacity plus an explicit interactive/security margin, pace relay and
  direct file chunks through a dynamic token bucket, and shrink file rate/buffering first on fair
  or poor network health. File-only sessions retain normal link use without an invented video cap.
- Expose only bounded inferred media headroom, refresh native bandwidth descriptions, and document
  the scheduling flow. The disconnect-ordering fix was packaged and website-verified as 0.9.40
  before the user requested that further packaging be deferred; the bandwidth scheduler remains
  source-only and no later version was created.

Validation:

- Application tests passed 138/138, including secure relay ordering, canceled pacing without a
  record gap, input preemption, and relay transfer completion without session teardown.
- Media tests passed 81/81 with one live-TURN environment test skipped; the real WebRTC 8 MiB
  backpressure loopback kept input responsive and completed. Windows Release build passed with
  zero warnings and zero errors.

Risk:

- SIPSorcery does not expose TWCC capacity, so spare bandwidth is a bounded inference from active
  bitrate, loss, RTT, jitter, and adaptive target. Real link loss or outage cannot be made
  freeze-proof; the policy responds conservatively by throttling file traffic first.
- The website's 0.9.40 installers contain the disconnect-ordering fix but not the later source-only
  bandwidth scheduler; packaging is intentionally deferred until the user's remaining tasks land.

Rollback:

- Revert the channel send gates, adaptive allocation/pacer, bounded headroom estimate, tests, UI
  copy, map entry, version-default edits, and this changelog entry together.

## 2026-08-24 - Prevent local observability address collisions

Task:

- Restore the click-to-start development workspace when existing Docker networks prevent the
  OpenTelemetry collector from starting and no browser pages open.

Changed:

- Move the collector's reserved observability address from `172.29.61.11` to
  `172.29.61.10`, update Fluent Forward senders and the HA overlay, and constrain dynamic
  observability addresses to `172.29.61.128/25` in development and staging.
- Add a deployment regression that keeps the collector address outside the dynamic pool.

Validation:

- Development Compose configuration passed. The focused observability regression passed 1/1.
- `peeronq-start.bat` completed with exit code 0; Phase 3 and every Phase 6 health check are ready,
  the collector is running at `172.29.61.10`, and the public site, Admin, Cloud API, Portal,
  Grafana and Prometheus returned HTTP 200.
- Full staging rendering remains intentionally blocked in the local environment because the
  required production SMTP host is not configured.

Risk:

- Compose may recreate the observability network and briefly restart attached development
  containers while applying the corrected IPAM. Persistent volumes and exposed ports are unchanged.

Rollback:

- Revert the Compose address/IPAM changes, regression test and this entry together. A persisted
  observability network may then allocate the collector address to another service again.

## 2026-08-23 - 0.9.38 relay completion acknowledgement ordering

Task:

- Keep the remote-desktop screen session alive while a file transfer reaches its final
  receiver-side verification and acknowledgement step.

Changed:

- The receiving peer now sends the authenticated completion acknowledgement before it publishes
  the transfer as complete or drops that transfer's cryptographic context. Both peers therefore
  reach the same terminal state only after the acknowledgement has entered the secure transport.
- Added an encrypted relay end-to-end regression that transfers a multi-record file, verifies the
  final bytes on the receiver, and asserts both secure collaboration transports remain ready.
- Bumped the development client build defaults to 0.9.38 and corrected the relay record limit in
  the routing map.

Validation:

- Focused secure transport tests passed: 19/19; complete file-transfer application tests passed: 46/46.
- The desktop Release build passed with zero warnings or errors. The x64 and ARM64 0.9.38 MSI
  packages passed the build script's installer validations, and each package plus `SHA256SUMS.txt`
  is available from the local website download endpoint with HTTP 200.

Risk:

- The completion acknowledgement remains subject to the authenticated relay's normal network
  delivery guarantees; a real network interruption still pauses/fails the file transfer safely
  rather than reporting it complete.

Rollback:

- Revert the acknowledgement ordering, relay completion regression, map/default-version entries,
  and the paired 0.9.38 client packages together.

## 2026-08-23 - 0.9.37 relay record capacity for large file transfers

Task:

- Prevent an authenticated file transfer from failing with `ArgumentOutOfRangeException` for
  `payload` when a legal encrypted collaboration record exceeds the old relay envelope.

Changed:

- Expanded the bounded opaque relay record from 384 KiB to 1,040 KiB, sufficient for the largest
  legal encrypted collaboration record while retaining bounded per-record memory and no total file
  size or product bandwidth limit.
- Added codec and two-client signaling regressions at the exact relay-record boundary.

Validation:

- Targeted relay codec and bidirectional authenticated signaling tests passed: 4/4.
- Full signaling suite passed: 113/113; 4 Docker/distributed opt-in tests were skipped.
- Release builds of the signaling server and native client passed with zero warnings or errors.
- x64 and ARM64 0.9.37 MSI packages passed WiX/ICE03 validation with zero warnings or errors.
- Both published MSI packages and `SHA256SUMS.txt` returned HTTP 200; each package checksum matched
  the manifest.
- The rebuilt local signaling, TURN and proxy stack is healthy at `signal.peeronq.com`.

Risk:

- Both endpoints and the signaling server must use the 0.9.37 relay envelope before transfers
  larger than the legacy record capacity are attempted.

Rollback:

- Revert the relay-envelope constant, boundary tests, server deployment, and 0.9.37 package pair
  together.

## 2026-08-23 - 0.9.36 full-control drag/drop transfer delivery

Task:

- Make drag/drop and copied-file transfer usable inside an approved Full Control viewer session.

Changed:

- Attach the file-transfer service to an already-open viewer when the encrypted collaboration
  channel becomes ready after the viewer window is created.
- Treat the remote owner's existing Full Control approval as file-transfer consent and receive
  incoming offers directly in `Downloads\\PeerOnQ` with safe rename-on-collision behavior. View
  only and standalone file-transfer scopes retain their explicit receive destination flow.

Validation:

- Native WinUI project built successfully with zero warnings or errors.
- Application test suite passed: 136/136.
- x64 and ARM64 0.9.36 MSI packages passed WiX/ICE03 validation with zero warnings or errors.
- Published download packages and `SHA256SUMS.txt` both returned HTTP 200; each package checksum
  matched the published manifest.

Risk:

- Full Control intentionally permits remote-originated files to arrive in the dedicated local
  Downloads folder; existing permission gates, bounded transfer transport and malware scanning
  remain in effect.

Rollback:

- Revert the late viewer attachment, automatic Full Control receive flow and 0.9.36 package pair
  together.

## 2026-08-23 - 0.9.35 preserve High Quality video resolution

Task:

- Prevent a healthy remote desktop session from being reduced to a blurry sub-1080p image when
  the user selected High Quality.

Changed:

- Made the dashboard's default High Quality selection explicitly request 4K/UHD, bounded by the
  physical source display without upscaling, and raised its configured video ceiling to 36 Mbps.
- Kept High Quality's requested resolution stable during adaptation; it now reduces frame rate
  before pixels. Other profiles retain the existing resolution-downscale ladder for responsiveness.
- Stopped treating a low encode rate from a static desktop as a bandwidth measurement; loss,
  jitter, RTT, and encoder backpressure still reduce quality when they show real pressure.

Validation:

- Targeted media regression tests passed 2/2; the full media suite passed 77/77, with one
  explicitly opt-in live TURN test skipped.
- Native WinUI project built successfully with zero warnings or errors.
- 0.9.35 x64 and ARM64 MSI builds passed WiX/ICE03 validation with zero warnings or errors.
- Both installers and `SHA256SUMS.txt` were published to the local website; all three download
  endpoints returned HTTP 200 after the preview restart and both SHA-256 values matched.

Risk:

- The physical source display still bounds detail; a 720p source is not artificially upscaled.
  Very constrained links now trade frame rate for High Quality clarity rather than reducing pixels.

Rollback:

- Revert the High Quality profile, adaptive resolution guard, bandwidth-estimate guard, tests and
  0.9.35 package pair together.

## 2026-08-23 - 0.9.34 remote-selected attended access

Task:

- Simplify the native connection screen and let the remote computer choose View only or Full
  control for standard attended sessions.

Changed:

- Removed Dashboard connection-mode buttons; attended connections request the protected
  full-control envelope and default to the High quality profile.
- Added a remote approval choice with a least-privilege View only default. Full control grants
  screen view, input, and bidirectional file transfer; clipboard remains separate.
- Bound the selected scope atomically in the signaling server and propagated it to both native
  session runtimes before media, input, or file-transfer services start. Missing or invalid scope
  confirmation fails closed.

Validation:

- Application tests passed 136/136 and signaling tests passed 112/112 (4 Docker/Redis tests skipped).
- Native WinUI project built successfully with zero warnings or errors.
- 0.9.34 x64 and ARM64 MSI builds passed WiX/ICE03 validation with zero warnings or errors.
- Both installers and `SHA256SUMS.txt` were published to the local website; all three download
  endpoints returned HTTP 200 after the preview restart.
- `https://signal.peeronq.com:5443/health/ready` returned HTTP 200 (`{"status":"ready"}`).

Risk:

- Both laptops and the signaling server must use 0.9.34 for this new attended-selection flow;
  a missing scope confirmation is deliberately rejected rather than granting full control.

Rollback:

- Revert the remote-scope signaling fields and the 0.9.34 package pair together.

## 2026-08-23 - 0.9.33 bounded file-relay receiver backpressure

Task:

- Prevent a high-throughput relay transfer from overflowing the remote client's receive work and
  interrupting the active remote-control session.

Changed:

- The binary relay receive event now waits for authenticated decrypt and collaboration dispatch.
- File transfer processing now serializes incoming bulk chunks and completion records before
  returning control to the relay receive loop, making TCP apply backpressure at the receiver's
  real storage rate without serializing independent transfer starts.
- Authorization, hybrid encryption, immutable session permissions, chunk size, and file quotas are
  unchanged; the transfer has no artificial bandwidth cap.

Validation:

- Full Application tests passed 134/134, including the new relay receiver-backpressure regression.
- The 0.9.33 x64 and ARM64 unsigned MSI packages passed payload and ICE03 validation.
- Both 0.9.33 website download endpoints returned HTTP 200 after the preview restart.
- The local signaling/TURN/proxy stack remained ready at `signal.peeronq.com:5443`.

Risk:

- A slow receiving disk now slows transfer throughput rather than risking unbounded memory or a
  dropped connection; a physical two-device large-file run remains required.

Rollback:

- Revert the synchronous relay/file-dispatch backpressure changes and the 0.9.33 package pair
  together.

## 2026-08-23 - 0.9.31 high-throughput bidirectional file relay

Task:

- Remove the WebRTC SCTP small-burst bottleneck from authorized file transfer and allow both
  connected peers to transfer files at available network and disk capacity.

Files changed:

- `src/PeerOnQ.{Application,Transport,Signaling.Server}/` file-transfer, session, WebSocket relay,
  capability and distributed-routing boundaries
- File relay/session-security/application/signaling regression tests
- `ROUTES_MAP.md`, `PROJECT_MAP.md`, `docs/SECURE_TRANSPORT.md`, `PHASE4.md`, and Windows build defaults

Reason:

- The prior SCTP data-channel sender emits only a small burst per timer interval, which limited a
  local 8 MiB transfer to approximately 174 KiB/s despite an available high-speed link.

Validation:

- Zero-warning Release builds of the signaling server and WinUI client passed.
- The authenticated two-client WebSocket integration test routes 256 KiB opaque records in both
  directions; hybrid transport tests prove file records bypass the direct ICE requirement only when
  the negotiated relay is present.
- Both unsigned 0.9.31 Windows installer architectures passed MSI payload and ICE03 validation,
  were published to the local website, and their download endpoints returned HTTP 200.
- The rebuilt local signaling, TURN, and proxy containers reported healthy; the ready endpoint
  returned `{"status":"ready"}`.
- Corrected the server-side ICE issuance gate to allow the accepted `Negotiating` state. ICE is
  requested before the first SDP offer makes a session `Active`; rejected, ended, non-owner, and
  non-participant sessions remain denied. This is a server-only correction and does not require a
  new client installer.

Risk:

- The maximum speed is bounded by the two devices' Internet paths, the signaling server bandwidth,
  and receiver disk performance; an external two-device 1 GiB transfer measurement is still needed.

Rollback:

- Revert the `file.relay.v1` capability, binary WebSocket envelope/route, persistent writer change,
  0.9.31 package pair, and website download manifest together.

## 2026-08-23 - Publish Windows 0.9.30 Full Control file transfer

Task:

- Let an accepted Full Control session transfer files dropped onto the viewer or pasted from the
  local clipboard, and use available link capacity for authorized transfers by default.

Files changed:

- `src/PeerOnQ.Domain/Sessions/Phase1SessionScope.cs`
- `src/PeerOnQ.Domain/Sessions/SessionPrimitives.cs`
- `src/PeerOnQ.Transport/WebSocketSignalingClient.cs`
- `src/PeerOnQ.Application/Sessions/SessionCoordinator.cs`
- `src/PeerOnQ.App/{PermissionDialogHost,ViewerWindow,MainWindow}.{xaml,cs}`
- Native/domain/application regressions and website download-version checks
- Windows development/public-pilot/portable-support version defaults, `PROJECT_MAP.md`, and
  `AI_CHANGELOG.md`

Reason:

- Full Control did not include the separately protected file-transfer capability, the viewer had
  no secure drop/paste route, and Full Control started every transfer in the smallest 256 KiB bulk
  window.

Validation:

- `dotnet build src/PeerOnQ.App/PeerOnQ.App.csproj -c Release --no-restore` passed with no
  warnings or errors.
- Domain tests passed 64/64; application tests 132/132; media tests 75/75 (one live TURN test is
  intentionally skipped); the focused website route suite passed 38/38.
- Both x64 and ARM64 `0.9.30` installers passed payload/ICE03 validation and were published with
  SHA-256 `2726828e8f3433233334ea3bcd79590e2350a7985055a43ab4b0994708f507af` and
  `2aef322cab3b7fcc20454b45b1dfadf9bb0aa788c4b24f5c3ee1827093ff90de` respectively. The local
  website preview selects `0.9.30`; both package URLs returned HTTP 200.

Risk:

- The receiving user must still explicitly accept the session and each file-transfer offer. The
  application applies no bitrate cap, but retains a bounded data-channel buffer so a transfer
  cannot exhaust memory; input and security messages remain higher priority. Legacy Full Control
  sessions without FileTransfer remain valid but do not receive the new drop/paste capability.

Rollback:

- Revert the permission-profile, viewer event wiring, priority default, tests, and `0.9.30`
  download manifest as one change; then republish the preceding checksum-verified package pair.

## 2026-08-23 - Keep protocol prefixes out of session history

Task:

- Remove the internal `LNK-` prefix from every user-facing active-session and Session History ID.

Files changed:

- `src/PeerOnQ.Domain/Identity/PeerOnQId.cs`
- `src/PeerOnQ.App/MainWindow.xaml.cs`
- `tests/PeerOnQ.Domain.Tests/PeerOnQIdTests.cs`
- `scripts/windows/test-phase5-native-ui.ps1`
- `AI_CHANGELOG.md`

Reason:

- Legacy masked audit values cannot be parsed as a complete ID, so the prior Session History
  fallback displayed the protocol-only `LNK-` prefix.

Validation:

- Domain regression covers full, masked, and already user-formatted stored IDs.

Risk:

- This changes only presentation; persisted audit records and CSV export retain their canonical
  masked value.

Rollback:

- Revert the display formatter usage and its regression together.

## 2026-08-23 - Publish Windows 0.9.27 LAN development downloads

Task:

- Publish the current DNS-configured LAN client as version `0.9.27` through the website's single
  device-aware Windows download action.

Files changed:

- `scripts/windows/build-phase5-development.ps1`
- Windows development/public-pilot/portable-support version defaults and Phase 3 build guidance
- `artifacts/peeronq/public/downloads/PeerOnQ-0.9.27-unsigned-development-{x64,arm64}.msi`
- `artifacts/peeronq/public/downloads/SHA256SUMS.txt`
- `artifacts/peeronq/src/test/routeSeparation.test.tsx`
- `PROJECT_MAP.md`
- `AI_CHANGELOG.md`

Reason:

- The previous generic `0.9.26` website package used a local-only default signaling endpoint and
  could remain disconnected from the active LAN server.

Validation:

- Built and installer-payload-validated the x64 (78,520,320 bytes) and ARM64 (74,194,944 bytes)
  MSIs for `wss://signal.peeronq.com:5443/ws`.
- Published SHA-256 values are x64
  `20a05cc342b94a144dc2288e2fd28271209b24fdf75412d10cd3c1d5ae124337` and ARM64
  `d6c831a88be1459f4db9574bd5e593b999a0ef64bb3d15966f22a8a8a2864c29`; both website URLs return
  HTTP 200 after the automatic preview restart.
- The scoped root verifier returned HTTP 200 for the signaling readiness endpoint, and the focused
  website route suite passed 38/38.

Risk:

- This remains an unsigned, controlled LAN-development release. Its TLS development root is pinned
  inside the app package only; it does not install or require a Windows root certificate. It must
  be replaced by the signed public-release pipeline before Internet production distribution.

Rollback:

- Restore the prior complete checksum-verified package pair and manifest, then restart the website
  preview. Do not replace the DNS endpoint with an IP literal or weaken TLS validation.

## 2026-08-23 - Preserve confirmed fixes in future website releases

Task:

- Record the standing release rule that every new website-distributed version is published to the
  website and that confirmed fixes are not changed without an explicit user request.

Files changed:

- `AGENTS.md`
- `AI_CHANGELOG.md`

Reason:

- Version publication and preservation of previously approved work must be enforced as repository
  instructions, not depend on repeated reminders.

Validation:

- Reviewed the existing version-publication invariant and added the complementary confirmed-fix
  preservation rule directly beside it.

Risk:

- A future change that genuinely requires modifying a confirmed area must document why that
  interaction is necessary; unrelated redesigns remain disallowed.

Rollback:

- Remove this entry and the `Confirmed Fix Preservation` section from `AGENTS.md`.

## 2026-08-23 - Enforce website publication for every development MSI version

Task:

- Make versioned Windows development package publication automatically update and verify the local
  device-aware website, and record the rule for future work.

Files changed:

- `AGENTS.md`
- `scripts/windows/build-phase5-development.ps1`
- `PROJECT_MAP.md`
- `AI_CHANGELOG.md`

Reason:

- Building a new MSI without restarting and verifying the website preview left users with an older
  download even though a newer package existed locally.

Validation:

- The build script now restarts the scoped preview and fails unless both new x64/ARM64 download
  URLs return HTTP 200 after publication.

Risk:

- A website-preview startup failure now blocks a website-published development build instead of
  allowing an unverified version to be reported as ready.

Rollback:

- Restore the prior website-copy block and remove this release-publication invariant.

## 2026-08-23 - Publish Windows 0.9.26 development downloads

Task:

- Package the healthy-session heartbeat fix in the next Windows development version and make the
  local device-aware website select the matching x64 and ARM64 installers.

Files changed:

- Windows development, public-pilot, and portable-support build defaults
- Phase 3 build guidance, deployment examples, and local download route tests
- `artifacts/peeronq/public/downloads/PeerOnQ-0.9.26-unsigned-development-{x64,arm64}.msi`
- `artifacts/peeronq/public/downloads/SHA256SUMS.txt`
- `AI_CHANGELOG.md`

Reason:

- The source fix cannot alter an already installed 0.9.25 executable. A versioned package gives
  both LAN test devices the same verified client payload without overwriting an existing MSI.

Validation:

- Both MSI payloads, their SHA-256 manifest, and the device-aware download flow are validated with
  this release.

Risk:

- These are unsigned development installers for controlled LAN testing only; they are not a public
  production release or an automatic-update channel.

Rollback:

- Restore the previous verified 0.9.25 MSI pair and checksum manifest, then restart the local
  website controller.

## 2026-08-23 - Keep healthy remote sessions connected

Task:

- Prevent a healthy established remote session from being dropped by the Windows client's
  unnecessarily short signaling heartbeat deadline.

Files changed:

- `src/PeerOnQ.App/AppServices.cs`
- `AI_CHANGELOG.md`

Reason:

- The local-development client considered signaling unavailable after 12 seconds even though the
  signaling server allows 45 seconds. This could create a false disconnect while neither user had
  ended the session and the connection was otherwise healthy.

Validation:

- Focused signaling tests and the Windows app build are run with this change.

Risk:

- A genuine dead signaling connection can take up to 45 seconds to be declared unavailable.
  Explicit session end, input revocation, and authenticated reconnect limits are unchanged.

Rollback:

- Restore the previous conditional heartbeat timeout values.

## 2026-08-23 - Keep approved Full Control active without a resume action

Task:

- Remove the viewer's manual `Resume control` overflow action and restore control automatically
  after safe focus/monitor transitions or re-entry to the remote image for an approved active Full
  Control session.

Files changed:

- `src/PeerOnQ.App/ViewerWindow.xaml{,.cs}`
- `scripts/windows/test-phase5-native-ui.ps1`
- `PROJECT_MAP.md`, `docs/SECURITY.md`, `AI_CHANGELOG.md`

Reason:

- The viewer paused input after safely releasing held keys/buttons, then exposed a redundant manual
  recovery action. The active session already has authenticated Full Control permission and can
  request a fresh focus generation automatically when the viewer is active again.

Validation:

- Native build and focused UI source assertions are run with this change.

Risk:

- The automatic recovery still requires an authenticated active session and a host focus
  acknowledgment. Disconnect, session end, input failure, and remote-owner revocation remain
  fail-closed.

Rollback:

- Restore the secondary command and the prior manual recovery handler.

## 2026-08-23 - Show clean peer IDs in Session History

Task:

- Remove the legacy `LNK-` protocol prefix from the user-facing Session History list.

Files changed:

- `src/PeerOnQ.App/MainWindow.xaml.cs`
- `tests/PeerOnQ.Domain.Tests/PeerOnQIdTests.cs`

Reason:

- Session audit records preserve the canonical protocol ID, but the WinUI list rendered it directly
  while the current-session UI already uses the intended prefix-free masked display value.

Validation:

- Regression coverage asserts the prefix-free masked PeerOnQ ID form.

Risk:

- Malformed legacy audit values remain visible as-is rather than being guessed or altered.

Rollback:

- Restore the direct Session History use of `entry.PeerMaskedId`.

## 2026-08-23 - Prioritize interactive latency in LAN sessions

Task:

- Remove the LAN-development startup override that forced the most demanding media profile and
  apply the measured latency-QoS controller to every interactive profile.

Changed:

- LAN/dev startup now keeps the existing `Automatic` profile selected. It starts at a balanced
  30 FPS / 4 Mbps target and uses measured loss, jitter, RTT, and local-backpressure.
- Manual profiles retain their selected maximum, but now reduce frame rate before resolution when
  those measurements show congestion. Full Control starts with the narrowest existing bulk-transfer
  window (256 KiB) and the quality loop evaluates every second, preserving input/video
  responsiveness while retaining the user's in-session override.

Validation:

- Targeted media-profile inspection confirms `Performance` is 60 FPS / 12 Mbps while `Automatic`
  is the adaptive 30 FPS / 4 Mbps profile. A regression test covers manual Performance keeping
  that maximum while enabling the latency-QoS controller. No package, installer, website download,
  DNS, or signaling configuration was changed.

Risk:

- A user who requires 60 FPS must select `Performance` manually. This change cannot improve delay
  caused by Wi-Fi interference or a relay path, but a manual profile will now react to measured
  congestion rather than continue to build latency.

Rollback:

- Restore the one LAN-only `QualityPicker.SelectedIndex = 3` startup assignment.

## 2026-08-23 - Correct the LAN signaling port in Windows 0.9.25

Task:

- Replace the website's 0.9.24 LAN client, which was compiled for default HTTPS port 443, with a
  matching package for the active Phase 3 WSS listener on port 5443.

Files changed:

- Windows development/portable/public-pilot version defaults and Phase 3 build guidance
- Website download-route regression fixture, deployment guide, project map, and changelog

Reason:

- The active LAN controller is healthy at `wss://signal.peeronq.com:5443/ws`, but the 0.9.24
  package was built with an endpoint lacking `:5443`; it therefore attempted port 443 and stayed
  disconnected. DNS resolution and the scoped TLS health check both succeeded.

Validation:

- The LAN controller reports DNS `signal.peeronq.com -> 10.0.0.10`, scoped TLS health `ready`, and
  healthy proxy, signaling and TURN containers on the expected port 5443.
- Built x64/ARM64 `0.9.25.0` MSIs, passed installer payload validation for both architectures,
  verified both SHA-256 values, and confirmed each published application payload embeds
  `wss://signal.peeronq.com:5443/ws`.
- Published the verified pair and manifest to the local website preview. After restart, its
  controller selected `0.9.25`; both package URLs return HTTP 200.

Risk:

- 0.9.24 remains unsuitable for this LAN stack. The replacement is an unsigned, development-only
  package and must not be used as a public production release.

Rollback:

- Restore a prior package compiled for the exact active WSS endpoint; do not change server TLS,
  certificate pinning, or firewall rules to accommodate an incorrectly compiled client.

## 2026-08-23 - Reduce active-session latency and simplify remote-control chrome

Task:

- Reduce avoidable Full Control render delay and make the viewer/host session controls less
  intrusive without removing the local owner's direct stop path.

Files changed:

- `src/PeerOnQ.Media/WebRtcMediaSession.cs`
- `src/PeerOnQ.App/{ViewerWindow,SharingIndicatorWindow}.xaml{,.cs}`
- `src/PeerOnQ.App/MainWindow.xaml.cs`
- `scripts/windows/test-phase5-native-ui.ps1`
- Windows development/portable/public-pilot build defaults and Phase 3 LAN build guidance
- `artifacts/peeronq/public/downloads/PeerOnQ-0.9.24-unsigned-development-{x64,arm64}.msi`
- `artifacts/peeronq/public/downloads/SHA256SUMS.txt`
- Website download-route regression fixture
- `docs/PERFORMANCE_REPORT.md`
- `PROJECT_MAP.md`
- `AI_CHANGELOG.md`

Reason:

- The latest-frame encoder queue still woke only at its next frame-period poll, adding avoidable
  delay after a new capture frame. The active Full Control toolbar exposed a redundant pause
  control, fullscreen required a keyboard shortcut to discover its exit control, and the host
  indicator stayed top-left with permanently exposed actions.

Validation:

- WinUI Debug build completed with 0 warnings/errors; focused source assertions, six
  `VideoFrameQueueTests`, and the WebRTC Full Control loopback test passed. The broader UI script
  is currently blocked before its assertions by the pre-existing missing
  `artifacts/peeronq/src/layouts/PortalLayout.tsx` file.
- Built and installer-validated matching x64/ARM64 `0.9.24` LAN development MSIs, then verified
  their SHA-256 values before publishing them to the local website download directory. The
  restarted website controller selected `0.9.24`; both MSI URLs and its manifest return HTTP 200.
- PeerOnQ frontend TypeScript validation completed without errors.

Risk:

- The encode wake removes a known local wait but cannot by itself guarantee network, decoder, Wi-Fi,
  or relay latency. Physical two-device LAN validation remains required.

Rollback:

- Revert this entry's media/window/UI/test/documentation changes together; retain the existing
  capture-exclusion and fail-closed input-release behavior.

## 2026-08-23 - Publish verified 0.9.23 LAN downloads to the local website preview

Task:

- Make the website's single device-aware download action resolve the current DNS-configured LAN
  development MSI pair rather than the prior `0.9.22` files.

Files changed:

- `artifacts/peeronq/public/downloads/PeerOnQ-0.9.23-unsigned-development-{x64,arm64}.msi`
- `artifacts/peeronq/public/downloads/SHA256SUMS.txt`
- `AI_CHANGELOG.md`

Reason:

- The local website controller chooses the highest complete checksum-verified pair from its static
  download directory. The current LAN pair had been built but was not yet copied there.

Validation:

- Restarted the local website preview; its controller selected version `0.9.23`.
- The x64 MSI, ARM64 MSI, and checksum manifest each return HTTP 200; both published hashes match
  the manifest.

Risk:

- These are unsigned, LAN-only development artifacts that pin the development WSS root. They must
  not be included in a public production website or treated as a production release.

Rollback:

- Restore the prior complete checksum-verified local package pair and restart the website preview.

## 2026-08-23 - Use DNS-only development endpoints in Windows 0.9.23

Task:

- Remove IP-derived client endpoint defaults from the LAN signaling controller and release a new
  test version that can use the same canonical PeerOnQ hostnames across environments.

Files changed:

- `scripts/windows/peeronq-phase3-local.ps1`
- `scripts/windows/PeerOnQ.LocalHttpsVerifier.cs`
- `tests/PeerOnQ.Signaling.Tests/DockerRestartAcceptanceTests.cs`
- `src/PeerOnQ.App/AppServices.cs`
- Windows development, Portable Support, and public-pilot build scripts
- Download integration test, development certificate test, deployment documentation, and project map

Reason:

- The generic 0.9.22 development client fell back to an IP-derived loopback hostname, while the
  active LAN deployment is DNS-routed through `signal.peeronq.com` and `turn.peeronq.com`.
- The controller also changed the Windows CurrentUser root store while its client already performs
  scoped development-root pinning, which can block certificate rotation without improving trust.
- Nginx does not reload a changed bind-mounted certificate unless its container is recreated. The
  scoped custom-root validation API is supplied by a small .NET verifier so it works from the
  repository's Windows PowerShell controller as well.

Validation:

- Built and MSI-validated the unsigned x64 and ARM64 `0.9.23` LAN kits for
  `signal.peeronq.com`; the client-facing TLS health check succeeded with the scoped root.
- Phase 3 acceptance passed: 108 signaling tests (4 intentional multi-node skips), 49 end-to-end
  tests, authenticated TURN allocation, UDP/TCP media relay, file transfer, and restart/re-auth.
- The live restart acceptance reads the same scoped root as the development app, so certificate
  rotation is covered without mutating a Windows certificate store. PeerOnQ's 61 frontend tests,
  frontend typecheck, and workspace typecheck passed.

Risk:

- LAN DNS must resolve `signal.peeronq.com` and `turn.peeronq.com` to the development server before
  controller restart; otherwise the fail-closed TLS health check prevents a misleading client build.

Rollback:

- Restart the Phase 3 controller with explicit prior DNS host arguments, restore the previous
  development package, and revert this entry with the endpoint/version changes.

## 2026-08-23 - Advance Windows development downloads to 0.9.22

Task:

- Make the current Windows client fixes available through one matching local-development version
  instead of leaving the website download flow at 0.9.21.

Files changed:

- `scripts/windows/build-phase5-development.ps1`
- `scripts/windows/build-phase11-portable-support.ps1`
- `scripts/windows/build-peeronq-public-pilot.ps1`
- `artifacts/peeronq/src/test/routeSeparation.test.tsx`
- `docs/DEPLOYMENT.md`
- `PROJECT_MAP.md`

Reason:

- The download preview selects the latest complete x64/ARM64 installer pair. Its default release
  version still pointed to the prior Windows build, while the domain-pinned public-pilot wrapper
  was still pinned to 0.5.1.

Validation:

- Built, installer-validated and SHA-256-verified x64 and ARM64 `0.9.22` MSI packages; the local
  preview returns HTTP 200 for both packages and their manifest. Focused download-link tests and
  the PeerOnQ frontend typecheck passed.

Risk:

- These are unsigned local-development artifacts. A LAN-specific build remains intentionally
  isolated, and an Internet-facing production release still requires the signed release pipeline.

Rollback:

- Restart the preview after restoring the prior complete package pair and its matching checksum
  manifest, then revert the version-default and test changes together.

## 2026-08-23 - Reduce Full Control render delay and UI noise

Task:

- Improve the two-device LAN Full Control experience without weakening the host's session-safety
  boundary.

Files changed:

- `src/PeerOnQ.App/ViewerWindow.xaml`
- `src/PeerOnQ.App/ViewerWindow.xaml.cs`
- `src/PeerOnQ.App/SharingIndicatorWindow.xaml`
- `src/PeerOnQ.App/SharingIndicatorWindow.xaml.cs`
- `scripts/windows/test-phase5-native-ui.ps1`
- `docs/PERFORMANCE_REPORT.md`
- `PROJECT_MAP.md`
- `AI_CHANGELOG.md`

Reason:

- The viewer expanded every 1080p BGR24 frame byte-by-byte on the UI thread, Full Control exposed a
  redundant checked `Control active` toolbar item, fullscreen retained aspect-reducing chrome, and
  the host safety disclosure occupied a 1180x96 critical-red strip.

Validation:

- The original and packed 1080p conversion produced identical output; the measured mean fell from
  3.507 ms/frame to 1.490 ms/frame on the local x64 device (2.35x).
- The unchanged VP8 encoder measured 3.82-4.55 ms mean and about 5.5 ms p95 at 1080p, within the
  16.67 ms 60 FPS budget, so no speculative codec/FPS downgrade was applied.
- `dotnet build src/PeerOnQ.App/PeerOnQ.App.csproj -c Debug --no-restore` passed with zero warnings
  and errors; targeted viewer/indicator regression assertions and `git diff --check` passed.
- The aggregate native UI script remains blocked before its assertions by its pre-existing missing
  `artifacts/peeronq/src/layouts/PortalLayout.tsx` dependency.

Risk:

- Packed unaligned reads are bounded by a scalar final pixel and validated for identical output.
  The host indicator remains always-on-top, excluded from capture, and preserves visible local
  revoke/end actions whenever tray/pass-through setup fails.

Rollback:

- Revert the five runtime/test files and the matching map/changelog entry together.

## 2026-08-23 - Harden the production public/private ingress boundary

Task:

- Align the single-node production bootstrap with the complete client-facing host set and document
  the exact NAT, private Admin and internal observability boundary.

Files changed:

- `scripts/linux/bootstrap-peeronq-production.sh`
- `src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml`
- `src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml`
- `src/PeerOnQ.Infrastructure.Deployment/.env.example`
- `tests/PeerOnQ.Observability.Tests/DeploymentConfigurationTests.cs`
- `docs/DEPLOYMENT.md`
- `PROJECT_MAP.md`

Reason:

- The bootstrap omitted the required Account Portal hostname, generated public website links with
  development port `8443`, and inherited a second proxy port binding; staging also omitted the
  Portal's named volumes, and NAT labels were easy to mistake for hostname access controls.

Validation:

- Targeted deployment configuration tests and merged production Compose validation.

Risk:

- Low; the patch corrects generated production host/port values and adds regression coverage without
  changing container topology or weakening the existing Admin CIDR restriction.

Rollback:

- Revert this entry and the seven files above; existing generated environments are unchanged until
  bootstrap is run again.

## 2026-08-22 - Rebuild and consolidate the public website

Task:

- Replace the primitive public marketing pages with a responsive, product-led PeerOnQ website and
  remove internal development tools from every public route.
- Explain the product strategy through four honest stages: complete the Windows workflow, make
  visible security a differentiator, preserve deployment freedom, and expand only with verified clients.
- Replace the Downloads card directory with one locally device-matched CTA that selects x64 or
  ARM64 without exposing fake links for unpublished platforms.
- Consolidate Product, Security, usage, strategy, FAQ, and download information into one focused
  home page; redirect the former marketing URLs to matching sections for backward compatibility.
- Remove public Sign in/account-portal links plus all repeated header, footer, hero, and conversion
  Download actions so the product page exposes exactly one device-matched installer action.
- Replace the repeated three-card/three-step rhythm with a session model, editorial product rows,
  a vertical security path, a four-stage strategy roadmap, and one automatic device-matched
  Download button without a surrounding metadata card.

Files changed:

- `artifacts/peeronq/src/layouts/PublicLayout.tsx`, `src/components/PublicMarketing.tsx`,
  `src/components/PlatformCard.tsx`, and `src/index.css`
- Public Home and device-aware download panel, public route redirects, Privacy, and Terms pages
- `artifacts/peeronq/src/App.tsx`, `src/layouts/DesktopPreviewLayout.tsx`, and public route tests
- `PROJECT_MAP.md`, `ROUTES_MAP.md`, and PeerOnQ frontend design/accessibility/architecture docs

Reason:

- The public site used isolated basic card layouts, a Windows-only primary CTA, and globally mounted
  Admin/Cloud/Grafana/Prometheus links. It did not present the real remote-access, collaboration,
  hybrid post-quantum security, open-source, or platform story at a competitive product standard.

Validation:

- PeerOnQ typecheck, lint, focused route tests, production build, and all 61 frontend tests pass.
- Desktop and exact 375 px device-emulated renders show no horizontal overflow; the Phase 6 live
  website returns HTTP 200 and exposes one device-selected Download control with no metadata card.
- Route tests confirm old marketing URLs resolve into the matching section, with no duplicate
  Download, retired three-step copy, or development-service action.

Risk:

- Public copy documents implemented architecture and current platform status; future platform or
  release availability still depends on the existing fail-closed download configuration.

Rollback:

- Revert this entry and the listed public frontend files; move `Phase6DevToolbar` back to `App.tsx`
  only if operational links are intentionally required above every surface again.

## 2026-08-22 - Make Admin data authoritative and repair local operations

Task:

- Remove synthetic acceptance data from the live development Admin console and make every exposed
  Admin menu query, metric, and publication control reflect an authoritative server state.

Files changed:

- `artifacts/peeronq-admin/src/`
- `src/PeerOnQ.Admin.Api/`, `src/PeerOnQ.Cloud.Application/`,
  `src/PeerOnQ.Cloud.Infrastructure/`, `src/PeerOnQ.Shared.Contracts/`
- `src/PeerOnQ.Infrastructure.Deployment/redis/entrypoint.sh`,
  `src/PeerOnQ.Infrastructure.Deployment/acceptance/redis-acl-smoke.sh`,
  `src/PeerOnQ.Infrastructure.Deployment/scripts/backup-postgres.sh`
- `tests/PeerOnQ.Observability.Tests/OperationalScriptSafetyTests.cs`

Reason:

- Admin search and column sorting were accepted by the API but several repository queries ignored
  them; empty measurement windows were displayed as 0% crash and 100% session success; disabled
  release publishers appeared actionable; and 12 acceptance identities polluted fleet totals.
- The Presence expiration worker lacked its exact Redis lease-key permission and node-exporter could
  not read the backup metric created under the backup process's private umask.

Validation:

- Admin frontend typecheck, 19 UI/API tests, and production build passed.
- Admin API build and Admin API, Cloud Infrastructure, and Observability tests passed (82 tests).
- Live HTTPS validation confirmed authenticated real totals, nullable unobserved rates, capability-
  controlled publishing, working audit search/sort, supported Presence expiry sort, and all project
  containers healthy. Post-fix Presence and node-exporter error scans returned zero matches.

Risk:

- Presence now explicitly rejects unsupported search/date/region/version sorting instead of silently
  ignoring it; the UI exposes only the authoritative lease-expiry order. Overview rate clients must
  accept `null` when the measurement window has no denominator.

Rollback:

- Revert this entry and the listed code changes together. The 12 deleted local acceptance devices,
  installations, and presence-history rows can be restored from the verified
  `peeronq-pre-admin-cleanup-20260822.dump` archive in the local PostgreSQL data volume.

## 2026-08-22 - Make Windows workspace launchers self-starting and bounded

Task:

- Fix start, stop and restart BAT entry points hanging after the Phase 3 LAN firewall message, and
  open every development panel after startup.

Changed:

- Auto-start the installed Docker Desktop application for complete-workspace start/stop/restart and
  wait with bounded, visible readiness progress instead of relying on a pre-running Docker Engine.
- Bound every Docker CLI readiness probe so an unavailable engine cannot leave the BAT window
  apparently frozen. Keep stop scoped to PeerOnQ containers, including project-labeled leftovers
  from prior HA runs, and preserve their volumes.
- Apply the existing Private/LocalSubnet Phase 3 firewall rules automatically only when the launcher
  is already elevated; non-elevated direct use continues without changing firewall state.
- Repair the native Phase 3 Development fallback with the explicit TOFU settings already allowed by
  the signaling validator.
- Generate stable repository-scoped ECDSA P-256 development attestation keys and recreate only
  containers whose secret mounts still point at invalid or old workspace locations; named data
  volumes remain preserved.
- Exclude generated `app-updates` release packages from the Docker build context.
- Keep one post-health page list for the public website, Admin, customer portal, Grafana and
  Prometheus, and add a deployment regression for the launcher invariants.

Validation:

- Recorded in the task handoff after PowerShell parsing, deployment tests, and live
  start/restart/stop plus HTTP endpoint checks.

Risk:

- Complete-workspace stop may start Docker Desktop when the Engine is down so it can reliably stop
  existing scoped containers. A stale development container may be recreated, but Docker Desktop
  itself is not stopped and no volume is removed.

Rollback:

- Revert the three controller changes, certificate/key generator change, Docker context exclusion,
  deployment regression, map rows and this entry together.

## 2026-08-18 - Add native Linux viewer/controller source and portable package

Task:

- Implement a real Linux app that connects to an attended Windows host for video and approved input.

Files changed:

- `src/PeerOnQ.App.Linux/`, `src/PeerOnQ.Platform.Linux/`
- `tests/PeerOnQ.App.Linux.Tests/`, `tests/PeerOnQ.Platform.Linux.Tests/`
- `scripts/linux/build-peeronq-linux-viewer.sh`, `packaging/linux/README.md`
- `Directory.Packages.props`, `PeerOnQ.slnx`, `src/PeerOnQ.Infrastructure/PeerOnQ.Infrastructure.csproj`
- `PROJECT_MAP.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/CROSS_PLATFORM_CAPABILITIES.md`
- `artifacts/peeronq/DESIGN_SYSTEM.md`
- `DEPENDENCIES.md`, `THIRD_PARTY_NOTICES.md`

Reason:

- Linux previously had protocol labels and a planned web card but no native client. The new least-capability app reuses the real signaling/WebRTC session path without pretending Linux host or unattended support exists.

Validation:

- Linux app build: passed, 0 warnings.
- Linux app tests: 11 passed; Linux platform tests: 8 passed; infrastructure tests: 80 passed.
- Existing Windows app build: passed, 0 warnings.
- Self-contained `linux-x64` cross-publish and tar/SHA-256 generation: passed. Physical Linux/Windows-host E2E remains required before publication.

Risk:

- Avalonia UI, libsecret integration and cross-published native assets have not yet been exercised on physical Linux hardware.

Rollback:

- Remove the two Linux projects/tests and packaging files, remove their solution/package/map entries, and return `PeerOnQ.Infrastructure` to `net10.0-windows`.

## 2026-08-18 - Clarify and expose every app platform

Task:

- Show every platform clearly on the public Downloads page while distinguishing the only published app.

Files changed:

- `artifacts/peeronq/src/pages/DownloadsPage.tsx`
- `artifacts/peeronq/src/test/routeSeparation.test.tsx`

Reason:

- iOS and iPadOS were grouped into one card and the two-column desktop grid pushed them below the
  initial viewport, while the section copy did not state plainly that only Windows is published.

Validation:

- Downloads route test, frontend typecheck, lint, and production build.

Risk:

- Low; the change adds no download endpoints and keeps every unpublished app disabled as planned.

Rollback:

- Restore the two-column grid, previous section copy, combined iOS/iPadOS card, and test expectation.

## 2026-08-18 - Implement Phase 10 and 11 for the single-node pilot

Task:

- Proceed past the remaining Phase 9 HA blockers for the user's current no-failover scope and
  implement Phase 10 Smart Connection plus Phase 11 support/multi-session/portable capabilities.

Files changed:

- Native app, Application/Domain/Media/Transport/Signaling contracts and implementation, WiX and
  Windows build/validation scripts, affected tests, project maps/design/security/deployment docs,
  Phase 10/11 completion reports and validation reports.

Reason:

- Live adaptation did not receive its bandwidth input, transfer priority was fixed, quality health
  was not relayed, support invitations/portable packaging were absent, and viewer/input routing was
  not explicitly isolated across concurrent sessions.

Changed:

- Added deterministic connection health/adaptation, real 2/8/16 MiB bulk allocation and viewer
  telemetry; added hash-only expiring/revocable support invitations with mandatory Accept;
  session-bound multi-viewer routing/exclusive input focus; and a checksum-protected, ephemeral,
  unattended-disabled Portable Support build plus `peeronq://support` MSI activation.
- Preserved mandatory hybrid post-quantum transport and all consent/permission checks. Phase 9 HA
  remains failed/deferred rather than reclassified.

Validation:

- Passed Media policy 21/21, Application 132/132, Signaling 108/108 with four environment-gated
  distributed/failover skips, Domain 61/61 and End-to-End 49/49.
- Native Debug and x64 portable Release builds completed with zero warnings/errors. The 0.9.21
  portable ZIP contained 572 entries and matched SHA-256
  `27d37de67fda2b9bbb015ec3ce63038d6dab1730170bc79bd8edbc5446726758`.
- x64 WiX 0.9.21 MSI build, ICE03, administrative payload and `peeronq://` registry validation passed.

Risk:

- Physical 4K/shaped-network/relay/contention, x64+ARM64 clean-VM, Authenticode, two-device support,
  eight-hour soak, accessibility and independent security evidence remain external blockers.
  Current portable output is development/unsigned and hybrid-required only.

Rollback:

- Revert this entry's Phase 10/11 implementation and documentation files. Existing attended,
  unattended, reconnect and hybrid security architecture remains the baseline; no migration is
  required.

## 2026-08-18 - Keep LAN clients connected through the workspace controller

Task:

- Fix installed LAN-development 0.9.21 clients remaining disconnected after `peeronq-start.bat`
  reported the website and Phase 6 stack healthy.

Changed:

- Include an already-configured Phase 3 LAN signaling/TURN stack in workspace start, stop, restart
  and status operations.
- Read only the validated IPv4 bind address from the existing ignored Phase 3 state instead of
  hardcoding a machine address; installations without Phase 3 state retain the prior behavior.
- Start LAN signaling before the slower Phase 6 build so installed clients can reconnect promptly.

Validation:

- The failure was reproduced with 0.9.21 logging `Automatic signaling connection attempt failed`,
  no listener on its compiled `10.0.0.10:5443` endpoint, and the LAN health request unable to
  connect while the separate Phase 6 endpoints remained healthy.
- Rebuilding and starting the existing Phase 3 controller restored the LAN health endpoint and
  healthy signaling, TURN and TLS proxy containers.
- The patched unified `peeronq-start.bat` path exited with code 0, its status included the configured
  LAN stack, and the fresh signaling logs recorded three registration events with no error/failure
  events after startup.

Risk:

- A stale configured LAN address now fails the unified start explicitly instead of silently leaving
  LAN clients disconnected. Phase 3 remains opt-in and is skipped when its state file is absent.

Rollback:

- Revert the workspace-controller and map changes; manage the configured Phase 3 LAN stack manually
  with `peeronq-phase3-local.ps1` before launching 0.9.21 clients.

## 2026-08-18 - Publish current Windows 0.9.21 downloads on the web preview

Task:

- Replace the public Downloads page's stale `0.5.1` local-client links with the current verified Windows build and keep future local versions from requiring another hardcoded page edit.

Files changed:

- `artifacts/peeronq/src/pages/DownloadsPage.tsx`
- `artifacts/peeronq/src/test/routeSeparation.test.tsx`
- `artifacts/peeronq/.env.example`
- `artifacts/peeronq/README.md`
- `scripts/windows/peeronq-dev.ps1`
- `scripts/windows/build-phase5-development.ps1`
- `scripts/windows/build-peeronq-website-patch.ps1`
- `PROJECT_MAP.md`
- `AI_CHANGELOG.md`

Reason:

- The page, dev controller and website-publishing guard were pinned to `0.5.1`, while the latest tested native source had shipped as `0.9.21`. A newer MSI therefore could not become the active website download without editing several hardcoded paths.

Changed:

- Derive local x64/ARM64 URLs from a validated version variable and fail closed when availability is enabled without numeric version metadata.
- Make the dev controller select the newest complete x64/ARM64 pair only after recomputing both SHA-256 values against `SHA256SUMS.txt`; website-patch builds explicitly clear this local-only metadata.
- Allow the development installer publisher to publish future explicit versions and advance its default to `0.9.21`.
- Build and publish fresh unsigned local-test `0.9.21` x64 and ARM64 installers, then restart the web preview with version `0.9.21` selected.

Validation:

- Fresh x64 and ARM64 self-contained builds completed with zero warnings/errors; both MSI packages passed WiX ICE03 and exact installer payload validation.
- Published SHA-256: x64 `f14f5f721a8079baeda7df4bd9df51730a95d6a3b43da07e25c7029902ac296e`; ARM64 `df1138cb3919dafc06ad0487ca9b8320f63e319c4ea14c455820be636c669b9a`.
- Dev restart selected `0.9.21`; `/downloads`, both MSI URLs and `SHA256SUMS.txt` returned HTTP 200.
- Frontend tests passed 53/53; typecheck, ESLint, production Vite build and website-patch trust-boundary invariant passed.

Risk:

- These website files are unsigned local-development installers, not production-signed releases. The page keeps the existing controlled-test warning and checksum link.

Rollback:

- Revert the files above, restore the prior website download artifacts/checksum set and restart the preview; the page will return to the previous fixed-version behavior.

## 2026-08-18 - Show every planned native app on Downloads

Task:

- Make the public Downloads page clearly list macOS, Linux, Android, iOS and iPadOS in addition to Windows.

Files changed:

- `artifacts/peeronq/src/pages/DownloadsPage.tsx`
- `artifacts/peeronq/src/components/PlatformCard.tsx`
- `artifacts/peeronq/src/test/routeSeparation.test.tsx`
- `artifacts/peeronq/DESIGN_SYSTEM.md`
- `AI_CHANGELOG.md`

Reason:

- The non-Windows platforms were visually ambiguous and their availability details were hidden in a tooltip. The page now names each app, explains its target device class and states that no verified build exists yet.

Validation:

- Targeted route test: 29/29 passed.
- Frontend suite: 52/52 passed; typecheck, ESLint and production Vite build passed.

Risk:

- Low. The Windows download selection and release-security rules are unchanged; planned platforms remain disabled and have no fake links.

Rollback:

- Revert this entry and the four frontend/design-system files listed above.

## 2026-08-18 - Open every development UI and recover a hung preview

Task:

- Make `peeronq-start.bat` reliably open every user-facing PeerOnQ development interface, including
  the public web preview that previously appeared ready while returning no HTTP response.

Changed:

- Centralize browser launching in `peeronq-workspace-dev.ps1` after both Phase 6 and the preview
  report success. Open the public website, Admin console, account portal, Grafana and Prometheus
  exactly once, using configured Phase 6 ports.
- Add a `-NoBrowser` coordination switch to the preview and Phase 6 controllers so direct use keeps
  its existing browser behavior while the complete workspace avoids duplicate tabs.
- Replace the preview controller's port-only readiness decision with a real bounded HTTP check. If
  the tracked repository process owns port 5555 but is unresponsive, stop only that verified process
  tree, restart it, and wait for HTTP success before opening pages.

Validation:

- All three touched PowerShell scripts parse successfully and the five launch targets are present.
- The regression was reproduced with tracked PID 33160 listening on port 5555 while HTTP timed out;
  the updated controller detected it, stopped only that tracked tree, started PID 4460 and reached
  HTTP 200.
- A complete `peeronq-start.bat` run exited successfully. Public preview, Admin, Portal, Grafana and
  Prometheus each returned HTTP 200 after the run, and workspace status reported all endpoints ready.

Risk:

- Clicking Start intentionally opens five browser tabs. The first start can still take several
  minutes because the existing Phase 6 workflow rebuilds images and validates migrations before
  declaring the workspace ready.

Rollback:

- Revert the centralized page list, child `-NoBrowser` switches, preview HTTP recovery, map update
  and this entry; browser tabs will again be opened independently and a hung port owner may be
  reported as ready.

## 2026-08-18 - Ship the unattended password fix in LAN development 0.9.21

Task:

- Diagnose why password-based unattended access still failed when the user tested the 0.9.20
  package, then provide a build that actually contains the completed fix.

Changed:

- Confirm the installed/running Program Files process was still `0.9.19.0`; the `0.9.20` MSI had
  been produced before the later unattended permission-negotiation repair and therefore could not
  contain that repair.
- Rebuild the LAN signaling/TURN stack from the current source so the optional allowed-permission
  challenge field is relayed end to end, then publish the current password-proof and explicit
  permission-downgrade dialog changes as x64 `0.9.21.0`.
- Produce the isolated unsigned LAN-development MSI for
  `wss://signal.10.0.0.10.sslip.io:5443/ws`; no plaintext password, weakened permission, or silent
  unattended fallback was introduced.

Validation:

- Focused unattended Application tests passed 8/8, the real signaling challenge route passed 1/1,
  and the native UI/accessibility gate passed.
- The fresh self-contained x64 publish and MSI build completed with zero warnings/errors. WiX
  ICE03, administrative extraction, runtime presence and exact publish-payload comparison passed.
- The rebuilt LAN signaling endpoint returned ready (HTTP 200) and signaling, TURN and proxy
  containers reported healthy.
- The 78,512,128-byte MSI SHA-256 is
  `51DC4299F6264F399C6C7D8FD6732BA5BADBAF1C996E71C388F13E1F14E8BFA6`.

Risk:

- This remains an unsigned private-LAN development package. Both computers must fully exit the
  tray process and install `0.9.21`; a still-running `0.9.19`/`0.9.20` process will not gain the fix.
- Physical password connection between the user's two computers remains the final acceptance test.

Rollback:

- Stop distributing the `0.9.21` LAN kit, rebuild the LAN signaling stack from the prior revision,
  and reinstall matching `0.9.20` clients; the unattended mode-negotiation failure will return.

## 2026-08-18 - Expose unattended credentials and keep lossy video sessions alive

Task:

- Make the real unattended password/trusted-device controls understandable and usable from the
  connecting client, and stop normal packet loss or window minimization from ending active sessions.

Changed:

- Add a default-off Dashboard unattended switch with password/recovery entry, trusted-device-only
  empty-credential guidance, secure field clearing, and attended-versus-unattended status wording.
- Make Security setup expose its maximum allowed mode and authoritative persisted state; clarify
  that the password is entered on the connecting device, trusted devices are separately approved,
  and disabling revokes unattended credentials without deleting the separate trust directory.
- Permit video receive state to start at the first authenticated sequence that survives RTP loss.
  Drop isolated unauthenticated video frames, request a key frame, and retain session-level
  fail-closed termination after eight consecutive verification failures. Ordered control/input/
  clipboard/file records retain their immediate failure behavior.
- Increase the bounded reconnect attempt budget so exponential backoff can cover the existing
  30-second no-new-consent recovery window instead of exhausting attempts early.

Validation:

- Native UI/accessibility gate and `git diff --check` pass. Targeted unattended/security/reconnect
  tests pass 29/29 and targeted video tests pass 4/4.
- Full Application passes 125/125, Media 64/64 with the separately provisioned live-TURN case
  skipped, Signaling 100/100 with four explicit Docker/cluster integration skips, and End-to-End
  passes 45/45.
- The `0.9.20` x64 self-contained Release build has zero warnings/errors. WiX ICE03 and exact
  publish-payload validation pass; unsigned LAN-development MSI SHA-256 is
  `4FBB4244E20905EFE4808DB059EE4E2C0724D8462686C325B2687150308F2D46`.
- Repository secret scan passes 966 files and the compiled LAN signaling endpoint reports ready
  over HTTPS (`/health/ready`, 200).

Risk:

- Unattended access remains default-off, scope-bound, rate-limited, audited and visibly indicated.
  Bad video frames never reach the decoder; only an isolated lossy frame is recoverable.
- Automatic session resume remains bounded by the existing 30-second security confirmation limit.

Rollback:

- Revert the UI/status, video-loss budget, reconnect-attempt and regression-test changes. Existing
  encrypted unattended profiles and trusted-device records require no migration.

## 2026-08-17 - Add blocked and connected-device management

Task:

- Let a device owner see and reverse accidental blocks, automatically retain devices after a real
  outgoing connection, and edit or delete those entries directly from Devices.

Changed:

- Add an idempotent, parameterized unblock operation to the blocked-device store and expose the
  same persistence instance to the desktop UI.
- Add a Security-page blocked-device list with an empty state, accessible per-device Unblock
  controls, a confirmation dialog, disabled in-flight state, and visible success/error feedback.
- Refresh the authoritative list during startup and whenever Security is opened; add persistence
  regression coverage proving that only the selected ID is removed.
- Record authenticated outgoing connections in the encrypted local address book without replacing
  a user-edited alias, sort recently connected devices first, and expose Edit/Delete controls on
  Devices. Deleting a trusted entry revokes trust before removing its saved record, while retaining
  session audit history.
- Remove the Dashboard's redundant Security Posture and Recent Session presentation; security
  controls remain on Security and connected-device management now has one authoritative home.

Validation:

- Targeted blocked-device persistence tests pass (3/3), the full Application suite including the
  connected-device alias/last-seen regression passes (105/105), and the WinUI x64 Release build
  passes with zero warnings/errors.
- End-to-End passes (44/44). An isolated `0.9.9` UI Automation smoke exercised Add/Edit/Delete on
  Devices and verified the Security blocked-device section plus removal of the two Dashboard
  sections. The first combined UI locator treated a below-fold Security empty state as invisible;
  the corrected ScrollViewer-aware layout smoke passed.
- The final `0.9.10` x64 MSI passes its Release build with zero warnings/errors, WiX/ICE checks and
  exact publish-payload validation. Repository secret scan passes for 960 files and `git diff
  --check` is clean. SHA-256: `025A11B1207C74A4249766C8DC37873A2A4832110D9C1991A718F0C838E3B8A0`.

Risk:

- Unblocking only permits the peer to request a future attended session. It does not grant trust,
  unattended access, screen viewing or input control; every attended request still requires local
  approval.
- Auto-saving occurs only after an outgoing session reaches the authenticated connected state.
  Removing a directory entry does not erase the separate security audit or session history.

Rollback:

- Revert the store/UI change. Existing `blocked_devices` rows remain valid and no schema migration
  or device-identity reset is required.

## 2026-08-17 - Repair customer portal registration recovery

Task:

- Fix the customer portal state that showed account-service unavailability beside invalid
  credentials after a successful registration and prevented a clear verification/login flow.

Changed:

- Allow the refresh endpoint to use its secure cookie without requiring a JSON request body, make
  the portal send an explicit empty refresh body for compatibility, and clear stale service errors
  when a new login or registration request reaches the API.
- Keep service-level and form-level errors separate so one failure is never rendered twice, and add
  customer-authentication bootstrap regressions.

Validation:

- Portal tests pass (11/11), portal typecheck and production build pass, the Cloud API Release build
  passes with zero warnings/errors, and the repository secret scan passes.
- The rebuilt Compose portal and Cloud API are healthy, the deployed portal serves the new bundle,
  and an empty refresh request now fails closed with 403 instead of the framework-level 400 that
  was incorrectly rendered as service unavailability.
- Runtime logs confirm the affected registration completed with 202 and its email verification
  completed with 204. The user's password was neither accessed nor exposed; final interactive login
  remains a user-side check with the password chosen during registration.

Risk:

- Refresh authorization remains unchanged: an absent, invalid, expired or replayed cookie/token
  still fails closed with the generic session error and no account information disclosure.

Rollback:

- Revert the optional-body/client-state changes and their tests. Do not delete the customer account,
  mail evidence, Data Protection keys or PostgreSQL volume.

## 2026-08-17 - Make the low-latency LAN profile sharp at 1080p

Task:

- Remove the visibly blurred 720p default after a remote session connects while preserving the
  low-latency LAN behavior.

Changed:

- Move the Performance profile from 720p/60 at 3 Mbps to 1080p/60 at 12 Mbps and route that profile
  through the bounded low-latency capture conversion path.
- Update the native profile label, LAN guide/map, build recommendation, and regression coverage;
  keep Balanced and Native as explicit alternatives.

Validation:

- The 4K-to-1080p low-latency conversion measured 19.13 ms average on this host. Application passed
  104/104, End-to-End 44/44, and Media 60/60 with one normal opt-in live-TURN skip; the real
  1080p/60 VP8/WebRTC loopback passed.
- Native UI validation and the WinUI x64 Release build passed with 0 warnings/errors. A final pair
  of `0.9.7` payload clients completed real WSS/WebRTC Full Control at 1920x1080; capture logs
  confirmed `P1080`, 60 fps and `low-latency`, remote input reached Windows injection, and the
  captured viewer image was visually sharp.
- The `0.9.7` x64 MSI passed WiX/ICE and exact publish-payload validation. SHA-256 is
  `FA9AC311196649D7B205A13AF35A74230C02AADBADCA4CD793A31B9EE8D8773A`.

Risk:

- 1080p/60 requires more LAN bandwidth and encoder work than 720p/60. The latest-frame queue remains
  bounded so an overloaded endpoint drops obsolete frames instead of accumulating input latency.

Rollback:

- Revert this profile/label/test/documentation change and reinstall the prior matching client on
  both endpoints.

## 2026-08-17 - Prevent Phase 6 and Phase 9 from locking their own environment file

Task:

- Repair the Windows PowerShell 5.1 startup failure that prevented generation of the local
  Signaling Redis password and therefore blocked the Phase 9 HA profile.

Changed:

- Replace lazy `.env` reads with eager reads in the Phase 6 controller and Phase 9 harness so the
  file handle is closed before either script appends a generated secret.
- Add a deployment regression that keeps both secret-bootstrap paths free of lazy `ReadLines`.

Validation:

- Both PowerShell files parse successfully and the full Observability/deployment suite passed
  19/19, including the new environment-read regression.
- A real retry identified the still-open pre-fix Administrator PowerShell process as the sole
  remaining file-lock owner. That existing process must be closed once; the repaired scripts no
  longer create the lazy handle on subsequent runs.

Risk:

- The ignored development `.env` file is small; eager reading has negligible memory cost and does
  not print or otherwise expose its secrets.

Rollback:

- Revert the two read-path changes and their regression test. No stored secret or Docker volume is
  deleted or rewritten by this patch.

## 2026-08-17 - Remove the 4K capture conversion latency bottleneck

Task:

- Reduce interactive LAN remote-control latency to the lowest practical setting without removing
  the selectable Balanced or Native quality modes.

Files changed:

- Route native-size BGRA capture through the existing integer converter instead of the generic
  floating-point box scaler.
- Add a bounded low-latency BGRA-to-I420 downscale path for the explicit 720p/60 Performance
  profile, identify that path in privacy-safe capture logs, and make it the LAN-development default.
- Rename the visible profile to `Low latency (720p / 60 fps)` and add scaler, UI-default, and real
  720p/60 WebRTC regressions. Update the LAN guide and project map to version `0.9.6`.

Reason:

- A measured 3840x2160 native conversion took about 117 ms before VP8 encoding, limiting capture
  to roughly 8.5 fps and making stale-looking interaction unavoidable even on a low-RTT LAN. The
  generic quality downscaler also consumed about 26-29 ms when producing 720p from a 4K source,
  leaving insufficient headroom for a 60 fps profile.

Validation:

- The same five-frame 4K microbenchmark reduced native conversion from about 117 ms to about 25 ms
  and measured the 4K-to-720p low-latency path at about 9.5 ms average (about 105 conversion fps),
  while quality-oriented box averaging remains selectable.
- Frame-scaler tests passed 19/19; real VP8/WebRTC loopback cases passed 3/3 including 1280x720 at
  the 60 fps target; native UI validation and the WinUI x64 Release build passed with 0 warnings and
  0 errors.
- Two final `0.9.6` payload clients completed real WSS/WebRTC Full Control at 1280x720 with the
  capture log confirming `P720`, `60` fps, and `low-latency`; pointer movement reached the Windows
  input boundary and the viewer ended both sides cleanly.
- The `0.9.6` x64 MSI passed WiX/ICE and exact publish-payload validation. SHA-256 is
  `FC0D76C4A408E1BB259BD1CD20B09EE7D11D059BBD858F96E942A9F190650284`.

Risk:

- The low-latency profile intentionally trades some resize filtering/detail for responsiveness and
  uses 720p; users can still select Balanced or Native. Same-host evidence cannot replace a measured
  two-physical-laptop Wi-Fi latency run.

Rollback:

- Revert the scoped scaler/capture/default/test/map/guide changes and reinstall the previous
  matching client on both laptops.

## 2026-08-17 - Make every viewer scaling mode control the visible viewport

Task:

- Repair the viewer scaling menu because Fit, Fill, and Stretch appeared not to change the remote
  screen on differently sized client displays, then produce a tested LAN client.

Files changed:

- Added an explicit viewport container between the remote `Image` and its `ScrollViewer`; window
  scale modes pin that container to the current visible area while DPI-correct Actual size keeps
  its source-pixel dimensions and scrolling.
- Added viewport-resize/static wiring checks to the native UI regression script and updated the LAN
  build/install guide to version `0.9.5`.

Reason:

- WinUI measures a direct `ScrollViewer` child with an unconstrained extent. The remote `Image`
  therefore retained its source-derived size, so changing its Stretch value alone did not reliably
  produce the requested viewport fit, centered crop, or full-window distortion.

Validation:

- Native UI/accessibility validation passed; the WinUI x64 Release build passed with 0 warnings and
  0 errors; all 10 remote pointer/scale geometry tests passed.
- Real two-client WSS/WebRTC runs rendered 1920x1080 and visually distinguished Fit (centered
  aspect-preserving bars), Fill (centered edge crop), and Stretch (full-window distortion). The
  final `0.9.5` payload also delivered pointer movement through the selected scale mapping to the
  Windows input boundary. The broader same-host harness later stopped at its already-unclaimed
  global release-hotkey case; that unrelated case is not claimed as passed.
- The `0.9.5` x64 MSI passed WiX/ICE and publish-payload validation. SHA-256 is
  `FE47DB535A080815867DFDC9CF3ECA823F11CEFE7CCAD32D01BA48DBE1F1C3B8`.

Risk:

- Same-host recursive capture proves layout and the data path but is not a replacement for the two
  physical-laptop resolution/DPI check. Both laptops must replace the older client with `0.9.5`.

Rollback:

- Revert the scoped viewer viewport/test/guide changes and reinstall the previous matching client
  on both laptops.

## 2026-08-17 - Simplify connection choices and repair explicit acceptance

Task:

- Remove the three secondary connection toggles marked as unnecessary, diagnose the current LAN
  logs, repair explicit permission acceptance, and produce one protocol-compatible test client.

Files changed:

- Simplified the native Dashboard connection card to View Only, Full Control, or File Transfer;
  removed its custom file/clipboard/unattended composition code and added a static regression gate.
- Preserved validated application versions in sanitized structured logs instead of mistaking a
  four-part version for an IPv4 address.
- Replaced the permission dialog's background countdown/WinUI dispatch crossing with a UI-thread
  dispatcher timer and privacy-safe outcome diagnostics. Updated the LAN guide/controller to
  version `0.9.2`.

Reason:

- The extra toggles duplicated the three primary modes and made the normal connection path harder
  to understand. Logs also showed an old protocol-v2 client repeatedly reaching a protocol-v3
  server, while the sanitizer hid the exact application version needed to diagnose it.
- Live two-client reproduction proved that Accept returned `Primary`, then the decorative
  background countdown raised `InvalidCastException`; the fail-closed boundary converted that
  exception to `PermissionDeclined` and prevented the viewer from opening.

Validation:

- Native UI/accessibility validation passed; full Infrastructure tests passed 78/78; WinUI x64
  Release build passed with 0 warnings/errors.
- The current protocol-v3 LAN stack rebuilt healthy and a live WSS probe received a challenge.
- A real same-host two-client WSS/WebRTC run accepted Full Control, opened the viewer, rendered a
  1920x1080 stream with captured cursor disabled, and delivered authorized pointer movement to the
  Windows input boundary using the final `0.9.2` payload.
- The `0.9.2` x64 MSI passed WiX/ICE and exact publish-payload validation; SHA-256 is
  `A370BD55E902CA707363F8746507B7C13F6BB9B210CE1B080122299027443B04`. The extended same-host
  harness later stopped at its global emergency-hotkey case, which is not claimed as passed.

Risk:

- Both physical laptops must replace older clients with `0.9.2`; mixed protocol versions are
  deliberately rejected. The same-host multi-process emergency-hotkey case is not a physical-device
  substitute and remains unclaimed; two-physical-device validation remains external.

Rollback:

- Revert the scoped Dashboard/logging/guide/test changes and reinstall the matching previous
  client/server pair. Do not mix signaling protocol versions.

## 2026-08-17 - Add distributed Signaling ownership and active-active HA wiring

Task:

- Resolve the internal Phase 9 blocker that kept device routing, live sessions, resume/replay and
  unattended state inside one Signaling process.

Files changed:

- Added Redis Signaling backplane/session/security stores, owner-expiry cleanup and two-node tests;
  wired two Signaling replicas, dedicated Redis ACL, Nginx upstreams and per-replica metrics into the
  opt-in HA profile; migrated Windows/Linux secret bootstrap; updated the Phase 9 harness, maps,
  runbooks and report.

Reason:

- A second Signaling node could not find a device connected to the first node and restart lost
  session/resume authority. Concurrent cross-node requests also needed one atomic device limit.

Validation:

- Signaling Release build passed with 0 warnings/errors; full Signaling suite passed 85/85 with
  four explicit external-integration skips; real Redis two-node integration passed 4/4, including
  active-session node loss/reconnect/resume; deployment configuration passed 11/11; base/HA Compose,
  Prometheus/rules, Linux installer containment and an isolated Redis ACL smoke passed.

Risk:

- Phase 9 remains `FAIL`: PostgreSQL/Redis/TURN are still single-node and physical active-media,
  failover/data-loss, soak, capacity and PITR gates remain unmeasured.

Rollback:

- Disable `Signaling__Cluster__Enabled` for a single-process rollback, remove the second HA upstream,
  and revert only the scoped Redis stores/ACL/deployment files. Preserve Redis/PostgreSQL volumes;
  no schema down migration exists.

## 2026-08-17 - Block Phase 11 at the blocked Phase 10 transition gate

Task:

- Evaluate the Phase 11 execution contract without bypassing its mandatory Phase 10 prerequisite.

Files changed:

- Added the 21-section Phase 11 blocked completion report and indexed it in the project map.

Reason:

- Phase 10 is `BLOCKED` by Phase 9 `FAIL`; its required Smart Connection Engine and acceptance
  evidence therefore cannot be reused as a passed Phase 11 dependency.

Validation:

- Current Phase 9/10 final gates, repository snapshot and component versions were inspected. No
  Phase 11 build, test, cryptographic validation or physical gate was run or claimed.

Risk:

- Phase 11 remains unimplemented and unmeasured. Beginning it now would violate the execution
  contract and create unsupported support, isolation and post-quantum security claims.

Rollback:

- Remove only `PHASE_11_COMPLETION_REPORT.md` and this report index/changelog entry. Runtime behavior
  and data are unaffected.

## 2026-08-17 - Block Phase 10 at the failed Phase 9 transition gate

Task:

- Evaluate the Phase 10 execution contract without bypassing its mandatory Phase 1–9 prerequisite.

Files changed:

- Added the 21-section Phase 10 blocked completion report and indexed it in the project map.

Reason:

- Phase 9 is explicitly `FAIL`: Signaling ownership is node-local, PostgreSQL/Redis are single-node
  and required representative fault, recovery and capacity gates are incomplete.

Validation:

- Current Phase 9 final gate/readiness text, repository snapshot and component versions were
  inspected. No Phase 10 build, test or benchmark was run or claimed.

Risk:

- Phase 10 remains unimplemented and unmeasured. Beginning it before Phase 9 passes would violate
  the execution contract and produce invalid readiness claims.

Rollback:

- Remove only `PHASE_10_COMPLETION_REPORT.md` and this report index/changelog entry. Runtime behavior
  and data are unaffected.

## 2026-08-17 - Add measured Phase 9 management-plane HA foundations

Task:

- Add safe stateless warm standbys, distributed Cloud-worker ownership, graceful drain, repeatable
  local failover/load evidence, backup/restore and a Phase 9 truth report.

Files changed:

- Cloud worker/Redis lease, Presence shutdown tracking, common observability drain, Nginx/Compose HA
  overlay, targeted tests, Phase 9 harness, architecture/operations/deployment maps and reports.

Reason:

- Scaling the prior single-node stack duplicated singleton workers, broadcast Presence shutdown to
  healthy nodes, applied user limits to health probes and had no honest fault/recovery gate.

Validation:

- Observability 18/18, Presence 4/4, Cloud Infrastructure 40/40, SessionCoordinator 45/45 and
  Signaling 84/84 passed (one explicit Docker opt-in skip). The real local HA gate returned 0 errors
  for 60 requests at concurrency 8 with each stateless primary stopped; PostgreSQL backup/isolated
  35-table restore and Alertmanager-to-PostgreSQL delivery passed.

Risk:

- Phase 9 remains `FAIL`: Signaling ownership and the reference PostgreSQL/Redis are single-node;
  soak, capacity, PITR, active-session and real multi-host/region evidence are missing.

Rollback:

- Remove only the opt-in HA overlay/harness and revert the scoped drain/lease/proxy changes. Keep
  existing volumes and forward-only database state; do not run a down migration.

## 2026-08-16 - Restore Windows preview startup

Task:

- Make `peeronq-start.bat` open both the Phase 6 Admin console and the local product preview.

Files changed:

- `pnpm-workspace.yaml` and `pnpm-lock.yaml` now retain the native optional packages required by
  the supported Windows x64 development host.

Reason:

- Platform-pruning overrides removed Rollup's Windows x64 binary, so Vite exited before it could
  listen on port 5555 while the independent Docker-backed Admin console remained healthy.

Validation:

- `peeronq-start.bat` exited successfully; both `http://localhost:5555/` and
  `https://admin.dev.localhost:8443/` returned HTTP 200, and `peeronq-status.bat` reported both ready.
- Root lint, 68 frontend tests, typecheck, and the full pnpm build passed.

Risk:

- Low. The lockfile adds only the existing toolchain's Windows x64 optional binaries; Linux x64
  behavior and `minimumReleaseAge` remain unchanged.

Rollback:

- Revert this entry plus `pnpm-workspace.yaml` and `pnpm-lock.yaml` together.

## 2026-08-12 - Record the Phase 3 contract gate and local acceptance failure

Task:

- Apply the supplied execution contract to the current Phase 3 source, tests, and local Docker evidence.

Files changed:

- Phase 3 status documentation, project map, changelog, and a Phase 3 completion report.

Reason:

- The supported runtime still rejects the Phase 2 `ControlInput` scope, and the full Phase 3 local
  controller times out in its first UDP relay-media test even though targeted relay tests pass.

Validation:

- Signaling: 63 passed, 1 opt-in skip. Media: 56 passed, 1 opt-in skip. The isolated Phase 3
  controller completed signaling (63 passed, 1 skip) and end-to-end (37 passed), then failed the
  first live UDP relay-media test after 45 seconds.

Risk:

- No product capability changed. This records a real gate rather than treating targeted local
  evidence as full acceptance.

Rollback:

- Revert this documentation entry only after a passing replacement controller run and Phase 2
  completion evidence supersede it.

## 2026-08-12 - Allow isolated local Phase 3 TURN port ranges

Task:

- Run the repository-scoped Docker acceptance without interrupting the active Phase 6 local TURN container.

Files changed:

- Phase 3 controller, local Compose TURN advertisement/publishing, coturn runtime relay-port
  validation, and local development documentation.

Reason:

- The active Phase 6 local stack owns loopback TURN port 3478 and relay range 49160-49200, so the
  separate Phase 3 acceptance stack could not start.

Validation:

- Docker Compose resolved the isolated loopback mapping: 3479 -> internal 3478 and
  49201-49241 -> the same internal coturn relay range.
- The Phase 3 containers became healthy without stopping the active Phase 6 TURN container. Targeted
  live relay-only video passed over TCP 3479, UDP 3479 including a temporary relay interruption,
  and TLS 5349.
- The full legacy controller still times out in its first UDP media check after its earlier TURN utility
  probes; its separate port-isolation media checks pass. The broader controller sequencing issue remains
  outside this local-port change.

Risk:

- Local development only. Invalid/overlapping relay bounds fail before coturn starts; production
  compose defaults and ports remain unchanged.

Rollback:

- Revert this entry and the local controller/Compose/entrypoint/documentation change together.

## 2026-08-12 - Record the Phase 2 transition gate as blocked

Task:

- Evaluate the Phase 2 remote-control request against its mandatory Phase 1 prerequisite.

Files changed:

- Added the Phase 2 blocked-gate report and updated the current-state map.

Reason:

- Phase 1 requires physical two-Windows-device visual acceptance and its final gate remains
  `BLOCKED`; enabling remote input first would violate the execution contract.

Validation:

- Reviewed the Phase 1 completion report and its recorded Release build/full regression evidence.
  No executable source changed, so no build or test was rerun for this documentation-only decision.

Risk:

- No product capability changed. The only risk is schedule delay until the physical acceptance
  environment is available.

Rollback:

- Delete this blocked-gate report and revert the map/changelog entry only after a corrected report
  supersedes it with actual Phase 1 physical evidence.

## 2026-08-12 - Lock the native runtime to Phase 1 attended view-only scope

Task:

- Repair the mismatch between the Phase 1 contract and residual later-phase controls in the
  native app, session coordinator, signaling server, and WebRTC factory.

Files changed:

- Phase 1 scope policy, WinUI request/viewer affordances, application and signaling enforcement,
  WebRTC tests, scope regression tests, Phase 1 evidence report, and project-state documentation.

Reason:

- General session permission handling could accept control, file transfer, clipboard, custom, and
  unattended requests even though the Phase 1 product must only grant attended screen viewing.

Validation:

- Release solution build: 0 warnings/errors. Full .NET suite: 455 passed, 2 opt-in external tests
  skipped. The physical two-Windows-device gate remains explicitly blocked.

Risk:

- Existing clients requesting a non-Phase-1 scope now receive `unsupported_mode`; isolated
  lower-level collaboration code remains present but cannot create a Phase 1 media session.

Rollback:

- Revert this entry's change set as one unit; do not remove individual boundary checks because
  their defense-in-depth relationship is intentional.

## 2026-08-12 - Establish Recovery Gate R0 and canonical PeerOnQ identity

Task:

- Capture an immutable baseline, recover deterministic root commands, migrate active .NET projects
  and namespaces to PeerOnQ, isolate persisted compatibility, and publish the R0 truth/standards/
  open-source evidence set.

Files changed:

- Solution/core/test project paths and namespaces, build scripts, offline frontend boundary, local
  certificate controllers, release/config aliases, brand-purity gate, maps, contributor/notices,
  recovery documents, and machine-readable test evidence.

Reason:

- Root build orchestration included the throwaway sandbox, the offline prototype could report a
  fake API success, Phase 3/6 CAs shared a subject that made Windows chain selection ambiguous,
  active code still used the former identity, and Phase 1-6 claims lacked one current evidence gate.

Validation:

- Pre-change evidence: 30-project Release build, 452 .NET passes/2 opt-in skips, 67 frontend passes,
  dependency/secret scans, Compose configuration and runtime probes. Post-change Release build is
  warning/error-free; final TRX/JUnit, root build, purity, security and local-runtime results are
  recorded in `docs/phase-reports/R0_COMPLETION_REPORT.md`.

Risk:

- Project paths changed for downstream scripts/IDE state. Persisted data, device IDs, installer
  upgrade identifiers and update history remain compatibility-tested. Physical/public gates remain
  explicitly external.

Rollback:

- Revert the R0 commit as one unit. Do not selectively remove compatibility adapters before user
  data, DPAPI and update migration tests prove a safe replacement.

## 2026-08-12 - Close server and website release trust boundaries

Task:

- Make same-bundle retries path-safe and let Admin website upgrades update the Downloads UI without
  granting the website signing key authority over Windows installers.

Files changed:

- Linux server installer and invariant tests, website patch builder and invariant test, Admin static
  archive validation, edge routing/runtime validation, Downloads UI/tests, maps and deployment docs.

Reason:

- A tampered installer header could use `.` or `..` as its version and escape the intended release
  leaf during quarantine. Website patches could not update `/downloads`; directly serving their MSI
  would instead have expanded a UI-only signing key into a client-binary release authority. A
  retained legacy overlay could also restore the stale `Build required` page after server upgrade.

Validation:

- Installer semantic-version/containment regressions passed on Alpine 3.22 and Ubuntu 24.04,
  including two same-version retries. Website builder, Admin extraction and real Nginx TLS/runtime
  regressions passed for legacy fallback, compatible UI activation, immutable base MSI/hash metadata
  and rejection of arbitrary MSI paths. Frontend 48/48 and Admin API 20/20 tests passed.

Risk:

- Existing legacy website overlays intentionally fall back to the server-bundled Downloads page.
  Website patches are now UI-only; client upgrades require a full server release.

Rollback:

- Revert the installer containment and website trust-boundary changes together. Do not restore MSI
  support to website archives or unguarded release quarantine paths.

## 2026-08-12 - Stabilize quality selection, VP8 recovery and deployment retries

Task:

- Carry the viewer's quality selection to the sharer, recover VP8 promptly after reference loss,
  tolerate bounded registration clock skew, and make container/download/test retries deterministic.

Files changed:

- Application/transport/signaling quality setup, VP8 encoder and loss recovery, Cloud challenge
  validation, Docker edge/sidecar routing, embedded-client release gates, scoped SQLite test cleanup,
  focused tests, maps and performance/deployment documentation.

Reason:

- Quality previously stayed local to the viewer; a decoder that lost a VP8 reference could remain
  blank until a later key frame. Exact-lifetime challenge validation rejected fresh responses under
  small clock skew. A long-lived Nginx process cached replaced container IPs, its network-mode
  sidecar could remain on the old namespace, incomplete download bundles could pass packaging, and
  global SQLite pool cleanup made parallel or retried tests interfere with unrelated fixtures.

Validation:

- Application 57/57, Signaling 63/63 plus one explicit Docker opt-in skip, and EndToEnd 37/37 passed
  for exact quality values, legacy omission, malformed rejection and sharer snapshot behavior.
- VP8 visual/rate-change/key-frame and throttled PLI recovery tests passed; bounded Cloud challenge
  boundary tests passed. With the edge proxy left running, signaling was recreated and full live
  Cloud/Presence/Signaling acceptance passed after Docker DNS refresh; embedded-client valid and
  missing/corrupt cases, retry-safe infrastructure tests and scoped diff checks also passed.

Risk:

- Medium-low. The optional wire field is backward compatible, unknown values fail closed, PLI is
  throttled to 500 ms, the clock-skew allowance is capped at 30 seconds, and no public port, secret,
  permission or database schema changes.

Rollback:

- Revert the quality/media/Cloud/deployment/test changes and this entry together. Do not restore
  stale Docker DNS, clientless server bundles or global SQLite pool cleanup without rerunning their
  regression and live acceptance checks.

## 2026-08-12 - Make the public Windows download a server-release invariant

Task:

- Prevent any new server bundle or installation from succeeding without a complete explicitly
  classified Windows client, and force public Downloads navigation through the edge-owned route.

Files changed:

- Server/website builders, Linux installer and embedded-client validator, public website navigation
  and tests, Docker context/build metadata, deployment/project/route documentation, and changelog.

Reason:

- The server builder allowed a clientless `.run`, the installer treated missing embedded metadata as
  success, and SPA links could keep an older active website overlay's stale Downloads state.

Validation:

- PowerShell parsing and mandatory-MSI builder regression, POSIX parsing plus signed/unsigned and
  missing/corrupt payload cases, focused public route tests, frontend typecheck/build, and diff checks.

Risk:

- Server bundle creation now intentionally breaks callers that omit `-WindowsClientMsiPath`.
  Unsigned pilots still require the existing explicit switch and are labeled separately from signed MSI files.

Rollback:

- Revert this invariant as one unit. Do not deploy a rolled-back builder without separately proving
  the public Downloads page and exact MSI bytes before activation.

## 2026-08-12 - Fail deployment when public client prerequisites are unroutable

Task:

- Prevent a server upgrade from reporting healthy while the public API or Signaling virtual host
  rejects requests and newly installed Windows clients remain at `Enrollment pending`.

Files changed:

- Linux production installer, deployment guide, project map, and changelog.

Reason:

- The live host still exposed the earlier `0.6.8` behavior: enrollment challenges lasted about 120
  seconds, exact API/Signaling health routes returned HTTP 400, and the MSI path still used response
  buffering. Later local bundles held the fixes, but installation did not attest both control-plane
  routes before reporting success.

Validation:

- Live production diagnostics confirmed the old behavior: a fresh public challenge had a 120.4-second
  lifetime, exact API/Signaling health probes returned HTTP 400, and two complete MSI reads reset
  after 3.3 MiB and 8.1 MiB while bounded Range reads succeeded.
- POSIX shell parsing and Nginx template validation passed. A recreated local TLS edge returned API
  `healthy` and Signaling `ready`; the Cloud Application suite passed 19/19.
- The embedded x64 MSI passed installer validation, and the `0.6.11` bundle checksum, installer
  attestations, 90-second challenge source, non-buffered MSI route, and embedded MSI hash all matched.

Risk:

- Low and deployment-scoped. Installation now fails closed if either control-plane HTTPS route is
  misrouted; running releases and named persistent volumes retain the existing recovery path.

Rollback:

- Revert this installer attestation and deploy the previous verified server bundle.

## 2026-08-12 - Fail deployment when the public client route serves the SPA

Task:

- Prevent a server upgrade from reporting success when the public Windows MSI route returns an old
  SPA document or any bytes other than the embedded client.

Files changed:

- Linux server installer, deployment guide, and changelog.

Reason:

- The live `peeronq.com` origin still returned `text/html` and the old disabled Downloads asset for
  the canonical MSI URL. The installer proved the web container and rendered Nginx directives, but
  did not fetch and hash the complete artifact through the running TLS proxy.

Validation:

- POSIX shell syntax, real TLS proxy full-file hash, final server-bundle extraction, and public UI
  regression tests.

Risk:

- Installation now downloads the embedded MSI once through loopback before activation. The check
  is bounded to ten seconds for connection establishment and ten minutes for the full transfer.

Rollback:

- Revert this installer attestation and redeploy the previous server release.

## 2026-08-12 - Make the bundled Windows download resilient behind the public proxy

Task:

- Ensure an older active static website patch cannot hide the Windows client page or artifact after
  a server upgrade, stream the large MSI without proxy temp-file exhaustion, and preserve the
  public host header on exact API and Signaling health routes. Restore Windows pilot enrollment by
  leaving bounded clock/transport margin below the v1 client's challenge-expiry ceiling.

Files changed:

- Public and embedded-web Nginx routing/worker configuration and validation, Cloud challenge
  lifetime, installer publication attestation, tests, project/route maps, deployment guide, and
  changelog.

Reason:

- Website releases intentionally survive server upgrades and override the base site. That behavior
  could retain an older `/downloads` SPA state even when the new bundled web image contained the MSI.
  The read-only edge proxy also had only a 32 MiB cache tmpfs for a 78 MiB MSI, and exact health
  routes omitted the shared proxy headers required by ASP.NET host validation. The server also
  issued challenges at exactly the client's two-minute maximum, so sub-second clock skew made a
  fresh production challenge fail as `invalid_challenge`.

Validation:

- Nginx syntax, POSIX shell syntax, Cloud registration tests, public UI tests, extracted-bundle
  cache-free Docker build, and HTTP/hash checks against both the web image and reverse-proxy path.

Risk:

- Website patches can still customize the rest of the public site, but `/downloads` is intentionally
  owned by the server-bundled client channel. Streaming shifts backpressure to the upstream/client
  connection and intentionally avoids buffering the MSI on the edge proxy.

Rollback:

- Revert this entry's routing, worker, and challenge-lifetime changes and redeploy the previous
  release.

## 2026-08-12 - Include the embedded Windows client in the server web image

Task:

- Repair the Ubuntu server bundle so the validated x64 pilot MSI and its build marker reach the
  public web image instead of leaving Downloads in the disabled state.

Files changed:

- Root Docker context policy, public Dockerfile/proxy, installer publication attestation, server
  bundle preflight, deployment documentation, and changelog.

Reason:

- The root Docker context excluded the complete downloads directory, while server deployment
  health checks did not prove that the running web image contained the staged MSI and enabled UI.

Validation:

- PowerShell syntax/preflight, targeted public UI tests, an extracted-bundle Docker image build,
  rendered Downloads content, MSI response/hash, and final `.run` payload/checksum verification.

Risk:

- The embedded MSI remains explicitly labeled and permitted only as an unsigned controlled pilot.
  All unrelated local download artifacts remain excluded from Docker builds.

Rollback:

- Revert the allowlist/preflight and redeploy the prior server release; this restores the disabled
  signed-channel state and removes the embedded pilot from newly built web images.

## 2026-08-12 - Add LAN-only Admin and signed website upgrades

Task:

- Keep the Admin host local to the server LAN, include the controlled x64 Windows pilot in the public
  server website, and let release managers upload safely signed static website patches.

Files changed:

- Admin API/UI, public Downloads page, Nginx/Compose/bootstrap/installer, Windows bundle/website
  builders, security tests, deployment documentation, route/project/design maps, and changelog.

Reason:

- The production bundle had no Windows artifact in its web image, the Admin host was internet
  reachable, and Admin Releases governed only the Windows update channel. Website changes now use a
  separate offline P-256 trust root and bounded atomic static-file publication rather than host code
  execution.

Validation:

- Targeted .NET verifier/extractor tests, Admin/public Vitest contracts, TypeScript checks, Nginx
  syntax validation, production Compose rendering, POSIX/PowerShell parsing, and release builds.

Risk:

- Existing 0.6.6 environments are migrated to a derived LAN `/24`; operators with a different subnet
  must set `PEERONQ_ADMIN_ALLOWED_CIDR` explicitly. The embedded MSI remains an unsigned pilot.

Rollback:

- Roll back the 0.6.7 application bundle, restore the prior Nginx/Compose/env contract, and remove the
  website release volume only if its retained static releases are intentionally discarded.

## 2026-08-12 - Repair non-root access to production file-backed secrets

Task:

- Make the Ubuntu production-pilot deployment pass its remaining non-root secret reads without
  weakening container users or exposing secret values.

Files changed:

- Production bootstrap and server installer, staging Compose runtime groups, deployment/release
  documentation, project map, and changelog.

Reason:

- Local Compose file-backed secrets retain host ownership and mode. Bootstrap created every secret as
  root-only `0600`, so Admin/Cloud/Grafana/TURN could not read their mounted files and failed health.
  The installer now validates and repairs only its exact managed paths; shared TURN and Alertmanager
  files use narrow supplementary groups for their second non-root consumer.

Validation:

- POSIX shell parsing, production Compose validation, isolated bootstrap/upgrade permission checks,
  real-image non-root read/denial checks, bundle integrity, repository secret scan, and release diff
  review.

Risk:

- Low and deployment-scoped. The secret directory remains root-only, files are `0600`/`0640`, no
  container runs as root, and only services already mounting a shared file receive its numeric group.

Rollback:

- Revert this entry and its scoped bootstrap/installer/Compose/document changes before packaging;
  do not return managed secrets to root-only `0600` while non-root containers consume them.

## 2026-08-12 - Make production deployment repeat-safe and diagnosable

Task:

- Eliminate the recurring next-error cycle during the Ubuntu production-pilot installation.

Files changed:

- Diagnostic storage Compose initializer, Linux server installer, deployment and release documents,
  project map, and changelog.

Reason:

- The diagnostic initializer recursively traversed a persistent directory that its first run changed
  to UID 1654 mode `0700`; its least-privilege retry then failed without `DAC_OVERRIDE`. Both storage
  initializers now touch only their managed roots instead of recursively walking retained artifacts.
  The installer also rejected its own retained failed release, deleted container logs before preserving
  evidence, and allowed successful `--status` to fall through into deployment.

Validation:

- Exact-capability Docker reproduction fails on the old second run and passes repeated initialization,
  retained-file and UID 1654 write checks after the fix. An actual `0.6.5.run` failure/retry harness
  verifies root-only bounded logs, volume-safe cleanup, pristine same-bundle retry, quarantine and
  read-only status behavior; production Compose dry-run and non-root TURN health also pass.

Risk:

- Medium-low and deployment-scoped. No capability or public port is added, named volumes and failed
  releases are retained, active/rollback/unsafe targets fail closed, and database migrations are unchanged.

Rollback:

- Revert this entry and the scoped installer/Compose/documentation changes before packaging. Do not
  restore recursive diagnostic ownership or delete preserved volumes/quarantined releases as rollback.

## 2026-08-12 - Restore non-root TURN access to managed TLS files

Task:

- Resume the Ubuntu production-pilot deployment after coturn failed its health dependency.

Files changed:

- Production bootstrap, TLS renewal and server installer scripts; deployment guide and changelog.

Reason:

- The pinned coturn image correctly runs as `nobody:nogroup`, but bootstrap copied its certificate
  directory as `root:root` mode `0700` and private key as mode `0600`. The entrypoint therefore could
  not read the required TLS pair and exited with code 78 before the health check could pass.

Validation:

- Shell parsing, isolated bootstrap/upgrade permission checks, and a live non-root coturn TLS health
  test verify directory mode `0750` and file mode `0640` with group 65534.

Risk:

- Low and deployment-scoped. Only installer-managed certificate files are changed; root ownership,
  non-root coturn execution, private-key confidentiality, application data and named volumes remain.

Rollback:

- Revert this entry before packaging. On an installed host, do not restore the inaccessible modes;
  keep the managed TLS group access or replace it with an equivalent reviewed secret-mount design.

## 2026-08-12 - Align production .NET container SDK and runtime pins

Task:

- Resume Ubuntu deployment after the migration image could not satisfy the repository SDK pin.

Files changed:

- Six production .NET Dockerfiles, Linux deployment guide, release checklist, and changelog.

Reason:

- Floating `sdk:10.0` advanced to SDK `10.0.400`, while `global.json` intentionally selects
  `10.0.302` with patch-only roll-forward. Every Docker build copying the repository could fail SDK
  resolution, beginning with the migration bundle.

Validation:

- Microsoft Container Registry manifests exist for SDK `10.0.302` and ASP.NET runtime `10.0.10`;
  migration bundle and all production .NET service images build using the exact pinned images.

Risk:

- Low. Build/runtime base selection is now deterministic and matches the repository/tool/package
  contract; application code, migrations, secrets, network exposure, and persistent data are unchanged.

Rollback:

- Revert this entry and rebuild only if `global.json` is deliberately updated in the same reviewed
  release. Do not restore floating production image tags.

## 2026-08-12 - Restore production observability network IPAM

Task:

- Resume the first Ubuntu production-pilot install after Docker rejected the OTel collector address.

Files changed:

- Staging Compose observability network, Linux installer recovery, deployment guide, release
  checklist, and changelog.

Reason:

- The staging root network declaration dropped the development model's `172.29.61.0/24` IPAM
  subnet while the inherited OTel collector retained static address `172.29.61.11`. Compose syntax
  validation passed, but Docker daemon container creation correctly rejected the inconsistent model.
  The failed first-install path also retained the invalid network, which could block a corrected retry.

Validation:

- Merged production Compose JSON verifies the OTel address is contained by the restored subnet;
  isolated `.run` bootstrap/config validation, first-install cleanup behavior, and daemon-level
  network/container creation pass.

Risk:

- Low and deployment-scoped. The change restores the already intended private observability subnet;
  it does not expose a port, change secrets, or alter application/database behavior.

Rollback:

- Revert this entry only before deployment. Do not remove the subnet from an active stack while
  services use static observability addresses; restore the previous network from a maintenance window.

## 2026-08-11 - Add one-command public-pilot bootstrap and test client

Task:

- Prepare everything except router NAT for one Linux upload/install, two-laptop Internet acceptance,
  certificate renewal, diagnostics persistence, and later client/server upgrades.

Files changed:

- Linux bootstrap/renewal/installer and bundle builder; guarded Windows public-pilot builder;
  diagnostics storage validation/tests; production Compose/env; deployment, release, map and README docs.

Reason:

- The previous server bundle still required manual secret/TLS/Admin setup, coturn lacked an automatic
  public/private NAT mapping, single-node production diagnostics had no durable opt-in, and the public
  client endpoint kit was not reproducible.

Validation:

- Shell/PowerShell parsing, imported-certificate bootstrap integration, production Compose validation,
  isolated self-extracting `.run` dry-run, x64 MSI build/payload/endpoint verification, targeted .NET
  tests, full workspace checks and repository secret scan.

Risk:

- High: the pilot is single-node, the Windows MSI and current `.run` are unsigned, ACME could not be
  exercised before missing DNS records/NAT exist, and physical two-device/public-host acceptance plus
  independent external penetration testing remain required.

Rollback:

- Install the previous verified `.run` application bundle or use installer `--rollback`; preserve
  forward-migrated data and `/etc/peeronq`. Remove the test MSI from both laptops to undo client testing.

## 2026-08-11 - Harden signed releases and production-pilot deployment

Task:

- Add a safe Admin patch/release workflow, production web/update routing, Linux server bundle,
  public-site polish, MIT licensing, and security validation before public deployment.

Files changed:

- Admin release domain/contracts/API/UI/tests; public site/layout/runtime; Phase 6 Compose/Nginx/env;
  server `.run` builder/installer; license, maps, deployment/release/Phase 6 docs, and this changelog.

Reason:

- Database-only rollout changes did not affect the signed client manifest, the website overclaimed
  unfinished capabilities, and no authenticated artifact publication or repeatable server upgrade existed.

Validation:

- Focused .NET/Admin/public tests, Docker web build/runtime headers, Nginx and dev/production Compose
  validation, live local stack health, pnpm/NuGet audits, secret scan, and critical/high container scans.

Risk:

- Medium-high: release publication, CSRF enforcement, persistent release storage, and deployment are
  security-sensitive. Public SSH/DNS/TLS, signed MSI/server artifacts, external pentest, backup/restore,
  and physical two-device acceptance remain explicit gates.

Rollback:

- Revert this entry's files; the local release volume and generated `.run` outputs are separate. Do not
  delete production releases or roll back forward database migrations without an approved recovery plan.

## 2026-08-11 - Add two-laptop LAN development kit

Task:

- Make the Windows client installable on two physical laptops for a same-LAN remote-session test.

Files changed:

- Phase 3 LAN controller, development MSI builder, deployment/project documentation, and this changelog.

Reason:

- Release-mode development MSIs were fixed to loopback, so each laptop tried to signal to itself.

Validation:

- PowerShell parsing, validation cases, LAN stack health, targeted signaling/E2E tests, and x64 MSI build.

Risk:

- The kit trusts a repository-scoped development CA and is safe only on an isolated trusted LAN.

Rollback:

- Revert this entry and the related script/documentation changes; remove the development CA and firewall rules from test machines.

## 2026-08-11 - Fix Infrastructure page render crash

Task:

- Restore the Phase 6 Admin Infrastructure page after a client-side render failure.

Files changed:

- Admin infrastructure metrics contract, fail-soft renderer, regression fixtures, and this changelog.

Reason:

- ASP.NET serializes `WebSocketConnections` as `webSocketConnections`, while the SPA expected
  `websocketConnections`. The missing metric reached the formatter as `undefined` and triggered the
  global error boundary even though the metrics endpoint returned HTTP 200.

Validation:

- The live development API response shape was checked without exposing credentials or token values.
- Admin typecheck and all 19 focused tests passed, including corrected real-response casing and a
  missing-metric fail-soft case.

Risk:

- Low. This is a contract-casing correction for one read-only metric; authentication, server metrics,
  storage and other Admin routes are unchanged.

Rollback:

- Revert this entry and the three Admin SPA contract/test changes, then rebuild `admin-ui`.

## 2026-08-11 - Complete Phase 6 Admin operations

Task:

- Finish incomplete Admin SPA pages and connect the existing privileged Phase 6 API capabilities.

Files changed:

- Admin routing/navigation, resource and administrator-session pages, shared action dialog, API
  client/types, responsive styling, focused tests, route/design maps, and this changelog.

Reason:

- The Admin API already exposed device revocation, installation blocking, release rollout,
  diagnostic detail and administrator-session controls, but the SPA only rendered generic read-only
  tables and had no administrator-session route.

Validation:

- Admin typecheck, 18 focused tests, and production Vite build passed.
- The development Admin image rebuilt successfully, became healthy, and both `/` and
  `/admin-sessions` returned HTTP 200 through the local TLS proxy.

Risk:

- Privileged actions now reach real server endpoints and remain protected by server RBAC, MFA claims,
  CSRF, validation, audit reasons and explicit confirmation dialogs. No synthetic operational data was
  added; empty environments still show honest empty states.

Rollback:

- Revert this entry and the associated Admin SPA/map changes, then rebuild the `admin-ui` service.

## 2026-08-11 - Allow password-only Admin login in local Development

Task:

- Remove the MFA prompt from the local Phase 6 Admin workflow without weakening staging or production.

Files changed:

- Admin authentication options/service/startup guard, development Compose, focused security test,
  project map, and this changelog.

Reason:

- The local Owner account always entered the TOTP/recovery flow, slowing UI development even though
  the operator stack was running in an isolated Development environment.

Validation:

- Admin API tests passed 10/10 and development Compose validation passed.
- The Admin API image rebuilt successfully and became healthy. A real local password login returned
  `authenticated` without an MFA challenge, emitted the dedicated bypass audit event, and its probe
  session was revoked afterward.

Risk:

- The bypass grants MFA-authorized policies after a correct password, but only when both the explicit
  flag and ASP.NET Development environment are present. Any non-Development opt-in fails startup.

Rollback:

- Remove the development Compose flag or set it to `false`, then recreate `admin-api`; normal MFA
  resumes without changing the admin account or its stored MFA material.

## 2026-08-11 - Fix Admin browser API invocation

Task:

- Restore the real Admin login screen in the local Phase 6 stack and clarify the production
  `api.peeronq.com` / `admin.peeronq.com` boundary.

Files changed:

- Admin API client and regression test; Phase 6 deployment guide.

Reason:

- The client stored native browser `fetch` directly as an object method. Chrome rejected the
  rebound invocation before a network request was sent, so the healthy Admin API appeared offline.

Validation:

- Admin typecheck and build passed; all 16 Admin tests passed. The rebuilt container changed from
  the unavailable state to the real login form in a clean Chrome profile, and the request reached
  the Admin API through the same-origin proxy.

Risk:

- Low. Injected test fetchers are unchanged; the default browser fetch is now called without an
  invalid receiver. Authentication, CSRF, MFA and server authorization remain unchanged.

Rollback:

- Revert this entry and the client/test/documentation changes, then rebuild `admin-ui`.

## 2026-08-11 - Expose the complete Phase 6 development workspace

Task:

- Make the existing Cloud/Admin/Presence/Downloads/observability stack start with the normal Windows
  dev entry point and expose its interfaces from every frontend development surface.

Files changed:

- Root BAT entry points; Windows web/workspace/Phase 6 controllers; development Compose, environment
  defaults and acceptance hosts; `Phase6DevToolbar`; route tests; maps and operating/UI documentation.

Reason:

- The product preview alone started on port 5555, while the real Admin Panel lived in a separate
  Docker stack whose legacy development names did not match the generated local certificate.

Validation:

- A full `peeronq-start.bat` run rebuilt the services, applied migrations/grants, and reached healthy
  Admin and Cloud endpoints. Admin, Cloud, Grafana, Prometheus and the preview each returned HTTP 200.
- Frontend lint, typecheck, production build and all 40 tests passed; the acceptance project built
  with 0 warnings/errors; controller parsing and the repository secret scan passed.

Risk:

- Low-to-medium. Windows start now requires Docker, .NET and a populated ignored Phase 6 `.env`, and
  intentionally takes longer because it starts the real stack. The service toolbar is development-only.

Rollback:

- Revert this entry and its code/documentation changes; existing Compose volumes can be preserved.

## 2026-08-11 - Close Phase 6 release and session acceptance gaps

Task:

- Finish the local Phase 6 acceptance boundary without leaving stale-session, revocation, release
  endpoint, or cross-message cleanup races.

Files changed:

- Session heartbeat contracts/domain/application/worker/client/outbox plus the additive
  `AddSessionActivityHeartbeat` migration; Admin token/auth configuration; protected Windows release
  script/workflow; file-transfer message ordering; Phase 6 maps and completion report.

Reason:

- Connected sessions could be reconciled stale from their start time, revoked Admin access tokens
  remained usable until expiry, the signed-release controller omitted Phase 6 endpoints, and
  concurrent cancel/chunk handling could publish Canceled before partial cleanup completed.

Validation:

- Release solution build passed 30 projects with 0 warnings/errors; full .NET tests passed 415 with
  0 failures and 2 explicit opt-in Docker skips; cancel cleanup passed 10/10 stress iterations.
- Fresh PostgreSQL 17.6 infrastructure passed 31/31 with all eight migrations. Live Docker acceptance
  passed proof-bound Cloud enrollment, Presence heartbeat, signed signaling, four invalid-attestation
  rejections, 16/16 health/metrics checks, Redis ACL denials, and immediate Admin token invalidation.

Risk:

- Medium. These changes strengthen existing lifecycle and trust boundaries; they do not expand
  permissions, alter media/input security, or persist heartbeat state on the Windows client.

Rollback:

- Disable session reconciliation as an emergency measure and revert the code changes together.
  Do not down-migrate the additive database migration; restore a verified backup if schema rollback
  is operationally required.

## 2026-08-11 - Validate diagnostic archives before persistence

Task:

- Harden the Phase 6 diagnostic upload boundary against unsafe or sensitive ZIP content.

Files changed:

- `src/PeerOnQ.Cloud.Api/DiagnosticArchiveValidator.cs`, `src/PeerOnQ.Cloud.Api/DiagnosticsStorage.cs`,
  `tests/PeerOnQ.Cloud.Infrastructure.Tests/DiagnosticUploadProcessorTests.cs`, its test project,
  and `PROJECT_MAP.md`.

Reason:

- The ingress processor previously verified only upload size, ZIP magic and a client-provided digest
  before persisting the archive; it did not parse or enforce the client diagnostic schema server-side.

Validation:

- Cloud API build passed; 12 targeted valid/adversarial processor tests passed, including zero-byte
  persistence and ingress-temp cleanup for every rejected archive.

Risk:

- Low-to-medium: intentionally rejects non-client ZIP layouts, unsafe compression, malformed JSON/text,
  and unredacted sensitive patterns before storage.

Rollback:

- Revert this entry and the files listed above.

## 2026-08-11 - Decouple the PeerOnQ production build from the dev-server port

Task:

- Allow the static PeerOnQ production build to run without a `PORT` while keeping dev and preview
  startup validation fail-closed.

Files changed:

- `artifacts/peeronq/vite.config.ts` and `PROJECT_MAP.md`.

Reason:

- The Vite config validated `PORT` while the module loaded, so `vite build` incorrectly depended on
  a setting used only when serving the application.

Validation:

- PeerOnQ typecheck, lint, 38/38 tests, and build passed with `PORT` unset; dev and preview still
  rejected a missing `PORT`. The admin SPA already had no port dependency and built successfully.

Risk:

- Low. Only command-specific configuration loading changed; server port validation is unchanged.

Rollback:

- Revert the Vite config, project-map row, and this entry to restore the previous build-time
  requirement.

## 2026-08-11 - Remove Phase 6 download temporary-file bottleneck

Task:

- Stream signed Windows artifacts larger than the container tmpfs safely while guaranteeing no
  unverified origin byte reaches a client and preserving range, cache and analytics behavior.

Files changed:

- Downloads streaming/cache/options/registration/Dockerfile, shared/domain `Partial` download
  result, focused streaming tests, and the project/route maps.

Reason:

- The service copied every artifact into `/tmp` before responding, but the hardened container has a
  64 MiB tmpfs and current runtime-complete MSI files are larger than that limit. Direct origin
  streaming cannot safely support ranges or origin compromise without first publishing verified bytes.

Validation:

- Downloads Release build passed with zero warnings; 17/17 Downloads tests passed, including a
  generated 65 MiB+ artifact, malicious-origin zero-byte response, cache-hit range, single-flight
  fill, mid-fill cancellation cleanup, startup cleanup, quota/LRU and no-queue concurrency.

Risk:

- The allowlisted HTTPS object store must provide exact size and a strong ETag. The dedicated cache
  volume must remain writable only by the non-root Downloads UID and be monitored below its logical
  quota; files are digest-named, revalidated after restart and served read-only.

Rollback:

- Revert the Downloads service/options/Dockerfile, `Partial` enum/mapping, tests and map/changelog
  rows together; doing so restores the known tmpfs size blocker and is unsuitable for current MSI sizes.

## 2026-08-11 - Rebrand the shipped product as PeerOnQ

Task:

- Replace the user-visible product, installer, runtime, web, deployment, and local-controller
  identity with PeerOnQ and publish fresh Windows clients.

Files changed:

- PeerOnQ web metadata/layouts/storage migration and brand assets under `artifacts/peeronq/`.
- Native Windows identity/assets, persisted-data migration, installer, update/release scripts, and
  deployment configuration under `src/`, `installer/`, and `scripts/windows/`.
- Workspace paths, launchers, solution/package names, maps, Phase documentation, and this changelog.

Reason:

- The previous brand was still visible in the application, installer, downloads, runtime paths,
  environment contract, and deployment names.

Validation:

- Workspace typecheck/build passed with the documented `PORT=5555` and `BASE_PATH=/` environment.
- Frontend lint/typecheck passed and all 37 Vitest tests passed.
- Release .NET build passed; 296 tests passed and 2 opt-in live tests were skipped in that run.
- Self-contained x64/ARM64 publish and WiX MSI validation passed; extracted payloads match publish
  output by path and SHA-256, and contain no old-branded payload names.
- Local TLS/signaling/STUN/TURN/reconnect acceptance passed after removing external DNS from the
  loopback controller; repository secret scan and PowerShell parsing passed.

Risk:

- The downloadable packages are intentionally unsigned development artifacts. Production release
  still requires the protected signing workflow and real signing material.
- Hidden legacy storage/env/update/DPAPI identifiers and internal .NET namespaces remain only for
  safe upgrade/data compatibility; they are not presented as product branding.

Rollback:

- Revert this rebrand change set and regenerate artifacts. Legacy user data is retained by the
  migration paths, so reverting does not require recreating device identity.

## 2026-08-10 — Center native pages and polish primary actions

Task:

- Keep every native product page centered at wide window sizes and give the marked primary/mode
  actions a consistent rounded Fluent appearance.

Files changed:

- `src/Linkora.App/{MainWindow.xaml,App.xaml}`, `artifacts/peeronq/DESIGN_SYSTEM.md`, `PHASE5.md`,
  and regenerated ignored x64/ARM64 local download artifacts.

Reason:

- The page `ScrollViewer` could retain horizontal movement and arrange its max-width child beyond the
  visible right edge; primary buttons also inherited a sharper default radius.

Validation:

- Native x64 build passed with 0 warnings/errors; maximized Dashboard and Devices windows were
  captured and visually inspected; final x64/ARM64 MSI ICE03/extraction/hash gates passed; full .NET
  suite passed 274 with 2 opt-in Docker skips; final x64 SPDX SBOM passed 556/556 files.

Risk:

- Layout/style-only change; session, input, authentication, persistence, and navigation handlers are untouched.

Rollback:

- Revert the two XAML files plus documentation and republish both MSI artifacts.

## 2026-08-10 — Make Windows installers runtime-complete

Task:

- Remove the Windows App Runtime 1.8 prerequisite prompt seen after installing PeerOnQ on another
  Windows device.

Files changed:

- `src/Linkora.App/Linkora.App.csproj`, `installer/Package.wxs`,
  `installer/PeerOnQ.Installer.wixproj`,
  `scripts/windows/{build-phase5-release,test-phase5-installer,normalize-windows-app-sdk-msi-language}.ps1`,
  installer/Phase 5 documentation, repository map, and regenerated ignored local download artifacts.

Reason:

- The MSI carried a self-contained .NET runtime but the WinUI application still depended on the
  separately installed Windows App Runtime 1.8 framework package.

Validation:

- Fresh x64/ARM64 publish and MSI builds passed with 0 warnings/errors; final ICE03, administrative
  extraction, runtime sentinel, full relative-path/SHA-256 comparison, local x64 launch/module-load,
  and 556/556 SPDX SBOM validation passed.

Risk:

- Runtime-complete MSI files are roughly 74–78 MB. The ARM64 binary was cross-built/extracted but not
  launched on physical ARM64 hardware; clean-VM install and production Authenticode remain release gates.

Rollback:

- Revert this entry's project/installer/scripts/docs together and replace the generated download files;
  doing so restores the known framework-dependent package and is not suitable for clean Windows devices.

## 2026-08-10 — Unify remote connection flow and repair device-directory persistence

Task:

- Remove the duplicate Remote Access surface, make Dashboard the single connection entry, use
  digits-only public ID presentation, add the real Add Device dialog, and remove asymmetric gaps.

Files changed:

- Native WinUI Dashboard/navigation/device directory and automatic signaling startup in
  `src/Linkora.App/`
- Backward-compatible DPAPI collaboration-profile ID serialization and persistence tests
- Desktop-preview routes/navigation/ID validation plus project, route, design, and accessibility maps

Reason:

- Dashboard and Remote Access exposed the same primary operation, technical signaling/capture cards
  occupied product UI, and persisted address-book IDs deserialized to an uninitialized value. This
  made saved devices invisible until the protected profile format was corrected.

Validation:

- Native x64 build passed with zero warnings/errors; real UI Automation confirmed seven unique nav
  items, one Start Session action, balanced columns, no visible `LNK-` prefix, immediate Add Device
  refresh, and legacy profile persistence. The full .NET suite passed 274 tests with two explicit
  live Docker/TURN skips; the frontend 33-test suite passed; fresh x64/ARM64 MSI payload validation
  passed. Machine-wide install correctly refused the non-elevated shell and rolled back completely.

Risk:

- Existing prefixed IDs remain accepted by native parsing and legacy encrypted profiles are migrated
  on read; retired browser routes redirect to Dashboard.

Rollback:

- Revert this entry's WinUI layout/navigation, profile converter/test, and desktop-preview route/ID
  patches together.

## 2026-08-10 — Ship the complete real Windows product shell instead of the stale engineering UI

Task:

- Make the installed Windows client open with the production WinUI design across Dashboard, Remote
  Access, Devices, Sessions, File Transfer, Address Book, Security, and Settings; keep actions and
  states connected to the real agent; and prevent stale publish folders from being repackaged.

Files changed:

- Native product shell, shared equal-width connection-mode cards, theme, page layouts, real state
  bindings, filtered audit history, and sanitized CSV export in `src/Linkora.App/`
- Fresh development package pipeline and exact extracted-payload validation in `scripts/windows/`
- Project/design maps and this changelog entry

Reason:

- The previously published MSI was rebuilt from `dist/development/0.5.0` without first publishing the
  current application. It therefore installed the old engineering shell even though current source
  contained the product navigation. The connection-mode controls also retained compact native
  alignment instead of the three equal-width product cards.

Validation:

- Release x64 WinUI build passed with zero warnings/errors; the full .NET suite passed 272 tests with
  two explicit opt-in Docker/live-TURN skips. All eight pages, the green toggle state, shared mode
  cards, and dark/light theme were captured and visually checked from the real WinUI executable.
- Download-site TypeScript checking passed and its Vitest suite passed 31/31 tests.
- Fresh self-contained x64 and ARM64 publish/WiX rebuilds passed exact administrative-extraction
  relative-path and SHA-256 manifest comparison.
- Real x64 per-machine install launched from the public desktop shortcut at
  `C:\Program Files\PeerOnQ\PeerOnQ.exe`; the installed application DLL matched the fresh publish,
  the Dashboard was captured and visually checked, Installed apps registration was present, and clean
  uninstall removed binaries/shortcut/registration while preserving user data.
- Local HTTP download returned 45,560,100 bytes and matched the published x64 package SHA-256
  `ef62d11306865c26eb58096c1118d370fc71f04922902f27c5514ba8eb06a4cc`. The ARM64 package SHA-256 is
  `88ff9030a1babbdccc1286e9889ddb8a2767d6383e59e0d09bdf52bb6e2f1d0c`.

Risk:

- Low-medium. The shell layout, read-only audit presentation, and package freshness gate changed;
  remote screen, input, authentication, permission, signaling, and update trust implementations were
  not changed. ARM64 package structure was validated but not launched on physical ARM64 hardware.

Rollback:

- Revert this entry's WinUI layout/resources and publish-manifest checks together, then rebuild from a
  fresh publish directory. Generated unsigned development packages can be replaced by the prior local
  artifacts; preserved `%LOCALAPPDATA%\PeerOnQ` user data must not be deleted.

## 2026-08-10 — Default Windows installation to Program Files

Task:

- Install x64/ARM64 PeerOnQ under `C:\Program Files\PeerOnQ` by default, retain Browse, select the
  desktop shortcut by default, and replace stock WiX graphics with PeerOnQ branding.

Files changed:

- WiX package scope/shortcut defaults/brand assets, installer validation/docs, and project map

Reason:

- The dual-purpose package explicitly defaulted to per-user context, causing Windows Installer to
  redirect its 64-bit Program Files directory to `%LOCALAPPDATA%\Programs`.

Validation:

- Clean x64/ARM64 rebuild and MSI validation; elevated x64 Program Files install/uninstall; default
  public-desktop shortcut/target and installed icon; embedded brand hashes; localhost download hash.

Risk:

- Medium. Installation now requires UAC approval; desktop is selected by default and startup remains
  explicit opt-in.

Rollback:

- Restore dual-purpose scope, previous shortcut component GUIDs/defaults, and per-user documentation.

## 2026-08-10 — Enable Windows installer folder selection

Task:

- Make the Custom Setup Browse action selectable for the main PeerOnQ feature.

Files changed:

- WiX package feature metadata, MSI regression validation, and installer documentation

Reason:

- `WixUI_FeatureTree` only enables Browse when the selected feature has a configurable directory;
  `MainFeature` was not linked to `INSTALLFOLDER`.

Validation:

- Rebuilt and validated x64/ARM64 MSI packages, verified `MainFeature -> INSTALLFOLDER`, and passed
  an x64 custom-directory install/uninstall lifecycle plus localhost HTTP hash validation.

Risk:

- Low. The default directory and component ownership are unchanged; users can now override the path.

Rollback:

- Remove `ConfigurableDirectory` and its regression assertion, restoring the previous fixed path.

## 2026-08-10 — Guard Windows installed-app registration

Task:

- Ensure PeerOnQ is visible and removable from Windows Installed apps and Programs and Features.

Files changed:

- Windows MSI validation and installer documentation

Reason:

- The MSI already registered correctly, but no automated release gate prevented future ARP hiding or
  removal-disabling properties from being introduced.

Validation:

- Static MSI validation plus a real per-user install, registry registration check, and uninstall.

Risk:

- Low. No application payload, install scope, security behavior, or user-data policy changed.

Rollback:

- Revert the validation assertions and matching documentation/changelog entry.

## 2026-08-10 — Finish native Phase 5 client downloads and remote control

Task:

- Replace the engineering-form WinUI shell with the real product navigation, implement permission-
  scoped Windows mouse/keyboard control, and publish testable x64/ARM64 client installers locally.

Files changed:

- Application collaboration/input protocol, session coordinator, Windows `SendInput`, and tests
- WinUI main/viewer/sharing surfaces and centralized light/dark/high-contrast resources
- Website Downloads page/card/tests, package/dev controllers, maps, and Phase 5/security/installer docs

Reason:

- Full control was previously fail-closed with no input sink, and the website honestly had no client
  artifact to download. The native app also did not match the established product navigation.

Validation:

- Release build: 0 warnings/errors; full .NET suite: 272 passed, 2 opt-in skips
- Frontend typecheck/lint/build and 31 tests passed; native WinUI launch smoke passed
- Final Docker acceptance passed real TURN UDP/TCP/TLS, relay media/file, and signaling restart
- x64/ARM64 MSI validation passed; x64 install/launch/repair/uninstall and HTTP hash download passed
- Secret scan passed 558 files; pnpm/NuGet report no known vulnerable packages

Risk:

- Medium. Physical two-device/public-network input, clean VM, ARM64 launch, production signing,
  hardware codec paths, public update staging, and independent penetration testing remain external gates.

Rollback:

- Revert this entry's input/UI/download/package changes together and remove only generated ignored
  MSI files. Do not remove preserved user data or weaken signed-update/TURN trust boundaries.

## 2026-08-10 — Add Phase 5 production-readiness foundations and evidence

Task:

- Add measured performance controls, production Windows installer/release automation, signed update
  enforcement, tamper-evident audit/privacy controls, CI security gates, and honest beta evidence.

Files changed:

- Media capture/queue/statistics and source-generated hot-path logging
- Infrastructure update, audit, privacy/crash and process diagnostics services plus tests/fuzzing
- WinUI update/privacy/audit controls and semantic theme resources
- `installer/`, release/signing/measurement/security scripts, pinned SBOM tooling, and GitHub workflows
- Phase 5/security/privacy/installer/update/performance/incident/release documentation and repository maps
- Dependency lock/overrides and cross-platform pnpm package-manager guard

Reason:

- The real .NET product had no installable MSI, trusted update chain, structured retained audit,
  release signing/SBOM pipeline, repeatable benchmark, or evidence-based public-beta checklist.

Validation:

- Release build passes current .NET analyzers with warnings as errors and zero warnings/errors
- 268 .NET tests pass; 2 explicit live-Docker tests skip in the normal run; 30 Vitest tests pass
- pnpm typecheck/lint/build and dependency audits pass; .NET reports no vulnerable packages
- x64/ARM64 MSI structure and safe administrative extraction pass; local SPDX SBOM validates 280/280 files
- Secret-pattern scan passes 554 working-tree text files; signed-release/public-device gates remain open in `PHASE5.md`
- Final Release Docker acceptance passes STUN, TURN UDP/TCP/DTLS/TLS, relay video/interruption,
  TCP-relayed 8 MiB transfer, credential rejection/expiry, and signaling restart/re-authentication

Risk:

- Medium. Update/installer/audit code is security-sensitive. Public signing, clean-VM lifecycle,
  physical ARM64/public-network tests, hosted security workflows, and independent review remain required.

Rollback:

- Revert this entry's Phase 5 files and code changes together. Development builds have no compiled
  update trust anchor, so reverting cannot trigger or install an update.

## 2026-08-10 — Add repeatable Windows local Phase 3 acceptance stack

Task:

- Provide a local-domain TLS environment for signaling and a loopback-only Docker coturn stack so
  Phase 3 can be exercised before public deployment.

Files changed:

- `scripts/windows/PeerOnQ.LocalCertificate.cs` and `peeronq-phase3-local.ps1`
- `src/Linkora.Infrastructure.Deployment/docker-compose.local.yml`
- coturn development-only loopback-peer handling, ignore rules, README, project map, and deployment guide

Reason:

- The workstation had no repeatable local TLS/domain workflow, and the prior Compose baseline did
  not automatically prove authenticated UDP/TCP/TLS/DTLS allocations or credential rejection.

Validation:

- Generated and trusted a CurrentUser-only local CA/server certificate; private files remain ignored
- Docker Desktop `4.86.0` runs Nginx, signaling, and coturn healthy; local ports publish to loopback only
- HTTPS liveness/readiness, private aggregate health/metrics, and reverse-proxy WSS upgrade checks pass
- Authenticated TURN allocations pass over UDP, TCP, DTLS 1.2, and TLS 1.3; invalid and expired credentials fail
- Live `RelayOnly` WebRTC video passes with four relay candidates, `Relayed` selected on both peers, and rendered frames
- Production/local Compose validation passes; full build has zero warnings/errors and all 221 tests pass with live TURN enabled

Risk:

- Medium. The trusted development root and loopback-peer exception are local-only. Physical
  cross-network, NAT-transition, UDP-block, sleep/wake, and multi-node signaling tests remain external acceptance work.

Rollback:

- Stop the local controller, revert this entry's files, and remove only the generated root thumbprint
  recorded in `.peeronq-phase3/certs/certificate.json`; no production configuration or secret is changed.

## 2026-08-10 — Harden Phase 3 for fail-closed production deployment

Task:

- Complete non-conflicting production gaps in signaling/TURN deployment without connecting the
  repository's intentionally offline React prototype to a live backend.

Files changed:

- Signaling options/validation, exact trusted-proxy handling, and mounted-secret TURN issuance
- Immutable coturn image/config/entrypoint and hardened production Compose/Nginx/env template
- Central .NET package versions, production tests, deployment docs, README, project map, and Phase 3 record

Reason:

- Production defaults previously allowed incomplete runtime configuration, secrets had no mounted-file
  path, the deployment example was development-oriented, and requested NuGet versions caused fallback warnings.

Validation:

- `dotnet restore PeerOnQ.slnx` — passes without warnings
- `dotnet build PeerOnQ.slnx --no-restore` — passes with zero warnings and zero errors
- `dotnet test PeerOnQ.slnx --no-restore` — 220/220 pass
- All Compose YAML files parse with PyYAML; Docker/Compose/live relay tests remain unavailable locally

Risk:

- Medium-high. The production stack is intentionally single-signaling-node; real TURN/NAT and two-device
  external-network acceptance remain required before rollout or a Phase 3 completion claim.

Rollback:

- Revert this entry's signaling validator/secret-file, production deployment, package-version, test, and
  documentation changes; no production secret or data migration is involved.

## 2026-08-10 — Add Phase 3 internet connectivity and secure reconnect

Task:

- Add ICE/STUN/TURN configuration, authenticated coturn REST credentials, selected-path
  diagnostics, network-based quality adaptation, bounded reconnect/resume, signaling hardening,
  and deployable signaling/coturn infrastructure.

Files changed:

- .NET domain/application/transport/media/signaling/UI layers and targeted xUnit coverage
- `src/Linkora.Turn.Configuration/` and `src/Linkora.Infrastructure.Deployment/`
- `PHASE3.md`, README, project/route maps, ignore and line-ending rules

Reason:

- Phase 1 worked on one network only and had no relay fallback, nominated-path visibility,
  resumable ownership, reconnect safety, quality diagnostics, or production deployment baseline.
- The claimed Phase 2 input-control code is absent in this checkout, so an input-safety boundary
  was added without inventing or rewriting input behavior.

Validation:

- `dotnet build PeerOnQ.slnx --no-restore` — passes; five existing NU1603 resolution warnings
- `dotnet test PeerOnQ.slnx --no-restore` — 215/215 pass, including real same-host capture/VP8,
  authenticated signaling, nominated direct path, resume-token replay rejection, and heartbeat cleanup
- `git diff --check` — passes
- Docker/Compose/live coturn and two-device external-network tests not run because Docker and a
  second device/network are unavailable; `PHASE3.md` records every actual condition

Risk:

- Medium-high. Connectivity and reconnect paths are security-sensitive; multi-node signaling still
  requires a shared atomic session/presence implementation, and real TURN/NAT testing remains open.

Rollback:

- Revert this entry and the Phase 3 files/changes listed above; no database migration or production
  secret is involved.

## 2026-08-10 — Clear Fallow Problems-panel findings

Task:

- Remove real unused dependencies/code and configure Fallow to exclude intentional
  vendored/generated/throwaway analysis noise.

Files changed:

- `.fallowrc.json` — product-aware dead-code and duplication scope
- API/frontend/database/root package manifests and `pnpm-lock.yaml` — unused dependencies removed
- Frontend local-only types/exports, obsolete `AppShell.tsx`, and shared portal status markup
- Project, route, and design-system maps

Reason:

- Zero-config Fallow analyzed vendored shadcn and the throwaway sandbox, while also flagging
  dynamic Pino build dependencies and platform-exclusion overrides that static imports cannot see.
- Several scaffold dependencies and the obsolete `AppShell` alias were genuinely unused.

Validation:

- `fallow dead-code --no-cache --summary` — no issues
- `fallow dupes --no-cache --summary` — no duplication found
- `pnpm run lint` — passes with zero warnings
- `pnpm run typecheck` — passes across the workspace
- `pnpm run test` — 9 files, 30 tests, all passing
- `pnpm run build` — passes across the workspace

Risk:

- Low. Runtime behavior is unchanged; dependency removal is limited to packages with no imports or
  build/config consumers.

Rollback:

- Revert this entry, `.fallowrc.json`, package manifests/lockfile, and the targeted cleanup files.

## 2026-08-10 — Separate public, portal, and desktop-preview routes

Task:

- Split the mixed web/desktop frontend into independent public, account-portal, and desktop UI
  preview route trees and layouts.

Files changed:

- `artifacts/peeronq/src/app/router/index.tsx`, `src/layouts/`, and grouped `src/pages/` routes
- `src/components/OpenAppButton.tsx`, desktop `Sidebar`/`Topbar`, and landing-page links
- `src/test/routeSeparation.test.tsx`
- Root/frontend architecture, route, design-system, and accessibility maps

Reason:

- Public pages and desktop controls previously shared one `AppShell`; public “Open App” links
  navigated to `/dashboard` instead of attempting the native application protocol.

Validation:

- `pnpm run lint` — passes with existing React Compiler advisory warnings
- `pnpm run typecheck` — passes across the workspace
- `pnpm run test` — 9 files, 30 tests, all passing
- `pnpm run build` — passes across the workspace

Risk:

- Low-medium. Route URLs and layout ownership changed; legacy desktop URLs redirect to the new
  preview paths.

Rollback:

- Revert this changelog entry and the route-separation files listed above.

## 2026-08-06 — Two-instance acceptance run: two real bugs found and fixed

Task:

- Run the acceptance flow with two real app instances, and show the PeerOnQ ID without its
  protocol prefix.

Files changed:

- `src/Linkora.App/AppServices.cs` — `PEERONQ_DATA_DIR` override so two instances on one
  machine get separate identities
- `src/Linkora.Transport/WebSocketSignalingClient.cs` — reconnect now tears the previous
  connection down, ignores drops from a stale loop, and runs one reconnect at a time
- `src/Linkora.App/ViewerWindow.xaml.cs` — pixel buffer obtained via the CsWinRT cast; render
  failures surface in the statistics bar instead of being swallowed
- `src/Linkora.Domain/Identity/LinkoraId.cs` — `Display` / `MaskedDisplay` (no `LNK-` prefix)
- UI, permission dialog and session labels use the display form; the wire, SQLite and logs keep
  the prefix

Bugs found by running it (not by the test suite):

- Reconnect cascade: the server displaced one connection with the next, and every stale loop
  started another reconnect — an endless loop that only appeared with real sockets.
- Black viewer: `(IBufferByteAccess)(object)bitmap.PixelBuffer` throws under CsWinRT; the
  swallowed exception left the window black while statistics counted decoded frames.

Validation:

- `dotnet test PeerOnQ.slnx` — 198 tests, all passing
- Two published instances: distinct IDs, both registered, no reconnect churn over 40 s
- Permission dialog, accept, capture start, `26 fps / 116 kbps / 888 frames`, remote screen
  visibly rendered, Stop sharing → capture stopped and session closed

Still open:

- The two-**physical**-computer run
- A hardware H.264 encoder implementation (detection and fallback ship, the encoder does not)

Rollback:

- `git checkout -- src tests PHASE1.md`

## 2026-08-06 — Phase 1 follow-up: closed the six open items

Task:

- Finish the items left open after the first Phase 1 pass.

Files changed:

- `src/Linkora.Platform.Windows/Capture/FrameScaler.cs` (new) + capture wiring — real
  720p/1080p/Native/Automatic scaling with box averaging, plus a live downscale factor
- `src/Linkora.Media/Pipeline/AdaptiveQualityController.cs` (new) — five-rung ladder driven by
  RTCP loss and queue drops; `FrameRateLimiter.SetTargetFps` makes the rate adjustable
- `src/Linkora.Transport/Protocol/SignalingMessages.cs` — `session.displays` and
  `session.display.select`; server relays them, coordinator switches the shared screen
- `src/Linkora.Platform.Windows/Media/HardwareEncoderProbe.cs` (new) +
  `src/Linkora.Media/Pipeline/VideoEncoderSelector.cs` (new) — Media Foundation detection and
  an encoder policy that never claims unused acceleration
- `src/Linkora.Signaling.Server/Security/DevicePinStore.cs` (new) — pins survive a restart;
  TLS is required for non-loopback callers
- `src/Linkora.App` — viewer monitor picker wired to the coordinator
- `scripts/windows/publish-phase1.ps1` (new) — self-contained artifacts for a two-machine test
- `Directory.Packages.props` — pinned SQLitePCLRaw above advisory GHSA-2m69-gcr7-jv3q

Validation:

- `dotnet build PeerOnQ.slnx` — succeeds, no warnings
- `dotnet test PeerOnQ.slnx` — 193 tests, all passing
- The WinUI 3 app was launched, screenshotted, and registered against a running signaling
  server (`devicesOnline: 1`)
- Both published artifacts start from `dist/`

Still open:

- The two-physical-computer acceptance run (needs a second Windows machine)
- A hardware H.264 encoder implementation; detection and fallback ship, the encoder does not

Risk:

- Medium. Capture conversion, media pipeline and the signaling protocol all changed.

Rollback:

- `git checkout -- src tests Directory.Packages.props` and delete `scripts/windows/publish-phase1.ps1`.

## 2026-08-06 — Phase 1: real two-device view-only remote access (.NET)

Task:

- Build the first working PeerOnQ product for two Windows PCs: device identity, secure
  signaling, explicit permission, screen capture, WebRTC video, view-only, no input control.

Files changed:

- `src/Linkora.{Domain,Application,Infrastructure,Transport,Media,Platform.Windows,Signaling.Server,App}`
- `tests/Linkora.{Domain,Application,Infrastructure,Media,Signaling,EndToEnd}.Tests`
- `PeerOnQ.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `PHASE1.md`

Reason:

- The repo previously held only the web prototype; Phase 1 needed the real Windows product.

Key decisions:

- Registration proof is an ECDSA P-256 signature over a server challenge, verified against a
  trust-on-first-use pinned public key. The server never learns a device secret.
- The session id is chosen by the requester; the server rejects duplicates.
- Media is SIPSorcery WebRTC with one VP8 track. No audio and no data channel are created, so
  there is no transport for input events at all.
- Capture frames go through a bounded queue (newest wins), so a slow encoder never blocks
  capture and the queue cannot grow.

Validation:

- `dotnet build PeerOnQ.slnx` — succeeds, 0 warnings
- `dotnet test PeerOnQ.slnx` — 136 tests, all passing
- End-to-end suite drives real capture + real WebRTC + real signaling on one machine
- Two-physical-computer acceptance run: NOT performed

Risk:

- Medium. New product surface. Server key pinning is in-memory; WinUI windows are unverified
  on real hardware.

Rollback:

- Delete `src/`, `tests/`, `PeerOnQ.slnx`, `Directory.*.props`, `PHASE1.md`.

## 2026-08-06 — Windows control scripts on port 5555, scoped to this repo only

Task:

- Control the app locally from the repo root on <http://localhost:5555>, without the scripts
  being able to affect any other program or folder.

Files changed:

- `scripts/windows/peeronq-dev.ps1` (new) — start/stop/restart/status controller
- `peeronq-start.bat`, `peeronq-stop.bat`, `peeronq-restart.bat`, `peeronq-status.bat` (new)
- `.gitignore` — ignore `.peeronq-run/`
- README.md, PROJECT_MAP.md — documented them

Reason:

- Manual `pnpm --filter ... run dev` needs `PORT` and `BASE_PATH` set every time.
- The first version killed "whatever listens on 5555" and matched windows by title, which could
  hit an unrelated program. Replaced with PID tracking.

How the scoping works:

- `start` writes the launcher PID to `.peeronq-run/dev-server.pid`.
- `stop` kills only that PID tree, and only if the process command line still contains
  `@workspace/peeronq` (so a recycled PID cannot be hit).
- A foreign owner of port 5555 is reported and left running by both start and stop.
- The controller resolves its root from `$PSScriptRoot` and exits unless that folder contains
  `pnpm-workspace.yaml` and `artifacts/peeronq/`.

Validation:

- start → PID recorded, port LISTENING, HTTP 200; status reports "serving PeerOnQ"
- stop → tree killed, port free, PID file removed
- restart → full cycle, exit 0, HTTP 200
- foreign-process test: an unrelated listener was put on 5555; `start` exited 1 and `stop`
  exited 0, both refused to act, and the foreign PID was still alive afterwards

Risk:

- Low. Nothing in the app changed.

Notes for future agents:

- The `linkora-` prefix avoids a PATH collision with `C:\Program Files\ConfigCure Lens\stop.bat`.
- This machine sets `NoDefaultCurrentDirectoryInExePath=1`, so scripts must be invoked as
  `.\name.bat` from a terminal.
- The scripts call `netstat`/`findstr`/`taskkill`/`ping` by full System32 path; Git Bash's
  `timeout` shadows the Windows one, and `timeout.exe` fails without a console.

Rollback:

- Delete the three `.bat` files.

## 2026-08-06 — Close the gaps against the PeerOnQ master prompt

Task:

- Audit the frontend against the original build spec and implement what was missing.

Files changed:

- `src/components/DeviceDetailDrawer.tsx` (new) + `src/pages/DevicesPage.tsx` — device detail
  drawer with connect/remove actions (spec required one; device cards had no click target)
- `src/pages/AddressBookPage.tsx` — edit dialog, remove action, tags, working "create group"
  button (the `MoreVertical` and group `+` buttons previously did nothing)
- `src/pages/SessionsPage.tsx` — date-range filter (24h / 7d / 30d), purity-safe timestamp
- `src/pages/DownloadsPage.tsx` + `src/components/PlatformCard.tsx` — Android and iOS split into
  separate cards; the active button now reads "Windows build not published yet"
- `src/components/ErrorBoundary.tsx` — copy button for the sanitized error ID
- `src/components/ToastProvider.tsx` (new) + `src/App.tsx` — toast outlet mounted through a
  provider; explicit `/not-found` route added next to the wildcard fallback
- `src/lib/validation.ts` + `src/features/settings/useSettings.ts` — `settingsSchema`; stored
  settings are now validated and repaired instead of trusted
- `src/features/connections/useConnectionState.ts`, `src/features/files/useFileTransfer.ts`,
  `src/types/index.ts` — typed stubs, new `FileTransfer` type, no `any`
- `src/test/setup.ts` — in-memory `localStorage` when jsdom exposes an incomplete one
- `src/test/permissionDialog.test.tsx` — dialog keyboard accessibility test
- `eslint.config.js`, `package.json` (peeronq + root), `.prettierrc.json`, `.prettierignore` —
  ESLint/Prettier tooling and `lint` / `test` / `format` scripts
- Unused imports removed across 10 files; `any` removed from `SettingsPage`

Reason:

- The spec required these flows, tooling, and quality gates; they were absent or dead-ended.
- Five tests failed on Windows because the jsdom `localStorage` lacked the Storage API.

Validation:

- `pnpm run typecheck` — passes (all 4 packages)
- `pnpm --filter @workspace/peeronq run lint` — 0 errors, 6 warnings (React Compiler advisories)
- `pnpm --filter @workspace/peeronq run test` — 8 files, 18 tests, all passing
- `PORT=23586 BASE_PATH=/ pnpm --filter @workspace/peeronq run build` — succeeds

Risk:

- Low-medium. Address Book was restructured; devices/sessions changes are additive.
- ESLint was added as a devDependency of `@workspace/peeronq`.

Rollback:

- `git checkout -- artifacts/peeronq package.json` and delete `.prettierrc.json`,
  `.prettierignore`, `artifacts/peeronq/eslint.config.js`.

## 2026-08-06 — Fill in the agent documentation set

Task:

- Replace the `<PLACEHOLDER>` template content in the root `.md` files with the real project
  map, so agents stop re-scanning the repository on every task.

Files changed:

- PROJECT_MAP.md — real stack, directories, 5 core flows, critical files, env vars, risks
- ROUTES_MAP.md — API route, all 15 frontend routes, repositories/hooks, localStorage keys, models
- AGENTS.md, CLAUDE.md, CODEX.md — project quick facts, pnpm commands (were `npm`), forbidden paths
- README.md — real project README instead of the template's own instructions
- replit.md — filled the "populate as you build" sections
- TASK_TEMPLATE.md — single-language placeholders
- artifacts/peeronq/README.md, FRONTEND_ARCHITECTURE.md, DESIGN_SYSTEM.md,
  INTEGRATION_CONTRACT.md, ACCESSIBILITY.md — synced with the current code

Reason:

- The docs were an unfilled template. Agents could not use them to locate files, so every task
  started with a full repo scan and wasted tokens.
- The validation commands said `npm`, which the root `preinstall` script rejects.
- `DESIGN_SYSTEM.md` listed a `ToastProvider` component that does not exist, and the frontend
  docs predated the `/remote-access` page.

Validation:

- Cross-checked every path, route, script, env var, and storage key against the source files.
- Docs only — no code touched, so no build/test run was required.

Risk:

- Low. Documentation only. The maps go stale if future changes skip the update rule in AGENTS.md.

Rollback:

- `git checkout -- *.md artifacts/peeronq/*.md`

## 2026-08-06 — Remote Access page (uncommitted work in tree)

Task:

- Add a dedicated `/remote-access` page with connect form, connection mode, quality selector,
  and approval/notification toggles.

Files changed:

- artifacts/peeronq/src/pages/RemoteAccessPage.tsx (new)
- artifacts/peeronq/src/App.tsx — route registered
- artifacts/peeronq/src/components/Sidebar.tsx — nav entry added
- artifacts/peeronq/src/pages/not-found.tsx removed in favor of `NotFoundPage.tsx`
- artifacts/peeronq/DESIGN_SYSTEM.md (new)

Reason:

- The connect flow needed its own page instead of living on the dashboard.

Validation:

- Not recorded at the time of writing.

Risk:

- Low. Prototype UI only; the submit handler just opens `PermissionDialog`.

Rollback:

- Revert the files above; remove the `/remote-access` route and nav item.
## 2026-08-10 — Phase 4 collaboration, trust and unattended access

Task:

- Implement production-oriented file/folder transfer, plain-text clipboard, trusted devices,
  unattended authentication, encrypted address book/groups, presence and security audit without
  rewriting the existing screen, authentication, ICE/TURN or reconnect implementation.

Changed:

- Added immutable Phase 4 session profiles/permission masks to domain, signaling and coordinator.
- Added an ordered DTLS/SCTP collaboration channel that is absent from ViewOnly sessions.
- Added versioned/bounded transfer and clipboard protocol, secure paths, `.partial` lifecycle,
  chunk/file SHA-256, resume, collision policies, disk limits and Windows AMSI scanning.
- Added explicit plain-text clipboard sync with size/type checks, pause, cleanup and loop prevention.
- Added DPAPI-protected address/trust/unattended profile, SQLite schema-v2 security audit, actual
  signaling presence, fingerprint-change revocation, PBKDF2/HMAC challenge proof, recovery codes
  and lockout.
- Added WinUI management for profiles, transfers, clipboard, address book/groups, trusted devices,
  unattended setup/revocation and persistent permission/access indicators.
- Added Phase 4 unit, integration, real WebRTC data-channel/file-transfer and regression tests plus
  `PHASE4.md`.

Validation:

- `dotnet build PeerOnQ.slnx --no-restore` passed with zero warnings.
- Application 49/49, Media 43/43 (+ one opt-in live TURN test), Signaling 44/44 and Infrastructure
  21/21 passed in targeted runs.
- `dotnet test PeerOnQ.slnx --no-build` passed 247 tests with zero failures; the one skipped test is
  the explicitly opt-in external live-TURN case.
- Docker local signaling image rebuilt; TLS readiness/metrics and coturn UDP/TCP/DTLS/TLS,
  authentication, invalid credentials and quota checks passed with the repository controller.

Risk:

- High security surface. Public two-device/NAT/sleep/large-file soak tests still require external
  hardware and deployment. FullControl remains fail-closed because Phase 2 input control is absent.

Rollback:

- Revert this changelog entry and the Phase 4 domain/application/transport/media/infrastructure/UI
  files together. SQLite schema v2 is additive; leaving `security_audit` in place is harmless.

## 2026-08-10 - Docker network acceptance expansion

Task:

- Execute every meaningful Phase 3/4 network condition that can be reproduced on the local Docker
  host and make the checks repeatable.

Changed:

- Expanded the local acceptance controller to run real relay-only media over TURN UDP, TURN TCP,
  and TURN-over-TLS, pause coturn during active media, transfer 8 MiB through TURN TCP, and restart
  signaling with authenticated clients connected.
- Added a live signaling restart test that requires reauthentication, rejects stale resume state,
  and establishes a fresh session after restart.
- Fixed file-transfer reconnect so the receiver leaves `Paused` when requesting resume and safely
  accepts a validated in-flight chunk without expanding permission or losing partial progress.
- Added deterministic large-file generation, size/SHA-256 validation, and transfer-interruption
  regression coverage.

Validation:

- Real relay video passed over UDP 3478, TCP 3478, and TLS/TCP 5349.
- A two-second full coturn pause during active relay video recovered.
- Signaling container restart reauthenticated both clients, rejected the stale session resume, and
  allowed a fresh attended session.
- A 64 MiB file completed over relay-only TURN TCP in 4m38s with matching size and SHA-256; the
  repeatable controller uses 8 MiB to keep routine acceptance bounded.

Risk:

- Medium. The tests exercise destructive container pause/restart only when explicitly enabled by
  the repository-scoped controller. Public NAT, separate physical devices, and sleep/wake remain
  external acceptance work.

Rollback:

- Revert this entry, the acceptance script/test changes, and the two targeted reconnect changes in
  `FileTransferService.cs`.

## 2026-08-10 - Install repository-scoped engineering skill

Task:

- Install the supplied `pro-software-engineer` skill pack and merge its marked engineering
  contract without weakening existing repository instructions.

Changed:

- Installed the supplied skill under `.agents/skills/pro-software-engineer/`.
- Appended the idempotently marked `Codex engineering contract` section to `AGENTS.md`.
- Registered the repository-scoped skill directory in `PROJECT_MAP.md`.

Validation:

- Verified the destination did not exist before installation, copied only manifest-listed skill
  content, compared source/destination SHA-256 hashes, and confirmed exactly one contract marker.
- Documentation/agent configuration only; product build and tests were not rerun.

Risk:

- Low. No product code, dependency, runtime configuration, or production behavior changed.

Rollback:

- Remove `.agents/skills/pro-software-engineer/`, the marked contract block in `AGENTS.md`, and
  this changelog entry.

## 2026-08-10 - Install Codex UI/UX skill

Task:

- Install the supplied `nextlevelbuilder/ui-ux-pro-max-skill` for Codex only.

Changed:

- Installed the current GitHub `ui-ux-pro-max` skill under `.agents/skills/ui-ux-pro-max/` from
  upstream main commit `abb7f2fd5a083fa1ff55c326a963ff0d95c33f99`.
- Replaced Claude-specific command paths with repository-scoped Codex paths.
- Removed six unrelated/Claude-oriented sibling skills emitted by the older published CLI package;
  the repository retains only `pro-software-engineer` and `ui-ux-pro-max`.
- Updated `PROJECT_MAP.md` to describe the repository skill directory.

Validation:

- Compiled all 6 installed Python files without executing imports.
- Real PeerOnQ design-system JSON and WinUI stack-guidance searches returned results.
- Compared all 42 intentionally unmodified files with the checked-out upstream source after line-ending normalization.
- Confirmed no `CLAUDE_PLUGIN_ROOT`, `.claude/skills`, or Claude references remain in the installed
  UI/UX skill.

Risk:

- Low. Repository-scoped agent guidance/data only; product code and runtime behavior are unchanged.

Rollback:

- Remove `.agents/skills/ui-ux-pro-max/`, revert the project-map row, and remove this entry.

## 2026-08-11 - Phase 6 cloud platform and operations control plane

Task:

- Implement the production cloud, presence, downloads, diagnostics, admin, observability, retention,
  backup and deployment foundation without rewriting the working Phase 1-5 remote-access paths.

Changed:

- Added versioned shared contracts and layered Cloud Domain/Application/Infrastructure projects with
  a 20-entity EF Core/PostgreSQL model, Redis atomic stores and four forward-only migrations.
- Added secure installation/device bootstrap, canonical ECDSA challenge authentication, short-lived
  device tokens, installation telemetry, session/update outboxes and build-fixed endpoint discovery.
- Added authenticated SignalR presence leases, real lifecycle/download/update analytics, signed-only
  release delivery, consented diagnostics ingestion and governed legal-hold-aware retention.
- Added MFA/RBAC/CSRF Admin API and responsive production admin SPA with immutable privileged-action
  auditing, diagnostics access evidence, session revocation and signed-release rollout control.
- Added shared structured logging/OTel/health/metrics, Prometheus/Loki/Tempo/Grafana/Alertmanager,
  real coturn recording rules, development/staging Compose and tested backup/restore automation.
- Added Phase 6 tests, project/route maps, privacy/data-classification/retention documents, operator
  runbooks and `PHASE6.md` with actual evidence and external staging gates.

Validation:

- Release solution build passed for 30 projects with 0 warnings and 0 errors.
- Full .NET suite passed 360 tests; two explicit opt-in Docker tests were skipped by the generic run.
- Real PostgreSQL 17 + Redis 8 infrastructure suite passed 12/12, including all migrations,
  append-only triggers, governed retention and Redis atomicity.
- Full pnpm typecheck/test/build passed; admin 15/15 and offline product 38/38 tests passed.
- Current Docker stack returned 16/16 Phase 6 health/metrics checks, 11/11 Prometheus targets UP,
  live MFA/RBAC queries, authenticated Alertmanager ingestion and a successful PostgreSQL restore.
- Dependency, container critical/high vulnerability, metrics-sensitive-data and repository secret
  scans found no production blocker.

Risk:

- High security/operations surface. Public DNS/TLS, signed release/CDN delivery, physical installed
  clients, managed backup restoration and multi-region failover remain required staging gates.
- Audit retention is fail-safe off by default. Separate runtime DB roles need explicit EXECUTE grants
  for only the governed retention functions.

Rollback:

- Stop the Phase 6 Compose stack and disable client cloud endpoint metadata to return to the local
  Phase 1-5 product. The database migrations are forward-only; restore from a verified backup rather
  than attempting a destructive down migration.

## 2026-08-11 - Proof-bound cloud signaling attestation

Task:

- Remove server-alias first-claim/TOFU squatting from production signaling while retaining the
  existing fresh ECDSA challenge proof and explicit local-development fallback.

Changed:

- Added a strict bounded `pqsa1` P-256 token binding issuer, audience, numeric alias, cloud DeviceId,
  InstallationId, exact SPKI fingerprint, key ID, issue/expiry times and unpredictable token ID.
- Cloud now issues the five-minute attestation only after proof-bound authentication using a
  mounted private-key file; signaling receives current/previous public keys only.
- Staging/Production signaling fails closed on missing/invalid attestation configuration. Valid
  attestations are authoritative and stateless for horizontal scaling; node-local pins are bypassed.
- The Windows cloud client retains the token only in memory, refreshes based on either access-token
  or attestation expiry, and fetches the latest attestation for every registration/reconnect.

Validation:

- Cloud API, Windows app and signaling builds completed with zero warnings/errors.
- Signaling attestation tests cover valid registration, stolen-token/no-key rejection, exact
  alias/key/audience/signature/expiry binding, parser bounds, rotation overlap and explicit dev TOFU.
- Cloud issuer concurrency/configuration tests and client reconnect-after-attestation-expiry tests passed.
- The real two-device EndToEnd harness explicitly selects Testing-only TOFU fallback; all 37
  EndToEnd tests passed without changing Production/Staging validation.

Risk:

- High trust-boundary change. Deployment must mount the Cloud private key only into Cloud and the
  matching current/previous public keys only into signaling, with a bounded overlap during rotation.

Rollback:

- Revert this entry's source/config changes together. Production/Staging must not be switched back
  to TOFU; disabling cloud attestation is permitted only for explicit local Development/Testing.

## 2026-08-11 - Connected-session activity reconciliation

Task:

- Keep legitimate long-running remote sessions active without leaving abandoned sessions online.

Changed:

- Added a bounded authenticated `/v1/sessions/heartbeat` contract tied to an exact session
  participant, with clock-skew validation and server-observed activity time.
- Added `LastActivityAtUtc` plus a bounded last-event idempotency marker through a forward-only,
  existing-row-backfilled PostgreSQL migration.
- The Windows cloud loop now heartbeats only in-memory connected sessions; terminal events remove
  them immediately and a process restart cannot resurrect them.
- Stale reconciliation now uses configurable negotiation and connected-inactivity thresholds.
  Legitimate late terminal events correct stale rows, and retention remains based on terminal time.
- Scoped `DeviceRegistrationService` registration to the Cloud attestation composition so
  Presence/Admin hosts do not require the Cloud private signer.

Validation:

- Cloud Application 19/19, Cloud Domain 7/7, client heartbeat/outbox 8/8, and Cloud Infrastructure
  31/31 tests passed. The infrastructure suite also passed against a fresh PostgreSQL 17.6
  container, including forward migration, governed retention, heartbeat-session retention and
  no-inherit migration paths.
- Cloud Infrastructure and Windows Infrastructure builds completed with zero warnings/errors.

Risk:

- Heartbeats are operational metadata only; they do not alter permissions, authentication, media,
  input, or security settings. A three-minute inactivity threshold tolerates at least three
  configured client heartbeat intervals.

Rollback:

- Disable only `CloudWorkers:EnableSessionReconciliation` during an emergency rollback, then
  revert the endpoint/client/worker changes together. Keep the additive migration applied.

## 2026-08-16 - Windows LAN development build reliability

Task:

- Remove the repeated firewall, certificate-path, and MSI validation failures blocking physical
  laptop testing on a trusted LAN.

Changed:

- Passed TURN ports to Windows Firewall as individual values instead of an invalid comma-delimited
  string.
- Resolved a supplied development root certificate through the PowerShell filesystem provider so
  relative paths work even when the process working directory differs from the shell location.
- Quoted MSI administrative-extraction paths before passing them through `Start-Process`, including
  repositories whose paths contain spaces.
- Kept development artifact notices as a string array so each instruction is written on its own
  line even when the build has only one initial notice.

Validation:

- All three changed PowerShell scripts parse successfully.
- MSI administrative extraction and payload comparison pass from the repository path containing
  spaces.
- The x64 `0.5.3` LAN development kit builds successfully as an upgrade over pre-existing `0.5.2`
  test installations, for
  `wss://signal.10.0.0.10.sslip.io:5443/ws` with its development root certificate included.
- The generated notice retains separate artifact, endpoint, and certificate-trust instructions.

Risk:

- Changes are limited to local Windows development tooling. Firewall rules remain restricted to
  Private profiles, the selected bind address, and `LocalSubnet` clients.

Rollback:

- Revert this entry and the three Windows script changes together; manual firewall rules and
  absolute certificate paths remain available as temporary workarounds.

## 2026-08-16 - One-install LAN client with three attended modes

Task:

- Produce a physical-laptop client that installs once, skips cloud enrollment for the explicit LAN
  endpoint, and supports attended view-only, full-control, and file-transfer-only sessions.

Changed:

- Expanded the exact session scope to attended view-only, full control, and file transfer; full
  control grants only screen viewing plus input, while file transfer is a separate data-only grant.
- Exposed all three choices in the WinUI request surface, enabled the existing File Transfer page,
  and delayed its file/folder controls until the encrypted data channel is ready.
- Enabled viewer input capture only when the accepted immutable permission scope contains
  `ControlInput`; file-transfer-only sessions start no capture and open no viewer window.
- Marked explicit LAN builds in assembly metadata so they cannot enter the cloud-enrollment path.
- Bundled the public local-development CA inside the MSI and pinned it only for the configured WSS
  connection with hostname validation preserved; no Windows trust store is modified.
- Updated SIPSorcery and its media abstractions from `10.0.13` to `10.0.15`, which contains the
  upstream fixes for the two high-severity WebRTC/TURN denial-of-service advisories found at build.
- Propagated the requested development version into assembly/file metadata and documented the
  single-MSI physical-laptop flow.

Validation:

- Targeted domain, coordinator, signaling, development-certificate, and real WebRTC video/input/file
  data-channel tests pass.
- A two-device in-process E2E test passes a 256 KiB file through real signaling and WebRTC after
  remote acceptance, verifies byte equality, and confirms that no screen/input permission is added.
- WinUI x64 Release build succeeds for LAN development metadata; the final tested MSI is version
  `0.6.1` so Windows Installer upgrades every earlier `0.5.x`/intermediate `0.6.0` test build.
- The complete solution passes 460 tests with zero failures; two opt-in live-network cases are
  skipped by the normal suite and pass in the explicit local-stack acceptance run.
- Live acceptance passes WSS health, signaling, STUN, authenticated TURN allocation over UDP, TCP,
  DTLS, and TLS, relay-only video over UDP/TCP, relay-only file transfer, and signaling restart.
- MSI administrative extraction/payload verification passes, and the packaged `0.6.1.0` client
  registers with local signaling from a clean profile without enrollment or connection errors.
- The final WinUI window was captured from the packaged client and visually verified with three
  equal mode controls; NuGet reports no known vulnerable packages for the application graph.

Risk:

- Full control is restricted to attended sessions, explicit remote acceptance, an explicit local
  viewer toggle, non-elevated Windows input boundaries, and the exact `ViewScreen|ControlInput`
  permission mask. The development MSI remains unsigned and LAN-only.

Rollback:

- Revert this entry's domain/application/media/transport/UI/build changes together and return the
  LAN instructions to separate certificate import plus view-only sessions.

## 2026-08-16 - Refresh LAN signaling protocol before physical sessions

Task:

- Fix physical-client sessions that reached permission acceptance but never opened media, while
  full-control and file-transfer requests were rejected by the running local server.

Changed:

- Made the Phase 3 Docker start path rebuild the current signaling source even when the existing
  containers report healthy; unchanged Docker layers remain cached and unchanged services stay up.
- Made sharer startup failures notify the peer immediately and release capture/media/session state
  instead of leaving the viewer waiting for the negotiation timeout.
- Advanced the replacement LAN MSI instructions to `0.6.2` so installed `0.6.1` clients upgrade.

Validation:

- Reproduced the pre-fix server rejection as `unsupported_mode`, rebuilt the Docker signaling image
  through the corrected start path, and confirmed the stack returns healthy.
- The complete solution passes 461 tests with zero failures; the two normal opt-in live-network
  cases are skipped there and pass in the explicit local-stack run.
- Live acceptance passes WSS signaling, authenticated TURN over UDP/TCP/DTLS/TLS, relay-only video
  over UDP and TCP, relay-only file transfer over UDP, and signaling restart/re-authentication.
- Two isolated packaged `0.6.2` WinUI processes complete explicit Accept flows for all three modes:
  view-only opens the viewer, full control enables the local `Control input` toggle, and file
  transfer enables file/folder selection after the encrypted data channel opens.
- The `0.6.2` MSI passes ICE03, administrative extraction, and payload-hash validation; a clean
  packaged profile registers with the LAN server without enrollment or signaling errors.
- NuGet reports no known vulnerable direct or transitive packages for the application graph.

Risk:

- The local start command now checks/builds Docker source on every invocation and can recreate the
  signaling service when its image changed. Active development sessions should not be running then.

Rollback:

- Revert this entry's Phase 3 start and coordinator failure-propagation changes, then rebuild the
  local signaling image manually before using the three-mode client.

## 2026-08-16 - Make full control immediate and improve desktop-stream quality

Task:

- Fix accepted full-control sessions that still behaved as view-only, prevent the PeerOnQ host UI
  from covering the shared desktop, and remove diagnostic clutter from the viewer.
- Keep a 1920x1080 LAN stream from immediately falling back to 960x540 under software VP8 load.

Changed:

- Auto-enable viewer input only after the target explicitly accepts `FullControl` and the encrypted
  collaboration channel reports ready; the visible control toggle remains available to pause input.
- Configure the bundled libvpx encoder for real-time desktop throughput (`VP8E_SET_CPUUSED=16`) and
  disable camera-oriented noise filtering instead of leaving the expensive default CPU setting.
- Maximize a newly opened viewer, minimize the sharer's main PeerOnQ window while sharing, restore it
  when the session ends, and retain the separate visible sharing/control safety indicator.
- Simplify the viewer toolbar and footer: keep primary session controls visible, show the monitor
  picker only for multiple displays, and move actual-size/diagnostic actions into overflow.
- Advance LAN-development installer guidance and status output to version `0.6.4`.

Validation:

- Targeted VP8/adaptive-quality coverage passes 25 tests, including a real 1920x1080 desktop-pattern
  encode/decode case.
- The complete solution passes 461 tests with zero failures; two opt-in live TURN/Docker cases remain
  skipped by the normal suite.
- WinUI x64 Release builds with zero warnings/errors. The `0.6.4` MSI passes ICE03, administrative
  extraction, and packaged-payload verification.
- Two isolated packaged `0.6.4` WinUI processes complete explicit Full Control acceptance against the
  live WSS server: input becomes active, the viewer maximizes, the target app minimizes, and the
  stream remains 1920x1080 for the observed interval with zero adaptive downscale events.
- NuGet reports no known vulnerable direct or transitive packages for the application graph.

Risk:

- The current software VP8 path now prioritizes real-time throughput and latency; visual quality for
  fast complex motion still depends on endpoint CPU and available network bitrate.
- Full control remains attended and non-elevated, and starts only after the immutable accepted
  permission includes screen viewing plus input and the encrypted channel is ready.

Rollback:

- Revert this entry's media/UI/session-lifecycle changes together and install the previous LAN MSI.

## 2026-08-16 - Align full-control input and enable native 4K LAN sessions

Task:

- Fix the viewer pointer landing at a different position on the controlled device, including Fit,
  actual-size scrolling, multi-monitor DPI, and pointer-capture interruption cases.
- Make the LAN-development client request the source display's native resolution, including 4K,
  without making fixed native/16 Mbps the production default.

Changed:

- Map pointer input through the real transformed image rectangle instead of reconstructing an
  imaginary rectangle from the overlay. Letterbox bars are rejected, ScrollViewer offsets are
  preserved, and actual-size dimensions are recalculated in DIPs when XamlRoot scaling changes.
- Keep Fit explicitly centered and viewport-constrained. Actual Size uses a DPI-correct Fill box;
  while Full Control is active it is disabled so the input overlay cannot hide its scrollbars.
- Release remote state on pointer-capture loss or viewer deactivation, retain capture while another
  mouse button is still held, and keep the existing selected-monitor Windows SendInput mapping.
- Exclude the remote cursor from Full Control capture to remove the latency-delayed duplicate while
  retaining cursor capture for View Only.
- Select the native-resolution Quality profile only for LAN-development metadata, raise its ceiling
  to 16 Mbps, pace WGC frames before GPU readback/conversion, keep one newest encoder frame, bound
  pending UI work, and transfer decoded frame-buffer ownership without a second 4K-sized copy.
- Prefer the primary display for initial capture and advance the LAN MSI/instructions to `0.6.6`.

Validation:

- The complete solution passes 473 tests with zero failures; the two live TURN/Docker opt-in cases
  remain skipped by the normal suite. This includes pointer geometry at Fit/scroll offsets and
  100/125/150/200% DPI, native 3840x2160 scaling, a real 4K VP8 sharpness case, and a synthetic
  3840x2160 frame sent and decoded over a real local WebRTC peer connection.
- WinUI x64 Release builds with zero warnings/errors and NuGet reports no known vulnerable direct or
  transitive packages for the application graph.
- The `0.6.6` MSI passes ICE03, administrative extraction, and exact packaged-payload validation.
- Two isolated packaged `0.6.6` clients connect to the live WSS server and complete attended Full
  Control: native profile selected, control active, captured cursor excluded, 1920x1080 source held
  without downscale on this machine, and session/window cleanup succeeds.

Risk:

- The local machine has a 1920x1080 physical display. The automated 4K codec/WebRTC path is real,
  but sustained physical 4K capture/render FPS and end-to-end pointer feel still require the two
  requested 4K laptops; software VP8 performance depends on their CPU and LAN conditions.
- Full control remains attended, standard-user only, and fail-closed on focus/reconnect/session loss.

Rollback:

- Revert this entry's viewer geometry, capture pacing/profile, cursor, media-queue, tests, and docs
  together, then reinstall the previous LAN MSI.

## 2026-08-16 - Restore raw full-control input and synchronize session shutdown

Task:

- Fix Full Control showing active while ordinary mouse clicks did nothing, remove the marked viewer
  footer and sharing banner from the transmitted desktop, and ensure ending on one PC closes the
  session on the other PC.

Changed:

- Replace the transparent WinUI `Button` input overlay with a focusable neutral `ContentControl`
  containing a full-size transparent `Border` pointer surface, so `ButtonBase` cannot consume raw
  presses and hit-testing remains stable. Clicking the desktop restores keyboard focus after a
  toolbar command; pointer capture/state and routed-event handling now settle before transport
  awaits, preventing an ordinary release or Tab key from pausing/escaping Full Control.
- Keep the sharer's attended-session safety banner visible locally while applying
  `WDA_EXCLUDEFROMCAPTURE` to its top-level HWND; when an app-owned tray stop command is available,
  make the excluded window layered/click-through so it cannot become an invisible input dead zone.
  Collapse the viewer-only diagnostic footer.
- Treat viewer title-bar close as an explicit session end, suppress duplicate end messages during
  peer-driven UI cleanup, notify the peer of `ApplicationShutdown` before signaling disposal, and
  hold the first main-window close until asynchronous cleanup finishes. Bound peer notification,
  make disposal idempotent, and cancel/wait in-flight session startup before releasing resources.
- Do not treat the initial media `Connected` callback as a reconnect that resets already-enabled
  viewer input, and ignore delayed close callbacks from an older session when a newer session is
  already active in the same client UI.
- Mirror hidden resolution/connection state into the remote image's accessibility metadata and log
  the focused control surface; log one sanitized, content-free success marker when an approved
  session first reaches `SendInput`.
- Extend the real local WebRTC data-channel test through pointer move, left-button down/up, and key
  input; add unit and two-device shutdown-propagation coverage.
- Add `End current session` to the app-owned tray menu and advance the LAN-development installer
  guidance to `0.6.8`.

Validation:

- The complete solution passes 479 tests with zero failures; the two explicitly opt-in live
  Docker-restart/TURN cases remain skipped by the normal suite.
- WinUI x64 Release builds with zero warnings/errors, and NuGet reports no known vulnerable direct
  or transitive packages in all 30 projects.
- The `0.6.8` MSI passes WiX/ICE03, administrative extraction, and exact packaged-payload validation;
  its SHA-256 is `4BD3AE69D2F8BBB0AAACE6C542461D8DAE9E4F86C230E84BC8F8B34462AD4BCC`.
- Two isolated packaged `0.6.8` clients pass the complete live WSS/WebRTC Full Control harness in
  three consecutive pre-final runs and again from the final MSI payload at 1920x1080 with zero
  downscale: raw pointer movement reaches the host Windows injection boundary, the safety indicator
  remains locally visible with affinity `0x11` while absent from the stream, its top-level HWND is
  click-through, and the footer consumes no visible space.
- Viewer toolbar End, sharer main-window End, viewer title-bar close, and target application close all
  close both peers within the 15-second acceptance window and release/restore their session UI.

Risk:

- Windows intentionally blocks standard-user input injection into elevated/UAC secure-desktop
  targets. Capture exclusion relies on DWM/public Windows capture behavior and deliberately falls
  back to showing the local safety indicator in the stream if the OS call fails.

Rollback:

- Revert this entry's viewer input surface/lifecycle, indicator affinity, teardown notification,
  tests, version guidance, and documentation together, then reinstall the previous LAN MSI.

## 2026-08-16 - Preserve early WebRTC negotiation messages

Task:

- Diagnose repeated accepted sessions that started capture but never connected and ended with
  `NegotiationTimeout` on physical clients.

Changed:

- Serialize remote SDP/ICE handling with per-session media initialization instead of silently
  discarding messages received before `IMediaSession` is attached.
- Keep a bounded, arrival-ordered per-session negotiation queue for the smaller scheduling window
  where a signaling callback runs before media initialization acquires its lifecycle gate; drain it
  immediately after media attachment.
- Add a deterministic regression that delays the viewer ICE-configuration response, delivers the
  sharer offer and ICE first, and proves the viewer still answers, applies ICE, and enters
  `Connecting`. Make related reconnect tests wait for the real SDP state transition before emitting
  their synthetic media-connected event.
- Advance LAN-development installer guidance to `0.6.9`.

Validation:

- All 480 solution tests pass; the two explicitly opt-in live Docker-restart/TURN cases remain
  skipped by the normal suite. The Application suite passes 76/76, including 37/37 coordinator
  cases.
- WinUI x64 Release builds with zero warnings/errors.
- The `0.6.9` MSI passes WiX/ICE03, administrative extraction, and exact packaged-payload
  validation; its SHA-256 is
  `E571B5FFBA352DEDF53E848786587B71D0BB0B46B4C04293AC4B362FB2396E3F`.
- Two isolated packaged-payload `0.6.9` clients complete four consecutive live WSS/WebRTC Full
  Control sessions. Both logs contain four media connections and four matching clean closures,
  with zero Warning/Error/Fatal events and no negotiation timeout; raw pointer input reaches the
  host Windows injection boundary.

Risk:

- The live acceptance used two isolated clients on one Windows host. The deterministic delayed-media
  regression covers the physical-client timing failure, but the final MSI still needs the requested
  two-laptop LAN confirmation for firewall, Wi-Fi, and sustained 4K performance.

Rollback:

- Revert this entry's coordinator negotiation queue/test changes and version guidance together,
  then reinstall the `0.6.8` LAN-development MSI.

## 2026-08-17 - Harden Phase 2 input binding, revocation, and session safety

Task:

- Complete Phase 2's explicit-consent keyboard/mouse control path without adding another signaling,
  media, or collaboration stack, and produce a locally verified LAN Development installer.

Changed:

- Add input protocol v2 envelopes that bind every focus request/result, input command, held-state
  release, and permission revocation to the signaling session ID, reconnect generation, focus
  generation, protocol version, and monotonic sequence.
- Require a host acknowledgment before viewer input becomes active; keep pointer movement coalesced,
  cap pending sends, and fail closed on replay, cross-session, pre-reconnect, stale-focus, malformed,
  rate-excessive, or unauthorized traffic.
- Add monotonic in-session `ControlInput` revocation. The host disables input before attempting remote
  notification, records a content-free `PermissionChanged` audit event, keeps view active, and
  restores only the reduced effective mask after reconnect.
- Add horizontal wheel forwarding, monitor-switch focus reset, Ctrl+Alt+Shift+Esc viewer release,
  tray revoke/end commands, and Ctrl+Alt+Shift+F12 host emergency end registration.
- Preserve Windows boundaries: standard `SendInput` only, reject secure-attention synthesis, do not
  bypass UIPI/UAC/Secure Desktop, and retain possibly held state when a release fails so it can be
  retried instead of silently forgotten.

Validation:

- `dotnet build PeerOnQ.slnx -c Release --no-restore`: passed, 0 warnings, 0 errors.
- `dotnet test PeerOnQ.slnx -c Release --no-restore`: 482 passed, 0 failed, 2 explicit opt-in
  Docker/TURN tests skipped.
- The 25-session lifecycle regression now waits for the accepted-session audit boundary before
  ending each iteration; it passed three repeated targeted runs and the final full solution suite.
- Real local WebRTC loopback Full Control test passed through focus acknowledgment, pointer/button,
  key, and the authenticated data channel.
- Two isolated `0.7.1` packaged-payload clients passed a real local WSS/WebRTC/UIAutomation run at
  1920x1080: raw pointer reached the Windows injection boundary, the viewer release shortcut paused
  input, host revocation disabled the viewer while view stayed active, the global emergency shortcut
  closed both sides, six capture sessions started/stopped cleanly, and final logs contained no
  Warning/Error/Fatal/NegotiationTimeout/input-rejection matches.
- The unsigned development x64 MSI passed WiX ICE03, administrative extraction, self-contained
  runtime checks, and exact payload comparison. SHA-256:
  `B556AB6C82038458B899FBA2F7DB3E684F61FB3D44BECAC88255BF3482C8DB68`.
- A fresh NuGet vulnerability query reported no known vulnerable direct or transitive package in
  all 30 projects; brand-purity validation also passed.

Risk:

- The input v2 frames are intentionally fail-closed and require both endpoints to run `0.7.1` or a
  later compatible client for Full Control. View-only media remains on the existing session stack.
- Same-host isolated clients do not prove LAN firewall behavior, two physical keyboards/mice,
  elevated-window restrictions, multi-monitor hardware, sustained 4K, or the required ten-minute
  physical session. Those Phase 2 acceptance items remain externally blocked.

Rollback:

- Revert the Phase 2 protocol/session/UI/input-controller changes and their tests/docs together, then
  reinstall `0.6.9` on both endpoints; mixed input protocol versions must not be treated as compatible.

## 2026-08-17 - Recover physical sessions after Wi-Fi loss and sharpen LAN video

Task:

- Diagnose a physical two-laptop session that remained disconnected after Wi-Fi returned and improve
  the visibly blocky LAN desktop stream without retesting unrelated completed phase work.

Changed:

- Enforce bounded application and WebSocket PONG deadlines so a half-open LAN signaling connection is
  detected in roughly 12-15 seconds and reconnect begins without waiting for an OS TCP timeout.
- Always authenticate/consume the resume token after a signaling interruption, including when media
  entered reconnect first or re-registration completed before the recovery loop observed it.
- Notify the participant that stayed online when its peer resumes. Both sides fail closed and finish a
  new ICE negotiation with the sharer as deterministic offerer before restoring view/input.
- Set the LAN Quality profile to native/30 fps with a 24 Mbps ceiling, enable libvpx desktop
  screen-content mode, and bound its worst quantizer to preserve small text more clearly.
- Replace the ambiguous one-shot Fit command with an accessible display-scaling menu for Fit, Fill,
  Stretch, and DPI-correct Actual size; keep pointer coordinates aligned with every visible mode.
- Add signaling protocol v1 negotiation and fail closed with `unsupported_version` before a partial
  connection; bind each resume record to that version, re-fetch device attestation after
  interruption, and reject a revoked device.
- Record current RFC/library support and honest local/external limits in
  `docs/PROTOCOL_COMPLIANCE.md`; update the Phase 3 gate from the stale ViewOnly blocker to
  `PASS_WITH_EXTERNAL_BLOCKERS` after the real local controller passed.
- Advance LAN development guidance and the physical-test installer to `0.7.5`; rebuild the local
  signaling server with the peer-resume behavior.

Validation:

- Application suite: 87/87 passed. The focused media-first/signaling-first/peer-resume/path ICE
  regressions passed again after the final transport change.
- Signaling suite: 70 passed, 1 explicit opt-in Docker-restart test skipped. The real server resume
  test proves the still-online peer is notified; heartbeat deadline boundaries are unit tested.
- Media suite: 58 passed, 1 explicit opt-in live-TURN test skipped; VP8 encoder tests include the
  native 3840x2160/24 Mbps path.
- End-to-End suite: 40/40 passed. The full local Phase 3 controller passed in 147.6 seconds, including
  real WSS, STUN, TURN UDP/TCP/TLS/DTLS, forced relay video/file transfer, interruption, and restart.
- WinUI and full-solution Release builds passed with 0 warnings/errors. WiX ICE03, administrative extraction,
  self-contained runtime checks, and exact MSI payload comparison passed.
- Local WSS/TURN stack reports healthy on `10.0.0.10`. The unsigned `0.7.5` Development MSI is
  78,438,400 bytes; SHA-256
  `D097DDDAEED809247B6ED5EC534B4A5AFF868BE328C189373A3ED33E4AD2FC00`.

Risk:

- The Wi-Fi and ICE recovery branches are deterministically tested, but toggling the actual physical
  adapters and judging image quality still require the user's two laptops. Native 4K/24 Mbps also
  depends on endpoint CPU/GPU and LAN capacity; it is a quality target, not an unmeasured FPS claim.

Rollback:

- Revert this entry's heartbeat, peer-resume/ICE, signaling protocol, media-profile, codec, scaling,
  test, and documentation changes together; reinstall `0.7.1` on both endpoints and rebuild the
  previous local signaling image. Mixed signaling/input protocol versions are not compatible.

## 2026-08-17 - Complete Phase 4 collaboration and unattended runtime wiring

Task:

- Repair the existing file/folder transfer, plain-text clipboard, address-book, trusted-device and
  unattended-access stack; expose it in the native client and verify only Phase 4 plus affected
  regressions before producing the next LAN-development package.

Changed:

- Bump signaling and collaboration to protocol v2. Every collaboration frame is now bound to the
  exact session ID and permission generation; old/missing/new signaling versions fail clearly before
  a partial connection.
- Remove arbitrary default transfer-size caps while retaining administrator quotas, bounded manifest
  and frame sizes, streaming chunks, exact offset/index checks, chunk/final SHA-256, disk preflight,
  repeated destination/reparse checks, per-file atomic finalization and terminal cleanup.
- Add interactive/normal/bulk data priority and a bounded bulk buffered budget so remote input can
  pre-empt file chunks under transfer backpressure.
- Make text clipboard explicit, bilateral, default-off and 120 KiB bounded; preserve origin/change
  loop prevention and content-free logs/audit.
- Expose custom attended capabilities and explicit unattended requests in WinUI; complete address
  and group CRUD without accepting user-supplied OS/last-seen as authenticated metadata.
- Harden trusted expiry/fingerprint/scope behavior and add recovery-code proof, one-use consumption,
  replay/scope binding and credential-path lockout for unattended authentication.
- Update architecture, security, threat, protocol, design and Phase 4 truth documentation. No new
  dependency or persistent schema migration was introduced.

Validation:

- Phase 4 application tests: 41/41 passed; full Application: 104/104; Domain: 61/61; End-to-End:
  40/40; Phase 4 Infrastructure: 4/4.
- Signaling: 70 passed and 1 explicit live-Docker test skipped; Media: 59 passed and 1 explicit
  live-TURN test skipped.
- The real local WebRTC test transferred 8 MiB in 1 KiB chunks while interactive input crossed the
  same authorized channel in 260.4 ms under deliberate backpressure; the synthetic streaming test
  transferred 16 MiB in 64 KiB chunks.
- WinUI x64 Release build passed with 0 warnings/errors; brand-purity validation passed.
- The local v2 WSS/TURN stack rebuilt healthy. The final unsigned `0.8.2` x64 MSI passed ICE03,
  administrative extraction, self-contained runtime and exact payload comparison; it is 78,438,400
  bytes with SHA-256 `366AF8F62A09E8ABA3EA0852205E97F767CA4D9DD84B69E1EB540E58FDC425C9`.

Risk:

- Protocol v2 intentionally requires matching new client/server builds. Two physical devices, 1 GB
  and larger-safe-data transfers, real interruption/resume, control under transfer, restart/lock/
  elevation behavior and physical clipboard/trust flows remain external acceptance gates.
- Repeated path checks reduce reparse replacement risk but are not a kernel-level no-follow handle;
  atomicity is per file, not one transaction for a complete folder.

Rollback:

- Revert this entry's collaboration/domain/media/WinUI/test/documentation changes as one versioned
  unit and redeploy the prior signaling server plus matching clients. Do not mix protocol v1 and v2.

## 2026-08-17 - Complete Phase 5 native UX and local release evidence

Task:

- Make the Windows x64 client and unsigned LAN-development package release-reviewable without
  claiming unavailable production signing, clean-VM, ARM64, physical accessibility, or performance
  evidence.

Changed:

- Make the native shell adaptive and accessibility-aware, expose real version/local-data actions,
  improve scaled consent/sharing surfaces, and clarify MIT/no-activation behavior in native and web
  About surfaces.
- Remove the obsolete Billing route/navigation, embed the full MIT and security/data notice in WiX,
  and ship `LICENSE.txt` plus `THIRD_PARTY_NOTICES.md` beside the executable.
- Reject unexpected manifest/package redirects, recheck staged MSI size/SHA-256 immediately before
  Authenticode/publisher verification and launch, and delete substituted staged content.
- Replace raw structured JSON logging with a bounded sanitizer that redacts sensitive properties and
  emits exception type without message/stack.
- Add Windows PowerShell 5.1-compatible native UI, release-evidence and guarded clean-VM lifecycle
  scripts. The evidence gate now inventories the app project's NuGet graph, fails on a non-successful
  SBOM report even when the tool exits zero, and emits SPDX 2.2, dependency/vulnerability, legal,
  provenance and checksum artifacts.
- Remove avoidable Node 25/Vite warning noise without editing vendored UI primitives or adding a
  dependency.

Validation:

- Cumulative .NET regressions: 516 passed, 0 failed, 2 explicit opt-in live Docker/TURN skips.
- Phase 5 update/audit tests: 24/24; PeerOnQ web: 51/51; admin web: 19/19.
- Root pnpm typecheck/build, WinUI x64 Release and format verification passed; native build reported
  0 warnings/errors.
- MSI ICE, administrative extraction, self-contained runtime, full license and exact payload checks
  passed. Secret scan passed 891 files and brand purity passed.
- Approved SPDX 2.2 evidence contains 18 packages and 573 files with `Result=Success`, zero validation
  errors and no private workstation paths. .NET and pnpm production reports contain zero known
  vulnerabilities.
- Final unsigned x64 MSI is 78,483,456 bytes with SHA-256
  `C98A0D74511145619370D884A3651E8656C9709CBEC0F15C2CFA6C1173D79F95`.

Risk:

- The artifact is deliberately Development/Unsigned. Current clean-VM lifecycle, trusted signing,
  physical ARM64, two-laptop accessibility/performance and production updater validation remain
  external release blockers; the gate is `PASS_WITH_EXTERNAL_BLOCKERS`, not production approval.

Rollback:

- Revert the Phase 5 UX/web, MSI/legal, update/logging, tests/scripts and documentation changes as one
  unit. Do not relax redirect, hash, Authenticode, publisher, consent or local-data safety checks.

## 2026-08-17 - Harden and validate the Phase 6 self-hosted stack

Task:

- Complete the Phase 6 self-hosted cloud, administration, downloads, deployment, observability,
  backup, and operations gate without rerunning unrelated earlier-phase suites.

Changed:

- Upgrade development and validation Redis to 8.4.4 to fix ACL/AOF transaction replay failures,
  preserve per-service ACL isolation, and keep existing data volumes.
- Repair current OpenTelemetry component names, disable Tempo usage reporting, provision a real
  source-controlled Grafana operations dashboard, and classify handled 4xx requests correctly.
- Require generated loopback TURN TLS in development, use a writable coturn PID path, and retain the
  physical-LAN relay acceptance path in the Phase 3 controller.
- Generate stable ignored local operations/backup credentials, synchronize Grafana credentials via
  binary standard input, and keep secrets out of command lines and tracked configuration.
- Send the current signaling protocol version from the Phase 6 acceptance client and pin every
  production Compose image and Dockerfile base image to an immutable digest.
- Add deployment configuration regressions and update the project/deployment/operations maps plus
  the Phase 6 completion report.

Validation:

- Affected Phase 6 suites passed 112/112: Cloud Domain 7, Cloud Application 19, Cloud
  Infrastructure 33, Presence 4, Admin API 20, Downloads 17, and Observability 12.
- The 20-container development stack restarted with migrations current and required health checks
  ready. Redis ACL denial probes, attested enrollment/Presence/signaling acceptance, TURN TLS,
  PostgreSQL backup, and isolated 22-table restore all passed.
- Production Compose interpolation validation and all eight custom Docker build checks passed.
  Repository secret and brand gates are recorded in the Phase 6 report.
- Docker Scout found no high or critical issue in `redis:8.4.4-alpine`; two unfixed low-EPSS medium
  Alpine package findings remain tracked.

Risk:

- Public trusted MSI delivery, an external Admin deny probe, Internet TURN/NAT testing, two physical
  laptops, off-host encrypted restore, and production secret/signing infrastructure remain external
  gates. Phase 6 is `PASS_WITH_EXTERNAL_BLOCKERS`, not production approval.

Rollback:

- Revert the Phase 6 deployment, telemetry, acceptance, tests, and documentation changes together,
  restore only from a verified backup if schema/data rollback is required, and never delete named
  volumes or relax authentication, TLS, ACL, digest, or signed-release controls.

## 2026-08-17 - Add isolated customer accounts, organizations and the real Phase 7 portal

Task:

- Implement Phase 7 self-hosted customer identity, tenant collaboration, server policy, privacy and
  a real API-backed account portal without licensing/activation dependencies or breaking accountless LAN.

Changed:

- Add customer registration/verification, Identity password hashes, TOTP/recovery, reset, revocable
  rotating sessions, `__Host-` cookies, CSRF, rate limits, security history and privacy requests in a
  security domain separate from internal Admin users/RBAC.
- Add organizations, memberships, customer-only roles, protected invitations, teams, explicit owner
  transfer/deletion guards, tenant-scoped devices/session metadata/audit, device claim and policy.
- Extend signed managed-device attestations with organization policy and enforce them in Signaling;
  preserve the v1 unmanaged/accountless path.
- Add the separate `peeronq-portal` React/Vite SPA and `portal.*` deployment route. Retire active
  public `/app` preview routes and remove billing/pricing/invoice/subscription/payment portal surfaces.
- Add a forward-only customer migration, append-only customer audit trigger, least-privilege runtime
  grants, persistent customer Data Protection/mail volumes, SMTP configuration and development sink.
- Fix portal image startup, Cloud endpoint body binding, no-lock customer login, string-enum JSON,
  Data Protection restart persistence, refresh replay concurrency, SMTP default credentials and
  cross-tenant customer rate-limit partitioning.
- Add the real HTTPS multi-organization/multi-role acceptance script, portal UI/route tests, policy,
  migration, security and deployment regressions, data inventory and Phase 7 documentation.

Validation:

- Real HTTPS API acceptance passed registration, verification, session/MFA setup persistence across
  Cloud restart, CSRF, two-organization isolation, invitation replay, self-escalation denial, teams,
  policy denial, ownership transfer, audit and refresh-family replay.
- Affected .NET tests passed: customer domain 7/7, customer/migration security 9/9,
  signaling policy 5/5, deployment/controller 3/3. Cloud Release build had 0 warnings/errors.
- Customer portal typecheck, 9/9 tests and production build passed; public route separation 28/28,
  public typecheck and build passed. Local portal/API/stack health and HTTPS security headers passed.

Risk:

- In-app browser automation could not start because the Browser MCP omitted required sandbox
  metadata. DOM unit tests, HTTPS/API evidence and static delivery passed, but Phase 7 remains
  `PASS_WITH_EXTERNAL_BLOCKERS` until a real browser walkthrough is recorded. Customer audit cleanup
  remains an operator-governed append-only maintenance concern; policy changes apply through
  evaluation/new short-lived attestation rather than terminating every existing media session.

Rollback:

- Revert the portal, customer Cloud/API/domain, attestation policy, deployment and documentation
  changes as one unit. Do not down-migrate a database that contains customer data; restore only from
  a verified pre-Phase-7 backup and never delete customer key/mail/database volumes to force rollback.

## 2026-08-17 - Add signaling v3 and the Phase 8 cross-platform evidence boundary

Task:

- Build the shared, versioned capability-negotiation foundation for Phase 8 while keeping Windows
  as the only supported native client and refusing unsupported non-Windows claims.

Changed:

- Bump signaling to v3 with exact min/max incompatibility fields, bounded platform/capability
  manifests, server/optional-feature negotiation and client verification of the negotiated result.
- Gate session creation on directional viewer/host abilities for screen, input, file, clipboard and
  unattended scopes; preserve the existing consent and data-plane authorization layers.
- Decode unknown well-formed message types as inert bounded frames and return
  `unsupported_message`; keep malformed JSON fail-closed.
- Add canonical UTF-8 Linux-viewer hello and v2-rejection vectors; documentation explicitly says
  that a Linux-named vector is not a Linux client, plus pure/live capability/version/vector tests.
- Declare the current real Windows abilities beside `PeerOnQ.Platform.Windows`; update the native
  app and Phase 6 synthetic acceptance hello without inventing another platform implementation.
- Add the per-platform capability/security/evidence matrix and Phase 8 completion report; record
  Linux/macOS/Android/iOS/iPadOS native clients as `NOT_FOUND`.

Validation:

- Signaling passed 84/84 with one normal opt-in Docker-restart skip; Application passed 104/104;
  Windows end-to-end passed 40/40.
- x64 WinUI and the Phase 6 acceptance executable built in Release with 0 warnings/errors.
- Deployment acceptance regression passed 1/1; secret scan passed 942 files and brand purity passed.

Risk:

- Signaling v3 intentionally rejects v2 before challenge. Client/server rollout must be coordinated.
  No non-Windows package/toolchain/physical runtime exists, so Phase 8 is
  `PASS_WITH_EXTERNAL_BLOCKERS`, not a cross-platform product-support claim.

Rollback:

- Revert the v3 messages/codec/client/server gate, Windows profile, acceptance caller, tests/vectors
  and Phase 8 documentation together. Do not advertise v3 while deploying a v2-only binary, and do
  not delete existing data or weaken consent/authorization to recover compatibility.

## 2026-08-17 - Protect session content with an authenticated hybrid post-quantum record layer

Task:

- Add real hybrid post-quantum end-to-end protection for every desktop session and make direct
  device-to-device file transfer use available link capacity without an arbitrary size/rate cap.

Changed:

- Add signaling-identity-bound Ed25519 + ML-DSA-65 device identities and an X25519 + ML-KEM-768
  transcript-authenticated handshake that must finish before a session becomes connected.
- Add directional/channel/transfer/epoch HKDF-SHA-512 keys and AES-256-GCM records for encoded
  video, control, remote input, clipboard and file data, including replay rejection, rekeying and
  stale-video drop across an unordered rekey boundary.
- Require the hybrid capability on both endpoints and fail closed on downgrade, malformed,
  unauthenticated, wrong-context or tampered traffic. Show the post-quantum UI state only after the
  peer is authenticated and the secure state is installed.
- Keep file data off TURN by removing relay candidates before file-enabled peer creation and
  rechecking every record; remove default product byte/rate ceilings, use CPU-bounded parallel
  workers and a bounded 16 MiB SCTP backpressure window, and retain streaming integrity, resume,
  path safety, malware scanning and atomic finalization.
- Add protocol, identity, record-layer, coordinator, direct-file, full-control, 1080p/4K protected
  media and compatibility regressions plus the secure-transport threat model and lifecycle docs.

Validation:

- Application passed 117/117, including ready-order, signature/transcript, replay, rekey and parallel-
  transfer regressions. Media passed 62/62 with
  one normal opt-in live-TURN skip, Signaling passed 86/86 with
  four normal opt-in Docker/HA skips, and Windows end-to-end passed 44/44.
- Exact protected 320p/1080p/4K loopback video, direct WebRTC file transfer and hybrid full-control
  input passed. The x64 WinUI Release build completed with 0 warnings/errors.

Risk:

- .NET 10 ML-KEM/ML-DSA APIs remain marked experimental and depend on supported Windows platform
  cryptography. The client therefore advertises the mandatory capability only when both are
  available. Independent cryptographic review, physical two-device throughput/reconnect testing
  and a current external protected-TURN run remain release gates.

Rollback:

- Revert the hybrid identity/handshake/record layer, media and collaboration integration,
  capability gate, tests, package reference, UI indicator and documentation together. Never retain
  the post-quantum label or capability advertisement without the mandatory data-plane protection.

## 2026-08-17 - Add tray Settings and graceful Exit commands

Task:

- Add Settings and Exit actions to the PeerOnQ notification-area icon's right-click menu.
- Keep the marked upper-left system-caption area visually empty.

Changed:

- Extend the existing native tray menu without removing its remote-control revoke and session-end
  safety actions.
- Restore and activate the main window on Settings, navigate to the real Settings surface, and route
  Exit through the existing bounded application shutdown path so active sessions and resources are
  released before the window closes.
- Set an explicitly empty window title so WinUI cannot substitute its `WinUI Desktop` fallback, and
  hide the caption icon where Windows App SDK title-bar customization is supported.

Validation:

- WinUI x64 Release build passed with zero warnings/errors.
- Application lifecycle/session regression suite passed 117/117.
- An isolated real client runtime exposed Settings and Exit in the native tray menu; Settings opened
  the real Settings page, Exit closed the process through MainWindow, and the system caption was
  blank. Temporary test identity, logs and screenshots were removed after verification.
- The unsigned LAN-development `0.9.11` x64 MSI passed WiX/ICE03, administrative extraction and
  exact published-payload validation against the healthy `10.0.0.10` server build. SHA-256:
  `F3DEB47E4137C4F9C2F2907A3657C21B28A43E807CD2CC678A71D6DDBF9C3D26`.

Risk:

- The menu uses the existing owner-window subclass and therefore remains available only while the
  main PeerOnQ window and tray icon are alive.

Rollback:

- Revert the TrayIcon callbacks/menu entries, MainWindow Settings callback, and matching map entry.

## 2026-08-17 - Clarify native connection and compatibility status

Task:

- Replace technical signaling wording in the native header with a simple binary connection state.
- Make mixed-client secure-protocol failures actionable without weakening the capability gate.

Changed:

- Show only `Connected` or `Disconnected` in the header and use the existing theme-aware green
  accent dot only while signaling registration is active; retain text so status is not color-only.
- Replace the raw `capability_mismatch` banner with instructions to install the same latest PeerOnQ
  version on both computers, exit the old tray process, and reopen the app.
- Extend the native UI static gate for the status text, semantic color resource and recovery message.

Validation:

- Native UI/accessibility validation passed, and the x64 WinUI Release build completed with zero
  warnings/errors.
- An isolated 0.9.12 client connected to the real LAN signaling endpoint; UI Automation read the
  header state as `Connected` and the dashboard version as `0.9.12`. Temporary identity data was
  removed after the check.
- The unsigned x64 development MSI passed WiX/ICE03 and exact published-payload validation. SHA-256:
  `2765F5697017AD4AB0503F2F05CB68A9BB373C66ADD84BF2B82A08124922A911`.

Risk:

- The header intentionally compresses connecting, enrollment and fault details into `Disconnected`;
  the dashboard agent status and error banner retain the detailed recovery context.

Rollback:

- Revert the native header XAML, status/error formatting helpers, static assertions and this entry.

## 2026-08-17 - Compensate automatically for endpoint clock and region differences

Task:

- Prevent a correctly authenticated client from receiving `replay_detected` solely because its
  Windows clock, date or timezone differs from the signaling server.

Changed:

- Use the authenticated signaling registration `serverTime` to calculate a client-side UTC offset,
  refresh it from heartbeat responses, and timestamp new session requests in server time.
- Keep the server's nonce replay guard and clock-skew rejection unchanged.
- Add a real signaling regression proving a client whose local clock is one day behind can request
  a session while deliberately stale raw protocol messages remain rejected.

Validation:

- The clock-offset regression plus stale-timestamp and duplicate-nonce protections passed 3/3.
- The full Signaling suite passed 87/87 with four expected opt-in Docker/HA skips, and the x64
  WinUI Release build completed with zero warnings/errors.
- The unsigned LAN-development `0.9.14` x64 MSI passed WiX/ICE03 and exact published-payload
  validation. SHA-256: `F80C74F855AA7C22AEE2F638F0217A400051806D90DA83FB50EE3F4C86951051`.

Risk:

- The offset is trusted only from the configured authenticated WSS signaling endpoint and does not
  modify Windows time, timezone or regional settings.

Rollback:

- Revert the signaling-client offset calculation, its regression, and this changelog entry together.

## 2026-08-17 - Report same-version security capability failures accurately

Task:

- Diagnose why two current PeerOnQ clients were reported as version-incompatible and could not
  create a session.

Changed:

- Preserve the signaling server's bounded capability side and missing-capability list through the
  client abstraction instead of discarding those machine-readable fields.
- Replace the misleading same-version warning with targeted Windows ML-KEM/ML-DSA recovery guidance
  when `security.hybrid-pq-v1` is unavailable, while retaining the mandatory fail-closed encryption
  gate.
- Keep a separate recovery message for non-security mode capabilities and assert the propagated
  target-side capability details in the signaling integration suite.

Validation:

- Capability negotiation tests passed 8/8, including the client-visible target side and exact
  missing capability.
- Application regressions passed 117/117; the native UI/accessibility static gate passed.
- WinUI x64 Release build completed with zero warnings/errors; `git diff --check` passed.

Risk:

- A computer whose Windows cryptographic provider lacks ML-KEM or ML-DSA remains intentionally
  unable to start a session. It must use a Windows build with those primitives; PeerOnQ does not
  silently downgrade session encryption.

Rollback:

- Revert the signaling error-notification fields, native capability-specific formatter, regression
  assertions and this entry together. Do not remove the mandatory hybrid capability requirement.

## 2026-08-17 - Make hybrid security available on supported Windows builds

Task:

- Deliver a working native client after same-version `0.9.14` devices remained unable to create a
  session because one Windows cryptographic provider did not advertise ML-KEM/ML-DSA support.

Changed:

- Use the repository's existing Bouncy Castle 2.6.2 dependency for managed ML-KEM-768 and
  ML-DSA-65 key generation, import, signing, verification, encapsulation and decapsulation instead
  of depending on optional Windows Insider CNG algorithms.
- Preserve the exact hybrid protocol suites, post-quantum capability gate, dual signatures,
  transcript binding, key confirmation and AES-GCM record layer; no classical-only downgrade was
  added.
- Keep existing standardized PKCS#8 ML-DSA device keys interoperable in both directions and retain
  DPAPI storage, bounded inputs and explicit secret clearing at the application boundary.
- Advertise `security.hybrid-pq-v1` on every supported Windows build and document that the managed
  provider is not the separately certified Bouncy Castle FIPS distribution.
- Build the fixed LAN-development client as version `0.9.15` for the healthy
  `wss://signal.10.0.0.10.sslip.io:5443/ws` endpoint.

Validation:

- Managed provider round-trip, tamper/context rejection and bidirectional .NET platform
  interoperability passed for ML-KEM-768 and ML-DSA-65.
- Secure transport passed 15/15; full Application passed 121/121; Signaling passed 87/87 with four
  normal opt-in Docker/HA skips; Windows end-to-end passed 45/45 with real signaling, WebRTC and
  hybrid session establishment.
- Native UI/accessibility validation passed; the WinUI x64 Release build completed with zero
  warnings/errors.
- The unsigned `0.9.15` x64 MSI passed WiX/ICE03, administrative extraction, self-contained runtime
  and exact published-payload comparison. It is 78,524,416 bytes with SHA-256
  `4F73D2C21D5316455E386BF8528898C7CF9FECFB4CA1F17692A6B0979094BBC4`.

Risk:

- Bouncy Castle's general package implements the standardized algorithms but is not its separately
  certified FIPS module; independent cryptographic review remains a release gate. The artifact is
  unsigned, pins only the LAN development root for its configured WSS endpoint, and is not a public
  production release.

Rollback:

- Revert the managed provider, identity/handshake integration, capability profile, regressions,
  documentation and this entry together. Reinstall one matching previous build on both devices;
  never remove the mandatory hybrid gate or mix an unsupported `0.9.14` peer into the rollback.

## 2026-08-17 - Preserve remote control focus and refresh signaling on Wi-Fi changes

Task:

- Fix an accepted full-control session whose viewer opened but mouse/keyboard input did nothing,
  and recover signaling promptly after Wi-Fi was disabled and enabled again.

Changed:

- Serialize viewer input-focus enable/disable transitions so duplicate collaboration-ready and
  connected-state UI notifications share the already approved focus generation instead of
  advancing the outbound generation while the first acknowledgment is pending.
- Keep the existing session-generation, permission, replay, rate, focus acknowledgment and
  fail-closed input checks unchanged.
- Monitor Windows network availability and address changes after signaling starts. A live socket is
  refreshed immediately through the existing bounded reconnect flow; a previously faulted client
  retries when connectivity returns. Registration proof, current attestation, resume token and
  fresh ICE negotiation remain mandatory.
- Build the fixed LAN-development client as version `0.9.16` for
  `wss://signal.10.0.0.10.sslip.io:5443/ws`.

Validation:

- The new concurrent-focus regression failed against the old behavior with
  `An input-focus request is already pending`, then passed after the serialization fix; all
  Application tests passed 122/122.
- Signaling passed 93/93 with four normal opt-in Docker/HA skips; Windows end-to-end passed 45/45.
  Secure first-record, six coordinator reconnect and authenticated signaling-resume regressions
  passed separately.
- Scoped formatter verification, native UI/accessibility validation, brand purity, a 966-file
  secret scan and the x64 WinUI Release build passed; the build had zero warnings/errors. The full
  solution formatter remains blocked by two pre-existing whitespace findings in the unchanged
  `src/PeerOnQ.Cloud.Api/CustomerOrganizationService.cs`.
- The unsigned `0.9.16` x64 MSI passed WiX/ICE03, administrative extraction, self-contained runtime
  and exact published-payload comparison. It is 78,544,896 bytes with SHA-256
  `4CCACC2D06960A915E7646EBC809364AA4197590BF5A502C026C0D1C19ABA21D`.

Risk:

- The network watcher intentionally refreshes an authenticated signaling socket when Windows
  reports interface/address changes, which briefly pauses input and performs fail-closed session
  resume. Physical two-computer Wi-Fi interruption remains a user-environment acceptance check;
  automated reconnect/resume and real two-device WebRTC tests passed.
- The artifact is unsigned, pins only the LAN development root for its configured WSS endpoint,
  and is not a public production release.

Rollback:

- Revert the focus-transition gate, network-change monitoring, their regressions, documentation and
  this entry together. Reinstall matching `0.9.15` builds on both devices; remote input generation
  drift and delayed dead-socket detection will return.

## 2026-08-17 - Preserve the initial screen frame through media negotiation

Task:

- Fix a session that reached WebRTC Connected but displayed no shared screen on the viewer.

Changed:

- Create and subscribe the sharer media session before starting Windows.Graphics.Capture so an
  immediately emitted initial desktop frame cannot be lost between capture startup and media
  event registration.
- Keep the newest bounded capture frame queued while WebRTC or the authenticated media protector
  is not ready; encode it as soon as both are established instead of discarding it during
  negotiation. Capture remains explicit, visible, permission-gated and bounded to one latest
  frame.
- Add deterministic regressions for capture-before-subscription and pre-connection queue loss.
- Build the fixed LAN-development client as version `0.9.17` for
  `wss://signal.10.0.0.10.sslip.io:5443/ws`.

Validation:

- Scoped formatter verification passed. Application passed 123/123; Media passed 63/63 with the
  normal opt-in live TURN test skipped; Windows end-to-end passed 45/45.
- The real Windows two-device acceptance test captured a display with Windows.Graphics.Capture,
  encoded VP8, transported it over WebRTC, decoded the first frame and reported it rendered.
- The x64 self-contained Release build completed with zero warnings/errors. The unsigned `0.9.17`
  MSI passed WiX/ICE03, administrative extraction, self-contained runtime and exact
  published-payload comparison. It is 78,520,320 bytes with SHA-256
  `1119FD9DA08296FABE6A75FF6B3BCE40AE65EB0B0E99DDDCF88DBA9DBA560EBC`.

Risk:

- The bounded queue now intentionally retains one latest pre-connection screen frame for the
  lifetime of negotiation; newer capture frames replace it, so latency and memory cannot grow
  without limit. Physical validation between the user's two separate computers remains required.
- The artifact is unsigned, pins only the LAN development root for its configured WSS endpoint,
  and is not a public production release.

Rollback:

- Revert the media/capture startup ordering, ready-frame dequeue guard, regressions, map update and
  this entry together. Reinstall matching `0.9.16` builds on both devices; the blank static-screen
  race will return.

## 2026-08-17 - Stabilize Wi-Fi signaling replacement and session resume

Task:

- Fix an active remote session that attempted to reconnect after Wi-Fi returned but expired with
  `ReconnectFailed` instead of resuming.

Changed:

- Make signaling registry removal atomically conditional on the exact connection that still owns
  the device. Cleanup from a displaced or stale socket no longer removes a newer owner.
- Mark session leases disconnected only when the socket being cleaned up was the current device
  owner, including heartbeat-timeout cleanup. Authentication, one-use resume tokens, grace limits
  and fresh ICE negotiation remain mandatory.
- Treat the server's explicit `replaced_by_new_connection` close as terminal for the displaced
  client instance. It stays disconnected instead of reconnecting and repeatedly displacing the
  newer instance; network loss and server-restart closes still use bounded reconnect.
- Rebuild and deploy the local LAN signaling container, and build the matching client as version
  `0.9.18` for `wss://signal.10.0.0.10.sslip.io:5443/ws`.

Validation:

- The owner-removal, terminal-replacement and authenticated resume regressions passed 6/6.
  Signaling passed 97/97 with four normal opt-in Docker/HA skips; Application passed 123/123;
  Windows end-to-end passed 45/45.
- The deployed TLS signaling endpoint returned ready, both real devices re-registered online, and
  the live Docker signaling restart/re-authentication acceptance passed.
- Scoped formatter verification passed. The x64 self-contained Release build completed with zero
  warnings/errors. The unsigned `0.9.18` MSI passed WiX/ICE03, administrative extraction,
  self-contained runtime and exact published-payload comparison. It is 78,524,416 bytes with
  SHA-256 `02C510DB778CC775F63E98D3B926E1219698FDDA9EC1998146AD85C0BBAF33FC`.

Risk:

- An intentionally displaced process remains open but disconnected; this prevents connection
  ping-pong without terminating a user process. The currently active/newer instance remains the
  sole signaling owner.
- Physical Wi-Fi interruption and session resume between the user's two separate computers must be
  repeated after both install `0.9.18`. The artifact is unsigned and limited to the configured LAN
  development endpoint.

Rollback:

- Revert the owner-aware removal result, cleanup guards, terminal replacement handling, tests, map
  update and this entry together. Redeploy signaling and reinstall matching `0.9.17` clients; the
  stale-cleanup and duplicate-instance reconnect races will return.

## 2026-08-17 - Keep authenticated sessions alive across connection-token rotation

Task:

- Prevent an active remote session from dropping when its short-lived signaling connection token
  expires, and stop the recoverable `token_expired` event appearing as a fatal dashboard error.

Changed:

- Schedule registration renewal before token expiry and repeat the existing signed challenge plus
  fresh cloud attestation on the same WebSocket, without changing the connected UI state or the
  active WebRTC/media channel.
- Preserve short token lifetime, signature verification, attestation revocation and fail-closed
  behavior. If expiry is still reached against an older server or after a delayed heartbeat, use
  the existing bounded reconnect/resume path without surfacing a misleading fatal banner.
- Add unit boundaries for refresh timing/error classification and a real signaling regression that
  spans multiple two-second token lifetimes while retaining the exact connection owner.

Validation:

- The real two-client signaling regression kept an accepted session on the exact same connection
  owners across multiple two-second token lifetimes, never entered `Reconnecting`, emitted no
  `token_expired` notification, and relayed SDP after rotation.
- Signaling passed 100/100 with four normal opt-in Docker/HA skips; Application passed 123/123 and
  Windows end-to-end passed 45/45.

Risk:

- Each continuously connected client performs one extra signed registration and attestation fetch
  shortly before token expiry; media and permissions are unchanged.

Rollback:

- Revert the in-socket registration refresh, recoverable-error classification, tests, map/security
  documentation and this entry together; periodic token expiry will again interrupt active sessions.

## 2026-08-17 - Simplify the viewer toolbar and add saved-device quick connect

Task:

- Remove implementation-specific security wording from the active viewer, clarify the optional
  diagnostic action, format manually entered device IDs, and expose saved aliases on Dashboard.

Changed:

- Remove only the visible `Post-Quantum Protected` viewer badge; the mandatory hybrid handshake,
  authentication and capability gate remain unchanged.
- Rename the overflow-only diagnostic action to `Copy connection details` and describe that it
  copies sanitized troubleshooting statistics without changing the session.
- Apply the existing twelve-digit formatter to Add Device and add a keyboard-accessible Saved Device
  picker that fills Remote ID while preserving the existing explicit mode and Start Session steps.

Validation:

- Native UI/accessibility/open-source validation passed, including the removed viewer badge,
  plain-language overflow action, saved-device selector and Add Device formatter wiring.
- Device identity/formatter tests passed 13/13; scoped formatter verification and the x64
  self-contained Release build passed with zero warnings/errors.
- The unsigned LAN-development `0.9.19` x64 MSI passed WiX/ICE03, administrative extraction,
  self-contained runtime and exact published-payload comparison. It is 78,528,512 bytes with
  SHA-256 `3C9EFAD9DC75A90C5B3FA24A3F9F8F6B43EDE4538D3DCE4B8411C1043A30B46C`.

Risk:

- Saved aliases are local convenience labels backed by the existing DPAPI-protected address book;
  selection grants no trust, permission or unattended access.

Rollback:

- Revert the viewer copy/badge changes, dashboard picker, Add Device formatter wiring, UI gate,
  map and this entry together.

## 2026-08-18 - Phase 6.5 measured media, quality, diagnostics, and competitive engineering slice

Task:

- Apply the Phase 6.5 execution specification without overwriting working architecture or claiming
  unmeasured/externally blocked behavior as complete.

Changed:

- Record a pre-change isolated `0.9.20.0` idle baseline and add a factual implementation inventory,
  public-source competitive outcome matrix, gap analysis, codec/protocol/security decisions,
  license audit, required architecture documents and 50-item completion report.
- Preserve the bounded VP8/WGC pipeline while exposing physical source, user-requested, encoded,
  decoded and rendered dimensions plus the actual software/hardware encoder and decoder state.
- Add a ten-second minimum resolution dwell, Office and Low Bandwidth policies, explicit
  non-upscaling 720p/1080p/1440p/2160p selection and additive legacy-compatible signaling fields.
- Add an on-demand bounded Network Doctor with address-free normal output, sanitized technical
  codes and honest `NotConfigured` results for standalone STUN/TURN/loss probes that require an
  approved session.
- Add a bounded real-event session timeline that survives local release and is included in copied
  connection details and explicitly consented diagnostic bundles.
- Serialize Windows Graphics Capture end-to-end test classes so they do not contend for the same
  capture resource while preserving every production capability check and assertion.
- Add the dependency inventory and update notices with the exact unresolved SIPSorcery/libvpx/WiX
  distribution gates; no dependency version or PeerOnQ license changed.

Validation:

- Media passed 66/66 with one opt-in live-TURN skip; the focused media diagnostics subset passed
  5/5. Application passed 126/126 after one transient async test timeout passed both isolated and
  full-suite reruns. Signaling passed 107/107 with four opt-in Docker/distributed skips.
  Infrastructure passed 80/80; the focused Windows scaler/profile subset passed 24/24.
- The x64 Release desktop project built with zero warnings and zero errors.
- The final full Release solution run passed 622 tests, skipped five explicit external/opt-in tests
  (four Docker/distributed signaling and one live TURN media), and failed none. Windows end-to-end
  passed 49/49. Frontend lint/typecheck/build passed and all 81 frontend tests passed.
- Repository secret scan passed 985 files; production pnpm audit reported no known vulnerabilities,
  .NET reported no known vulnerable packages, and the production pnpm license inventory completed.
  Scoped Phase 6.5 C# formatting passed. The full solution formatter remains blocked only by two
  pre-existing whitespace findings in unchanged `CustomerOrganizationService.cs`.

Risk:

- Hardware codecs, automatic local content classification, multi-region TURN scoring and
  standalone session-scoped STUN/TURN/loss probes remain unimplemented or partial.
- The dependency audit is a distribution blocker: restored SIPSorcery terms contain an additional
  restriction, native libvpx notice provenance is incomplete, and WiX fee terms need qualified
  review. Physical two-device LAN/WAN/relay, 1440p/4K/60 FPS, large-transfer and 8/24-hour soak
  evidence was not fabricated.

Rollback:

- Revert the Phase 6.5 telemetry/profile/resolution protocol fields, Network Doctor, timeline,
  related tests, competitive documents, map entry and baseline together. Existing signaling v3
  peers remain compatible because all new request/quality fields are additive and optional.

## 2026-08-18 - Repair password-based unattended connection mode negotiation

Task:

- Diagnose why a configured unattended password was followed by an immediate declined connection.

Changed:

- Preserve password challenge/proof authentication while adding the host's allowed unattended
  permission mask as an optional signaling-v3 challenge field. Older peers remain compatible.
- Reject broader permissions before submitting a session, then use a native confirmation dialog to
  offer the host-approved mode and retry with the same one-time password flow after user approval.
- Move the 600,000-iteration password-proof derivation off the UI thread, prevent duplicate Start
  Session submissions, and show an actionable generic message for password/mode rejection without
  exposing which credential check failed.

Validation:

- Sanitized local logs showed the affected attempts ending as `PermissionDeclined`; the user's
  selected Full Control mode did not match the host's default View Only unattended scope.
- Release desktop build passed with zero warnings/errors. Unattended Application tests passed 8/8;
  full Application passed 127/127; real Signaling challenge test passed and full Signaling passed
  107/107 with four explicit Docker/distributed skips. Native UI/accessibility validation passed.

Risk:

- The optional allowed-scope hint is not authorization: malformed values fail closed and the host
  independently rechecks password proof, identity, lockout and exact permissions.

Rollback:

- Revert the optional challenge field, mode-mismatch exception/dialog, background proof derivation,
  related regression assertions and this documentation entry together.

## 2026-08-18 - Clear Fallow dead-code and maintainability findings

Task:

- Resolve every actionable unused-code, complexity, duplication, and refactoring finding shown by
  the repository's Fallow 3.17 VS Code integration without weakening runtime or security behavior.

Changed:

- Removed six retired in-product portal preview files and the unused `framer-motion` dependency;
  kept the active Admin session/logout methods with narrow analyzer suppressions documenting the
  factory-return-type false positive.
- Split public download resolution, device/address-book results, platform cards, sidebar items,
  status tones, and Vite configuration into bounded helpers while preserving their UI contracts.
- Decomposed Admin API transport, overview, sessions, resource tables, website/release dialogs,
  login, app shell, and state panels into focused loaders, hooks, render components, and pure
  decision helpers. Authentication, CSRF, refresh retry, RBAC, auditing, and signed-release checks
  remain fail-closed.
- Added stable facade modules for the shared Admin state panel and customer portal API, shell, and
  UI states so existing imports remain compatible while implementations stay independently
  maintainable.
- Aligned Fallow health scope with the repository rules for vendored UI, generated test results,
  and the intentionally excluded custom fetch adapter.
- Added a shared VS Code setting that keeps Fallow's current-code health checks enabled while
  omitting the non-actionable Git-history hotspot candidate list from the live editor panel;
  historical hotspot analysis remains available explicitly from the CLI.

Validation:

- Fallow 3.17 reports zero dead-code findings, zero complexity findings, zero duplicate groups,
  and zero refactoring candidates. The editor no longer presents historical Git-change candidates
  as remaining code work.
- Workspace typechecks, tests, build, and final audit are recorded in the task handoff.

Risk:

- This is a broad internal decomposition across three SPAs. Public component/module entry points
  and runtime behavior are preserved; targeted and workspace validation guard the extraction.

Rollback:

- Revert this changelog entry, VS Code/Fallow configuration, dependency cleanup, retired preview
  deletion, facade modules, and the associated responsibility-splitting refactors together.

## 2026-08-24 - Add signed full-platform upgrades from Admin

Task:

- Let an authorized operator stage a development-built PeerOnQ release in Admin and safely upgrade
  the complete production server platform without changing the protected remote-desktop client path.

Changed:

- Added an Admin Upgrade page with authoritative host status, bounded signed bundle staging,
  exact-version confirmation, progress/check visibility, stale-response protection, and MFA Owner-only
  apply/rollback controls. Release Managers may inspect and stage but cannot execute a deployment.
- Added fail-closed Admin API contracts for status, stage, apply, and rollback; kept custom CSRF,
  persisted the privileged audit event before publishing a ready request, bounded multipart input,
  and accepted only canonical stable versions and fixed request files.
- Added a root-owned systemd upgrade agent that claims one request atomically, verifies SHA-256 and
  the detached OpenPGP signature against an exact pinned fingerprint, performs static-only staging,
  reverifies a root-only archive, runs the existing installer dry-run, then applies or rolls back the
  complete server stack with durable sanitized status and root-only evidence.
- Made updater installation transactional and forward-only, bounded verified-archive retention,
  retained crash/manual-recovery evidence, added first-install public-key trust bootstrap, and made
  server bundle publication immutable with the signature/checksum visible before the `.run` commit
  marker. Full apply retains forward-compatible migrations and the bundle's verified Windows/public
  download payload; installed clients remain on the separate signed AppRelease self-update channel.
- Updated deployment topology, Nginx upload scope, maps, runbooks, design-system documentation, and
  regression coverage. The protected desktop transport, media, input, and file-transfer code was not
  changed.

Validation:

- Admin UI typecheck and production build passed; 35/35 Admin UI tests passed.
- Admin API Release tests passed 49/49 with zero failures; the API Release build passed.
- Disposable Linux upgrade-agent and installer suites passed; deployment configuration passed
  16/16; development and merged production Compose configurations validated.
- Windows server-bundle publication/version/MSI invariants and repository diff checks passed.
- Final security review found no remaining Critical/High implementation blocker and confirmed that
  unsigned arbitrary code cannot cross the pinned host signature boundary.

Risk:

- A real production-host systemd/Docker apply/rollback was not run in this repository session. The
  isolated Linux, Compose, API, UI, and build/invariant checks passed.
- Owner/MFA/API audit is governance rather than cryptographic isolation from a fully compromised
  Admin container. Such a compromise could queue an already valid offline-signed release outside
  normal API audit, but cannot forge unsigned code, the pinned keyring, or root status. Removing that
  residual trust requires a separate host-verified asymmetric Owner authorization broker.
- The first updater-enabled production release requires one manual SHA/GPG-verified installation of
  the public-only keyring and exact signer fingerprint. Database migrations and updater trust remain
  forward-only across application rollback.

Rollback:

- Revert this entry, the Upgrade UI/API, host agent/units, installer/builder changes, deployment
  mounts/proxy/env configuration, tests, maps, and runbooks together. On an installed host, stop the
  path/service before removing the Admin inbox mount; retain root logs and pinned trust for evidence.

## 2026-08-24 - Keep Upgrade release files selectable before host readiness

Task:

- Fix the Admin Upgrade form whose file inputs were disabled whenever the local host updater was
  not configured or had not yet reported the current platform version.

Changed:

- Separated local form editing from the authoritative staging gate. Operators can now select the
  `.run`, `.sha256`, and `.asc` files and enter the audit reason while the controller is unavailable;
  the Stage action remains disabled until the host is configured and reports a trusted current
  version.
- Added contextual helper text and regression coverage for both an unknown current version and the
  Development `platform_upgrade_disabled` state. Backend GPG, Owner/MFA, audit, and host gates are
  unchanged.

Validation:

- Admin UI typecheck passed; the focused Upgrade suite passed 13/13; the full Admin UI suite passed
  36/36; production build passed.
- The local `admin-ui` container was rebuilt and returned HTTP 200 for `/upgrade`; its served asset
  contains the new selectable-file helper state.

Risk:

- Selecting files does not transmit them. Development still cannot stage or apply a release until
  a real host updater is configured, which preserves the fail-closed production boundary.

Rollback:

- Revert the Upgrade form gating/helper copy, its two regression assertions, and this entry.

## 2026-08-24 - Keep production service endpoints internal and domain-based

Task:

- Remove the Connection configuration from the native Settings page while keeping signaling active
  internally, and ensure production release connections are compiled with domain names rather than
  IP/development aliases.

Changed:

- Removed the Settings Connection card, its mutable signaling handler, and connectivity wording. The
  existing internal `AppServices` endpoint resolution and automatic signaling connection remain intact.
- Added one shared official-release endpoint validator used by the protected release builder. Every
  HTTPS/WSS service input must use a public-style DNS name; IP literals, single-label hosts,
  localhost/private-development suffixes, `sslip.io`, and `nip.io` fail before signing/build work.
- Kept the explicit unsigned LAN development path unchanged and added focused endpoint-policy and
  native-UI regression coverage plus deployment/map/design documentation.

Validation:

- Production endpoint-policy tests passed for the canonical PeerOnQ domains and seven invalid
  IP/development-host cases.
- The Windows App Release build passed with zero warnings/errors; focused Settings/internal-signaling
  assertions and PowerShell syntax checks passed.
- Existing internal signaling endpoint resolution tests passed 11/11.
- The full native UI invariant remains blocked by a pre-existing missing prototype file
  (`artifacts/peeronq/src/layouts/PortalLayout.tsx`) before its assertions execute.

Risk:

- Operators creating an official release must provide real DNS names backed by valid TLS. Local
  two-laptop `sslip.io` testing continues through the unsigned development builder only.

Rollback:

- Restore the Connection XAML/code-behind, remove the production endpoint validator call/tests, and
  revert the associated documentation and this entry.

## 2026-08-24 - Enable trusted server-to-client upgrades

Task:

- Make the native Verified Updates panel usable when the server publishes a newer client version,
  including controlled development/public-pilot clients, without weakening release verification.

Changed:

- Development Release builds now permit a complete public update-trust set from environment or
  compiled metadata; Production remains compiled-metadata-only. The client validates the HTTPS
  manifest URL, bounded key ID, ECDSA P-256 SPKI public key, and publisher fingerprints before it
  exposes the update service.
- Extended the development/public-pilot builders to compile architecture-specific signed manifest
  URLs, channel, public manifest key/key ID, and Authenticode publisher allowlist. Partial bootstrap
  input fails before build, while omitting all public trust values preserves the existing passive
  unsigned connectivity-test package; no private key is embedded.
- Kept Check disabled only during initialization or when trust is absent. With valid trust it becomes
  active and retains the existing real check, verified streaming download, explicit install, and
  active-session safety flow.
- Added update configuration regression coverage and updated public-pilot commands, deployment,
  security, project-map, and native Settings documentation.

Validation:

- Focused update configuration and update security tests passed 27/27.
- The complete Infrastructure test project passed 83/83.
- Development builder parsed successfully, rejected partial trust before build, and contains all
  architecture-specific update metadata inputs.
- The Windows App Release build passed with zero warnings and zero errors.

Risk:

- The current machine has no configured manifest public key or Authenticode publisher certificate,
  so an update-enabled MSI cannot be truthfully published until those public trust values and a
  genuinely signed server release exist. Unsigned packages are never accepted as upgrades.

Rollback:

- Revert the development trust resolution, builder parameters/wiring, Update Configuration hardening,
  UI initialization copy, regression test, documentation, and this entry together.

## 2026-08-24 - Remove native clipboard card

Task:

- Remove the visible Remote device / Share text clipboard card from the native file-transfer surface.

Changed:

- Removed the clipboard card and its now-unused native toggle handler/state without changing the
  clipboard transport implementation or the remote-control, display, and file-transfer pipelines.
- Expanded the remaining Local machine file-selection card to the full available width and added a
  regression assertion that prevents the removed clipboard surface from returning.
- Updated the native design-system contract to reflect the simplified file-transfer surface.

Validation:

- The Windows App Release build passed with zero warnings and zero errors.
- PowerShell syntax and focused XAML/code-behind removal assertions passed; whitespace validation
  passed, and the protected remote-control, transport, media, platform, and signaling paths are
  unchanged by this task.

Risk:

- The standalone in-session clipboard toggle is no longer available from this surface; internal
  clipboard collaboration code remains intact for compatibility.

Rollback:

- Restore the two-column XAML card layout, clipboard toggle state/handler, regression expectations,
  design-system wording, and this entry together.

## 2026-08-24 - Package PeerOnQ 0.6.13 Ubuntu server pilot

Task:

- Produce a PeerOnQ-only `.run` that can be copied to and manually installed on an x86_64 Ubuntu
  server without including or using the unrelated ConfigCure Lens publisher certificate.

Changed:

- Built and payload-validated PeerOnQ 0.9.58 x64 and ARM64 unsigned public-pilot clients from the
  current protected source state, then embedded the verified x64 package in the PeerOnQ 0.6.13
  self-extracting server bundle.
- Published the immutable local bundle and matching SHA-256 sidecar under `dist/server`; no
  ConfigCure Lens name or certificate is present in the tracked bundle source or extracted payload.
- Kept the release explicitly classified as unsigned pilot material. No detached GPG signature or
  production-signing claim was manufactured without the required PeerOnQ release authority.

Validation:

- Both Windows installer builds completed with zero warnings and zero errors; x64 and ARM64 payload
  validation and their recomputed `SHA256SUMS.txt` checks passed.
- Windows and Ubuntu 24.04 independently verified the server bundle checksum. Ubuntu validated the
  self-extracting tar paths, absence of symlinks, embedded Windows version/release metadata and MSI
  checksum, and confirmed the extracted payload contains no ConfigCure Lens publisher text.
- A disposable Ubuntu 24.04 bootstrap dry-run generated the protected PeerOnQ environment and
  completed the 0.6.13 installer preflight without printing credentials or private keys.

Risk:

- The `.run` and embedded Windows MSI are unsigned public-pilot artifacts because no approved
  PeerOnQ Authenticode/GPG production signing material is available. They are suitable for a
  controlled manual pilot after SHA-256 verification, but are not a signed production release.

Rollback:

- Remove the ignored `dist/server/peeronq-server-0.6.13.run*` and `dist/public-pilot/0.9.58`
  artifacts and revert this changelog entry; no server was changed by this packaging task.

## 2026-08-25 - Pentest hardening and PeerOnQ 0.9.59 / server 0.6.15 pilots

Task:

- Perform a repository, API, deployment, update, transport and package security assessment, close
  confirmed findings, and create new Windows and Ubuntu packages without changing the protected
  remote-control, video, input or file-transfer implementation.

Changed:

- Resolved the high-severity Nano ID advisory by pinning `nanoid` 3.3.18 and regenerating the frozen
  pnpm lockfile. Added a fingerprint-specific Gitleaks allowlist for reviewed historical false
  positives and marked the synthetic support-invitation password as a test vector; new findings
  remain blocking.
- Closed an Ubuntu production-bootstrap failure found during real package dry-run: bootstrap now
  creates a separate customer JWT signing key, requires and validates the approved SMTP host/port,
  preserves closed registration with email verification, and generates every variable required by
  the production Compose files. Added a deterministic bootstrap/Compose contract test.
- Removed the native UI validation script's obsolete reference to a deleted frontend layout so the
  existing security, accessibility, update-trust and commercial-surface assertions execute again.
- Built and payload-validated PeerOnQ 0.9.59 unsigned public-pilot x64 and ARM64 MSIs, then embedded
  the verified x64 MSI in the PeerOnQ 0.6.15 Ubuntu server bundle. The failed 0.6.14 candidate was
  quarantined under `dist/server/failed-validation` and must not be installed.

Validation:

- pnpm audit reports no known vulnerabilities; all .NET projects report no vulnerable direct or
  transitive packages. Repository secret scanning passed 1,617 files, and Gitleaks 8.30.0 scanned
  70 commits with no leaks after the reviewed fingerprint-specific exclusions.
- Typecheck, lint, all web tests (66 + 36 + 11), the complete web build, Release solution build,
  focused API/auth/update/transport/media/security projects, native UI validation and server-bundle
  invariants passed. The Release build completed with zero warnings and zero errors.
- Anonymous admin/portal access, CSRF, CORS, host/path traversal, invalid registration, security
  headers, unprivileged containers and fixed process/SQL/TLS sinks were exercised. OWASP ZAP baseline
  scans of public, Admin and Portal surfaces produced zero failures; only low-risk review warnings
  for SPA cache policy and CSP inline styles remain.
- The adaptive QUIC test sustained 977,691 Kbps confirmed goodput with 0.9 ms input p95. The isolated
  1 GiB gate completed in 40 seconds at 25.3 MiB/s with 0.6 ms input p95; the WebRTC bulk-transfer
  isolation test and full-control video/input test passed.
- Ubuntu 24.04 independently verified the 0.6.15 SHA-256, safe extraction, root-owned mode 0600
  environment, no symlinks/private-key files/ConfigCure material, embedded Windows metadata and MSI
  checksum, production Compose preflight and complete dry-run. Windows x64/ARM64 package hashes and
  the server bundle hash were recomputed against their sidecars.

Risk:

- This is a broad internal gray-box assessment, not the independent third-party penetration test
  required before a production security claim. The current artifacts are explicitly unsigned pilots
  because approved PeerOnQ Authenticode and GPG release authority are unavailable.
- One aggregate solution run can make a real-time media sample miss its timing threshold when the
  workstation is already loaded; the affected media/transport projects and exact tests passed in
  isolation, including the 1 GiB transfer gate. Keep that scheduling-sensitive gate under observation.

Rollback:

- Restore the previous dependency pin and lockfile, remove the fingerprint-specific secret-scan
  metadata and bootstrap contract changes, restore the prior test reference, and remove the ignored
  0.9.59/0.6.15 pilot artifacts. Do not restore or install the quarantined 0.6.14 candidate.

## 2026-08-25 - Migrate legacy customer SMTP configuration in server 0.6.16

Task:

- Fix an upgrade from a pre-customer-mail server environment that stopped because the protected env
  did not yet contain `PEERONQ_CUSTOMER_SMTP_HOST`.

Changed:

- Allowed the non-secret `--customer-smtp-host` and `--customer-smtp-port` options during a normal
  install/upgrade, not only during first bootstrap. They fill missing or empty protected values and
  never replace a different existing host or port; SMTP credentials remain file-only.
- Made the missing-host failure actionable, retained the required production mail gate, documented
  the one-time legacy migration command, and added shell regression coverage for successful fill,
  conflicting-value rejection and fail-closed missing input.
- Built PeerOnQ server 0.6.16 with the already verified PeerOnQ 0.9.59 x64 unsigned pilot MSI. No
  desktop app, remote-control, media, input or file-transfer implementation changed. The superseded
  0.6.15 artifacts were moved to `dist/server/failed-validation` so they are not reused.

Validation:

- Bootstrap/production Compose contract, installer semantic-version/containment, server bundle
  publication/MSI invariant, repository secret scan and whitespace diff checks passed.
- A disposable Ubuntu 24.04 environment created a valid protected bootstrap env, removed its SMTP
  host/port to reproduce the legacy state, then passed the 0.6.16 migration dry-run and production
  Compose preflight using the new upgrade options. The protected env remained root-owned mode 0600.
- The 0.6.16 bundle SHA-256 and embedded 0.9.59 MSI were validated during immutable publication.

Risk:

- The operator must supply the real approved SMTP relay host; the installer intentionally does not
  invent or silently replace production mail routing. The bundle remains an unsigned controlled
  pilot until approved PeerOnQ GPG/Authenticode signing material exists.

Rollback:

- Continue using the previous installed release and remove the ignored 0.6.16 bundle. If migration
  was applied, retain the valid SMTP values in the protected env because older releases ignore them.

## 2026-08-25 - Complete legacy production migration in server 0.6.18

Task:

- Stop an upgrade from an older server env failing next on missing `PEERONQ_PORTAL_HOST`, and verify
  the complete legacy-to-current production configuration instead of fixing variables one at a time.

Changed:

- Added a bounded public-host migration derived from the validated existing `PEERONQ_WEB_HOST`.
  Missing web-www, API, Portal, Admin, Downloads, Updates, Presence, Signaling, TURN realm/host and
  release-artifact hosts are filled; valid existing custom hosts are preserved.
- Added SMTP DNS resolution preflight so an invented or unprovisioned host cannot produce a false
  successful installation. Dry-run remains non-mutating and its deployment documentation now says
  to repeat the same command without `--dry-run` after success.
- Expanded regression coverage for all derived hosts, custom-host preservation, unresolved SMTP,
  conflicting SMTP and missing SMTP. Built server 0.6.18 with the unchanged verified PeerOnQ 0.9.59
  x64 pilot client; desktop transport, media, input and file-transfer code did not change. Superseded
  0.6.16 and 0.6.17 artifacts were moved to `dist/server/failed-validation`.

Validation:

- Extracted and compared the generated env contracts from real 0.6.8, 0.6.9 and 0.6.10 bundles.
  Each lacked the same five now-migrated required keys: customer SMTP, customer JWT, platform-upgrade
  enabled state, Portal host and Signaling Redis password.
- In a disposable Ubuntu 24.04 host, the real 0.6.8 bundle created its legacy protected env and the
  0.6.18 bundle then completed the full legacy migration dry-run and current production Compose
  preflight. File ownership/mode remained root:0600 and no secret was printed.
- Bootstrap/Compose migration tests and package payload/MSI validation passed.

Risk:

- `smtp.peeronq.com` did not resolve during validation and cannot be used until its DNS/service is
  provisioned. The operator must use a real resolvable SMTP relay and place any credentials only in
  the protected env. The package remains an unsigned controlled pilot.

Rollback:

- Keep the currently installed release and remove the ignored 0.6.18 bundle. Newly derived public
  host keys are backward compatible and can remain in the protected env if a release rollback occurs.

## 2026-08-25 - Make server 0.6.19 self-configuring without SMTP

Task:

- Remove the external SMTP prerequisite so the existing Ubuntu server can be upgraded with the bare
  `.run` command while keeping the complete PeerOnQ stack, including Grafana, in one package.

Changed:

- Added a bounded production `Disabled` customer-mail provider. It is valid only with closed
  registration and email verification disabled; the development file sink remains rejected.
- Password-reset and organization-invitation operations now fail explicitly with
  `customer_mail_disabled` before creating a token or other external work. Existing valid SMTP
  environments remain supported and are not silently replaced.
- Bootstrap and legacy-env migration now select disabled mail automatically when no SMTP host is
  configured, generate all remaining required protected values and validate the complete Compose
  configuration. Supplying an SMTP host remains an optional explicit way to enable mail later.
- Built `peeronq-server-0.6.19.run` with the unchanged verified PeerOnQ 0.9.59 x64 unsigned pilot
  client. Desktop transport, media, input and file-transfer code did not change. Superseded 0.6.18
  artifacts were moved to `dist/server/failed-validation`.

Validation:

- Full Release solution build completed with zero warnings/errors. All 42 Cloud infrastructure tests,
  including seven customer-portal security tests, passed.
- Bootstrap/Compose mail migration, installer semantic-version/containment, embedded Windows client
  and server publication/MSI invariant tests passed.
- A disposable Ubuntu 24.04 host completed a fresh 0.6.19 bootstrap dry-run without SMTP. A second
  Ubuntu 24.04 host created a real 0.6.8 legacy env and passed the exact bare-command 0.6.19 upgrade
  dry-run without SMTP; current production Compose validation passed and the protected env was not
  changed by dry-run.
- Bundle SHA-256: `c5a1bf061f65f734e5d8c979eee8ee40a064720d8d6b10234450c806080c8bb4`.

Risk:

- Customer email verification, password-reset email and invitation email are intentionally
  unavailable until an operator explicitly configures SMTP. The bundle is an unsigned controlled
  pilot because approved PeerOnQ GPG/Authenticode signing material is unavailable. Disposable-host
  validation covered full configuration/preflight; the final non-dry-run container start must occur
  on the target Docker host.

Rollback:

- Keep the currently installed release and remove the ignored 0.6.19 bundle. If 0.6.19 has already
  migrated the protected env, the added disabled-mail, host, key and signaling values are backward
  compatible and may remain while the previous installed release is restored.

## 2026-08-25 - Recover stale SMTP state with server 0.6.20

Task:

- Let an operator who explicitly does not use customer email recover from the stale
  `smtp.peeronq.com` state left by a failed 0.6.19 attempt, without manually editing the protected
  environment or disturbing the healthy installed 0.6.6 stack.

Changed:

- Added the mutually exclusive `--disable-customer-mail` installer option. It deliberately sets the
  bounded `Disabled` provider, closed registration and no email verification, and clears SMTP host,
  username, password and port override values before full Compose validation.
- Rejected combining explicit mail disable with SMTP host/port options and rejected all mail
  migration options outside install/upgrade mode.
- Added regression coverage starting from an explicitly configured SMTP provider with an
  unresolvable host and stored credentials. Built `peeronq-server-0.6.20.run` with the unchanged
  PeerOnQ 0.9.59 x64 pilot client; no desktop, media, input or file-transfer code changed.
- Moved superseded 0.6.19 artifacts to `dist/server/failed-validation` and copied the new `.run` plus
  checksum sidecar to the Windows Desktop for transfer together.

Validation:

- Bootstrap/Compose environment, installer semantic-version/containment, embedded Windows client
  and server publication/MSI invariant tests passed.
- A disposable Ubuntu 24.04 environment reproduced the unresolvable stale-SMTP failure, then passed
  the complete 0.6.20 production Compose dry-run with `--disable-customer-mail`. The dry-run did not
  mutate the protected env, and conflicting disable/SMTP CLI input was rejected.
- Bundle SHA-256: `cce003b48ef3fa5e5ee81f5d47c747781f1e005615ef518942ff2635bd2848cb`.

Risk:

- The explicit option intentionally removes stored customer SMTP routing and credentials; use it
  only when customer email must remain disabled. Password-reset and invitation email remain
  unavailable in that policy. The bundle is an unsigned controlled pilot.

Rollback:

- The failed dry-run leaves the active 0.6.6 release and protected env unchanged. After a completed
  upgrade, run the 0.6.20 bundle with `--rollback` to restore the prior installed release; re-enabling
  customer mail later requires an explicitly configured real SMTP provider.

## 2026-08-25 - Make the new base website authoritative with server 0.6.21

Task:

- Fix the target host continuing to serve the old public website after the complete 0.6.20 stack
  built and reached healthy state.

Changed:

- Removed the post-deploy dependency on obsolete Downloads-page classification copy. The installer
  now validates the immutable embedded MSI hash, the canonical download path compiled into the web
  assets, and byte equality between the public `/downloads` document and the new base web image.
- Added a constrained, networkless UID 1654 Compose operation that transactionally deactivates a
  superseded website overlay after stack health. A failed release restores the overlay before the
  prior application is restarted; a successful release commits the base website activation while
  retaining every stored website release directory.
- Added shell regression coverage for deactivate/restore/commit, unsafe-link rejection and the
  canonical verifier contract. Built `peeronq-server-0.6.21.run` with the unchanged verified PeerOnQ
  0.9.59 x64 pilot MSI; no desktop, media, input or file-transfer code changed.

Validation:

- Shell syntax, website-state transaction, bootstrap/production Compose contract, installer
  semantic-version/containment, embedded-client and server publication/MSI invariant checks passed.
- PeerOnQ frontend typecheck passed and all 66 tests passed. The extracted 0.6.21 payload passed its
  embedded MSI verifier; its actual Docker web image built and contained the canonical MSI URL.
- A disposable Docker-backed production bootstrap passed the complete 0.6.21 Compose dry-run. The
  hardened website-state container also completed deactivate and restore as UID 1654 against a real
  named volume with no network, read-only root filesystem and all capabilities dropped.
- Bundle SHA-256: `3944377b9b412e49311e4d8fd6964af8870632e9f7322a0ad5ced094ca6fb84a`.

Risk:

- A full platform upgrade intentionally deactivates any active UI-only website patch so the newly
  bundled website is authoritative. Stored patches remain available, but an operator could manually
  reactivate an older patch later. The bundle and embedded MSI remain unsigned controlled-pilot
  artifacts because approved production signing material is unavailable.

Rollback:

- A failed deployment restores both the prior website overlay and prior application automatically.
  After a successful upgrade, run the 0.6.21 bundle with `--rollback` to restore the previous
  application release; stored website patches can be selected separately through Admin if required.

## 2026-08-25 - Restore relayed Full Control media and add internal operations hosts

Task:

- Fix accepted production support requests closing before the remote screen appears, package the
  fix for the Admin full-platform upgrade flow, and serve Admin, Portal, Grafana, and Prometheus by
  their canonical `*.peeronq.com` names only to the configured internal network.

Changed:

- Corrected the WebRTC ICE policy so a mixed Full Control session retains STUN/TURN candidates for
  screen and input. Only a file-only primary connection and the dedicated bulk-file peer remain
  direct-only; relay-only Full Control omits that bulk peer and keeps the authenticated signaling
  file relay fallback.
- Added regression coverage for TURN retention and relay-only bulk-peer omission.
- Added CIDR-restricted TLS virtual hosts for `admin.peeronq.com`, `portal.peeronq.com`,
  `grafana.peeronq.com`, and `prometheus.peeronq.com`. The proxy uses the direct TCP source address,
  Grafana keeps its own authentication/WebSocket route, and Prometheus permits only GET/HEAD.
  Direct Grafana/Prometheus ports remain loopback-bound.
- Added production/bootstrap environment migration for the monitoring hostnames, certificate SAN
  validation/ACME expansion before the active release is stopped, and renewal-time coverage checks.
- Built validated unsigned public-pilot clients `0.9.60` for x64 and ARM64. Built
  `peeronq-server-0.6.27.run` with the x64 client embedded for Admin full-platform staging.

Validation:

- The installed 0.9.59 production log showed signaling acceptance followed immediately by WebRTC
  media `Failed`/`Closed` and `MediaLost`; code inspection confirmed that the Full Control
  `FileTransfer` flag incorrectly removed TURN from the shared screen/input configuration.
- Full Release solution build succeeded with zero warnings/errors. All seven focused ICE tests
  passed. The full media run passed 110, failed one timing gate and skipped the opt-in live TURN
  test; the isolated timing gate still failed on this workstation because baseline input p95 was
  38.0 ms against a 35 ms ceiling, before bulk-load comparison.
- Nginx syntax/runtime routing passed in the pinned container, including `403` from an out-of-CIDR
  source for all four internal hosts. Shell syntax and bootstrap/production Compose contract tests,
  server publication invariants, both MSI payload validations, and extracted server-payload hash,
  traversal, embedded-client, media-fix, and host checks passed.
- Client SHA-256: x64
  `b614d740c2e25595eaabeb5d2db3b77de04d9dbdc3ca3712076de759de00aced`; ARM64
  `15c8cc6c259a1c286ae63b8b5e0073de3f0184fb08c75f04d2543edf60a2db1b`.
- Server bundle SHA-256:
  `0f3ec8b8d9cf4217f6f5346132e3b129e3f5ea3aaa862759847be86a98424d3f`.

Risk:

- The Windows MSI and server bundle are controlled-pilot artifacts. This workstation has no GPG
  command/private release key, so no `.asc` exists; the Admin updater must reject the bundle until
  it is detached-signed by the already enrolled offline platform key.
- An existing exact-SAN certificate must be expanded for Grafana and Prometheus. The installer does
  this before downtime when the original Let's Encrypt lineage is available; public DNS/TCP 80 must
  be ready for HTTP-01. Otherwise import a trusted SAN/wildcard certificate. External HTTPS remains
  blocked by CIDR. The validating workstation network also resolved PeerOnQ public names to a Palo
  Alto sinkhole, which must be cleared independently for live production media/TURN acceptance.

Rollback:

- Do not stage the unsigned bundle. After a signed 0.6.27 upgrade, use the Admin rollback action or
  run the same bundle with `--rollback`; the retained release and all named data volumes are
  preserved. Remove the four internal split-DNS records if those routes are rolled back.

## 2026-08-25 - Keep client enrollment recovering in server 0.6.28

Task:

- Fix externally installed clients that remain at Enrollment pending without a PeerOnQ ID after a
  transient API/DNS outage, and produce one replacement Admin full-platform patch containing this
  recovery together with the previously validated Full Control TURN and internal-host fixes.

Changed:

- Changed the desktop cloud client from a one-shot reconnect window to lifetime recovery using
  bounded retry bursts and a bounded cooldown. Successful authentication resets the retry burst;
  permanent identity, authentication, blocked-device, and unsupported-version failures still stop
  fail closed.
- Added regression coverage proving that a client receives its server-assigned ID after an initial
  transient registration failure without restarting the application.
- Built validated unsigned public-pilot clients `0.9.61` for x64 and ARM64. Built
  `peeronq-server-0.6.28.run` with the x64 0.9.61 client embedded and with the 0.6.27 media/TURN and
  internal Admin/Portal/Grafana/Prometheus host changes retained.

Validation:

- The focused cloud-client tests passed 9/9 and the focused ICE tests passed 7/7. Full Release
  solution build succeeded with zero warnings/errors, and the server publication/version/MSI
  invariant tests passed.
- The extracted 0.6.28 payload passed its internal payload hash and traversal checks, embedded MSI
  verification, version/release-type checks, and source-presence checks for enrollment recovery,
  TURN retention, and all four internal virtual hosts.
- Client SHA-256: x64
  `1ad990f0b230efa33a92a530b10bb6babb00b332deb5fed1a4e991ef384fed9d`; ARM64
  `5517c1621446a6cccf81e1c7f0b43d2861482f207acacc83ce53a9d9c711abad`.
- Server bundle SHA-256:
  `b5ab99ec44b2db5ee81c28f27d7d654553f93ebd31dc11cc113e85e49ecf5363`.

Risk:

- The Windows MSI and server bundle are unsigned controlled-pilot artifacts. This workstation has
  no GPG command/private release key, so the Admin updater must reject 0.6.28 until the `.run` is
  detached-signed by the already enrolled offline platform key.
- The currently deployed proxy can return `403` before patch upload when its
  `PEERONQ_ADMIN_ALLOWED_CIDR` does not include the real direct client source network. Correct that
  protected server setting through SSH/local console; never broaden it to `0.0.0.0/0`.
- Existing exact-SAN TLS certificates still require the four internal names, or a trusted wildcard
  certificate. HTTP-01 expansion requires public DNS/TCP 80; private split DNS alone cannot satisfy
  that challenge.

Rollback:

- Do not stage the unsigned bundle. After a signed 0.6.28 upgrade, use the Admin rollback action or
  run the same bundle with `--rollback`; the retained prior release and all named data volumes are
  preserved. Installed 0.9.61 clients can be downgraded with the prior approved MSI if necessary.

## 2026-08-25 - Remove client wall-clock dependency from enrollment in server 0.6.29

Task:

- Fix production 0.9.60 and 0.9.61 clients reaching Cloud registration but permanently stopping
  with `invalid_challenge`, leaving their public routing/signaling status disconnected even after
  DNS, TLS, service health, TURN, and operating-system time synchronization were verified.

Changed:

- Replaced client-wall-clock challenge validation with fail-closed canonical validation. The client
  now requires unique parseable `issued_at` and `expires_at` fields, exact agreement between the
  signed payload and response expiry, a positive lifetime, and the unchanged two-minute maximum.
  The Cloud challenge store remains authoritative for real-time expiry and one-use consumption
  during proof authentication, so stale or replayed challenges still cannot authenticate.
- Added categorical local rejection logging without payload, proof, token, key, or device data.
  Added regression coverage for bounded challenges issued on a server clock ahead of the client,
  overlong canonical lifetimes, and response/payload expiry mismatch.
- Built validated unsigned public-pilot clients `0.9.62` for x64 and ARM64. Built
  `peeronq-server-0.6.29.run` with the x64 0.9.62 client embedded while retaining the Full Control
  TURN, lifetime transient enrollment retry, and internal operations-host changes.

Validation:

- Focused cloud-client tests passed 10/10 and the complete Infrastructure suite passed 85/85. Full
  Release solution build succeeded with zero warnings/errors; scoped formatter verification and
  server publication/version/MSI invariant tests passed.
- Both MSI payload validations passed. The extracted 0.6.29 payload passed its internal hash,
  traversal, embedded-client version/release-type, canonical challenge-fix, retained media/TURN,
  and internal-host checks.
- Client SHA-256: x64
  `ac2c3da85efb5607cbd2048f57ba8d317cd747ff95d5982ffdaddd608c8b2263`; ARM64
  `c616d34af24f7c6128c01b45a373dd559ff562a3d0ffc0226273e9e3b99e70d4`.
- Server bundle SHA-256:
  `14c7788074ed21054aac6cecc0c0a4275f8ae722ef8a9bbdd77dce1654b98975`.

Risk:

- The Windows MSI and server bundle are unsigned controlled-pilot artifacts. This workstation has
  no approved private release key, so Admin must reject 0.6.29 until its `.run` receives a detached
  signature from the already enrolled offline platform key.
- Production enrollment must still be verified after staging the signed bundle and installing
  0.9.62. A malformed, mismatched, non-positive, or overlong challenge remains a permanent
  `invalid_challenge`; the new categorical reason identifies which invariant failed.

Rollback:

- Do not stage the unsigned bundle. After a signed 0.6.29 upgrade, use Admin rollback or run the
  bundle with `--rollback`; retained releases and named volumes are preserved. Reinstall the prior
  approved 0.9.61 MSI only if client rollback is required.

## 2026-08-25 - Preserve TURN relay under client clock skew

Task:

- Fix accepted Full Control sessions that work on one LAN but never show the remote screen when the
  two connected clients are on different networks.

Changed:

- Stopped the media client from discarding freshly issued TURN endpoints by comparing the
  server-authored credential expiry with the workstation's local wall clock. Signaling still issues
  credentials only for an authenticated accepted session, and coturn remains authoritative for HMAC
  validation and expiry when the client requests an allocation.
- Corrected the production bootstrap to configure coturn's single public external address instead
  of mapping it to the host LAN address that is not present inside the Docker bridge. In-place
  upgrades migrate the legacy `public-ip/server-lan-ip` form while preserving or deriving the Admin
  LAN CIDR before removing that invalid coturn mapping.
- Replaced the local-expiry regression with coverage proving that a client whose clock is ahead
  retains UDP/TCP/TLS TURN candidates for relay negotiation. Added bootstrap/upgrade regression
  coverage for the corrected TURN external address and legacy environment migration.

Validation:

- Focused ICE tests passed 7/7; the complete Media suite passed 111 with the opt-in live TURN test
  skipped. The full Release solution build completed with zero warnings/errors, scoped formatter
  verification passed, and the bootstrap/production Compose migration contract passed.
- Built and payload-validated unsigned public-pilot clients `0.9.63` for x64 and ARM64. Built
  `peeronq-server-0.6.30.run` with x64 `0.9.63` embedded. The server publication invariant,
  checksum, archive traversal list, embedded MSI hash/version, migration header, bootstrap mapping,
  and embedded media fix all passed stream validation.
- Client SHA-256: x64
  `52a97cfc22049a787cb22efe1fbbd1ead5687d8b18bb73f7da762490426819f3`; ARM64
  `86ac2faf92a2003ecde9efdfc098c11e7b3b4090ac9f9024d3792c9f5c3615cb`.
- Server bundle SHA-256:
  `031860747d4022d84db7fa67f5e326f2e473a243451227d0ad5afd1b3a3a503b`.

Risk:

- An already expired credential may now reach coturn instead of being rejected locally; coturn
  rejects it using server time and the shared-secret HMAC, so this changes reliability rather than
  relay authorization. External DNS, router port-forwarding, and host firewall remain independent
  deployment requirements.
- These artifacts are unsigned controlled-pilot builds. No detached `.asc` was created because the
  approved offline release key is not available on this workstation; Admin must continue to reject
  the `.run` until it is signed by the already enrolled platform release key.

Rollback:

- Restore the client-side `CredentialExpiresAt` filter, legacy TURN external-IP generation, migration
  and tests, then reinstall the prior approved client/server bundle. After a signed `0.6.30` upgrade,
  use Admin rollback or run the bundle with `--rollback`; retained releases and named volumes remain.
## 2026-08-25 - Automate split-DNS production TLS with Spaceship DNS-01

- Added a bundled Certbot hook that uses the fixed Spaceship DNS records API with a DNS-only
  `dnsrecords:read`/`dnsrecords:write` key, exact case-sensitive TXT cleanup, pagination, retries,
  authoritative nameserver propagation polling, and root-only stale-token debt recovery.
- Bootstrap and in-place upgrades now accept a root-owned `acme-spaceship/api-key` and
  `api-secret` directory. When present, the server obtains `peeronq.com` plus `*.peeronq.com`
  without stopping Nginx or binding TCP 80; the weekly renewal timer preserves the active pair on
  all provider/ACME failures.
- Added hook unit tests, payload inclusion, hardened renewal service settings, and deployment
  instructions for internal `admin`, `portal`, `grafana`, and `prometheus` split DNS names.
- Built immutable pilot bundle `peeronq-server-0.6.40.run` with the verified Windows client
  `0.9.64`; the bundle checksum is recorded beside the artifact. It is intentionally unsigned
  until the enrolled offline release key signs the final file.
- Fixed the Certbot 5.7 apex-plus-wildcard hook contract: both active annotated identifiers are
  normalized to `peeronq.com`, so the hook now accepts one or two exact zone identifiers while
  continuing to reject any unrelated name before an API call.
- `peeronq-server-0.6.40.run` SHA-256: `c9f20f94315d7dbb658f5b52b257b03fd6d8b0285da6b36eb2fc13b31fef174d`.

## 2026-08-25 - Allow Spaceship DNS-01 through its Cloudflare edge

- Added an explicit non-secret `PeerOnQ-DNS01/1.0` user agent to every Spaceship DNS API request.
  Spaceship's Cloudflare edge rejected Python urllib's default browser signature with HTTP 403,
  error 1010, before otherwise valid API-key scopes and credentials reached Spaceship.
- Added regression coverage for the outbound user-agent contract and built server patch 0.6.41
  while retaining the embedded Windows public-pilot client 0.9.64.
- `peeronq-server-0.6.41.run` SHA-256:
  `f900eed86b201067bc5358bdbd0dc4390d3d5ed598786a65dba0d014bd63cbf4`.
