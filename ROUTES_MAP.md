# Routes Map

**Purpose:** find any route, page, repository, or storage key without scanning the repo.
Update this file whenever a route, page, endpoint, or storage key is added or renamed.

**Last updated:** 2026-08-23

## Backend API Routes

Base path is `/api` (mounted by the Replit artifact config, `artifacts/api-server/.replit-artifact/artifact.toml`).

| Method | Route | Handler File | Purpose | Notes |
| --- | --- | --- | --- | --- |
| GET | `/api/healthz` | `artifacts/api-server/src/routes/health.ts` | Liveness/startup probe | Response validated by `HealthCheckResponse` from `@workspace/api-zod` |

App wiring: `artifacts/api-server/src/app.ts` (pino-http, cors, json/urlencoded) →
`src/routes/index.ts` (router aggregator) → `src/index.ts` (requires `PORT`, starts listener).

No other endpoints exist yet. To add one: update `lib/api-spec/openapi.yaml`, add a router file
under `src/routes/`, register it in `src/routes/index.ts`, then run
`pnpm --filter @workspace/api-spec run codegen`.

### .NET signaling service routes

These operational routes belong to `src/PeerOnQ.Signaling.Server`; they are independent of the
prototype Express `/api` contract.

| Method | Route | Handler File | Purpose | Notes |
| --- | --- | --- | --- | --- |
| GET | `/health/live` | `SignalingApp.cs` | Process liveness | No identifiers or secrets |
| GET | `/health/ready` | `SignalingApp.cs` | TURN configuration readiness | 503 when configured TURN lacks a usable server secret |
| GET | `/health` | `SignalingApp.cs` | Compatibility health summary | Aggregate device/session counts only |
| GET | `/metrics` | `SignalingApp.cs` | Prometheus signaling metrics | Low-cardinality; restrict to monitoring network |
| WS | `/ws` | `SignalingConnectionHandler.cs` | Authenticated signaling, ICE/resume, presence, immutable permissions, unattended challenges, support invitations, and negotiated opaque file-record relay | TLS required outside loopback; JSON control frames max 64 KB. `file.relay.v1` binary records max 1,040 KiB, require FileTransfer permission plus current session ownership, and are never decrypted or logged by the server |

The WebSocket protocol additionally routes Phase 4 `presence.query/result`, short-lived
`unattended.challenge.*` control messages and capability-gated support invitation credentials;
invitation secrets route only to the named target and are not server persistence. Challenge results may add the remote allowed permission
scope so the viewer can request an explicit safe mode change before proof submission. Clipboard and
interactive-control payloads use the separately negotiated WebRTC DTLS/SCTP data channel. When both
endpoints negotiate `file.relay.v1`, file records remain hybrid-session AES-256-GCM protected and
are routed as opaque binary WebSocket frames; otherwise file transfer retains its direct-P2P-only
fallback policy.

### Native URI activation

| URI | Handler | Purpose | Safety |
| --- | --- | --- | --- |
| `peeronq://support?...` | `src/PeerOnQ.App/App.xaml.cs` → `MainWindow.ApplySupportInvitationUri` | Load an expiring support invitation into the native Dashboard | Strict canonical parse; never auto-connects; remote Accept remains mandatory |

### Phase 6 Cloud API routes

These routes belong to `src/PeerOnQ.Cloud.Api/CloudApiEndpoints.cs`. Device bearer tokens are opaque,
short-lived, installation-bound credentials; registration/authentication endpoints are rate-limited
and all telemetry writes are idempotent.

| Method | Route | Purpose | Authorization |
| --- | --- | --- | --- |
| POST | `/v1/installations/register` | Confirm/reconcile the proof-bound installation after device authentication | Device token; owner installation only |
| POST | `/v1/installations/heartbeat` | Record installation heartbeat/version/region | Device token; owner installation only |
| POST | `/v1/installations/version` | Record explicit application version transition | Device token; owner installation only |
| POST | `/v1/installations/unregister` | Mark the installation unregistered | Device token; owner installation only |
| POST | `/v1/devices/register` | Issue a one-use canonical ECDSA challenge bound to installation/SPKI/client metadata | Anonymous, rate-limited; persists no claim |
| POST | `/v1/devices/authenticate` | Atomically verify proof, create Device+Installation, and assign a 12-digit routing alias | Anonymous challenge holder; replay/clone safe |
| POST | `/v1/sessions/start` | Record requested session lifecycle start | Device token |
| POST | `/v1/sessions/connected` | Record the first actual media-connected state | Device token |
| POST | `/v1/sessions/heartbeat` | Refresh server-observed activity for a connected session | Device token; session participant only |
| POST | `/v1/sessions/end` | Record terminal result/failure/path metadata | Device token |
| POST | `/v1/updates/events` | Record offered/downloaded/failed/installed update event | Device token |
| POST | `/v1/diagnostics/requests` | Create a short-lived consented diagnostic upload request | Device token |
| POST | `/v1/diagnostics/uploads` | Upload bounded sanitized ZIP and SHA-256 metadata | Device token; 20 MiB request boundary |
| GET | `/v1/diagnostics/{diagnosticId}/status` | Read owner-visible processing/expiry state | Device token |

