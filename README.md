# PeerOnQ

Secure remote-access platform — "Connect securely. Work anywhere."

This repository contains the offline **frontend prototype**, workspace scaffolding, and the real
.NET remote-access product. The web prototype still stores everything in `localStorage` and makes
no network calls; the separate .NET product under `src/` provides authenticated WebRTC streaming,
ICE/STUN/TURN connectivity, diagnostics, bounded secure reconnect, encrypted file transfer and text
clipboard, permission-gated mouse/keyboard control, address book, trusted devices, and unattended
access. Its native WinUI shell provides the real Remote Access/Devices/Sessions/File Transfer/
Address Book/Security/Settings surfaces. The .NET product also includes
signed-update enforcement, tamper-evident local security audit, default-off sanitized crash
reporting, and WiX x64/ARM64 installer/release foundations. Public-beta readiness remains
evidence-gated; see `PHASE5.md` before distributing any build.
Phase 6 adds the separate PostgreSQL/Redis cloud control plane, authenticated presence, tracked
signed-release downloads, consented diagnostics, MFA/RBAC admin dashboard, observability, alerts,
and backup/restore foundation. See `PHASE6.md` before public staging deployment.

PeerOnQ is open-source software released under the [MIT License](LICENSE).
Public source: [pasha555/PeerOnQ](https://github.com/pasha555/PeerOnQ).

The public `main` branch starts with a source-only snapshot; local build packages, credentials and
test reports are excluded. Development builds remain subject to the documented acceptance gates.
The current 4K improvements do not establish sustained 4K/30 fps or parity with other remote-desktop
products; the 35 ms input-latency acceptance gate remains open. See [AI_CHANGELOG.md](AI_CHANGELOG.md).

## Public pilot artifacts

The self-extracting Linux server bundle can create the protected production-pilot environment,
obtain/import TLS, generate one-time Admin MFA onboarding material, apply migrations, wait for health,
and preserve the previous application release for rollback. Docker Engine/Compose, public DNS and NAT
remain host/network prerequisites. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the exact command.

The repository also contains a native Android 7.1+ attended viewer/controller under
`src/PeerOnQ.App.Android`. Build a server-bound development-signed APK with
`scripts/android/build-peeronq-android-viewer.ps1`; exact toolchain and physical-device release gates
are documented in [packaging/android/README.md](packaging/android/README.md). Android hosting,
unattended access, file/clipboard transfer and public/store distribution are not claimed.

For two-laptop internet acceptance, run the public-pilot builder. The three update-trust arguments
below are public values from the protected release environment; providing all three makes Verified
Updates active, while omitting all three preserves an update-disabled connectivity-only build:

```powershell
.\scripts\windows\build-peeronq-public-pilot.ps1 `
  -Architectures x64 `
  -UpdateChannel beta `
  -UpdatePublicKeySpkiBase64 $env:PEERONQ_UPDATE_PUBLIC_KEY_SPKI `
  -UpdateKeyId $env:PEERONQ_UPDATE_KEY_ID `
  -PublisherCertificateSha256 $env:PEERONQ_PUBLISHER_CERTIFICATE_SHA256 `
  -IUnderstandThisIsNotProductionSigned
```

The resulting MSI is deliberately unsigned and test-only, but its client updater trusts only the
compiled public manifest key and publisher fingerprint. Production Windows upgrades use the signed
MSI/manifest workflow in Admin **Releases**; Linux server upgrades use a newer verified `.run`.

## Repository Layout

```text
artifacts/
  peeronq/          PeerOnQ frontend prototype (React 19 + Vite + Tailwind v4)  ← the product
  peeronq-admin/    Production API-backed operator dashboard
  api-server/       Legacy Express 5 skeleton (only /api/healthz)
  mockup-sandbox/   Throwaway UI sandbox, not part of the product

lib/
  api-spec/         OpenAPI spec + Orval config (source of truth for the API)
  api-client-react/ Generated React Query client — do not edit by hand
  api-zod/          Generated Zod schemas — do not edit by hand
  db/               Drizzle ORM + Postgres setup (schema still empty)

scripts/            Workspace scripts

src/                .NET 10 remote-view product and signaling service
  signaling server  ASP.NET Core WebSocket signaling
  TURN config       coturn container and security baseline
  deployment        Compose, Nginx, TLS and monitoring examples
  Phase 6 cloud     Cloud API, Presence, Admin API, Downloads and Observability hosts
```

## Getting Started

This is a pnpm workspace. **Use pnpm** — `npm install` and `yarn` are rejected by the root
`preinstall` script.

```bash
pnpm install

PORT=23586 BASE_PATH=/ pnpm --filter @workspace/peeronq run dev   # frontend prototype
pnpm --filter @workspace/api-server run dev                      # API server (needs PORT)

pnpm run typecheck                              # whole workspace
pnpm run lint                                   # eslint where configured
pnpm run test                                   # vitest where configured
pnpm run build                                  # typecheck + build everything
pnpm run format                                 # prettier

dotnet build PeerOnQ.slnx --no-restore          # .NET product
dotnet test PeerOnQ.slnx --no-restore           # .NET tests
```

For the production signaling/TURN stack, prepare public DNS, trusted certificates, and an external
secret file, then validate and start the hardened Compose definition:

```bash
cd src/PeerOnQ.Realtime.Deployment
docker compose --env-file /secure/peeronq.env -f docker-compose.production.yml config
docker compose --env-file /secure/peeronq.env -f docker-compose.production.yml up -d --build
```

Start from `.env.production.example`, but keep the populated file outside Git. The complete DNS,
firewall, TLS, monitoring, rotation, and release checklist is in the deployment guide linked below.

On Windows, run the local Phase 3 TLS/signaling/TURN acceptance stack with:

```powershell
.\scripts\windows\peeronq-phase3-local.ps1 start
.\scripts\windows\peeronq-phase3-local.ps1 test
```

The controller uses loopback-only local domains and reports whether it ran the complete Docker
coturn stack or the signaling-only fallback.

#### Two physical Windows laptops on one trusted LAN

Use the development computer as the signaling/TURN server. This flow is LAN-only: do not use the
router's public IP, enable port forwarding, or run it on a public Wi-Fi network.

1. Connect the development computer and both laptops to the same trusted network. On the
   development computer, find the exact LAN address and interface with `Get-NetIPConfiguration`.
   Set that connection to `Private` only if the network is trusted:

   ```powershell
   Set-NetConnectionProfile -InterfaceAlias 'Wi-Fi' -NetworkCategory Private
   ```

2. From an Administrator PowerShell in this repository, replace the example address with the
   development computer's LAN address, then start the LAN stack and build the client kit:

   ```powershell
   $ServerIp = '10.0.0.7'
   $ClientVersion = ([xml](Get-Content .\Directory.Build.props -Raw)).Project.PropertyGroup.PeerOnQWindowsClientVersion
   .\scripts\windows\peeronq-phase3-local.ps1 start -BindAddress $ServerIp -ConfigureFirewall
   .\scripts\windows\peeronq-phase3-local.ps1 status -BindAddress $ServerIp
   .\scripts\windows\build-phase5-development.ps1 `
     -SignalingUrl "wss://signal.${ServerIp}.sslip.io:5443/ws" `
     -Architectures x64 `
     -DevelopmentRootCertificate '.peeronq-phase3\certs\root-ca.cer' `
     -SkipWebsitePublish
   ```

3. From `dist\lan-development\signal.<server-ip>.sslip.io\$ClientVersion`, copy only the MSI to each
   laptop. The LAN MSI contains its public development root and pins it only for the compiled WSS
   endpoint; it does not modify Windows trusted-root stores. On each laptop, verify connectivity and
   install the MSI:

   ```powershell
   Resolve-DnsName signal.10.0.0.7.sslip.io
   Test-NetConnection 10.0.0.7 -Port 5443
   $Msi = (Resolve-Path ".\PeerOnQ-$ClientVersion-unsigned-development-x64.msi").Path
   Start-Process msiexec.exe -Verb RunAs -Wait `
     -ArgumentList '/i', "`"$Msi`""
   ```

4. Start PeerOnQ on both laptops. On laptop B, copy its Device ID. On laptop A, enter that ID,
   select **View only**, and start the session. Accept the permission prompt on laptop B. Repeat
   with **Full control** only after view-only succeeds. Finally select **File Transfer**, accept the
   separate request on laptop B, and use the File Transfer page to choose the exact file or folder.
   LAN clients start with **Low latency (1080p / 60 fps)**; select Native only when maximum detail is
   more important than interaction latency.
   For Phase 4, also test pause/resume after a real Wi-Fi interruption, the default-off text
   clipboard toggle on both sides, trusted-device revoke, and explicit unattended access after an
   app restart. Do not expect pre-logon, Secure Desktop, UAC bypass, or locked-screen control.
   File Transfer does not start screen capture or grant mouse/keyboard control.

The MSI is development-only and upgrades older test versions. Logs are under
`%LOCALAPPDATA%\PeerOnQ\logs`. When the test is complete, stop the stack with
`peeronq-phase3-local.ps1 stop -BindAddress <server-ip>` and remove the scoped firewall rules from
an Administrator PowerShell with
`Get-NetFirewallRule -Group 'PeerOnQ Local Development' | Remove-NetFirewallRule`.

For the complete Phase 6 control plane, copy the secret-free template and populate only an ignored
local `.env`. The Windows workspace controller then creates and trusts a repository-scoped
`*.dev.localhost` certificate, applies migrations/runtime grants, starts every service, and opens
the real Admin console. Staging/production secrets must come from the deployment secret manager.

```powershell
Copy-Item src\PeerOnQ.Infrastructure.Deployment\.env.example src\PeerOnQ.Infrastructure.Deployment\.env
.\peeronq-start.bat
```

### Windows control scripts

Double-click these in Explorer, or run them from a terminal with the `.\` prefix:

| Script | What it does |
| --- | --- |
| `peeronq-start.bat` | Starts/rebuilds Phase 6, applies migrations/grants, starts <http://localhost:5555>, then opens the preview and Admin console |
| `peeronq-stop.bat` | Stops the scoped preview and Phase 6 containers while preserving PostgreSQL/Redis volumes |
| `peeronq-restart.bat` | Restarts and rebuilds the complete development workspace |
| `peeronq-status.bat` | Shows preview ownership, Admin/API and observability readiness, plus all Phase 6 containers |

All four invoke [scripts/windows/peeronq-workspace-dev.ps1](scripts/windows/peeronq-workspace-dev.ps1),
which keeps the scoped web controller and Phase 6 Compose controller separate. Run
`scripts/windows/peeronq-dev.ps1` directly only when intentionally working on the offline preview
without Docker.

**Scope guarantees — these scripts never touch anything outside this folder:**

- `start` records the launcher PID in `.peeronq-run/dev-server.pid` (gitignored, inside the repo).
- `stop` kills **only** that recorded process tree, after confirming the process is still this
  repo's dev server. It never kills "whatever is on port 5555".
- If port 5555 belongs to a process these scripts did not start, both `start` and `stop` report
  the owning PID and leave it alone.
- The script derives its root from its own location and refuses to run unless that folder is the
  PeerOnQ workspace (`pnpm-workspace.yaml` + `artifacts/peeronq/`).
- Phase 6 binds management endpoints to loopback, uses browser-resolvable `*.dev.localhost` names,
  and stores generated private TLS material only under ignored `.peeronq-phase6/`.

Other notes:

- `PORT=5555` and `BASE_PATH=/` are set by the script; `vite.config.ts` requires both and uses
  `strictPort`, so the app always lands on 5555.
- Run them as `.\peeronq-start.bat` — this machine has `NoDefaultCurrentDirectoryInExePath=1`,
  so a bare `peeronq-start.bat` is not found from the current directory.
- The `peeronq-` prefix is deliberate: a plain `stop.bat` collides with
  `C:\Program Files\ConfigCure Lens\stop.bat`, which is on PATH.
- The dev server runs in a minimized `cmd` window; its output is the place to look if a start
  attempt times out.

Frontend env vars (`artifacts/peeronq/.env.example`):

| Variable | Purpose |
| --- | --- |
| `VITE_PEERONQ_API_BASE_URL` | API base URL; empty means `apiClient` returns `NotConfigured` |
| `VITE_ENABLE_DEMO_DATA` | `true` shows demo session rows on `/dashboard` and `/sessions` |
| `VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE` | Shows generated local-test Windows MSI links only when the controller verifies the package set |
| `VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION` / `VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER` | Controller-owned canonical version and exact verified local package classification; never set these independently |
| `VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL` | Optional absolute HTTPS origin for user-clicked tracked Windows downloads; invalid values fail closed |
| `VITE_PEERONQ_MACOS_DOWNLOAD_URL` / `VITE_PEERONQ_ANDROID_DOWNLOAD_URL` / `VITE_PEERONQ_IOS_DOWNLOAD_URL` | Optional HTTPS-only published install destinations for matching macOS, Android and iOS/iPadOS devices; leave unset until the platform release is approved |
| `VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL` / `VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL` | Optional HTTPS-only published Linux destinations selected by the detected architecture |
| `VITE_PEERONQ_ADMIN_PANEL_URL` | Dev-only Admin console link shown in the Phase 6 toolbar |
| `VITE_PEERONQ_CLOUD_HEALTH_URL` | Dev-only Cloud readiness link |
| `VITE_PEERONQ_GRAFANA_URL` / `VITE_PEERONQ_PROMETHEUS_URL` | Dev-only observability links; HTTP is accepted only for loopback hosts |

Publish Windows clients to the local Downloads page only after the LAN signaling/TURN stack is
running. The published pair is LAN-specific and contains the public development root needed to pin
the matching WSS endpoint:

```powershell
$ServerIp = '10.0.0.7'
.\scripts\windows\peeronq-phase3-local.ps1 start -BindAddress $ServerIp -ConfigureFirewall
.\scripts\windows\build-phase5-development.ps1 `
  -SignalingUrl "wss://signal.${ServerIp}.sslip.io:5443/ws" `
  -DevelopmentRootCertificate '.peeronq-phase3\certs\root-ca.cer' `
  -Architectures x64,arm64 `
  -PublishLanWebsiteDownloads
```

The generated packages are explicitly unsigned development builds and only work on the trusted LAN
that can reach the selected server. The elevated per-machine
installer defaults to `Program Files\PeerOnQ`, provides a destination chooser, and installs Start
menu and desktop shortcuts by default; startup remains optional.

## Changing the API Contract

Edit `lib/api-spec/openapi.yaml`, then regenerate:

```bash
pnpm --filter @workspace/api-spec run codegen
```

Never hand-edit anything under `src/generated/`.

## Documentation Map

These files exist so AI agents (and people) can find things **without scanning the whole repo**.
Keep them true — an outdated map is worse than no map.

| File | What it is for |
| --- | --- |
| [AGENTS.md](AGENTS.md) | Base rules for any agent working in this repo |
| [CLAUDE.md](CLAUDE.md) | Claude-specific reading order and token rules |
| [CODEX.md](CODEX.md) | Codex-specific reading order and token rules |
| [PROJECT_MAP.md](PROJECT_MAP.md) | Structure, stack, core flows, critical files, env vars |
| [ROUTES_MAP.md](ROUTES_MAP.md) | API routes, frontend routes, repositories, storage keys, models |
| [Current state](docs/CURRENT_STATE.md) | R0 source-backed inventory and Phase 1-6 truth matrix |
| [Brand migration](docs/BRAND_MIGRATION.md) | Canonical identity and isolated compatibility register |
| [Local development](docs/LOCAL_DEVELOPMENT.md) | Clean restore/build/test/start/stop runbook |
| [Test matrix](docs/TEST_MATRIX.md) | Automated, loopback, container, physical and external evidence boundaries |
| [Known limitations](docs/KNOWN_LIMITATIONS.md) | Unverified and externally blocked behavior |
| [Recovery backlog](docs/RECOVERY_BACKLOG.md) | P0/P1/P2 closure order |
| [R0 completion report](docs/phase-reports/R0_COMPLETION_REPORT.md) | Recovery gate evidence and exact next-phase decision |
| [PHASE3.md](PHASE3.md) | Internet connectivity architecture and verified test matrix |
| [Protocol compliance](docs/PROTOCOL_COMPLIANCE.md) | Signaling/WebRTC/STUN/TURN standards, library versions, evidence and limits |
| [PHASE4.md](PHASE4.md) | Collaboration protocol, security decisions, limitations and verified test matrix |
| [PHASE5.md](PHASE5.md) | Installer/update/audit/performance implementation, actual evidence, 25 beta scenarios and open gates |
| [PHASE6.md](PHASE6.md) | Cloud/admin/presence/download/observability implementation, actual evidence and staging gates |
| [Architecture](docs/ARCHITECTURE.md) | Desktop/server/relay/update trust boundaries |
| [Security](docs/SECURITY.md) | Trust anchors, permissions, data minimization and remaining security gates |
| [Installer](docs/INSTALLER.md) | Install/upgrade/repair/uninstall and enterprise deployment behavior |
| [Update security](docs/UPDATE_SECURITY.md) | Manifest/package verification, rollout, recovery and key rotation |
| [Release checklist](docs/RELEASE_CHECKLIST.md) | Evidence required before distribution |
| [Deployment guide](src/PeerOnQ.Realtime.Deployment/README.md) | DNS, TLS, firewall, secrets and monitoring |
| [Phase 6 deployment](docs/DEPLOYMENT.md) | Cloud DNS/TLS/topology/secrets/scaling deployment contract |
| [Phase 6 operations](docs/OPERATIONS.md) | Monitoring, backup, retention, rotation and operational checks |
| [Phase 6 runbooks](docs/RUNBOOKS.md) | Alert thresholds, owners, response and escalation procedures |
| [AI_CHANGELOG.md](AI_CHANGELOG.md) | Short history of AI-made changes and why |
| [TASK_TEMPLATE.md](TASK_TEMPLATE.md) | Prompt template for handing a task to an agent |
| [replit.md](replit.md) | Replit workspace notes: run/operate, stack, gotchas |

Frontend-specific docs live in `artifacts/peeronq/`:

| File | What it is for |
| --- | --- |
| [README](artifacts/peeronq/README.md) | Prototype overview and feature status |
| [FRONTEND_ARCHITECTURE](artifacts/peeronq/FRONTEND_ARCHITECTURE.md) | Directory layout, state, routing, theming |
| [DESIGN_SYSTEM](artifacts/peeronq/DESIGN_SYSTEM.md) | Color tokens, typography, spacing, component inventory |
| [INTEGRATION_CONTRACT](artifacts/peeronq/INTEGRATION_CONTRACT.md) | Planned backend contract |
| [ACCESSIBILITY](artifacts/peeronq/ACCESSIBILITY.md) | Accessibility implementation and gaps |

### Update rule

When a change affects the maps, update them in the same change:

- new/renamed route, page, endpoint, repository, storage key → ROUTES_MAP.md
- new directory, env var, critical file, flow → PROJECT_MAP.md
- new design token or shared component → `artifacts/peeronq/DESIGN_SYSTEM.md`
- any non-trivial change → append an entry to AI_CHANGELOG.md

## Goals of This Setup

- Prevent full repository scans.
- Keep token usage low.
- Keep changes small, safe, and easy to roll back.
