# PeerOnQ current state

## Current source contract — 2026-09-28

Source facts below were checked against the current checkout. They describe implementation, not
production approval or a fresh execution of the historical tests farther down this page.

- `Directory.Build.props` defines the canonical server/Windows client version as `0.9.75`. Linux, Android
  and Apple client versions derive from it; Android and Apple bundle codes are `9075`. A source
  version does not establish that a matching signed package has been built or published.
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
the proposed fix only. Production installation, SMTP authentication, sender authorization and
actual recipient delivery remain untested. Capabilities or a banner do not prove mail delivery.

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