### Phase 7 customer portal API

Handlers: `CustomerPortalEndpoints.cs` and `CustomerOrganizationEndpoints.cs`. The
`PeerOnQCustomer` scheme, claims, cookies, roles and rate partitions are separate from internal
Admin authentication. Unsafe authenticated requests require the root-scoped double-submit CSRF
cookie/header.

| Method | Route | Purpose | Authorization |
| --- | --- | --- | --- |
| POST | `/portal/v1/auth/register`, `/verify-email`, `/login`, `/refresh` | Account creation, email proof and rotating browser session | Anonymous/rate-limited; refresh cookie + CSRF |
| POST | `/portal/v1/auth/password-reset/request`, `/complete` | Enumeration-safe reset issue and one-use completion | Anonymous/rate-limited |
| POST | `/portal/v1/auth/logout` | Revoke current customer session | Customer session + CSRF |
| GET/PUT | `/portal/v1/account/profile` | Read/update current profile | Customer session; PUT + CSRF |
| POST/DELETE | `/portal/v1/account/mfa/setup`, `/mfa/confirm`, `/mfa` | Time-limited TOTP setup, recovery codes, verified disable | Customer session + CSRF |
| GET/DELETE | `/portal/v1/account/sessions`, `/sessions/{id}` | List and revoke only the current account's sessions | Customer session + CSRF on delete |
| GET/DELETE | `/portal/v1/account/trusted-devices`, `/trusted-devices/{id}` | List/revoke expiring account trust records | Customer session + CSRF on delete |
| POST | `/portal/v1/account/data-requests` | Request account export or guarded deletion | Customer session + CSRF |
| GET/POST | `/portal/v1/organizations/` | List memberships or create an organization | Customer session; create + CSRF |
| GET/PUT/DELETE | `/portal/v1/organizations/{org}/members...` | Tenant-scoped membership and least-privilege role changes | Member read; Owner/Admin mutation + CSRF |
| POST/DELETE | `/portal/v1/organizations/{org}/invitations...` | Issue/revoke protected short-lived invitations | Owner/Admin + CSRF |
| POST | `/portal/v1/organizations/invitations/accept` | One-use invitation acceptance bound to account email | Customer session + CSRF |
| GET/POST/DELETE | `/portal/v1/organizations/{org}/teams...` | Tenant-scoped team and team-membership management | Member read; Owner/Admin mutation + CSRF |
| GET/PUT/POST | `/portal/v1/organizations/{org}/policy...` | Read/update/evaluate security and operations policy | Member read/evaluate; Owner/Admin update + CSRF |
| GET/POST | `/portal/v1/organizations/{org}/devices...` | Tenant devices and proof-bound device claim | Member read; Owner/Admin claim + CSRF |
| GET | `/portal/v1/organizations/{org}/sessions` | Tenant-scoped remote-session metadata | Organization member |
| GET | `/portal/v1/organizations/{org}/audit`, `/audit/export` | Redacted immutable organization events/JSON export | Organization member |
| POST | `/portal/v1/organizations/{org}/transfer-ownership` | Explicit owner transfer | Current owner + CSRF |
| DELETE | `/portal/v1/organizations/{org}` | Guarded deletion request after members/devices are resolved | Current owner + CSRF |

### Phase 6 Presence Server

| Transport | Route | Handler | Purpose | Authorization |
| --- | --- | --- | --- | --- |
| SignalR WebSocket | `/presence/v1/hub` | `PresenceHub.cs` | Installation-scoped lease registration, heartbeat and session state | Device bearer token; query token accepted only on this HTTPS hub path |

