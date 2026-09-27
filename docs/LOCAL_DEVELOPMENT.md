# PeerOnQ local development

These commands are authoritative for Windows development. They do not require production secrets or
publish external artifacts.

## Prerequisites

- Git; Windows 11 or supported Windows 10 for the native client.
- Node.js 24 LTS and Corepack/pnpm 10.33.0.
- .NET SDK 10.0.302 (`global.json`).
- Docker Desktop with Linux containers for TURN/cloud acceptance.
- Visual Studio Build Tools/Windows SDK for native packaging; the WiX SDK is restored by .NET.

## Restore, build and test

```powershell
corepack enable
pnpm install --frozen-lockfile
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

The root pnpm commands exclude `artifacts/mockup-sandbox`; it is not a product dependency.

## Offline web product

```powershell
$env:PORT='5555'
$env:BASE_PATH='/'
pnpm --filter @workspace/peeronq run dev
```

The prototype stores data only in browser storage and has no configured API transport. The Admin SPA
is separate and talks to the local Admin API only when the Phase 6 stack is running.

## Phase 3 signaling/TURN

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
running and give the Phase 3 controller a separate external port and same-sized relay range:

```powershell
./scripts/windows/peeronq-phase3-local.ps1 -Action test `
  -TurnPort 3479 -TurnRelayMinPort 49201 -TurnRelayMaxPort 49241
```

The coturn container keeps its internal listener ports; only the loopback-published ports and
signaling-advertised URLs change. This is local Docker evidence, not physical-device evidence.

## Phase 6 workspace

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
Phase-6-specific local CA, applies migrations/grants, then starts the complete stack. Do not reuse
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
