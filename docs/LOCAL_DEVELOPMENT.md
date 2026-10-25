# PeerOnQ local development

These commands are authoritative for Windows development. They do not require production secrets or
publish external artifacts.

## Prerequisites

- Git; Windows 11 or supported Windows 10 for the native client.
- Node.js 24 LTS and Corepack/pnpm 10.33.0.
- .NET SDK 10.0.402 (`global.json`).
- Docker Desktop with Linux containers for TURN/cloud acceptance.
- Visual Studio Build Tools/Windows SDK for native packaging; the WiX SDK is restored by .NET.

## Restore, build and test

```powershell
corepack enable
pnpm install --frozen-lockfile
dotnet run --project eng/PeerOnQ.DependencyBuilder -c Release -- --output eng/sipsorcery/packages
dotnet restore PeerOnQ.slnx --locked-mode
dotnet build PeerOnQ.slnx -c Release --no-restore
dotnet test PeerOnQ.slnx -c Release --no-build --no-restore
pnpm run typecheck
pnpm run lint
pnpm run test
pnpm run build
./scripts/quality/test-brand-purity.ps1
./scripts/security/scan-repository-secrets.ps1
```

The dependency builder produces the maintained `PeerOnQ.SIPSorcery` package from its pinned source;
`NuGet.config` resolves that package only from `eng/sipsorcery/packages`. The root pnpm commands
cover every workspace package under `artifacts/`, `lib/` and `scripts/`.

## Offline web product

```powershell
$env:PORT='5555'
$env:BASE_PATH='/'
pnpm --filter @workspace/peeronq run dev
```

The prototype stores data only in browser storage and has no configured API transport. The Admin SPA
is separate and talks to the local Admin API only when the control-plane stack is running.

## Local signaling and TURN stack

The controller generates repository-scoped secrets and a unique local CA under ignored
`.peeronq-phase3/`; it never writes production credentials.

On first use, Windows can require an interactive confirmation before adding that root to
`CurrentUser\\Root`. Verify that the certificate comes from `.peeronq-phase3/certs/root-ca.cer` and
that its subject is `CN=PeerOnQ Phase 3 Local Development Root`; do not approve an unexpected
certificate. Headless automation must report this as an external trust step rather than bypass it.

```powershell
./scripts/windows/peeronq-phase3-local.ps1 -Action start
./scripts/windows/peeronq-phase3-local.ps1 -Action test
./scripts/windows/peeronq-phase3-local.ps1 -Action status
./scripts/windows/peeronq-phase3-local.ps1 -Action stop
```

When another local stack already owns the default TURN listener or relay ports, keep that stack
running and give the controller a separate external port and same-sized relay range:

```powershell
./scripts/windows/peeronq-phase3-local.ps1 -Action test `
  -TurnPort 3479 -TurnRelayMinPort 49201 -TurnRelayMaxPort 49241
```

The coturn container keeps its internal listener ports; only the loopback-published ports and
signaling-advertised URLs change. This is local Docker evidence, not physical-device evidence.

## Control-plane workspace

Copy the complete secret-free contract, populate its empty development values with generated local
values, and keep the resulting file ignored:

```powershell
Copy-Item src/PeerOnQ.Infrastructure.Deployment/.env.example `
  src/PeerOnQ.Infrastructure.Deployment/.env
./peeronq-start.bat
./peeronq-status.bat
```

The template names every required database, Redis, HMAC, token, TURN, TLS, diagnostics, bootstrap,
retention and host value. The controller rejects blanks/unsafe placeholders, generates and trusts a
workspace-specific local CA, applies migrations/grants, then starts the complete stack. Do not reuse
these values outside local development.

Stop without deleting PostgreSQL/Redis volumes or native user data:

```powershell
./peeronq-stop.bat
```

Volume deletion is intentionally not wrapped by a convenience command. If a developer explicitly
discards the Compose volumes, first stop the stack and verify the exact Compose project/volume names;
never remove `%LOCALAPPDATA%\PeerOnQ` as part of development cleanup.

## Native two-instance development

Build/publish the native app, then launch each instance with a different `PEERONQ_DATA_DIR`; otherwise
both processes share one identity and database. This is useful for UI/state checks but is not proof
of capture, input, NAT, hardware, firewall, DPI, or latency behavior on two physical machines.

Physical/public acceptance must record two devices, Windows builds, networks/NAT, nominated ICE
pair, permissions, media correctness, reconnect behavior and measurements. Mocks and loopback cannot
close that gate.

## Workspace control scripts (Windows)