The Redis lease includes server ownership/epoch and bounded expiry. Duplicate ownership displaces the
old lease; disconnect and graceful deployment release only the matching epoch.

### Phase 6 Downloads Service

| Method | Route | Purpose | Notes |
| --- | --- | --- | --- |
| GET | `/windows/latest?architecture=&channel=` | Resolve and stream latest active signed release | 404 when absent; origin is fully verified into the bounded digest cache before any response byte |
| GET | `/windows/{version}/{architecture}` | Stream one exact eligible release | Full/range response reads verified immutable cache; 206 records `Partial`; bounded concurrency/deadline |
| POST | `/v1/downloads/start` | Create idempotent download event/completion capability | Rate-limited |
| POST | `/v1/downloads/complete` | Record verified completion | One-use keyed completion token |

The managed local Vite preview observes only exact versioned development-MSI GET paths and reports
their actual start, full/partial completion, cancellation, or failure to these endpoints server-side.

### Phase 6 Admin API

Handler: `src/PeerOnQ.Admin.Api/AdminEndpoints.cs`. Login/MFA/refresh endpoints are separately
rate-limited. Refresh uses an HttpOnly Secure SameSite=Strict cookie; authenticated unsafe requests
also require the double-submit CSRF value. RBAC policies hide and reject unauthorized resources.

| Method | Route | Purpose | Policy |
| --- | --- | --- | --- |
| POST | `/admin/v1/auth/login` | Password authentication and MFA challenge | Anonymous |
| POST | `/admin/v1/auth/mfa/verify` | TOTP/recovery verification and session creation | Anonymous challenge holder |
| POST | `/admin/v1/auth/refresh` | Rotate refresh session/access token | Refresh cookie + CSRF |
| POST | `/admin/v1/auth/logout` | Revoke current admin session | Refresh cookie + CSRF |
| GET | `/admin/v1/auth/session` | Current operator identity, roles and expiry | `admin.read` |
| GET | `/admin/v1/overview` | Persisted product counters plus live Phase 3 online-device/open-session overlay | `admin.read` |
| GET | `/admin/v1/overview/distributions` | Bounded app/Windows version distributions | `admin.read` |
| GET | `/admin/v1/devices` | Paged/searchable devices | `admin.read` |
| GET | `/admin/v1/installations` | Paged installation/version/region state | `admin.read` |
| GET | `/admin/v1/presence` | Authorized presence lease summaries | `admin.read` |
| GET | `/admin/v1/sessions` | Session lifecycle/path/failure history | `admin.read` |
| GET | `/admin/v1/downloads` | Download/update events and aggregates | `admin.read` |
| GET | `/admin/v1/releases` | Signed release metadata and rollout state | `admin.read` |
| GET | `/admin/v1/website-releases` | List verified static website versions and active/rollback state | `admin.release` |
| GET | `/admin/v1/platform-upgrades/status` | Read the root-owned full-platform upgrade agent's bounded durable status | `admin.release` |
| GET | `/admin/v1/diagnostics` | Sanitized diagnostic metadata | `admin.diagnostics` |
| GET | `/admin/v1/diagnostics/{diagnosticId}` | Open one diagnostic and append access evidence | `admin.diagnostics` |
| GET | `/admin/v1/infrastructure` | Region/service health history recorded from fixed Prometheus targets | `admin.operations` |
| GET | `/admin/v1/infrastructure/metrics` | Fixed allowlisted Prometheus queries | `admin.operations` |
| GET | `/admin/v1/audit` | Immutable privileged-action audit | `admin.security` |
| GET | `/admin/v1/alerts` | Alert lifecycle events | `admin.operations` |
| GET | `/admin/v1/admin-sessions` | Active/revoked operator sessions | `admin.security` |
| PUT | `/admin/v1/installations/{installationId}/block` | Block/unblock installation with reason | `admin.security-or-operations` + audit |
| POST | `/admin/v1/releases` | Verify and atomically publish one offline-signed manifest/MSI pair | `admin.release` + CSRF + audit |
| PUT | `/admin/v1/releases/{releaseId}/manifest` | Replace only the verified signed manifest/rollout for immutable release metadata | `admin.release` + CSRF + audit |
| PUT | `/admin/v1/releases/{releaseId}/rollout` | Compatibility rejection directing old clients to signed-manifest replacement | `admin.release`; always 409 |
| POST | `/admin/v1/website-releases` | Verify a dedicated P-256 manifest, bounded MSI-free static ZIP and byte-identical Downloads UI compatibility entry, then activate atomically | `admin.release` + CSRF + audit |
| POST | `/admin/v1/website-releases/{version}/activate` | Activate a retained verified website release and retain the current version for rollback | `admin.release` + CSRF + audit |
| POST | `/admin/v1/platform-upgrades/stage` | Stream a bounded server bundle/checksum/signature trio into the host-agent inbox for static integrity/signature verification and root-only retention | `admin.release` + CSRF + audit |
| POST | `/admin/v1/platform-upgrades/apply` | Queue an exact-version full-platform deployment; the root host reverifies it and runs the executable dry-run preflight before activation | `admin.platform-upgrade` (MFA Owner only) + CSRF + audit |
| POST | `/admin/v1/platform-upgrades/rollback` | Queue rollback to the retained verified application release without down-migrating data | `admin.platform-upgrade` (MFA Owner only) + CSRF + audit |
| POST | `/admin/v1/devices/{deviceId}/revoke` | Irreversibly revoke device trust | `admin.security` + audit |
| POST | `/admin/v1/admin-sessions/{sessionId}/revoke` | Revoke another operator session and immediately invalidate its sid-bound access token | `admin.security` + audit |
| POST | `/internal/v1/alerts/alertmanager` | Ingest validated Alertmanager events | Internal bearer secret, rate-limited |

