# Project Map

**Purpose:** this file lets an AI agent understand the repository without scanning it.
Read this instead of running a full repo scan. If something here is wrong, fix this file
in the same change.

**Last updated:** 2026-09-27

## Product Summary

**Project name:** PeerOnQ (public repo: [pasha555/PeerOnQ](https://github.com/pasha555/PeerOnQ))

The public MIT-licensed `main` branch begins with a source-only snapshot. Earlier local history is
retained on `feat/phase1-remote-view` and is not pushed because it contains generated packages.
Commits on the publication branch are pushed to `origin` and verified; installers and deployment
remain subject to the separate release gates.

Current source contract: canonical client version `0.9.67` comes from `Directory.Build.props`;
Linux/Android/Apple versions derive from it. Signaling accepts exactly v3. Native non-Windows
projects are attended viewer/controller previews with separate physical-device/release gates;
see `docs/CURRENT_STATE.md` and `docs/CROSS_PLATFORM_CAPABILITIES.md`. Dated phase reports retain
their historical artifacts/results and do not certify the current checkout.

Release policy: each new versioned product patch advances the server and clients together using
`PeerOnQWindowsClientVersion`. The full server bundle and rebuilt Windows x64/ARM64 packages must
share that version, including releases with only server/web behavior changes. Historical bundles
retain their original versions; publication still requires validation and the existing signing gates.

**Main purpose:** secure remote-access / remote-desktop platform — "Connect securely. Work anywhere."
The repo contains an offline **frontend prototype** (`artifacts/peeronq`), an API skeleton
(`artifacts/api-server`), and the real .NET remote-view product under `src/`. The .NET product has
authenticated signaling, screen streaming, ICE/STUN/TURN, diagnostics, bounded secure resume,
hybrid post-quantum application encryption for video/collaboration, direct-only WebRTC file
transfer, clipboard, trusted devices, address book, unattended access,
tamper-evident audit, signed-update enforcement, WinUI remote input control, and WiX x64/ARM64
installers. Negotiated peers move permission-gated input onto a dedicated ordered WebRTC peer while
the primary channel remains a compatibility fallback; Windows injection uses `SendInput`, and
reconnect/error/session end releases held state and disables control fail-closed.
Phase 6 adds a PostgreSQL/Redis cloud control plane, installation presence, signed-release download
delivery, consent-based diagnostics, an MFA/RBAC operator surface, governed retention, and a
deployable observability/backup stack without moving screen, input, clipboard, or file payloads
through the cloud APIs.

**Main users:**

- End users connecting to their own machines remotely (dashboard, devices, sessions, file transfer).
- The internal team, using the prototype to validate UX before the desktop agent is built.

**Critical behavior (must never break):**

- The prototype must never make real network calls — it is a preview. Enforced by
  `artifacts/peeronq/src/test/noNetworkRequest.test.ts`.
- Prototype data lives in `localStorage` only; nothing leaves the browser.
- Light/dark theming and the design tokens in `src/index.css` are the product's visual contract.
- The native runtime accepts validated attended `ViewOnly`, `FullControl`, `FileTransferOnly`, or
  explicit `Custom` scopes. `Phase1SessionScope` is enforced by the WinUI request flow,
  `SessionCoordinator`, signaling server, permission prompt, and WebRTC media factory. Dashboard
  attended sessions request a full-control envelope, but the remote owner chooses View only or Full
  control through an explicit, initially unselected permission-dialog choice; the signaling server
  atomically binds that accepted choice before media starts. The compact native dialog presents
  requester, exact scope, local request time, and timeout through theme-aware Fluent rows without
  changing that fail-closed selection contract. Full
  control grants screen view, input, and explicitly listed file transfer; text clipboard remains a
  separate requested capability.
  The viewer attaches authorized file-transfer and remote-input channels even when secure
  collaboration becomes ready after the viewer window; Full Control receives offered files
  directly into `Downloads\\PeerOnQ`, while lesser scopes retain the explicit receive flow.
  The sharer arms the capture-to-input display mapping before its SDP offer can reach the viewer;
  input injection remains disabled until the authenticated viewer focus request is acknowledged.
  The LAN development default High Quality profile requests up to 4K/UHD (never above the source display)
  and preserves its requested resolution under adaptation, reducing frame rate before pixels.
  Authorized file-transfer sessions start in File Transfer Priority. Live sessions reserve the
  adaptive video target plus authenticated mouse/keyboard/security capacity. Direct, relay, and
  negotiated native-QUIC chunks share the adaptive file pacer: a finite 150 Mbps File Transfer
  Priority ceiling replaces unlimited bursts until usable measurements exist, then file traffic
  uses the measured remainder (within a defensive 1 Gbps bound) and is reduced first when loss,
  RTT, jitter, or backpressure worsens. New peers on the selected direct-LAN path authenticate an
  ephemeral QUIC connection inside PNQE and pin each new transfer to that path. New Full Control
  peers keep screen RTP, input SCTP and file SCTP/QUIC on separate connections; old peers retain the
  primary input/file fallback. Mixed screen/input sessions keep TURN available on the primary and
  input peers, while file-only primary peers and the dedicated bulk peer remain direct-only. Under
  an explicit relay-only policy the bulk peer is omitted, so screen/input can connect through TURN
  and authorized file records use only the negotiated authenticated signaling relay. After native
  chunks are actually written, the receiver returns bounded authenticated cumulative receipts.
  Their measured goodput may raise the native file budget by 25% per confirmed window; stale
  feedback or degraded media/input health immediately restores the conservative allocation.
  Every pull request and main-branch push runs an explicit 1 GiB native-QUIC contention gate that
  requires at least 1 GiB/minute payload throughput while preserving the interactive input p95.
  Bounded relay completion work never converts queued signaling heartbeats into a media-session
  disconnect.
  Unattended access is a distinct default-off, credential/trust-bound request.

## Tech Stack

**Monorepo:** pnpm workspaces (`pnpm-workspace.yaml`), Node.js 24, TypeScript 5.9.
**pnpm only** — `npm install` is blocked by the root `preinstall` script.
`.nvmrc` selects Node 24; root `engines.node = 24.x` and pnpm `engineStrict: true` enforce that
runtime major. The shared `@types/node` catalog targets 24; CI remains on Node 24.

**Frontend:**

- React 19 + Vite + TailwindCSS v4 (`@tailwindcss/vite`)
- Routing `wouter`; UI shadcn/Radix; animation `framer-motion`
- Forms `react-hook-form` + `zod`; data layer shell `@tanstack/react-query`
- Tests `vitest` + Testing Library + `jsdom`

**Backend:** the production cloud surface is ASP.NET Core/.NET 10: Cloud API, Presence Server,
Admin API, Downloads Service, and shared OpenTelemetry/Serilog hosting defaults. The Express 5
`artifacts/api-server` package remains a legacy artifact skeleton with only `/api/healthz`.

**Database:** Phase 6 uses PostgreSQL through EF Core/Npgsql with forward-only migrations in
`src/PeerOnQ.Cloud.Infrastructure/Persistence/Migrations`; Redis is used for one-use challenges,
presence leases, distributed ownership, Signaling device/session routing, rate coordination, and
Admin Data Protection keys. The
legacy Drizzle schema under `lib/db` remains empty and is not the cloud source of truth.

**API contract:** OpenAPI spec `lib/api-spec/openapi.yaml` → Orval codegen →
`lib/api-client-react` (React Query hooks) and `lib/api-zod` (Zod schemas).

**Background jobs:** the Cloud API retention, presence-expiration and stale-session workers and the
Admin infrastructure-snapshot worker acquire renewable Redis operation leases before mutation, so
only one API replica owns each run. The Admin worker records fixed Prometheus service availability
and scrape latency every minute. Retention
performs bounded, policy-governed cleanup and anonymization with legal-hold checks and immutable
aggregate evidence. Deployment also includes
PostgreSQL backup/restore-test jobs and monitoring/alerting services.

**Deployment:** `src/PeerOnQ.Infrastructure.Deployment` contains development and staging Compose,
an opt-in single-host/single-region application-tier HA validation overlay,
TLS reverse-proxy routing with bounded high-concurrency Nginx worker configuration,
PostgreSQL/Redis, OTel Collector, Prometheus, Loki, Tempo, Grafana with the source-controlled
`PeerOnQ Operations` dashboard, Alertmanager, coturn/signaling integration, and backup/restore
scripts. Production examples pin every base/service image by immutable digest. Replit artifact definitions
remain only for the offline prototype and legacy API skeleton.

**.NET remote-view product:** .NET 10, WinUI 3, SIPSorcery WebRTC/VP8, Bouncy Castle
X25519/Ed25519/ML-KEM/ML-DSA, AES-GCM/HKDF-SHA-512, ASP.NET Core WebSockets, SQLite/DPAPI, Windows AMSI,
xUnit. Deployment uses Docker Compose, Nginx, and coturn REST credentials.

## Important Directories

```text
artifacts/
  peeronq/            MAIN PRODUCT: PeerOnQ frontend prototype (React + Vite)
    src/app/router/     Route table and legacy redirects
    src/layouts/        Separate public and desktop-preview layouts
    src/pages/          Public pages and offline desktop-preview pages
    src/components/     Shared components; components/ui = shadcn primitives (vendored)
    src/features/       Feature-sliced logic (repositories + hooks)
    src/repositories/   Barrel re-exporting src/features/*/xRepository.ts
    src/services/       apiClient.ts — offline boundary; always returns NotConfigured and never performs I/O
    src/compatibility/  Explicit browser-storage brand migration adapter
    src/types/          All domain types (Device, Session, Contact, AppSettings, ...)
    src/hooks/          useTheme, useLocalStorage, use-toast, use-mobile
    src/lib/            utils.ts (cn), validation.ts (Zod schemas)
    src/test/           Vitest suites incl. accessibility + no-network guards
    src/index.css       Design tokens — single source of truth for colors/typography
    eslint.config.js    Flat ESLint config (ignores components/ui and shadcn hooks)

  api-server/         Legacy Express 5 skeleton (health route only)
  peeronq-admin/      Production React/Vite admin SPA; API-backed and RBAC-aware
  peeronq-portal/     Production customer SPA; cookie/CSRF API, organizations, teams and policy
  mockup-sandbox/     Throwaway UI sandbox — NOT part of the product

lib/
  api-spec/           OpenAPI spec + Orval config (source of truth for the API)
  api-client-react/   GENERATED React Query client — never edit by hand
  api-zod/            GENERATED Zod schemas — never edit by hand
  db/                 Drizzle setup; schema currently empty

src/                  .NET 10 / C# remote-access product, native viewers and cloud services
  PeerOnQ.Domain          PeerOnQ ID, device identity, typed session state machine, Phase1SessionScope
  PeerOnQ.Application     Ports + SessionCoordinator (timeouts, lifecycle, cleanup)
  PeerOnQ.Infrastructure  Cross-platform SQLite/logging/audit plus Windows update verification
  PeerOnQ.Transport       Signaling v3/WebSocket client + active same-LAN reliable QUIC file data plane
  PeerOnQ.Media           Bounded frame pipeline/statistics plus separate WebRTC VP8, input and compatibility bulk peers
  PeerOnQ.Platform.Windows Windows.Graphics.Capture + D3D11 readback + scoped SendInput
  PeerOnQ.Platform.Linux   Viewer-only capability profile, libsecret keyring adapter, fail-closed capture boundary
  PeerOnQ.Platform.Android Viewer-only capability profile, VP8 header/key mapping and fail-closed capture boundary
  PeerOnQ.Platform.Apple   macOS/iOS/iPadOS viewer capability, input/viewport mapping and fail-closed capture boundary
  PeerOnQ.Signaling.Server ASP.NET Core WebSocket signaling
  PeerOnQ.App             WinUI 3 desktop app (unpackaged, asInvoker)
  PeerOnQ.App.Linux       Avalonia Linux viewer/controller using shared signaling, session and WebRTC layers
  PeerOnQ.App.Android     Native .NET Android attended viewer/controller using Keystore + MediaCodec
  PeerOnQ.App.Apple       Shared native UIKit iPhone/iPad + Mac Catalyst attended viewer/controller using Keychain + static libvpx
  PeerOnQ.Turn.Configuration Pinned coturn image, runtime config, quotas and health check
  PeerOnQ.Realtime.Deployment Phase 3 signaling/TURN Compose, Nginx, TLS and internal Phase 6 monitoring bridge
  PeerOnQ.Shared.Contracts      Versioned Phase 6 wire contracts and canonical challenge payloads
  PeerOnQ.Cloud.Domain          Cloud entities and security/lifecycle invariants
  PeerOnQ.Cloud.Application     Use cases and persistence/identity abstractions
  PeerOnQ.Cloud.Infrastructure  EF Core/PostgreSQL, Redis, migrations, repositories and retention
  PeerOnQ.Cloud.Api             Device/install/session API plus isolated customer account/organization API
  PeerOnQ.Presence.Server       Installation-scoped SignalR presence and lease ownership
  PeerOnQ.Admin.Api             MFA/RBAC/CSRF-protected operator API and immutable audit boundary
  PeerOnQ.Downloads.Service     Signed-release resolution, streaming and privacy-safe telemetry
  PeerOnQ.Observability         Shared health, metrics, OTel and structured logging defaults
  PeerOnQ.Infrastructure.Deployment Phase 6/7 stack, separate portal host, web/update routing, monitoring and backup/restore

tests/                Product and Phase 6 xUnit projects covering domain through live infrastructure

installer/            WiX Toolset 6 per-machine x64/ARM64 self-contained MSI and branded UI assets (defaults to Program Files)
docs/                 Architecture, security, privacy, recovery, standards, contributor, installer, update and release operations
  competitive/        Phase 6.5 inventory, baseline, gap/license analysis, architecture decisions and factual completion report
benchmarks/            Committed sanitized performance evidence; missing measurements remain explicit nulls

scripts/              Workspace scripts (post-merge.sh, src/hello.ts)
  apple/build-peeronq-apple-viewer.sh macOS-only iPhone/Mac Catalyst builder with separate registered-device development and distribution signing, WSS, static-libvpx, architecture, profile, signature and version gates
  apple/test-peeronq-apple-viewer-version.ps1 Canonical Apple display-version/bundle-code and build-contract regression gate
  android/build-peeronq-android-viewer.ps1 Server-bound, development-signed arm64/x86_64 APK builder plus separate BlueStacks Nougat 32 x86 variant and payload/signature/hash gate
  android/test-peeronq-android-viewer-version.ps1 Canonical Android display-version/version-code regression gate
  linux/build-peeronq-linux-viewer.sh Canonical-version x64/ARM64 Linux viewer tarball, manifest and SHA-256 builder
  linux/test-peeronq-linux-viewer-version.sh Linux project/builder canonical-version regression gate
  linux/test-peeronq-linux-viewer-package.sh Linux archive traversal/manifest/permission/checksum validation gate
  windows/peeronq-dev.ps1   Scoped offline-preview process controller
  windows/PeerOnQ.LocalHttpsVerifier.cs  Scoped custom-root HTTPS health verifier for the Phase 3 controller
  windows/PeerOnQ.LocalDockerNetwork.ps1  Shared fail-closed creator/validator for the internal local observability bridge
  windows/peeronq-phase6-dev.ps1 Real Phase 6 Compose/migration/TLS/Admin controller; creates stable ignored operations/attestation secrets, health-gates the canonical HTTPS Grafana endpoint, repairs moved-workspace secret mounts, stops project-labeled leftovers, and manages backup/restore credentials
  windows/peeronq-workspace-dev.ps1 Unified preview + Phase 6 orchestrator that starts Docker Desktop with bounded readiness, manages an already-configured Phase 3 LAN stack, and opens every development panel after health succeeds
  windows/peeronq-phase3-local.ps1 Local TLS/signaling/coturn acceptance controller that rebuilds current signaling source before start, derives `*.sslip.io` LAN DNS names from its bound IPv4 address, validates development TLS with a scoped root, and refuses implicit certificate rotation
  windows/build-phase5-development.ps1 Fresh unsigned LAN or explicit public-pilot x64/ARM64 publish/MSI build with payload verification; website publication requires a compiled reachable WSS endpoint and matching scoped root
  windows/build-peeronq-public-pilot.ps1 Guarded public endpoint wrapper for the unsigned two-laptop Internet acceptance MSI
  windows/build-phase5-release.ps1 Protected signed release, update manifest, SBOM and checksums; official service inputs must pass the DNS-only production endpoint policy
  windows/assert-phase5-production-endpoints.ps1 Shared official-release HTTPS/WSS and public-style DNS endpoint gate
  windows/test-phase5-production-endpoints.ps1 Regression coverage for accepted production domains and rejected IP/development hosts
  windows/build-peeronq-server-run.ps1 Self-extracting complete Linux server/Portal bundle requiring an explicit signed-by-default embedded x64 MSI, checksum and optional detached GPG signature
  windows/test-peeronq-server-run-invariant.ps1 Regression check that a server bundle cannot be emitted without an explicit MSI
  windows/build-peeronq-website-patch.ps1 Signed UI-only website release with verified Downloads-page compatibility entry and no client-binary authority
  windows/test-peeronq-website-patch-invariant.ps1 Regression check that website releases cannot select or embed a Windows client
  windows/test-phase5-installer.ps1 MSI scope/architecture/features/persistence/extraction and optional publish-manifest validation
  windows/test-phase5-native-ui.ps1 Native page/action wiring, accessibility baseline, viewer sizing, consent, and open-source surface gate
  windows/test-phase5-installer-lifecycle.ps1 Explicit disposable-clean-VM x64 install/launch/repair/upgrade/downgrade/uninstall/data-preservation gate
  windows/test-phase5-local-release.ps1 GitHub-independent restore/build/test/frontend/installer/security/release-evidence gate
  windows/new-phase5-local-evidence.ps1 SPDX SBOM, dependency/license/vulnerability JSON, SLSA-shaped provenance, checklist, and checksums
  windows/normalize-windows-app-sdk-msi-language.ps1 Preserves WinUI payload while normalizing ICE03-incompatible MSI language metadata
  windows/measure-phase5-performance.ps1 Repeatable startup/process CPU/memory/GPU measurement
  windows/generate-peeronq-brand.ps1 Deterministically rebuilds PeerOnQ web SVGs, favicon, Windows ICO and WiX graphics
  security/scan-repository-secrets.ps1 Bounded tracked/untracked working-tree secret pattern scan
  quality/test-brand-purity.ps1 Manifest-backed active-product identity gate
  linux/peeronq-server-installer.sh Hardened production-pilot installer with first-run bootstrap, Spaceship DNS-01 wildcard migration/renewal (including safe creation of the installed hook parent during HTTP-01-to-DNS-01 upgrades), signal-safe standalone-ACME proxy handoff for legacy SAN expansion, transactionally rollback-safe in-place SAN/wildcard certificate import, atomic installed-renewal refresh, volume-preserving old-stack quiesce, bounded dependency reconciliation, managed secret/TLS permission repair, repeated storage preflight, protected failure diagnostics, same-bundle retry, transactional base-website activation, streaming embedded-client publication checks, public control-plane routing, and rollback
  linux/peeronq-spaceship-dns-hook.py Pinned Spaceship DNS-01 Certbot hook; exact TXT add/remove, pagination, authoritative propagation polling, root-only credentials, and stale-token reconciliation
  linux/verify-embedded-windows-client.sh Fail-closed embedded MSI metadata/artifact/hash validator used before bootstrap and after deployment
  linux/test-embedded-windows-client.sh Deterministic signed/unsigned and missing/corrupt embedded-client invariant tests
  linux/test-peeronq-website-platform-state.sh Regression gate for safe website-overlay deactivate/restore/commit and current Downloads verifier assumptions
  linux/bootstrap-peeronq-production.sh Root-owned per-service-group secrets, HTTP-01 or Spaceship DNS-01 wildcard TLS/Admin onboarding, canonical TCP 443 URLs and NAT-aware single-node bootstrap
  linux/test-peeronq-bootstrap-compose-contract.sh Regression gate requiring bootstrap to generate every production-required Compose variable, preserve ACME proxy/renewal safety, and safely migrate, explicitly disable, or preserve configured customer mail
  linux/renew-peeronq-tls.sh Certificate SAN expansion/renewal with scoped, failure-safe proxy handoff plus verified proxy/TURN restart

.agents/skills/      Repository-scoped Codex skills: production engineering and UI/UX design intelligence
.vscode/settings.json Workspace Fallow integration: loads the repository config and omits historical Git-churn candidates from the live editor health view
.fallowrc.json        Fallow analysis boundaries: excludes throwaway/vendored/generated noise
peeronq-start.bat     Windows: start a configured Phase 3 LAN stack, build/start Phase 6 plus the preview, then open the public site, desktop preview, Admin, Portal, Grafana and Prometheus UIs; retain startup errors on double-click
peeronq-stop.bat      Windows: stop scoped preview/Phase 6/configured Phase 3 processes while preserving their state and volumes
peeronq-restart.bat   Windows: restart the complete development workspace
peeronq-status.bat    Windows: report preview, Phase 6 endpoints and container health
.peeronq-run/         Gitignored PID file written by the scripts (never commit)
.peeronq-phase3/      Gitignored local CA, TLS keys, TURN secret, PID and logs (never commit)
.peeronq-phase6/      Gitignored Phase 6 development CA, TLS keys, operations secrets, backups and controller state (never commit)
```

Root dependency/release evidence is described by `DEPENDENCIES.md`, `THIRD_PARTY_NOTICES.md` and
the exact-artifact SPDX generator in `scripts/windows/new-phase5-local-evidence.ps1`.

The Linux server installer keeps bounded root-only failure logs under
`/var/log/peeronq/installer` and quarantines unreferenced failed payloads under
`/opt/peeronq/failed-releases`; it never removes named Compose volumes during recovery. Local Compose
file-backed secrets stay below a root-only directory and are repaired to the narrow runtime group and
mode required by each non-root consumer before deployment.

## Do Not Read Unless Needed

```text
node_modules/
dist/
build/
coverage/
logs/
backups/
runtime/
app-updates/
attached_assets/
.git/
.local/
lib/api-client-react/src/generated/
lib/api-zod/src/generated/
artifacts/*/src/components/ui/
artifacts/mockup-sandbox/
```

## Core Flows

### Flow 1: App shell and navigation

**Entry points:** `artifacts/peeronq/src/main.tsx`, `artifacts/peeronq/src/App.tsx` (providers),
`artifacts/peeronq/src/app/router/index.tsx` (all routes and redirects).

**Important files:**

- `src/layouts/PublicLayout.tsx` — responsive public navbar, real Portal/Sign in links, keyboard-accessible mobile menu, skip link and product/community footer
- `src/pages/LandingPage.tsx` + `src/pages/DownloadsPage.tsx` — product hero, access modes, portal entry, security, MIT source, downloads and FAQ; one locally device-matched, fail-closed package action
- `src/components/PublicMarketing.tsx` — shared `PublicPageHero` used by the active privacy and terms pages; retired standalone marketing pages and their unused helpers have been removed, while legacy URL redirects remain in the router
- `src/lib/accountPortal.ts` — validates the separate HTTPS customer-portal URL
- `src/layouts/DesktopPreviewLayout.tsx` — desktop sidebar, topbar, permanent preview banner and
  development-only Admin/health/observability toolbar
- `src/components/Sidebar.tsx` — desktop-preview nav item list
- `src/components/Topbar.tsx`

**Notes:** the public marketing site is one page at `/`, with a captioned static client illustration,
View Only / Full Control / File Transfer / Unattended Access terminology and evidence-bound security
copy. The hero contains the single device-matched `DownloadsPage` action at `#client-download`,
beside Open Portal. Header/mobile Download PeerOnQ anchors return to that selector; `#download`
explains platform/release status. Numbered access-mode rows and connection/recovery/diagnostics/update
sections state implemented capabilities and validation limits. The desktop sidebar says Offline UI preview.
Known unsigned development/pilot packages are labeled and unavailable platforms fail closed.
`/features`, `/security`, `/downloads`, `/about`, and `/help` retain their anchor redirects; `#strategy`
is a compatibility anchor in Open source. Privacy and Terms remain dedicated legal documents.
`.public-site` tokens scope the new Segoe/green/navy presentation away from the offline desktop preview
(`/desktop-preview…`). Former `/app…` preview routes stay retired. Public Portal/Sign in and desktop-preview
Open portal links navigate to the separate HTTPS `artifacts/peeronq-portal` deployment. Its customer
routes belong only in `peeronq-portal/src/App.tsx`; no customer API calls enter the offline prototype.

### Flow 2: Connect to a remote device (desktop preview)

**Entry points:** `/desktop-preview/dashboard`, `src/pages/DashboardPage.tsx`,
`src/components/ConnectionForm.tsx`. The retired `/desktop-preview/remote-access` URL redirects to
the dashboard so the desktop preview has one connection flow and one navigation item.

**Important files:**

- `src/lib/validation.ts` — `deviceIdSchema` (`XXX-XXX-XXX-XXX`, digits only)
- `src/components/PermissionDialog.tsx` — explains that no real connection happens
- `src/features/connections/useConnectionState.ts`

**Notes:** submitting the form must only open `PermissionDialog`. Never open a socket or fetch.

### Flow 3: Local data (devices / sessions / contacts / settings)

**Entry points:**

- `src/features/devices/deviceRepository.ts` + `useDevices.ts`
- `src/features/sessions/sessionRepository.ts`
- `src/features/address-book/contactRepository.ts`
- `src/features/settings/useSettings.ts`

**Important files:** `src/hooks/useLocalStorage.ts`, `src/types/index.ts`.

**Notes:** all persistence is `localStorage`. Records created in the browser carry
`isPrototypeRecord: true`. Demo rows are hardcoded inside `DashboardPage`/`SessionsPage` and
appear only when `VITE_ENABLE_DEMO_DATA=true`; they are never written to storage.
`src/compatibility/legacyBrandStorageMigration.ts` performs the one-way, idempotent pre-render brand-key migration while
preserving legacy keys for rollback and refusing malformed values.

### Flow 4: Theming

**Entry points:** `src/hooks/useTheme.ts`, `src/index.css`.

**Important files:** `src/components/ThemeSelector.tsx`.

**Notes:** theme persists to `localStorage` key `peeronq_theme`; values `system | light | dark`.
Colors are HSL triples without the `hsl()` wrapper, consumed through `@theme inline`.
Never hardcode a hex color in a component — add or reuse a token.

### Flow 5: API contract change

**Entry points:** `lib/api-spec/openapi.yaml`.

**Important files:** `artifacts/api-server/src/routes/` (implementation),
`lib/api-client-react/src/generated/` and `lib/api-zod/src/generated/` (regenerated output).

**Notes:** edit the spec, then run `pnpm --filter @workspace/api-spec run codegen`.
Never hand-edit files under `src/generated/`.

### Flow 6: .NET internet connection and secure resume

**Entry points:** `src/PeerOnQ.App/AppServices.cs`, `src/PeerOnQ.App.Linux/LinuxAppServices.cs`,
`src/PeerOnQ.App.Android/AndroidAppServices.cs`, `src/PeerOnQ.App.Apple/AppleAppServices.cs`,
`src/PeerOnQ.Application/Sessions/SessionCoordinator.cs`, and
`src/PeerOnQ.Signaling.Server/SignalingApp.cs`.

**Important files:**

- `src/PeerOnQ.Application/Collaboration/RemotePointerMapper.cs` - DPI/scroll-aware viewer-to-remote pointer geometry
- `src/PeerOnQ.Platform.Windows/Capture/WindowsGraphicsCaptureSource.cs` - explicit-display WGC capture and pre-conversion frame pacing
- `src/PeerOnQ.Application/Abstractions/ConnectionPolicy.cs` - deterministic connection health/adaptation and bounded transfer allocation
- `src/PeerOnQ.Application/Collaboration/PeerClockSync.cs` - bounded NTP-style clock codec and uncertainty estimator for optional frame-age telemetry
- `src/PeerOnQ.Media/Pipeline/VideoFrameTelemetryCodec.cs` - negotiated authenticated capture timestamp/sequence envelope around encoded VP8
- `src/PeerOnQ.Media/Pipeline/BulkDataFrameCodec.cs` - bounded ordered bulk fragmentation/reassembly before normal secure-record authentication
- `src/PeerOnQ.Application/Collaboration/RemoteInputFocusCoordinator.cs` - exclusive process-local input ownership across concurrent sessions
- `src/PeerOnQ.Application/Collaboration/SupportInvitationService.cs` - expiring/revocable hash-only support invitation lifecycle

- `src/PeerOnQ.Application/Abstractions/Connectivity.cs` — ICE, path, resume and input-safety contracts
- `src/PeerOnQ.Application/Collaboration/RemoteInputSession.cs` — ordered/rate-bounded remote input protocol
- `src/PeerOnQ.Application/Security/HybridDeviceIdentityService.cs` — OS-protected (DPAPI/Secret Service) ML-DSA-65 + Ed25519 identity bound to the signaling P-256 key
- `src/PeerOnQ.Application/Security/PostQuantumCryptography.cs` — managed ML-KEM-768/ML-DSA-65 provider and bounded key operations
- `src/PeerOnQ.Application/Security/SecureSessionProtocol.cs` — strict hybrid handshake framing and transcript contract
- `src/PeerOnQ.Application/Security/SessionTrafficProtector.cs` — per-direction/channel/transfer/epoch AES-256-GCM record keys
- `src/PeerOnQ.Platform.Windows/Input/WindowsInputController.cs` — standard-user Windows input boundary
- `src/PeerOnQ.Transport/Protocol/SignalingMessages.cs` — signaling protocol v3 handshake and machine-readable compatibility errors
- `src/PeerOnQ.Transport/Protocol/CapabilityNegotiation.cs` — bounded platform manifests, optional-feature and session capability negotiation
- `src/PeerOnQ.Application/Abstractions/RemoteSessionTransport.cs` - protocol-v1 native channel delivery, priority and lane contract
- `src/PeerOnQ.Application/Collaboration/NativeBulkNegotiationCodec.cs` - bounded PNQE-protected native certificate-pin/listener-port negotiation
- `src/PeerOnQ.Application/Collaboration/NativeBulkCapacityEstimator.cs` - receiver-confirmed native file goodput probing, stale-feedback expiry and media/input backoff
- `src/PeerOnQ.Transport/DataPlane/` - real TLS 1.3 QUIC reliable streams, ephemeral certificate pinning, bounded framing and active negotiated same-LAN file transport
- `src/PeerOnQ.Transport/DataPlane/ManagedQuicBulkTransportFactory.cs` - session-ephemeral certificate lifecycle and application-facing QUIC endpoint factory
- `tests/PeerOnQ.Transport.Tests/` - two-peer loopback QUIC authentication, channel isolation, ordering and size-bound tests
- `docs/competitive/NATIVE_QUIC_MIGRATION.md` - measured baseline, target data path, migration gates and explicit non-claims
- `src/PeerOnQ.Platform.Windows/WindowsClientCapabilityProfile.cs` — only abilities wired into the current native Windows client
- `src/PeerOnQ.Platform.Linux/LinuxClientCapabilityProfile.cs` — viewer/render/input-send/reconnect abilities only; no host or unattended claim
- `src/PeerOnQ.Platform.Linux/Security/SecretToolDeviceSecretStore.cs` — profile-bound Linux Secret Service adapter with stdin-only secret transfer
- `src/PeerOnQ.App.Linux/MainWindow.axaml.cs` — attended viewer lifecycle, decoded-frame rendering and explicitly enabled remote input
- `src/PeerOnQ.Platform.Android/AndroidClientCapabilityProfile.cs` — least-capability Android viewer/render/input-send/reconnect manifest
- `src/PeerOnQ.App.Android/AndroidKeyStoreDeviceSecretStore.cs` — private-preference ciphertext protected by a non-exportable Android Keystore AES-GCM key
- `src/PeerOnQ.App.Android/AndroidMediaCodecVideoSink.cs` — authenticated VP8 to Android MediaCodec/Surface path; desktop libvpx is not packaged
- `src/PeerOnQ.App.Android/MainActivity.cs` — attended viewer lifecycle, adaptive phone/tablet UI and explicitly enabled touch/keyboard forwarding
- `src/PeerOnQ.Platform.Apple/AppleClientCapabilityProfile.cs` — macOS/iOS/iPadOS least-capability viewer/render/input-send/reconnect manifests
- `src/PeerOnQ.App.Apple/AppleKeychainDeviceSecretStore.cs` — non-synchronizing data-protection Keychain storage for private identity material
- `src/PeerOnQ.App.Apple/AppleNativeLibraryBootstrap.cs` — static Apple libvpx resolver; Apple builds fail when the reviewed archive is absent
- `src/PeerOnQ.App.Apple/MainViewController.cs` — shared UIKit attended viewer lifecycle, bounded frame rendering and explicitly enabled pointer/text forwarding
- `test-vectors/signaling-v3/` — canonical cross-implementation UTF-8 signaling vectors
- `docs/CROSS_PLATFORM_CAPABILITIES.md` — per-platform truth, OS restrictions and native evidence gates
- `src/PeerOnQ.Transport/Protocol/QualityProfileWire.cs` — exact, legacy-compatible session quality wire mapping
- `src/PeerOnQ.Media/WebRtcMediaSession.cs` — candidate gathering, nominated-pair diagnostics and adaptation
- `src/PeerOnQ.Media/Codecs/Vp8ScreenEncoder.cs` — low-latency libvpx rate control, safe dynamic-size restart, and key-frame boundary
- `src/PeerOnQ.Media/Codecs/Vp8ScreenDecoder.cs` — stateful libvpx decoding with bounded parallel I420-to-BGR conversion for desktop viewers
- `src/PeerOnQ.Media/Pipeline/RtcpNetworkFeedback.cs` — coherent monotonic loss/jitter reports with ten-second expiry and connection/restart reset
- `src/PeerOnQ.Signaling.Server/Security/TurnCredentialService.cs` — authenticated, expiring coturn credentials
- `src/PeerOnQ.Signaling.Server/SignalingOptionsValidator.cs` — fail-closed production configuration boundary
- `src/PeerOnQ.Signaling.Server/Sessions/SessionRegistry.cs` — ownership and rotating resume-token store boundary
- `src/PeerOnQ.Signaling.Server/Sessions/RedisSessionStore.cs` — atomic cross-node live-session and one-use resume authority
- `src/PeerOnQ.Signaling.Server/Registry/RedisSignalingBackplane.cs` — expiring device ownership and cross-node WebSocket routing
- `src/PeerOnQ.Realtime.Deployment/docker-compose.production.yml` — hardened single-node production topology
- `src/PeerOnQ.Realtime.Deployment/docker-compose.local.yml` — loopback-only local TLS/coturn acceptance topology
- `src/PeerOnQ.Realtime.Deployment/README.md` — DNS, TLS, firewall, monitoring and rotation
- `docs/PROTOCOL_COMPLIANCE.md` — current IETF/library support and evidence limits
- `PHASE3.md` — implemented scope and honest physical-network test matrix

- `docs/phase-reports/PHASE_03_COMPLETION_REPORT.md` — Phase 3 `PASS_WITH_EXTERNAL_BLOCKERS` evidence and truth matrix

- `src/PeerOnQ.Transport/Protocol/CaptureResolutionWire.cs` - optional 720p/1080p/1440p/2160p non-upscaling preference mapping
- `src/PeerOnQ.Application/Sessions/SessionTimelineStore.cs` - bounded address-free real session event history
- `src/PeerOnQ.Infrastructure/Diagnostics/NetworkDoctorService.cs` - bounded DNS/API/signaling/UDP/RTT/protocol/clock diagnostics

**Notes:** clients request ICE configuration only for an authenticated, accepted session. The
nominated candidate pair is the only source for the displayed path. Signaling loss disables input,
uses Windows network-availability/address changes plus bounded client PONG detection to refresh the
socket, revalidates device identity, consumes a short-lived resume token, notifies the still-online
peer, and completes a fresh deterministic-offerer ICE negotiation before restoring the session.
Connection replacement is owner-aware: cleanup from a displaced socket cannot remove a newer
device owner or mark that owner's sessions disconnected. A client closed explicitly with
`replaced_by_new_connection` stays disconnected instead of reconnecting and displacing the newer
instance; network loss and server-restart close reasons continue through bounded reauthentication.
Before a short-lived connection token expires, the client repeats the signed challenge and fetches
fresh cloud attestation on the same WebSocket. Successful rotation does not move the signaling
owner, disturb the active media channel, or trigger session resume; expiry remains a bounded
reconnect fallback instead of a fatal dashboard error.
One pending registration refresh runs alongside application heartbeats, so a slow cloud response
cannot suppress Ping/Pong while the current token is valid. The original token expiry bounds the
refresh; rejection, expiry, disconnect and socket replacement cancel/observe it without extending
credentials or bypassing validation.
The in-memory `ISessionStore` is single-node; add a shared atomic store plus distributed presence and
message routing before scaling signaling horizontally.
Production accepts forwarded HTTPS metadata only from one configured proxy IP and reads the TURN
secret from a mounted file; unsafe/default production options fail before the server listens.
Signaling protocol v3 is exchanged in `hello`/`registered`; a missing, v2, or newer peer receives
bounded `unsupported_version` details before challenge/session work. A valid v3 client declares its
native capabilities, verifies the server echo, negotiates implemented optional features, and cannot
create a session whose requester/target lacks the required directional abilities. Every session also
requires `security.hybrid-pq-v1` on both endpoints. The existing managed Bouncy Castle provider
implements ML-KEM-768 and ML-DSA-65 on every supported Windows build, so capability advertisement
does not depend on optional Windows Insider CNG algorithms.
The viewer's selected profile crosses session setup as `automatic`, `office`, `balanced`,
`performance`, `quality`, or `low-bandwidth`; an omitted legacy field is Automatic. The optional
resolution preference is strictly mapped as Automatic, 720p, 1080p, 1440p, 2160p or legacy Native,
and the scaler never upscales beyond the source display. The sharer snapshots both validated values
for capture and encoding. Additive telemetry relays source/requested/encoded dimensions and actual
encoder/hardware state without carrying candidate addresses.
Saved Devices reuses the DPAPI-protected address book: Add Device applies the same twelve-digit
grouping used by the dashboard, and the Dashboard exposes aliases in a keyboard-accessible picker
that fills the Remote ID before the existing View Only, Full Control or File Transfer request. An
explicit default-off unattended switch reveals a password/recovery field; the credential is used
only to create the existing one-time challenge proof and is cleared from the UI after the request.
An empty credential can authenticate only through a separately approved fingerprint/scope trust.
The central deterministic connection policy consumes measured loss, RTT, jitter, encoder queue
drops and a conservative available-bitrate estimate. Its decision feeds the existing adaptive
quality ladder, additive session telemetry, dynamic 64 KiB-2 MiB bulk windows, and a file token
bucket that reserves adaptive video plus input/security capacity on both direct and relay paths.
With live media but no usable estimate, finite 50/100/150 Mbps mode ceilings prevent unlimited
bursts; file-only sessions remain uncapped. A trustworthy measurement may allocate the larger
post-media/input remainder up to 1 Gbps. File traffic slows before media; input, clipboard, and file
records use separate serialized send and receive lanes in every mode. Negotiated native LAN file
traffic additionally learns from authenticated receiver-confirmed disk delivery, never from queued
sender bytes: the probe grows by at most 25% per confirmed window, expires after one second without
feedback, and cannot override input pressure or Fair/Poor/loss/RTT/jitter backoff.
Whole-file integrity/malware completion work remains on the file lane and cannot hold input; video
continues on its independent authenticated RTP path. Each decoded frame carries an exact local
monotonic timestamp through latest-frame UI coalescing, and only successful presentation contributes
to bounded capture-to-encode and decode-to-render p50/p95/p99 diagnostics. New peers additionally
negotiate `x-peeronq-video-frame-timing:1`, exchange encrypted NTP-style probes on an isolated
Telemetry record context, and authenticate capture time/sequence inside the encrypted video frame.
Successful UI presentation contributes capture-to-present p50/p95/p99 only with bounded clock
uncertainty. Optional timing failure disables only that measurement; AEAD/replay failure remains
fail-closed. The existing authenticated session-quality message also returns viewer decode/render
FPS, decode-to-render p95, capture-to-present p95, input-to-injection p95 and their clock
uncertainty to the sharer. This makes input pressure visible to a sharer sending bulk data in the
opposite direction: bulk backoff is immediate, while sustained pressure also lowers video on the
next adaptive evaluation. One central connection policy owns the 35 ms input, 75 ms (1080p) /
100 ms (4K) frame-age, 50 ms render and 75% render/decode thresholds; feedback expires after five
seconds. Both roles publish on one bounded 250 ms loop (4 msg/s per active session). A per-client
20 msg/s feedback budget preserves five messages/second beneath the default signaling limit for
ICE/session control and automatically stretches per-session cadence above five concurrent sessions.
A transient signaling send failure is isolated to that tick, so registration recovery resumes
feedback without restarting media or spawning retry loops. While the sharer is actively encoding,
two consecutive fresh feedback windows with zero viewer decode and render FPS are treated as
`viewer_video_stall`. The central policy lowers production and coalesces a fresh key-frame request
once per adaptive window until presentation resumes; one empty window, stale feedback, old peers and
a truly idle sender do not trigger it. Full Control peers negotiate
`x-peeronq-input-data-lane:1`: protected Input records move to a separate ICE/DTLS/SCTP peer with a
64 KiB sender bound, while missing/unready/closed negotiation retains the permission-gated primary
fallback without changing media state. They independently negotiate `x-peeronq-input-ack:1`; at
most one input
per 100 ms is timestamped, and the host acknowledges it only after successful authorized OS
injection. Sanitized input-to-injection p50/p95/p99 and clock uncertainty feed the live file pacer:
reliable p95 above 35 ms forces the conservative bulk tier and preserves at least 1 Mbps interactive
reserve, while five seconds without a fresh sample clears the pressure. Optional ack failure never
disables control, and old peers receive no added input field or acknowledgement. Viewer pointer
motion is coalesced and paced at no more than 240 commands per second,
keeping high-polling mice below the unchanged 2,000-message receiver safety boundary without
adding perceptible control latency. An optional ML
provider interface exists only as an extension point and has no implementation or network provider.
The sharer creates and wires the media session before starting Windows capture, and its bounded
latest-frame queue retains the initial captured frame until both WebRTC and the authenticated media
protector are ready. This prevents a static desktop's one initial WGC frame from being lost during
negotiation.
The WinUI LAN-development client starts with High Quality and an explicit 4K/UHD target (30 fps,
36 Mbps ceiling); production starts with Automatic. Capture never upscales the source display.
Every selected
profile supplies a maximum to the measured latency-QoS ladder: it holds the selected target while
healthy and drops frame rate before resolution when loss, jitter, RTT, or encoder queue pressure
shows congestion. The 1080p/60 fps/12 Mbps Performance profile remains an explicit opt-in; native
and quality-oriented profiles retain box averaging, while native-size capture bypasses the generic
floating-point scaler. Resolution changes have a ten-second dwell in addition to the existing
four-healthy-window recovery rule. Full Control starts with a 256 KiB bounded file-transfer
window so interactive input is not delayed by bulk traffic; the user may change it in-session.
Diagnostics include a bounded real-event timeline; the on-demand Network Doctor reports
session-scoped STUN/TURN/loss checks as not configured instead of creating a hidden session or
minting credentials.
Adaptive input/frame-age pressure allows half a fresh path-minimum ICE RTT (at most 75 ms) for
estimated one-way transit; rising RTT cannot increase the allowance beyond that baseline.
Local encoder/render pressure, packet loss, high RTT and jitter still reduce production. Raw input
latency still throttles bulk first. RTCP reports expire after ten seconds and reset on connection
changes/ICE restart. Missing reports hold video recovery and keep bulk conservative until fresh
feedback arrives; they are never interpreted as newly recovered bandwidth.
Desktop VP8 decoding retains ordered prediction state and identical BGR pixels, while full-screen
color conversion uses a bounded half-CPU worker budget (maximum eight). Platform decoder sinks
remain separate; local loopback timings do not establish physical-device or WAN latency.
Desktop VP8 bitrate is configured after libvpx defaults are loaded. Adaptive bitrate/FPS or capture
size changes recreate the encoder and begin with a key frame; encoded prediction frames are never
discarded after encode. A connected viewer sends RTCP PLI at most every 500 ms after a decode
failure; the sharer coalesces requests onto its encode loop and makes the next transport frame a key
frame. A fresh media connection and ICE restart also force a recovery key frame.
File-relay completion records one bounded heartbeat grace timestamp before releasing receive
backpressure, so a PONG already queued behind completion work is read before timeout evaluation.

### Flow 7: .NET file transfer, clipboard, address book, and unattended access

**Entry points:** `src/PeerOnQ.Application/Collaboration/`,
`src/PeerOnQ.Media/WebRtcMediaSession.cs`, `src/PeerOnQ.App/MainWindow.xaml.cs`, and
`src/PeerOnQ.Application/Sessions/SessionCoordinator.cs`.

**Important files:**

- `CollaborationProtocol.cs` — versioned/bounded control and binary chunk frames
- `MediaCollaborationTransport.cs` — mandatory hybrid handshake, encrypted records and negotiated opaque file relay with receiver backpressure
- `FileTransferService.cs` / `SafeTransferPath.cs` — high-throughput bounded queue, integrity, partial, collision and path policy
- `ClipboardSyncService.cs` — explicit plain-text synchronization and loop prevention
- `CollaborationProfileStore.cs` — DPAPI-backed profile boundary
- `TrustedDeviceService.cs` / `UnattendedAccessService.cs` — fingerprint/scope trust and challenge auth
- `AddressBookService.cs` — encrypted contacts/groups plus signaling-backed presence
- `src/PeerOnQ.Platform.Windows/Security/WindowsAmsiMalwareScanner.cs` — pre-rename malware scan
- `src/PeerOnQ.Infrastructure/Persistence/PeerOnQDatabase.cs` — schema v3 security audit and HMAC-chain fields
- `docs/SECURE_TRANSPORT.md` — threat model, algorithms, state machine, key schedule and direct-P2P file policy
- `PHASE4.md` — collaboration behavior, cleanup limitations and verified matrix

**Notes:** every session uses an ordered primary DTLS/SCTP data channel for a mandatory authenticated
ML-KEM-768 + X25519 / ML-DSA-65 + Ed25519 handshake. Full Control peers negotiate a second physical
WebRTC peer/SCTP association restricted to authenticated Input records; file-capable peers negotiate
a third for direct bulk records. Negotiated same-LAN peers prefer the separately pinned QUIC file
path, while older peers retain the primary-channel compatibility paths. Native receivers coalesce
authenticated cumulative disk-delivery receipts so
the sender can learn usable file goodput without trusting enqueue/write completion. Bulk records are split into bounded, adaptive fragments and
reassembled before their unchanged secure-record routing and AEAD checks. Encoded video and all
collaboration payloads are AES-256-GCM protected with separate HKDF-derived direction/channel keys
only after `SECURE`.
Unauthenticated video records are never decoded. Because RTP may lose or truncate one frame, an
isolated rejected frame is dropped and requests a recovery key frame; sustained consecutive
verification failures still terminate the session fail-closed. Ordered collaboration-record
verification failures remain immediately terminal.
When both authenticated endpoints negotiate `file.relay.v1`, file records use the existing TLS
signaling connection as an opaque 1,040 KiB-bounded binary relay; session ownership and immutable
FileTransfer permission are checked before every route, while the server never receives plaintext
names, contents, or record keys. The hybrid record layer accepts only FileTransfer records from
that route. Per-channel secure send serialization preserves record order without letting a blocked
relay file write hold mouse/keyboard traffic. On Direct LAN, a transfer offer waits up to four
seconds for negotiated native QUIC even when file relay is available; relay remains the fallback.
Adaptive pacing follows the transport actually selected, so native QUIC retains measured high-speed
allocation while an available but unused relay cannot suppress it. Native and direct-WebRTC chunks
remain adaptively paced; the selected relay uses its own TLS-socket backpressure and does not inherit
the media token bucket. Video, handshake, clipboard and interactive control remain on their existing
WebRTC paths. A non-negotiated peer retains the
direct LAN/internet file-transfer fallback and fails closed on TURN/unknown paths. Defaults impose
no byte quota: 256 KiB streaming chunks, persistent sequential destination streams, controlled
parallel workers, and 10 Hz UI progress snapshots consume the dynamically measured network
remainder without rendering every received chunk.
Incoming files require a local offer decision,
remain `.partial` until size/hash/AMSI checks pass, and are never executed. The address/trust/
unattended profile is a DPAPI CurrentUser secret, not plaintext SQLite. Online presence comes only
from the authenticated signaling registry. Full control uses input protocol v2: the viewer requests
a new focus generation, the host acknowledges it, and every input/release/revocation frame carries
the signaling session ID, reconnect generation, focus generation, and monotonic sequence. The host
can monotonically revoke `ControlInput` without ending the view; reconnect restores only the reduced
effective scope. Negotiated sampled acknowledgements bind the exact command sequence and are sent
only after successful injection; measurement failure remains separate from authorization and input
delivery. Full control sends validated normalized input commands; the viewer derives those
coordinates from the transformed image rectangle, including DPI scale and
actual-size scroll offsets, and the sharer maps them only into the selected display. The viewer's
display-scaling menu offers aspect-preserving Fit, aspect-preserving Fill with centered crop,
window Stretch, and DPI-correct Actual size; pointer mapping follows the visible mode. Decoded
BGR24 frames retain the latest-frame-only queue and expand into the WinUI BGRA32 back buffer with
a bounded packed-pixel path so render work does not accumulate stale frames on the UI thread.
While a remote viewer is active, the main PeerOnQ window is hidden and restored when the last
viewer closes, leaving one visible remote-session window instead of duplicate app/viewer windows.
Windows App SDK keyed activation keeps installed and Portable Support processes separately
single-instance: a second launch redirects its activation/support link to the existing viewer,
sharing indicator, or main window instead of registering the same device twice. A server
`replaced_by_new_connection` close is terminal for every heartbeat/network reconnect source until
an explicit connect, preventing displaced clients from starting a reconnect loop.
Full Control places the real multi-file picker beside Fullscreen and binds a short 184 px,
left-to-right 0-100 progress bar in the same top toolbar to authoritative `FileTransferService`
snapshots; completion is shown at exactly 100 percent for two seconds and then automatically hides
without changing the isolated file transport.
Approved Full Control starts input automatically and re-requests a fresh host-acknowledged focus
generation when the viewer regains focus, changes monitor, or returns the pointer to the remote
image; there is no manual resume-control action. Its immersive fullscreen hides viewer chrome, keeps F11
local for exit, and reveals a transient top-edge Exit fullscreen control on pointer hover. Full-control
capture excludes the delayed remote cursor image because the viewer already has an immediate local
pointer. The viewer uses a focusable neutral input control with a full-size transparent Border as
the pointer hit/capture surface, so WinUI button class handling cannot consume raw mouse presses.
The sharer's compact neutral local safety indicator is centered at the top of the physical display,
shows the active capability and duration, expands only on local click for revoke/end controls, and
retains a compact direct End button. It is excluded only from the captured frame with the Windows
display-affinity API; it remains locally clickable so the physical owner always has a direct stop
path.
Concurrent technician viewers are keyed by `SessionId`; frames, displays, collaboration callbacks
and transfer UI are routed to the matching session. Activating another viewer selects that session,
and a process-local focus coordinator revokes the previous input route before the shared Windows
input sink accepts the new owner.
Support invitations use a 256-bit random token whose SHA-256 hash and optional PBKDF2 password
verifier are kept inside the existing DPAPI secret boundary. Expiry, revoke/use count, exact
permissions and optional requester/fingerprint restrictions are checked before the standard native
permission prompt; a use is consumed only after explicit Accept. `peeronq://support` loads the link
but never auto-connects.
Replay, cross-session/pre-reconnect/stale-focus, invalid/rate-excessive commands, monitor switching,
reconnect, permission revocation, or session end disables injection and releases every held
key/button. The viewer retains the visible End session action; the host registers Ctrl+Alt+Shift+F12
as an emergency session stop when available. The tray retains session revoke/end
actions and can also open Settings or exit through the graceful application shutdown path.
Closing the viewer or gracefully shutting down the app sends a terminal
session message before signaling is disposed, so the peer also removes its viewer/indicator. This
notification is bounded and in-flight ICE/capture startup is cancelled before local release. UIPI
and secure-desktop boundaries are intentionally not bypassed.

### Flow 8: .NET release, installer, update, audit, and privacy

**Entry points:** `installer/Package.wxs`, `scripts/windows/build-phase5-release.ps1`,
`src/PeerOnQ.Infrastructure/Updates/`, and `src/PeerOnQ.Infrastructure/Persistence/SqliteSecurityAuditLog.cs`.

**Important files:**

- `Directory.Build.props` — canonical `PeerOnQWindowsClientVersion` shared by every distributable Windows client surface; unpublished Linux, Android and Apple previews derive their display versions from it
- `scripts/windows/PeerOnQ.ClientVersion.ps1` — strict reader/assertion used by client, server-bundle and website tooling
- `scripts/windows/test-peeronq-client-version-invariant.ps1` — cross-surface canonical-version regression gate
- `scripts/windows/build-phase11-portable-support.ps1` — no-install canonical-version development package, marker and SHA-256 manifests
- `scripts/linux/test-peeronq-linux-viewer-version.sh` — Linux metadata and builder mismatch gate

- `src/PeerOnQ.App/PeerOnQ.App.csproj` — canonical Windows client metadata and fail-closed update trust inputs
- `src/PeerOnQ.Infrastructure/Updates/UpdateManifestVerifier.cs` — bounded ECDSA manifest policy
- `src/PeerOnQ.Infrastructure/Updates/UpdateService.cs` — HTTPS streaming/hash/Authenticode/publisher/atomic staging
- `src/PeerOnQ.Infrastructure/Diagnostics/PrivacyAndCrashReporting.cs` — default-off sanitized local crash consent
- `.github/workflows/quality.yml`, `codeql.yml`, `release.yml` — quality/security/protected-release gates; Quality builds once and runs .NET test projects serially without rebuilding, with native QUIC acceptance isolated from other xUnit collections
- `docs/` — operator and user-facing production documentation
- `PHASE5.md` — actual evidence, 25-scenario checklist, and unresolved external gates

**Notes:** development packages are unsigned and updates are disabled without compiled trust
metadata. Development/public-pilot clients can opt into a complete public-only update bootstrap and
then upgrade only to signed manifests and Authenticode MSIs matching the compiled publisher;
Production ignores runtime trust overrides. Official release creation requires PowerShell 7.4,
Authenticode PFX/password, publisher
fingerprint, ECDSA manifest key/key ID, HTTPS timestamp and update origins. It signs/verifies exact
artifacts, generates and validates SPDX 2.2 SBOMs, and never embeds private keys. The client verifies
signed size/hash and Windows trust/publisher before atomic staging, then asks visibly before starting
system Windows Installer. Security audit is local, HMAC chained, sanitized, exportable, retained and
clearable only through explicit APIs/UI.
The MSI registers `peeronq://` for support links. Portable Support uses the same desktop publish,
recognizes an adjacent marker, disables unattended access, stores its profile under a PID-bound
temporary directory and cleans normal/abandoned profiles without installing a service. The current
portable builder is explicitly unsigned development tooling; official distribution still requires
the existing Authenticode/release trust pipeline.

The native WinUI shell implements Dashboard, Devices, Sessions, File Transfer, Address Book, Security,
and Settings as one responsive product surface while keeping controls attached to the real services.
Settings does not expose or mutate service endpoints; signaling remains internally resolved from
environment/release metadata, and official release creation rejects IP, localhost, private-development,
`sslip.io`, `nip.io`, and single-label hosts across the complete HTTPS/WSS service set.
Settings/About offers an optional **Open Account Portal** action in the default system browser.
`CloudEndpointConfiguration.ResolveAccountPortalUri` reads the existing assembly metadata system's
`PeerOnQAccountPortalUrl` MSBuild value, defaulting to `https://portal.peeronq.com`. URLs must use HTTPS
and exclude credentials, queries and fragments. HTTP is accepted only for explicit loopback URLs in
Debug builds compiled with `PeerOnQDeploymentEnvironment=Development`; no runtime URL override is read.
This browser action is independent of CloudPlatform device/service enrollment and never introduces
customer authentication into accountless LAN startup. The portal owns customer credentials/cookies.
Native version labels read the assembly version; the WiX ProductVersion now also defaults to the
canonical `PeerOnQWindowsClientVersion` and rejects mismatches. Offline web preview device versions
are shown as unreported, while download versions continue to use verified release metadata.
Dashboard exclusively owns device identity and session initiation; signaling connects and retries
automatically, and the primary capture target is selected safely without exposing technical setup cards.
The Dashboard presents three explicit mode choices: view-only, full-control, and standalone file
transfer. Requests are attended by default and show the remote Accept dialog. A separate default-off
switch exposes unattended password/recovery entry and explains that an empty credential works only
for a separately approved trusted device. Security setup explicitly selects the maximum unattended
mode, password/trusted-device authentication paths, and shows persisted status; plaintext passwords
are never read back. The one-time password challenge also returns the remote allowed permission scope;
if the viewer selected a broader mode, the Dashboard asks before retrying with the narrower mode rather
than silently declining or weakening remote policy. The visible viewer toggle can pause or resume approved input.
File transfer starts no screen capture and enables file/folder selection only after its encrypted data
channel opens. The underlying clipboard and unattended services remain default-off and policy-bound;
they do not claim pre-logon, Secure Desktop, UAC bypass, or Windows service behavior.
Devices combines locally saved address-book entries with separately verified trust records; its DPAPI
profile serializer reads legacy object-shaped IDs and writes grouped numeric IDs. Session History reads
the local tamper-evident audit log and exports only its sanitized view.
`build-phase5-development.ps1` performs a fresh .NET and Windows App SDK self-contained publish,
force-rebuilds the selected WiX x64/ARM64 packages, administratively extracts each MSI, and rejects
it unless the required native runtime files are present and the installed application-file manifest
matches the publish directory by relative path and SHA-256. When it publishes the complete unsigned
local-test pair to the ignored website downloads directory, it now restarts `peeronq-dev.ps1` and
requires HTTP 200 for both new package URLs plus exact selected-version/classification state before
succeeding. All Windows client builders, Portable Support, official releases and server embedding
read one canonical `PeerOnQWindowsClientVersion` and reject an explicit mismatch. The local preview
accepts only a checksum-verified canonical x64/ARM64 pair with one explicit `unsigned-development`
or `unsigned-public-pilot` classification; stale-only content stops startup instead of silently
selecting an older version. Website publication is rejected
unless a reachable `wss://.../ws` LAN endpoint and its scoped development root are explicitly
supplied. An explicit signaling URL normally produces an isolated LAN kit, disables cloud enrollment, and optionally bundles the
public development root for hostname-preserving WSS pinning without changing Windows certificate
stores. A LAN kit can replace the local website pair only through the explicit
`-PublishLanWebsiteDownloads` opt-in, which requires both that DNS WSS endpoint and its scoped
development root. The guarded public-pilot wrapper requires the complete HTTPS/WSS production
endpoint set, emits an ignored test kit, and embeds no private signing material; publishing its
matching pair to the local preview requires the separate explicit `-PublishLocalWebsiteDownloads`
opt-in. When the
complete public manifest key/key ID and publisher fingerprint are supplied, its updater accepts only
a future manifest/MSI that passes the normal signed production release checks; omitting all three
preserves the older update-disabled connectivity-test kit.

### Flow 9: Phase 6 cloud platform, presence, downloads, and administration

**Entry points:** `src/PeerOnQ.Cloud.Api/Program.cs`,
`src/PeerOnQ.Presence.Server/Program.cs`, `src/PeerOnQ.Admin.Api/Program.cs`,
`src/PeerOnQ.Downloads.Service/Program.cs`, and `artifacts/peeronq-admin/src/App.tsx`.

**Important files:**

- `src/PeerOnQ.Shared.Contracts/V1/` - versioned client/server DTOs and canonical signed challenges
- `src/PeerOnQ.Shared.Contracts/Security/SignalingAttestationTokenV1.cs` - bounded versioned cloud-to-signaling alias/key attestation codec
- `src/PeerOnQ.Cloud.Infrastructure/Security/EcdsaSignalingAttestationIssuer.cs` - proof-bound short-lived token issuer; private key stays in Cloud
- `src/PeerOnQ.Signaling.Server/Security/CloudSignalingAttestationValidator.cs` - current/previous public-key validation with exact alias/SPKI binding
- `src/PeerOnQ.Infrastructure/Cloud/CloudPlatformClient.cs` - memory-only attestation refresh/provider for every signaling registration; transient enrollment/presence outages use bounded retry bursts and cooldowns for lifetime recovery; registration challenges validate canonical issued/expiry consistency and a bounded lifetime without depending on the client wall clock, while malformed identity/security responses remain stopped
- `src/PeerOnQ.Cloud.Infrastructure/Persistence/CloudDbContext.cs` - Phase 6 PostgreSQL model
- `src/PeerOnQ.Cloud.Infrastructure/Persistence/Migrations/` - forward-only schema and security triggers
- `src/PeerOnQ.Cloud.Infrastructure/Workers/RetentionWorker.cs` - bounded legal-hold-aware retention
- `src/PeerOnQ.Cloud.Infrastructure/Workers/StaleSessionReconciliationWorker.cs` - separate negotiation and connected-inactivity reconciliation thresholds
- `src/PeerOnQ.Cloud.Infrastructure/Redis/RedisDistributedOperationLeaseManager.cs` - renewable compare-and-renew/delete singleton-worker ownership
- `src/PeerOnQ.Cloud.Api/CloudApiEndpoints.cs` - authenticated device/install/session/update/diagnostics API
- `src/PeerOnQ.Cloud.Api/DiagnosticArchiveValidator.cs` - fail-closed ZIP/container/schema/privacy validation before diagnostic persistence
- `src/PeerOnQ.Presence.Server/PresenceHub.cs` - installation lease, heartbeat and session state
- `src/PeerOnQ.Admin.Api/AdminEndpoints.cs` - MFA/RBAC/CSRF admin queries and privileged actions
- `src/PeerOnQ.Admin.Api/AdminInfrastructureMetrics.cs` - bounded allowlisted Prometheus queries, including live Phase 3 online/open-session overlay
- `src/PeerOnQ.Admin.Api/AdminInfrastructureSnapshotWorker.cs` - leased one-minute persistence of fixed Phase 6 and live Phase 3 service health
- `src/PeerOnQ.Admin.Api/WebsitePublication.cs` - signed static-site extraction, Downloads UI compatibility validation and atomic activation
- `src/PeerOnQ.Admin.Api/PlatformUpgrade.cs` - bounded full-platform status validation plus audited, fixed-schema host-agent request spooling; it never executes host commands
- `artifacts/peeronq-admin/src/components/StatePanel.tsx` - stable state-panel facade; implementation lives in `StatePanelView.tsx`
- `artifacts/peeronq-admin/src/hooks/loadLifecycle.ts` - shared online-load controller startup used by Admin data pages
- `artifacts/peeronq-admin/src/pages/ResourcePage.tsx` - shared, responsibility-split resource tables, filters, metrics, and audited action dialogs
- `artifacts/peeronq-admin/src/pages/UpgradePage.tsx` - durable host-status, signed bundle staging and exact-version MFA Owner apply/rollback controls
- `src/PeerOnQ.Downloads.Service/DownloadEndpoints.cs` - signed release streaming and event tracking
- `artifacts/peeronq/server/localDownloadTelemetry.ts` - server-side, non-blocking start/completion telemetry for exact local development/public-pilot MSI GETs; start failures are handled during streaming so optional telemetry cannot interrupt downloads
- `src/PeerOnQ.Observability/ServiceDefaults.cs` - common health, low-cardinality metrics, logs and OTel
- `src/PeerOnQ.Observability/ServiceDrain.cs` - application-stop traffic admission and retryable drain response
- `src/PeerOnQ.Infrastructure.Deployment/` - Compose, proxy, monitoring, backup/restore and runbooks
- `src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md` - operator-facing server/client change and release-gate summary included in full server patch bundles; server patches must identify embedded client changes and signing classification
- `src/PeerOnQ.Infrastructure.Deployment/nginx/default.conf` and `nginx/peeronq.conf.template` - public customer portal with same-origin API, operator-only CIDR hosts, unknown HTTP/TLS host rejection and public metrics denial; `scripts/validate-nginx.sh` exercises these boundaries with locally trusted fixture TLS
- `src/PeerOnQ.Infrastructure.Deployment/scripts/website-platform-state.sh` - constrained website-overlay deactivate/restore/commit helper used transactionally by full-platform upgrades; stored website releases are retained
- `src/PeerOnQ.Infrastructure.Deployment/docker-compose.local-phase3-observability.yml` - development-only Prometheus bridge/target override; staging and production inherit empty local targets
- `src/PeerOnQ.Realtime.Deployment/docker-compose.local.yml` - Phase 3 metrics and async redacted logs bridged to Phase 6 over an internal Docker network
- `src/PeerOnQ.Infrastructure.Deployment/docker-compose.ha.yml` - opt-in local management warm standbys plus two active Redis-backed Signaling replicas; no PostgreSQL/Redis/TURN HA claim
- `scripts/windows/peeronq-phase6-dev.ps1` - trusted `*.dev.localhost`, migration/grant, full-stack and Admin entry controller
- `scripts/linux/peeronq-platform-upgrade-agent.sh` plus its systemd path/service units - root-owned checksum/GPG/fingerprint verification, preflight, apply and rollback boundary
- `scripts/linux/peeronq-server-installer.sh` and `scripts/windows/build-peeronq-server-run.ps1` - transactional full-stack and base-website activation installer plus immutable `.run`/checksum/detached-signature publication
- `scripts/windows/test-phase9-single-region-ha.ps1` - bounded local load, replica-loss, dependency-restart and evidence harness
- `src/PeerOnQ.Infrastructure.Deployment/Dockerfile.migrations` - private one-shot EF migration bundle
- `src/PeerOnQ.Infrastructure.Deployment/acceptance/` - bounded proof-first Cloud/Presence/Signaling positive and negative live client
- `docs/DEPLOYMENT.md`, `docs/OPERATIONS.md`, `docs/RUNBOOKS.md` - canonical Phase 6 operator guides
- `docs/HA_AND_DISASTER_RECOVERY.md`, `docs/PHASE9_GAME_DAY_RUNBOOK.md` - Phase 9 ownership truth and operator decision points
- `docs/phase-reports/PHASE_09_COMPLETION_REPORT.md` - failed Phase 9 gate and exact prerequisites before Phase 10
- `docs/phase-reports/PHASE_10_COMPLETION_REPORT.md` - single-node Phase 10 implementation evidence and remaining physical/HA gates
- `docs/phase-reports/PHASE_11_COMPLETION_REPORT.md` - support, portable, multi-session and preserved hybrid-security evidence
- `docs/PERFORMANCE_REPORT.md` - dated idle/transport evidence, 2026-09-27 input pipeline investigation and explicit unmeasured physical-session gates
- `tests/PeerOnQ.Media.Tests/InputLatencyProbe.cs` - test-only monotonic input/secure-record/delivery/ack boundary timing; fake sink, not Windows injection
- `docs/WORLD_CLASS_VALIDATION_REPORT.md` - Phase 11 local truth matrix and external release blockers
- `PHASE6.md` - architecture, actual test evidence, limitations and Phase 7 integration points

**Notes:** a stable InstallationId and device SPKI request a one-use challenge before any durable
claim exists. Successful proof atomically creates Device+Installation, assigns the 12-digit routing
alias, and is followed by authenticated installation confirmation. Cloud also issues a short-lived asymmetric attestation binding the
server-assigned numeric alias, cloud DeviceId, InstallationId and exact device SPKI fingerprint.
Signaling requires that attestation plus a fresh device challenge proof in Staging/Production and
does not consult node-local TOFU pins in attested mode. The Cloud private key is mounted only into
Cloud; signaling accepts current and previous public keys for bounded overlap rotation. Explicit
TOFU fallback is limited to Development/Testing. Device access tokens are short-lived and never substitute for accepted-session TURN
authorization. Presence is installation-scoped and Redis-backed; PostgreSQL remains authoritative
for durable state. Admin refresh cookies are HttpOnly/Secure/SameSite Strict. Outside the explicitly
opted-in Development Compose stack, privileged operations require MFA-derived RBAC and CSRF; enabling
the audited `AllowMfaBypassForDevelopment` password-only bypass in any other environment fails startup. Every mutation
emits immutable audit evidence. Downloads
resolve only active releases with a signed manifest digest. Origin bytes fill a quota-controlled
dedicated cache volume through a bounded buffer; size/SHA-256/strong-ETag verification and fsync
complete before atomic digest-key publication. Full and range responses read only that verified file,
and successful ranges record `Partial`, not a completed download. Cache fill is single-flight per
digest; concurrency and stream duration are bounded with no anonymous queue. The managed local Vite
preview also reports real GET lifecycle events for its exact versioned MSI paths to the Downloads
service without adding a browser-side network call. Diagnostics require explicit client
consent and short-lived upload authorization. Before object storage sees any byte, Cloud verifies the
server-computed digest and parses the bounded ZIP container, exact manifest/log allowlist, JSON/UTF-8
structure, compression limits, safe paths and server-side sensitive-data patterns. Official Release clients use build-time HTTPS/WSS
endpoint manifests; arbitrary production endpoint overrides fail closed.
The long-lived edge proxy re-resolves Docker service addresses through the embedded DNS resolver,
and Compose recreates the network-namespace-sharing signaling metrics sidecar whenever signaling is
replaced. Server bundles fail closed unless the embedded Windows MSI, release classification and
SHA-256 metadata validate before bootstrap and again through the running web/proxy publication path.
The embedded client link carries its exact version as a cache-busting query, and the edge marks the
canonical MSI plus version/checksum metadata `no-store`; activation checks the public version, MSI
hash and cache policy so an older same-name browser/proxy artifact cannot survive an upgrade.
Cloud registration challenges must carry unique canonical `issued_at`/`expires_at` timestamps whose
duration is positive and no more than the v1 two-minute maximum; the response expiry must match the
signed payload. Freshness and one-use consumption remain enforced authoritatively by the Cloud store,
so a valid server challenge is not rejected solely because a Windows client wall clock differs.
Website patches may replace the public `/downloads` UI only when their Admin-verified compatibility
entry passes; they never own the customer account portal.
Full-platform upgrades are a separate fail-closed production path. The unprivileged Admin service can
write only one fixed-schema request inbox and read bounded host status; it has no shell, Docker socket,
installer log, release archive, keyring or `/opt/peeronq` access. A root-owned systemd agent independently
rechecks the SHA-256, detached OpenPGP signature, exact pinned signer fingerprint, embedded stable version,
current-version precondition and retained archive before invoking the existing fixed-argument installer.
Apply covers the server stack, forward database migrations and the bundle's checksum-verified client/web
publication payload. Already-installed desktop clients continue to consume the separate signed AppRelease
self-update channel. The first updater-enabled production release requires one manually verified install to
place the root-owned public keyring and host agent; later releases can be staged and applied from Admin.

### Flow 10: Phase 7 customer accounts, organizations, and portal

**Entry points:** `artifacts/peeronq-portal/src/App.tsx`,
`src/PeerOnQ.Cloud.Api/CustomerPortalEndpoints.cs`, and
`src/PeerOnQ.Cloud.Api/CustomerOrganizationEndpoints.cs`.

**Important files:**

- `CustomerPortalAuthentication.cs` — separate customer bearer/cookie scheme, DB-backed session validation, CSRF
- `CustomerAccountService.cs` — registration, verification, password/reset, MFA/recovery, rotating sessions, privacy requests
- `CustomerOrganizationService.cs` — tenant-filtered membership, invitations, teams, RBAC, policy, device claim and audit
- `CustomerIdentityEntities.cs` — customer and organization invariants; no internal Admin role reuse
- `20260817060948_AddCustomerIdentityOrganizationsAndPolicy.cs` — forward-only schema and append-only customer audit trigger
- `artifacts/peeronq-portal/src/` — real same-origin API portal with explicit loading/error/empty states; stable `api.ts`, `shell.tsx`, and `components.tsx` facades re-export their focused implementations
- `artifacts/peeronq-portal/src/workspacePages.tsx` — authenticated overview, organization devices/remote-session history, downloads and support; resource responses are scoped to the current organization and aborted on scope changes
- `artifacts/peeronq-portal/src/theme.tsx` — shared auth/customer theme, system preference fallback and guarded non-secret preference storage
- `artifacts/peeronq-portal/src/portalShell.tsx` — grouped account/organization navigation, organization route gates and form remount boundaries; modal mobile navigation with deferred focus restoration
- `artifacts/peeronq-portal/src/uiStates.tsx` — async feedback and native confirmation dialog for session/trust/invitation revocation and deletion requests
- `artifacts/peeronq-portal/src/brand.tsx`, `public/brand/`, `src/styles.css` — shared canonical Q-mark, public/source destinations and standalone portal tokens/navigation
- `scripts/windows/test-phase7-customer-portal.ps1` — real HTTPS multi-organization and multi-role acceptance

**Notes:** internal Admin and customer identities use different schemes, claims, cookies, roles, routes,
and UI hosts. Customer access is tenant-filtered in every query/command; the browser cannot grant a
role or policy. Managed devices carry signed organization-policy claims into signaling, while
unmanaged/accountless LAN clients continue to use the v1 attestation path. Customer Data Protection
keys and development mail live in dedicated persistent volumes. Production accepts the bounded
`Disabled` or `Smtp` provider and rejects the file mail sink at startup; disabled mail requires
closed registration and no email-verification dependency.

The portal host is publicly reachable; its account and organization API still requires customer
authentication/authorization. Nginx network allowlists remain on Admin, Grafana and Prometheus only.
Signed-in users land on Overview, with independently loaded real account-session/trust counts,
identity verification/MFA and organization resources. Profile uses `/profile` with `/account` retained
as a compatibility alias. `/sessions` is labeled Sign-in sessions; `/trusted-devices` is Trusted
sign-in devices; `/remote-sessions` shows up to 500 host-side remote-session records. Policy and
organization forms retain server semantics and reset on tenant switches. A failed logout is shown
explicitly and does not imply that the cookie session has ended. Device installation proof and customer sign-in are
distinct identities linked through the existing organization device-claim API; the desktop client
has no self-service account sign-in/device-linking UI. The portal states this limitation explicitly.

Website patches may replace the public `/downloads` UI only when their Admin-verified compatibility
entry exactly matches the signed `index.html`. Old overlays fall back to the base page. Exact MSI,
SHA-256, version and release-classification routes always proxy to the immutable server-bundled web
image; `.msi` is not an allowed website-archive extension.

## Critical Files

| File | Purpose | Risk |
| --- | --- | --- |
| `Directory.Build.props` | Canonical distributable Windows version, derived Linux/Android/Apple preview versions, mobile bundle codes and shared .NET build policy | high |
| `artifacts/peeronq/src/app/router/index.tsx` | Route table and surface/layout boundary | high |
| `artifacts/peeronq/src/App.tsx` | Frontend provider composition | high |
| `artifacts/peeronq/src/index.css` | Design tokens; a bad edit breaks every page | high |
| `artifacts/peeronq/src/types/index.ts` | Domain types shared by all features | high |
| `artifacts/peeronq/src/layouts/DesktopPreviewLayout.tsx` | Desktop preview shell and warning boundary | high |
| `.fallowrc.json` | Static-analysis scope and intentional dependency suppressions | medium |
| `.gitleaksignore` | Fingerprint-specific reviewed historical false positives; new findings remain blocking | high |
| `lib/api-spec/openapi.yaml` | Source of truth for generated client + schemas | high |
| `lib/db/src/index.ts` | Throws at import if `DATABASE_URL` is missing | medium |
| `src/PeerOnQ.Application/Sessions/SessionCoordinator.cs` | Session permission, ICE, reconnect and cleanup invariants | high |
| `src/PeerOnQ.Application/Security/HybridDeviceIdentityService.cs` | Hybrid long-term identity and signaling-key binding | critical |
| `src/PeerOnQ.Application/Security/SecureSessionProtocol.cs` | Authenticated handshake/version/transcript contract | critical |
| `src/PeerOnQ.Application/Security/SessionTrafficProtector.cs` | AEAD key/nonce/replay/rekey lifecycle | critical |
| `src/PeerOnQ.Application/Collaboration/MediaCollaborationTransport.cs` | Secure-state gate and negotiated opaque file-relay policy | critical |
| `src/PeerOnQ.Application/Abstractions/RemoteSessionTransport.cs` | Versioned native data-plane channel/delivery/priority contract | high |
| `src/PeerOnQ.Transport/DataPlane/` | QUIC TLS/pinning/framing and reliable-channel ownership | critical |
| `src/PeerOnQ.Application/Collaboration/FileTransferService.cs` | File integrity, partial lifecycle and transfer authorization | high |
| `src/PeerOnQ.Application/Collaboration/UnattendedAccessService.cs` | Password/trusted-device authentication, scope and lockout | high |
| `src/PeerOnQ.Application/Collaboration/SafeTransferPath.cs` | Destination containment, reserved names and reparse policy | high |
| `src/PeerOnQ.Platform.Windows/Security/WindowsAmsiMalwareScanner.cs` | Windows pre-rename malware decision | high |
| `src/PeerOnQ.Signaling.Server/Sessions/SessionRegistry.cs` | Connection ownership and resume-token security | high |
| `src/PeerOnQ.Signaling.Server/SignalingOptionsValidator.cs` | Production fail-closed security and connectivity configuration | high |
| `src/PeerOnQ.Signaling.Server/Security/CloudSignalingAttestationValidator.cs` | Stateless alias/key trust boundary for horizontally scaled signaling | critical |
| `src/PeerOnQ.Cloud.Infrastructure/Security/EcdsaSignalingAttestationIssuer.cs` | Cloud-only attestation private-key boundary | critical |
| `src/PeerOnQ.Turn.Configuration/turnserver.conf` | Relay ports, quotas and abuse controls | high |
| `src/PeerOnQ.Realtime.Deployment/docker-compose.production.yml` | Public exposure, secret mounts and container hardening | high |
| `scripts/windows/peeronq-phase3-local.ps1` | Scoped local certificate, process and relay acceptance controller | high |
| `scripts/windows/PeerOnQ.LocalDockerNetwork.ps1` | Fail-closed local-only internal Docker network boundary shared by Phase 3 and Phase 6 controllers | high |
| `src/PeerOnQ.Realtime.Deployment/README.md` | Production network and secret-rotation contract | high |
| `artifacts/*/.replit-artifact/artifact.toml` | Deployment / service definition | medium |
| `pnpm-workspace.yaml` | Catalog versions + `minimumReleaseAge` security setting | medium |
| `src/PeerOnQ.Cloud.Infrastructure/Persistence/CloudDbContext.cs` | Authoritative cloud schema and query filters | high |
| `src/PeerOnQ.Cloud.Infrastructure/Persistence/Migrations/` | Forward-only schema, append-only triggers and retention functions | critical |
| `src/PeerOnQ.Cloud.Infrastructure/Workers/RetentionWorker.cs` | Legal-hold-aware bounded retention orchestration | high |
| `src/PeerOnQ.Cloud.Api/DiagnosticArchiveValidator.cs` | Diagnostic object-storage content and decompression security boundary | critical |
| `src/PeerOnQ.Admin.Api/AdminAuthService.cs` | MFA, refresh rotation, CSRF and privileged-session boundary | critical |
| `src/PeerOnQ.Admin.Api/WebsitePublication.cs` | Dedicated P-256 website-patch verification, bounded ZIP extraction and atomic activation/rollback | critical |
| `src/PeerOnQ.Admin.Api/PlatformUpgrade.cs` | Host-status trust validation, bounded upload spooling, MFA policy handoff and audit-before-ready invariant | critical |
| `scripts/linux/peeronq-platform-upgrade-agent.sh` | Root-only exact-fingerprint verification and fixed-argument full-platform preflight/apply/rollback | critical |
| `src/PeerOnQ.Website.ReleaseTool/Program.cs` | Cross-platform offline static-site manifest hashing, trust-root matching and P-256 signing | critical |
| `src/PeerOnQ.Infrastructure.Deployment/.env.example` | Complete secret-free Phase 6 deployment variable contract | high |
| `src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml` | Staging topology and hardening contract | critical |
| `src/PeerOnQ.Infrastructure.Deployment/Dockerfile.migrations` | Non-root private-network forward migration bundle | critical |

## Common Tasks

### Add or fix a frontend page

Start here:

- ROUTES_MAP.md
- `artifacts/peeronq/src/pages/<Page>.tsx`
- `artifacts/peeronq/src/app/router/index.tsx` only if the route itself changes

Do not inspect:

- `src/components/ui/` (shadcn primitives) unless the primitive itself is broken
- backend or `lib/` packages

### Change styling or design tokens

Start here:

- `artifacts/peeronq/DESIGN_SYSTEM.md`
- `artifacts/peeronq/src/index.css`

Never:

- introduce raw hex colors in components
- rename a token without updating DESIGN_SYSTEM.md

### Add or fix a legacy prototype API endpoint

Start here:

- ROUTES_MAP.md
- `lib/api-spec/openapi.yaml`
- `artifacts/api-server/src/routes/`

Then run `pnpm --filter @workspace/api-spec run codegen`.

### Add or fix a Phase 6 cloud endpoint

Start with `ROUTES_MAP.md`, the matching host endpoint file, the V1 contract under
`src/PeerOnQ.Shared.Contracts/V1`, and its application service. Frontends never access PostgreSQL
directly. Preserve bounded request limits, typed errors, authentication, rate limits, idempotency,
and immutable audit for privileged actions.

### Fix the legacy Drizzle skeleton

Start here:

- `lib/db/src/schema/` (one file per table)
- `lib/db/drizzle.config.ts`

Never delete data, drop a table, or run a destructive migration without approval.

### Change the Phase 6 cloud schema

Start with `CloudDbContext.cs`, the owning domain entity/repository, and existing migrations. Add a
forward-only EF Core migration and verify it against a real PostgreSQL container. Never bypass the
append-only audit triggers or legal-hold policy.

## Environment Variables

| Variable | Purpose | Default | Risk |
| --- | --- | --- | --- |
| `VITE_PEERONQ_API_BASE_URL` | Frontend API base URL; empty ⇒ `apiClient` returns `NotConfigured` | empty | low |
| `VITE_ENABLE_DEMO_DATA` | Shows inline demo sessions on desktop-preview dashboard/sessions routes instead of empty states | `false` | low |
| `VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE` | Exposes local x64/ARM64 MSI links only after the dev controller verifies both files against `SHA256SUMS.txt` | `false` | medium |
| `VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION` | Exact version used to derive the verified local MSI links; required whenever local Windows downloads are enabled | empty | medium |
| `VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER` | Exact verified local package classification (`unsigned-development` or `unsigned-public-pilot`); required with local Windows downloads | empty | medium |
| `PEERONQ_DOWNLOAD_TELEMETRY_BASE_URL` / `PEERONQ_DOWNLOAD_TELEMETRY_CA_FILE` | Server-only local-preview Downloads API origin and scoped trust chain used to record actual static MSI GET lifecycle | controller-provided / Phase 6 chain | high |
| `VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE` | Website-patch-only neutral link to the canonical server-bundled x64 MSI; carries no version or signing claim | `false` | high |
| `VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL` | Public website tracked-download HTTPS origin; invalid/non-HTTPS values fail closed | empty | medium |
| `VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL` / `VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION` / `VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT` | Explicit embedded x64 link/version and mandatory signed-vs-controlled-pilot classification; safe local MSI path and exact boolean only | empty | high |
| `VITE_PEERONQ_MACOS_DOWNLOAD_URL` / `VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL` / `VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL` / `VITE_PEERONQ_ANDROID_DOWNLOAD_URL` / `VITE_PEERONQ_IOS_DOWNLOAD_URL` | HTTPS-only published install destinations selected for the detected non-Windows platform/architecture; each remains empty until that platform's signing, physical-device and publication gates pass | empty | high |
| `VITE_PEERONQ_ADMIN_PANEL_URL` / `VITE_PEERONQ_CLOUD_HEALTH_URL` | Desktop-preview-only Phase 6 toolbar links to the Admin console and Cloud readiness | controller-provided | low |
| `VITE_PEERONQ_GRAFANA_URL` / `VITE_PEERONQ_PROMETHEUS_URL` | Desktop-preview-only observability links; insecure URLs are accepted only on loopback | controller-provided | low |
| `VITE_PEERONQ_ADMIN_API_BASE_URL` | Admin SPA API origin; production requires HTTPS and never stores tokens in localStorage | same origin | high |
| `VITE_PEERONQ_ACCOUNT_PORTAL_URL` | Public/preview links to the separate customer portal; absolute HTTPS URL without embedded credentials | `https://portal.peeronq.com` | high |
| `PEERONQ_HTTPS_BIND_ADDRESS` / `PEERONQ_HTTPS_PORT` | Host address and port published by the TLS proxy; development is loopback-only while production bootstrap binds canonical TCP 443 | `127.0.0.1` / `8443` | high |
| `PEERONQ_API_BASE_URL` / `PEERONQ_PRESENCE_URL` / `PEERONQ_DOWNLOADS_BASE_URL` / `PEERONQ_DIAGNOSTICS_BASE_URL` / `PEERONQ_UPDATES_BASE_URL` | Development-only desktop cloud endpoint overrides; official Release uses compiled metadata | unset | high |
| `PEERONQ_DEPLOYMENT_ENVIRONMENT` / `PEERONQ_REGION` | Desktop endpoint environment and deployment region | unset | high |
| `PEERONQ_REGION` / `PEERONQ_WEB_HOST` / `PEERONQ_WEB_WWW_HOST` / `PEERONQ_API_HOST` / `PEERONQ_PORTAL_HOST` / `PEERONQ_ADMIN_HOST` / `PEERONQ_GRAFANA_HOST` / `PEERONQ_PROMETHEUS_HOST` / `PEERONQ_DOWNLOAD_HOST` / `PEERONQ_UPDATE_HOST` / `PEERONQ_PRESENCE_HOST` | Phase 6 deployment region and exact proxy host allowlists | development values only | high |
| `PEERONQ_ADMIN_ALLOWED_CIDR` | Nginx direct-source allowlist for Admin, Grafana, and read-only Prometheus only; customer Portal is public. Production bootstrap derives the server LAN `/24` unless explicitly supplied | open in development / required outside development | critical |
| `PEERONQ_RELEASE_PUBLICATION_ENABLED` / `PEERONQ_RELEASE_SIGNING_KEY_ID` / `PEERONQ_RELEASE_SIGNING_PUBLIC_KEY_SPKI_BASE64` | Admin signed-release publication gate and offline signer public trust | disabled / none | critical |
| `PEERONQ_RELEASE_ARTIFACT_HOST` / `PEERONQ_RELEASE_MAXIMUM_PACKAGE_BYTES` | Exact HTTPS update origin allowlist and bounded Admin MSI upload size | none / 96 MiB | high |
| `PEERONQ_PLATFORM_UPGRADE_ENABLED` / `PEERONQ_PLATFORM_UPGRADE_MAXIMUM_BUNDLE_BYTES` | Fail-closed host-updater gate and bounded whole-platform upload size; request/status binds use fixed installer-owned host paths | disabled / 256 MiB | critical |
| `PEERONQ_WEBSITE_PUBLICATION_ENABLED` / `PEERONQ_WEBSITE_SIGNING_KEY_ID` / `PEERONQ_WEBSITE_SIGNING_PUBLIC_KEY_SPKI_BASE64` / `PEERONQ_WEBSITE_MAXIMUM_ARCHIVE_BYTES` | Separate signed static-site trust root, publication gate and compressed upload bound | disabled / none / 96 MiB | critical |
| `PEERONQ_POSTGRES_PASSWORD` / `PEERONQ_REDIS_PASSWORD` | PostgreSQL and Redis credentials injected at deployment | none | critical |
| `PEERONQ_CLOUD_WORKER_LEASE_DURATION` / `CloudWorkers__WorkerLeaseDuration` | Renewable Redis ownership duration for singleton Cloud workers; configuration is bounded from 30 seconds to 10 minutes | 2 minutes | high |
| `PEERONQ_REDIS_SIGNALING_PASSWORD` / `Signaling__Cluster__RedisConnectionString` | Dedicated Redis ACL credential and connection for shared Signaling ownership/session/routing state | none | critical |
| `PEERONQ_SIGNALING_INSTANCE_ID` / `Signaling__Cluster__InstanceId` | Deployment-unique Signaling routing instance identifier | `signaling-ha-1` in Compose | high |
| `Signaling__Cluster__DeviceLeaseDuration` / `Signaling__Cluster__DeviceLeaseRefreshInterval` | Hard-crash ownership expiry and renewal cadence; validator requires a safe bounded relationship | 45 seconds / 15 seconds | high |
| `PEERONQ_PUBLIC_DEVICE_ID_HMAC_KEY_BASE64` / `PEERONQ_ADMIN_TOKEN_SIGNING_KEY` / `PEERONQ_ADMIN_REFRESH_HASH_KEY` / `PEERONQ_DOWNLOAD_COMPLETION_TOKEN_KEY` | Server-only rotatable cloud/auth/download key material | none | critical |
| `PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY` / `PEERONQ_CUSTOMER_REGISTRATION_MODE` / `PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION` | Customer JWT key and self-hosted registration/verification policy | none / `Closed` / false | critical |
| `PEERONQ_CUSTOMER_MAIL_PROVIDER` | Production customer-mail policy; `Disabled` is bounded to closed registration without verification and `Smtp` enables delivery | `Disabled` | critical |
| `PEERONQ_CUSTOMER_SMTP_HOST` / `PEERONQ_CUSTOMER_SMTP_PORT` / `PEERONQ_CUSTOMER_SMTP_USERNAME` / `PEERONQ_CUSTOMER_SMTP_PASSWORD` | Optional self-hosted verification/reset/invitation SMTP transport; file sink is development/testing only | empty / 587 | critical |
| `PEERONQ_SIGNALING_ATTESTATION_ISSUER` / `PEERONQ_SIGNALING_ATTESTATION_AUDIENCE` | Exact HTTPS issuer and signaling audience shared by Cloud and signaling | none / `peeronq-signaling` | critical |
| `PEERONQ_SIGNALING_ATTESTATION_PRIVATE_KEY_FILE` / `PeerOnQ__SignalingAttestation__PrivateKeyFile` | Cloud-only mounted P-256 PKCS#8 private-key file | none | critical |
| `PEERONQ_SIGNALING_ATTESTATION_PUBLIC_KEY_FILE` / `Signaling__Attestation__PublicKeyFiles__*` | Signaling-only current/previous P-256 SPKI public-key files for overlap rotation | none | critical |
| `PEERONQ_DOWNLOAD_CACHE_MAX_BYTES` / `PEERONQ_DOWNLOAD_MAX_CONCURRENT_STREAMS` / `PEERONQ_DOWNLOAD_STREAM_DEADLINE_MINUTES` | Downloads verified-volume logical quota, no-queue concurrency and absolute deadline | 4 GiB / 16 / 30 min | high |
| `PEERONQ_ADMIN_BOOTSTRAP_ENABLED` / `PEERONQ_ADMIN_BOOTSTRAP_*` | Explicit one-time admin bootstrap gate plus email/password/TOTP/recovery-code inputs; the `.run` installer scrubs them after first healthy creation | disabled / none | critical |
| `PeerOnQ__AdminInfrastructureMetrics__SnapshotIntervalSeconds` / `PeerOnQ__AdminInfrastructureMetrics__SnapshotLeaseDuration` / `PeerOnQ__AdminInfrastructureMetrics__Region` | Bounded persisted Admin service-health sampling interval, renewable singleton lease and region label | 60 / 2 minutes / `local` | medium |
| `PEERONQ_DIAGNOSTICS_HTTP_ENDPOINT` / `PEERONQ_DIAGNOSTICS_HTTP_ALLOWED_HOST` / `PEERONQ_DIAGNOSTICS_HTTP_BEARER_TOKEN` | Optional allowlisted diagnostics object-storage boundary | none | critical |
| `PEERONQ_DIAGNOSTICS_ALLOW_SINGLE_NODE_FILESYSTEM` | Explicit production-pilot opt-in for the private persistent diagnostic volume; multi-node production must use managed object storage | `false` | high |
| `PEERONQ_TLS_CERT_DIR` / `PEERONQ_TLS_PRIVATE_KEY_FILE` / `PEERONQ_ALERTMANAGER_*` / `PEERONQ_GRAFANA_ADMIN_PASSWORD_FILE` | TLS, alert ingestion and monitoring secret-file inputs | none | critical |
| `PEERONQ_BACKUP_DIR` / `PEERONQ_BACKUP_RETENTION_DAYS` / `PEERONQ_RESTORE_TEST_FILE` / `PEERONQ_POSTGRES_PGPASS_FILE` | Encrypted backup and restore-test controls | none / 14 days | high |
| `DataRetention__PolicyVersion` / `DataRetention__AuditEventsEnabled` / `DataRetention__AuditEventsLegalHold` / `DataRetention__AlertEvents` / `DataRetention__AlertEventsEnabled` / `DataRetention__AlertEventsLegalHold` / `DataRetention__DiagnosticsLegalHold` | Governed retention policy, hard floors and legal holds | audit off; alerts on | critical |
| `PEERONQ_AUDIT_RETENTION_ENABLED` / `PEERONQ_AUDIT_LEGAL_HOLD` / `PEERONQ_ALERT_RETENTION_ENABLED` / `PEERONQ_ALERT_LEGAL_HOLD` / `PEERONQ_DIAGNOSTICS_LEGAL_HOLD` | Deployment mappings for governed retention enablement and legal holds | audit false; alert true; holds false | critical |
| `DATABASE_URL` | Postgres connection string; `lib/db` throws without it | none | high |
| `PORT` | Required by `artifacts/api-server` at runtime and by `artifacts/peeronq` dev/preview serving; the PeerOnQ production build does not require a server port | `8080` api / `23586` web | medium |
| `BASE_PATH` | Vite `base` for the peeronq build; `vite.config.ts` throws without it | `/` | medium |
| `NODE_ENV` | Standard environment flag | — | low |
| `PEERONQ_SIGNALING_URL` | Desktop signaling WebSocket URL; use `wss://` outside loopback | `ws://127.0.0.1:5080/ws` | high |
| `PEERONQ_UPDATE_MANIFEST_URL` / `PEERONQ_UPDATE_PUBLIC_KEY_SPKI` / `PEERONQ_UPDATE_KEY_ID` / `PEERONQ_PUBLISHER_CERTIFICATE_SHA256` / `PEERONQ_UPDATE_CHANNEL` | Development-only complete public trust bootstrap for the native client updater; Production accepts compiled release metadata only | unset / `beta` channel | high |
| `PEERONQ_DEVELOPMENT_ROOT_CERTIFICATE` | Linux viewer only: bounded private development CA file for local WSS validation; never disables TLS checks | unset | high |
| `PeerOnQSignalingUrl` / `PeerOnQDevelopmentRootCertificate` (Android MSBuild) | APK-compiled WSS `/ws` endpoint and optional embedded public development CA; the Android builder validates both and never disables hostname/TLS checks | unset | high |
| `PeerOnQSignalingUrl` / `PeerOnQDevelopmentRootCertificate` / `PeerOnQAppleLibVpxPath` (Apple MSBuild) | iPhone/Mac Catalyst compiled WSS `/ws`, optional scoped development CA and required reviewed static VP8 decoder archive; Apple build tooling rejects missing/mismatched inputs | unset | high |
| `PEERONQ_APPLE_CODESIGN_KEY` / `PEERONQ_APPLE_PROVISIONING_PROFILE` / `PEERONQ_APPLE_DEVICE_UDID` / `PEERONQ_APPLE_DEVELOPMENT_BUNDLE_ID` | Signing identity, optional development/required distribution profile selector, registered physical-iPhone identifier and optional Personal Team bundle-ID override consumed only by the macOS Apple builder; private keys remain in Apple Keychain, development builds verify the embedded profile/device/bundle and distribution builds retain the canonical ID and fail closed without their profile | unset | critical |
| `PEERONQ_SIGNALING_PORT` | Development Compose loopback port for signaling | `5080` | low |
| `PEERONQ_LOCAL_SIGNAL_HOST` | Local TLS signaling hostname used by the acceptance Compose stack | `signal.127.0.0.1.sslip.io` | low |
| `PEERONQ_LOCAL_TURN_HOST` | Local TURN TLS hostname used by the acceptance Compose stack | `turn.127.0.0.1.sslip.io` | low |
| `PEERONQ_LOCAL_TLS_PORT` | Loopback HTTPS port for local Phase 3 acceptance | `5443` | low |
| `PEERONQ_LOCAL_TURN_PORT` / `PEERONQ_LOCAL_TURN_TLS_PORT` | Loopback-published Phase 3 TURN listener ports | `3478` / `5349` | low |
| `PEERONQ_LOCAL_TURN_RELAY_MIN_PORT` / `PEERONQ_LOCAL_TURN_RELAY_MAX_PORT` | Loopback-published Phase 3 coturn relay allocation range | `49160` / `49200` | low |
| `PEERONQ_LOCAL_CERT_DIR` | Ignored local development certificate directory | generated by controller | high |
| `PEERONQ_LIVE_TURN_TEST` / `PEERONQ_LIVE_TURN_URL` / `PEERONQ_LIVE_TURN_USERNAME` / `PEERONQ_LIVE_TURN_CREDENTIAL` | Ephemeral opt-in settings injected by the local controller for the live relay-only media test | unset | high |
| `PEERONQ_LIVE_TURN_INTERRUPT_TEST` / `PEERONQ_LIVE_FILE_TRANSFER_TEST` / `PEERONQ_LIVE_FILE_TRANSFER_BYTES` | Ephemeral local-acceptance controls for a relay outage and bounded large-file run | unset | medium |
| `PEERONQ_LIVE_DOCKER_TEST` / `PEERONQ_LIVE_SIGNALING_URL` / `PEERONQ_LIVE_DOCKER_EXE` / `PEERONQ_LIVE_COMPOSE_ENV_FILE` / `PEERONQ_LIVE_COMPOSE_FILE` | Ephemeral controller settings for the opt-in signaling-container restart test | unset | medium |
| `PEERONQ_DATA_DIR` | Desktop identity, database and log root override | local app data | high |
| `PEERONQ_TURN_SHARED_SECRET` / `Signaling__Turn__SharedSecret` | coturn REST shared secret; server-side only, minimum 32 characters | none | critical |
| `PEERONQ_TURN_SHARED_SECRET_FILE` / `Signaling__Turn__SharedSecretFile` | Preferred production file containing the coturn REST shared secret | none | critical |
| `PEERONQ_SIGNAL_SERVER_ID` / `Signaling__ServerId` | Deployment-unique signaling node identifier | machine name | medium |
| `PEERONQ_SIGNAL_PUBLIC_HOST` / `AllowedHosts` | Public signaling DNS name and ASP.NET Host allowlist | none in production | high |
| `PEERONQ_SIGNAL_CERT_DIR` | Host directory containing signaling proxy certificate and key | none | critical |
| `Signaling__TrustedProxyIp` | Exact reverse-proxy IP allowed to supply forwarded scheme/address | empty | critical |
| `PEERONQ_TURN_REALM` | TURN authentication realm/DNS name | required by Compose | high |
| `PEERONQ_TURN_PUBLIC_HOST` | Client-reachable STUN/TURN hostname | required by Compose | high |
| `PEERONQ_TURN_EXTERNAL_IP` | coturn external mapping; the single-node Docker deployment behind NAT uses the public IPv4 address only | empty | high |
| `PEERONQ_TURN_MIN_PORT` / `PEERONQ_TURN_MAX_PORT` | Runtime coturn relay allocation range; local Compose forwards the controller-selected range | `49160` / `49200` | high |
| `PEERONQ_TURN_SERVER_ID` | Deployment-specific relay identifier shown in diagnostics | none in production | medium |
| `PEERONQ_TURN_REGION` | Non-sensitive relay region shown in diagnostics | none in production | low |
| `PEERONQ_TURN_CERT_DIR` | Host directory for TURN TLS/DTLS certificate and key; mandatory in production | empty | high |
| `PEERONQ_TURN_CERT_FILE` / `PEERONQ_TURN_KEY_FILE` | Coturn container paths for the mounted certificate/key | `/certs/fullchain.pem`, `/certs/privkey.pem` | critical |
| `PEERONQ_TURN_REQUIRE_TLS` | Makes missing TURN TLS/DTLS certificate files fatal | `false`; production sets `true` | high |
| `PEERONQ_TURN_ALLOW_PRIVATE_PEERS` | Development-only override for private relay peers | `false`; development Compose sets `true` | high |
| `PEERONQ_TURN_RELAY_ONLY` | Forces relay policy for diagnostics/acceptance testing | `false` | medium |

The exhaustive secret-free Phase 6 deployment contract is
`src/PeerOnQ.Infrastructure.Deployment/.env.example`; staging and production values must come from
the deployment secret manager, never a committed `.env` file.

## Known Risks

- Full repo scans waste tokens — `artifacts/*/src/components/ui/` alone is ~60 files.
- `src/components/ui/` and `src/generated/` are vendored/generated; edits there get overwritten.
- `artifacts/mockup-sandbox` duplicates the shadcn UI folder — do not confuse it with `peeronq`.
- The prototype must stay network-free; a stray `fetch` breaks `noNetworkRequest.test.ts`.
- Renaming tokens in `index.css` silently breaks Tailwind classes across every page.
- npm/yarn installs are blocked; using them corrupts the workspace.
- Phase 6 local Docker acceptance does not prove public DNS/TLS, multi-region failover, physical
  two-device internet behavior, production mail delivery, or managed backup restoration.
- Admin Data Protection keys protect both browser sessions and encrypted MFA secrets; production
  Redis must be durable/backed up or replaced with a durable external Data Protection key store.

## Testing

```bash
pnpm run typecheck                            # whole workspace
pnpm run lint                                 # eslint in every package that has it
pnpm run test                                 # tests in every package that has them
pnpm --filter @workspace/peeronq run typecheck
pnpm --filter @workspace/peeronq run lint     # config: artifacts/peeronq/eslint.config.js
pnpm --filter @workspace/peeronq run test     # vitest
pnpm run build                                # typecheck + build everything
pnpm run format                               # prettier (not applied repo-wide yet)
dotnet build PeerOnQ.slnx --no-restore        # .NET product
dotnet test PeerOnQ.slnx --no-restore         # all .NET xUnit projects
docker compose -f src/PeerOnQ.Realtime.Deployment/docker-compose.yml config
docker compose --env-file /secure/peeronq.env -f src/PeerOnQ.Realtime.Deployment/docker-compose.production.yml config
docker compose -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml config --quiet
docker compose -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml config --quiet
dotnet test tests/PeerOnQ.Cloud.Domain.Tests/PeerOnQ.Cloud.Domain.Tests.csproj
dotnet test tests/PeerOnQ.Cloud.Application.Tests/PeerOnQ.Cloud.Application.Tests.csproj
dotnet test tests/PeerOnQ.Cloud.Infrastructure.Tests/PeerOnQ.Cloud.Infrastructure.Tests.csproj
```

Run only the checks relevant to the change. Do not run expensive builds unless needed.

Infrastructure test fixtures clear only the SQLite pool for their own temporary database connection
string. Do not restore global `ClearAllPools`: parallel or retried tests can otherwise close unrelated
fixture connections and create non-deterministic cleanup failures.

**Windows note:** `pnpm-workspace.yaml` excludes every non-linux-x64 native binary, so `vitest`
and `vite build` fail locally on Windows/macOS until the matching `rollup`, `lightningcss`, and
`@tailwindcss/oxide` platform packages are present in `node_modules`. Typecheck and lint work
everywhere.