Run these from the repository root with the `.\` prefix (for example `.\peeronq-start.bat`), or
double-click them in Explorer. The prefix matters on systems that set
`NoDefaultCurrentDirectoryInExePath`.

| Script | What it does |
| --- | --- |
| `peeronq-start.bat` | Starts or rebuilds the configured LAN stack and the control plane, applies migrations and grants, starts the web preview on <http://localhost:5555> and opens the development interfaces |
| `peeronq-stop.bat` | Stops the preview and control-plane containers while keeping the PostgreSQL and Redis volumes |
| `peeronq-restart.bat` | Restarts and rebuilds the complete development workspace |
| `peeronq-status.bat` | Shows preview ownership, Admin/API and observability readiness, and the control-plane containers |

All four call [`scripts/windows/peeronq-workspace-dev.ps1`](../scripts/windows/peeronq-workspace-dev.ps1),
which keeps the web preview controller and the control-plane Compose controller separate. Use
`scripts/windows/peeronq-dev.ps1` directly only for the offline web preview without Docker. After a
successful start, the website, desktop UI preview (`/desktop-preview`), Admin console, account
portal, Grafana and Prometheus open in the default browser. If startup fails, the window stays open
so the error can be read.

The scripts act only inside this repository:

- `start` records the launcher PID in `.peeronq-run/dev-server.pid` (ignored by Git), and `stop`
  ends only that recorded process tree after confirming it is still this repository's dev server.
- If port 5555 belongs to another process, both `start` and `stop` report its PID and leave it alone.
- The scripts derive the root from their own location and refuse to run outside a PeerOnQ workspace.
- Management endpoints bind to loopback, use `*.dev.localhost` names, and keep generated TLS
  material under the ignored `.peeronq-phase6/` directory.
- Dev server output goes to `.peeronq-run/dev-server.stdout.log` and `.peeronq-run/dev-server.stderr.log`.

## Two Windows computers on a trusted LAN

Use the development computer as the signaling and TURN server. This flow is LAN-only: do not use
the router's public IP, enable port forwarding, or run it on a public Wi-Fi network.

1. Connect the development computer and both test computers to the same trusted network. Find the
   development computer's LAN address with `Get-NetIPConfiguration`, and set that connection to
   `Private` only if the network is trusted:

   ```powershell
   Set-NetConnectionProfile -InterfaceAlias 'Wi-Fi' -NetworkCategory Private
   ```

2. From an Administrator PowerShell in this repository, replace the example address with the
   development computer's LAN address, then start the LAN stack and build the client:

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
   test computer. The MSI contains the public development root and pins it only for the compiled
   WSS endpoint; it does not modify the Windows trusted-root stores. On each computer, check
   connectivity and install the MSI:

   ```powershell
   Resolve-DnsName signal.10.0.0.7.sslip.io
   Test-NetConnection 10.0.0.7 -Port 5443
   $Msi = (Resolve-Path ".\PeerOnQ-$ClientVersion-unsigned-development-x64.msi").Path
   Start-Process msiexec.exe -Verb RunAs -Wait -ArgumentList '/i', "`"$Msi`""
   ```

4. Start PeerOnQ on both computers. Copy computer B's Device ID, enter it on computer A, select
   **View only** and start the session, then accept the request on computer B. Try **Full control**
   only after view-only works. To test file transfer, select **File Transfer**, accept the separate
   request on computer B, and choose the file or folder on the File Transfer page. Also test pause
   and resume after a real Wi-Fi interruption, the text clipboard toggle on both sides,
   trusted-device revocation, and unattended access after an app restart. Pre-logon, Secure Desktop,
   UAC prompts and locked-screen control are not supported. File transfer does not start screen
   capture or grant mouse and keyboard control.

The MSI is for development only and upgrades older test versions. Logs are written to
`%LOCALAPPDATA%\PeerOnQ\logs`. When testing is complete, stop the stack with
`peeronq-phase3-local.ps1 stop -BindAddress <server-ip>` and remove the firewall rules from an
Administrator PowerShell:

```powershell
Get-NetFirewallRule -Group 'PeerOnQ Local Development' | Remove-NetFirewallRule
```

### Publishing LAN builds to the local Downloads page

With the LAN stack running, the development builder can publish the matching x64 and ARM64 MSIs to
the local website's Downloads page. These packages are unsigned development builds that only work on
the trusted LAN that can reach the selected server:

```powershell
$ServerIp = '10.0.0.7'
.\scripts\windows\peeronq-phase3-local.ps1 start -BindAddress $ServerIp -ConfigureFirewall
.\scripts\windows\build-phase5-development.ps1 `
  -SignalingUrl "wss://signal.${ServerIp}.sslip.io:5443/ws" `
  -DevelopmentRootCertificate '.peeronq-phase3\certs\root-ca.cer' `
  -Architectures x64,arm64 `
  -PublishLanWebsiteDownloads
```

The per-machine installer defaults to `Program Files\PeerOnQ`, offers a destination chooser, and
installs Start menu and desktop shortcuts by default; starting with Windows remains optional.

## Frontend environment variables

The website reads these variables (see `artifacts/peeronq/.env.example`):

| Variable | Purpose |
| --- | --- |
| `VITE_PEERONQ_API_BASE_URL` | API base URL; when empty, the API client reports `NotConfigured` |
| `VITE_ENABLE_DEMO_DATA` | `true` shows demo session rows on `/dashboard` and `/sessions` |
| `VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE` | Shows local-test Windows MSI links only when the controller has verified the package set |
| `VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION` / `VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER` | Set by the controller to the verified version and package classification; do not set them by hand |
| `VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL` | Optional absolute HTTPS origin for tracked Windows downloads; invalid values are rejected |
| `VITE_PEERONQ_MACOS_DOWNLOAD_URL` / `VITE_PEERONQ_ANDROID_DOWNLOAD_URL` / `VITE_PEERONQ_IOS_DOWNLOAD_URL` | Optional HTTPS install destinations for macOS, Android and iOS/iPadOS; leave unset until that platform is released |
| `VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL` / `VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL` | Optional HTTPS Linux destinations, selected by the detected architecture |
| `VITE_PEERONQ_ADMIN_PANEL_URL` | Development-only Admin console link |
| `VITE_PEERONQ_CLOUD_HEALTH_URL` | Development-only cloud readiness link |
| `VITE_PEERONQ_GRAFANA_URL` / `VITE_PEERONQ_PROMETHEUS_URL` | Development-only observability links; HTTP is accepted only for loopback hosts |