### Shared Phase 6 operational routes

Cloud, Presence, Admin and Downloads hosts all expose `GET /health/live`, `/health/ready`,
`/health/startup`, and `/metrics` through `PeerOnQ.Observability/ServiceDefaults.cs`. Metrics contain
no device/session IDs or addresses and are intended only for the monitoring network.

### .NET local persistence

| Store | Location | Shape | Protection |
| --- | --- | --- | --- |
| `collaboration-profile-v1` | DPAPI secret directory | Address book, groups, trusted fingerprints, unattended verifier/recovery hashes | DPAPI CurrentUser, versioned JSON inside encrypted blob |
| `security_audit` | SQLite schema v3 | Event ID/type/UTC time/masked peer/permission/outcome/safe metadata/app version plus HMAC chain | Sanitized export; no paths, filenames, clipboard content, credentials, tokens, full IDs or addresses |
| `audit-hmac-key` | DPAPI secret directory | Random HMAC key for local audit chaining | DPAPI CurrentUser; never exported or logged |
| `privacy-settings-v1` | DPAPI secret directory | Crash-report consent and audit-retention days | DPAPI CurrentUser; crash reporting defaults off |
| `crash-reports/` | `%LOCALAPPDATA%\PeerOnQ` | Opt-in sanitized local JSON reports | No automatic upload; no messages, stacks, frames, paths, credentials or tokens |
| `updates/` | `%LOCALAPPDATA%\PeerOnQ` | Verified MSI staging and non-executable `.partial` downloads | HTTPS + signed manifest + exact hash/size + WinVerifyTrust + publisher allowlist |

## Frontend Pages

All routes are declared in `artifacts/peeronq/src/app/router/index.tsx` using `wouter`.
`src/App.tsx` only mounts application-wide providers and the router. Paths below are relative to
`artifacts/peeronq/src/`.

### Public website — `PublicLayout`

| URL | Page File | Purpose | Data source |
| --- | --- | --- | --- |
| `/` | `pages/public/HomePage.tsx` → `LandingPage.tsx` | Public product, portal entry, security, MIT source, downloads and FAQ | Static copy, real HTTPS portal/GitHub links, local platform detection and fail-closed release flags |
| `/features` | redirect | Backward-compatible link to `/#product` | static |
| `/security` | redirect | Backward-compatible link to `/#security` | static |
| `/downloads` | redirect | Backward-compatible link to `/#download`; canonical MSI/hash/version/classification remain immutable server-bundle routes | static |
| `/about` | redirect | Backward-compatible link to `/#strategy` | static |
| `/help` | redirect | Backward-compatible link to `/#help` | static |
| `/privacy` | `pages/public/PrivacyPage.tsx` | Privacy policy | static |
| `/terms` | `pages/public/TermsPage.tsx` | Terms of service | static |

The responsive public navbar uses Product, Security, Download and Open source anchors plus a real
Portal link and Sign in action. Portal entries use the credential-free HTTPS
`VITE_PEERONQ_ACCOUNT_PORTAL_URL` (default `https://portal.peeronq.com`). Header/mobile Download PeerOnQ
anchors lead to the hero's single `DownloadsPage` package action (`#client-download`), beside Open Portal.
`#download` and the legacy `/downloads` redirect retain platform/release information with a return
anchor to that selector. Windows retains its
checksum/version-controlled release sources and shows known unsigned classifications; macOS, Linux,
Android and iOS/iPadOS need explicitly configured HTTPS publication URLs. Unknown or unpublished
platforms fail closed without a Windows fallback. Open source uses `#open-source` and retains
`#strategy` for the legacy `/about` redirect. The static client illustration is labeled. The surface
never mounts desktop `Sidebar`, `Topbar`, or the Phase 6 development toolbar and never promotes
Admin, Grafana or Prometheus as public navigation.

### Customer account portal — `artifacts/peeronq-portal`

The customer portal is a separately built/deployed SPA at `portal.*`; it is not mounted under the
public site's retired `/app` routes and does not share the internal Admin UI. Its public host serves
the sign-in page without an operator CIDR restriction; existing customer API authentication, CSRF
and tenant authorization still apply.

| URL | Page | Purpose | Data source |
| --- | --- | --- | --- |
| `/` | `OverviewPage` / unauthenticated `AuthPage` | Sign in, then real account/organization overview, assigned devices and recent remote sessions | `/portal/v1/auth/*`, `/account/profile`, `/organizations/*/{devices,sessions}` |
| `/account` | `ProfilePage` | Current account profile | `/portal/v1/account/profile` |
| `/remote-sessions` | `RemoteSessionsPage` | Organization host-side remote-session metadata, up to 500 recent records | `/portal/v1/organizations/*/sessions` |
| `/downloads` | `DownloadsPage` | Native app access modes and link to authoritative public downloads | Static; `https://peeronq.com/#download` |
| `/support` | `SupportPage` | Documentation, GitHub issues/security policy and account/device access limits | Static; public repository |
| `/verify-email`, `/reset-password` | token pages | Email verification and password recovery | `/portal/v1/auth/*` |
| `/sessions`, `/trusted-devices` | account security pages | Browser account session and sign-in trust revocation; distinct from remote history | `/portal/v1/account/*` |
| `/organizations`, `/members`, `/teams` | organization pages | Tenant membership, RBAC and teams | `/portal/v1/organizations/*` |
| `/invitations`, `/invitations/accept` | invitation pages | Issue/revoke/accept protected invitations | `/portal/v1/organizations/*/invitations*` |
| `/devices` | `DevicesPage` | Organization-scoped device visibility | `/portal/v1/organizations/*/devices` |
| `/policy` | `PolicyPage` | Server-enforced connection/security/retention settings | `/portal/v1/organizations/*/policy` |
| `/security` | `SecurityPage` | MFA, recovery and account controls | `/portal/v1/account/mfa*` |
| `/audit` | `AuditPage` | Tenant security/audit events | `/portal/v1/organizations/*/audit` |
| `/privacy` | `PrivacyPage` | Export/delete request controls | `/portal/v1/account/data-requests` |

Billing, pricing, invoice, subscription, entitlement, activation and payment routes are absent;
unknown paths render a real not-found state. Loading, error and empty states are explicit and tested.
Organization resources are scoped to the selected tenant; navigation groups workspace, organization,
account and help. Device cards report assigned/revoked status rather than inferred online presence.
Desktop installation identity remains separate from account sign-in; no self-service desktop account
login/device-linking UI is claimed. Existing device ownership proof remains required.

### Desktop application UI preview — `DesktopPreviewLayout`

| URL | Page File | Purpose | Data source |
| --- | --- | --- | --- |
| `/desktop-preview` | `pages/desktop-preview/DashboardPage.tsx` | Desktop preview entry/dashboard | local/demo state |
| `/desktop-preview/dashboard` | `pages/desktop-preview/DashboardPage.tsx` | Desktop overview | local/demo state |
| `/desktop-preview/remote-access` | redirect | Retired duplicate route | redirects to `/desktop-preview/dashboard` |
| `/desktop-preview/devices` | `pages/desktop-preview/DevicesPage.tsx` | Local desktop device list | `deviceRepository` |
| `/desktop-preview/sessions` | `pages/desktop-preview/SessionsPage.tsx` | Local session history | `sessionRepository` |
| `/desktop-preview/file-transfer` | `pages/desktop-preview/FileTransferPage.tsx` | Disabled file-transfer design preview | none |
| `/desktop-preview/address-book` | `pages/desktop-preview/AddressBookPage.tsx` | Local contacts and groups | `contactRepository` |
| `/desktop-preview/security` | `pages/desktop-preview/SecurityPage.tsx` | Desktop connection/security controls | `useSettings` |
| `/desktop-preview/settings` | `pages/desktop-preview/SettingsPage.tsx` | Desktop app settings | `useSettings` |

Every desktop preview route permanently renders “Desktop application UI preview — this is not
the production website.” Its sidebar includes Visit public website, Open account portal, and
Exit desktop preview.

### Production admin SPA - `artifacts/peeronq-admin`

All routes are declared in `src/App.tsx`; unauthenticated state renders `LoginPage` instead of the
shell. Resource visibility is filtered by the same RBAC policy names enforced by Admin API.
The Windows full-development controller exposes this separate surface at
`https://admin.dev.localhost:8443`; the offline preview has no `/admin` route, but its dev-only
toolbar links to the real SPA plus Cloud health, Grafana and Prometheus.

| URL | Page | Required policy |
| --- | --- | --- |
| `/` | `OverviewPage.tsx` | `admin.read` |
| `/devices`, `/installations`, `/presence`, `/sessions`, `/downloads`, `/releases` | `ResourcePage.tsx`; Releases adds signed MSI publication plus signed static website upload/activation/rollback | `admin.read`; release actions require `admin.release` |
| `/diagnostics` | `ResourcePage.tsx` | `admin.diagnostics` |
| `/infrastructure`, `/alerts` | `ResourcePage.tsx` | `admin.operations` |
| `/audit` | `ResourcePage.tsx` | `admin.security` |
| `/admin-sessions` | `AdminSessionsPage.tsx` | `admin.security` |
| `/upgrade` | `UpgradePage.tsx` | `admin.release`; apply/rollback require MFA Owner-only `admin.platform-upgrade` |

### Legacy redirects and fallback

| Old URL | Destination |
| --- | --- |
| `/dashboard` | `/desktop-preview/dashboard` |
| `/remote-access` | `/desktop-preview/dashboard` |
| `/devices` | `/desktop-preview/devices` |
| `/sessions` | `/desktop-preview/sessions` |
| `/files` | `/desktop-preview/file-transfer` |
| `/file-transfer` | `/desktop-preview/file-transfer` |
| `/address-book` | `/desktop-preview/address-book` |
| `/settings` | `/desktop-preview/settings` |

`/not-found` and the wildcard render `pages/NotFoundPage.tsx` without any surface layout.

## Repositories and Hooks

`src/repositories/index.ts` is only a barrel re-exporting the feature repositories below.

| Module | File | Purpose |
| --- | --- | --- |
| `deviceRepository` | `features/devices/deviceRepository.ts` | CRUD over `peeronq_devices` |
| `useDevices` | `features/devices/useDevices.ts` | React hook wrapping the device repository |
| `sessionRepository` | `features/sessions/sessionRepository.ts` | CRUD over `peeronq_sessions` |
| `contactRepository` | `features/address-book/contactRepository.ts` | CRUD over `peeronq_contacts` + `peeronq_groups` |
| `useSettings` | `features/settings/useSettings.ts` | App settings backed by `peeronq_settings` |
| `useConnectionState` | `features/connections/useConnectionState.ts` | Typed stub — always returns `activeSession: null` (websockets planned) |
| `useFileTransfer` | `features/files/useFileTransfer.ts` | Typed stub — always returns an empty `FileTransfer[]` |
| `useTheme` | `hooks/useTheme.ts` | Theme resolution + `peeronq_theme` persistence |
| `useLocalStorage` | `hooks/useLocalStorage.ts` | Generic typed localStorage hook |
| `migrateLegacyStorage` | `compatibility/legacyBrandStorageMigration.ts` | Idempotent pre-render migration; new PeerOnQ keys win and legacy values are retained for rollback |
| `apiClient` | `services/apiClient.ts` | Stub client; returns `NotConfigured` unless `VITE_PEERONQ_API_BASE_URL` is set |

## Storage Keys (localStorage)

This replaces the database for the prototype.

`compatibility/legacyBrandStorageMigration.ts` copies valid legacy records into these keys before React renders. It never
overwrites an existing PeerOnQ key and never deletes the legacy source during the rollback window.

| Key | Shape | Written by |
| --- | --- | --- |
| `peeronq_devices` | `Device[]` | `deviceRepository` |
| `peeronq_sessions` | `Session[]` | `sessionRepository` |
| `peeronq_contacts` | `Contact[]` | `contactRepository` |
| `peeronq_groups` | `ContactGroup[]` | `contactRepository` |
| `peeronq_settings` | `AppSettings` | `useSettings` |
| `peeronq_theme` | `"system" \| "light" \| "dark"` | `useTheme` |

## Native Apple Local Storage

The Apple viewer has no application route or cloud database. `AppleLocalStores.cs` writes only
non-secret local state to the app sandbox; `AppleKeychainDeviceSecretStore.cs` keeps private identity
material in the non-synchronizing data-protection Keychain.

| Key / service | Shape | Written by |
| --- | --- | --- |
| `peeronq.device-identity.v1` | Non-secret `StoredIdentity` JSON | `AppleDeviceIdentityRepository` / `NSUserDefaults` |
| `peeronq.blocked-devices.v1` | Bounded PeerOnQ ID string set | `AppleBlockedDeviceStore` / `NSUserDefaults` |
| `peeronq.session-audit.v1` | Last 100 masked `StoredAuditEntry` records | `AppleSessionAuditLog` / `NSUserDefaults` |
| Keychain service `io.peeronq.apple.device-secrets.v1` | Named private identity/key byte arrays, device-only and non-synchronizing | `AppleKeychainDeviceSecretStore` |

## Data Models

All types live in one file: `artifacts/peeronq/src/types/index.ts`.

| Type | Purpose | Notes |
| --- | --- | --- |
| `Device` | Remote machine record | `peerOnQId` display format `XXX-XXX-XXX-XXX` |
| `Session` | Connection history entry | `direction`, `mode`, `state` unions |
| `Contact` / `ContactGroup` | Address book entries | supports tags + favorites |
| `FileTransfer` | Transfer queue entry | `direction`, `state`, byte counters; queue is always empty in the prototype |
| `AppSettings` | Full settings object | theme, quality, privacy, performance |
| `NotConfigured` | "no backend configured" result | guard: `isNotConfigured()` |

The legacy Drizzle schema (`lib/db/src/schema/index.ts`) is empty. The Phase 6 server model is
`CloudDbContext`: Device, Installation, RemoteSession, SessionFailure, DevicePresenceHistory,
DownloadEvent, AppRelease, UpdateEvent, DiagnosticBundle, DiagnosticAccessEvent, AdminUser,
AdminRole, AdminUserRole, AdminRecoveryCode, AdminSession, AuditEvent, InfrastructureRegion,
ServiceHealthSnapshot, AlertEvent, RetentionBatchEvidence, and RetentionPolicy.
Phase 7 adds CustomerAccount, CustomerSession, CustomerAccountToken, CustomerRecoveryCode,
Organization, OrganizationMembership, OrganizationInvitation, Team, TeamMembership,
OrganizationPolicy, CustomerTrustedDevice, CustomerSecurityEvent, and AccountDataRequest. Customer
and internal Admin identity/role/session entities are deliberately unrelated.

## Background Jobs

- `RetentionWorker`: bounded presence/session/download/diagnostic/health/admin-session cleanup plus
  policy-controlled audit/alert retention; legal holds win and destructive batches emit immutable
  SHA-256 aggregate evidence.
- `AdminInfrastructureSnapshotWorker`: one leased writer records fixed Phase 6 targets and the live
  Phase 3 signaling target from Prometheus every minute; the existing retention policy bounds history.
- Deployment backup job: encrypted destination is operator supplied; `pg_dump` output is verified by
  a separate restore-test job before old backups pass retention.
- Prometheus/Alertmanager: fixed scrape/recording/alert rules; alerts enter Admin API only through the
  authenticated internal webhook.

## Route Debugging Guide

1. Find the route in this file.
2. Open only the page or handler file listed.
3. Open the repository/hook only if the data is wrong.
4. Open `src/types/index.ts` only if the shape is wrong.
5. Open `src/components/ui/` only if a shadcn primitive itself misbehaves.
6. For Phase 6, start at the host endpoint file and the exact `PeerOnQ.Shared.Contracts/V1` DTO;
   do not route production cloud work through the legacy Express/OpenAPI skeleton.

Do not scan unrelated route folders. Do not open `artifacts/mockup-sandbox` — it is not the product.
