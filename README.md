# PeerOnQ

**Connect securely. Work anywhere.**

PeerOnQ is an open-source remote-access platform. It provides native clients for viewing and
controlling another computer, and a self-hostable control plane for signaling, relay, presence,
administration and updates.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](global.json)
[![Node.js 24](https://img.shields.io/badge/Node.js-24-339933.svg)](.nvmrc)

> **Project status: public pilot (0.9.88).** PeerOnQ is under active development. Builds made from
> this repository are pre-release, and Windows installers are not yet production-signed. Review
> [Known limitations](docs/KNOWN_LIMITATIONS.md) before relying on PeerOnQ.

## Contents

- [Features](#features)
- [Platforms](#platforms)
- [Architecture](#architecture)
- [Getting started](#getting-started)
- [Deployment](#deployment)
- [Security](#security)
- [Code signing](#code-signing)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [License](#license)

## Features

### Remote sessions

- Screen streaming over WebRTC with VP8 (libvpx) and hardware H.264 (Media Foundation).
- Adaptive quality that targets at least 60 fps and up to 120 fps when the display and network allow it.
- View-only and full-control sessions. The remote side must accept every session, and mouse and
  keyboard control is a separate permission.
- Direct peer-to-peer connections through ICE and STUN, with TURN relay fallback and bounded,
  secure reconnects after network interruptions.
- Encrypted file and folder transfer, and an optional text clipboard (off by default).
- Automatic system sound and two-way voice (Opus), subject to the organization's audio policy.
- Address book, trusted devices and explicitly enabled unattended access.
- Network Doctor: real STUN and TURN probes with plain-language connection diagnostics.

### Security and trust

- Authenticated signaling over TLS and encrypted WebRTC media.
- Signed updates: the client accepts only manifests signed with its compiled public key and
  packages from the pinned publisher.
- Tamper-evident local security audit log.
- Crash reporting is off by default; when enabled, reports are sanitized.

### Self-hosted control plane

- Signaling server (ASP.NET Core) and a hardened coturn TURN configuration.
- Cloud API, presence, downloads, and an Admin API with MFA and role-based access control.
- Admin dashboard and account portal.
- PostgreSQL and Redis, with OpenTelemetry, Prometheus, Grafana, Loki and Tempo for observability.
- A self-extracting Linux server bundle that sets up TLS, applies migrations, waits for health
  checks and keeps the previous release for rollback.

## Platforms

| Platform | Role | Status |
| --- | --- | --- |
| Windows 10 and 11 (x64, ARM64) | Host, viewer and controller | Public pilot, MSI installers |
| Linux desktop (x64, ARM64) | Viewer and controller | Preview |
| Android 7.1 and later | Attended viewer and controller | Source available, not published |
| macOS, iOS and iPadOS | Attended viewer and controller | Source available, not published |
| Linux server | Signaling, TURN and control plane | Self-hosted pilot |

Each platform has its own release gates. Source code for a platform does not by itself mean that a
published app exists. See [platform capabilities](docs/CROSS_PLATFORM_CAPABILITIES.md) for details.

## Architecture

Clients find each other through the signaling service and then connect directly over WebRTC. When
a direct path is not possible, media is relayed through TURN. The signaling service never proxies
screen, input, file or clipboard data. The control plane manages accounts, devices, presence,
releases and diagnostics. See [Architecture](docs/ARCHITECTURE.md) and
[Protocol compliance](docs/PROTOCOL_COMPLIANCE.md).

```text
src/                         .NET 10 product
  PeerOnQ.Domain             Core model
  PeerOnQ.Application        Session coordination and policies
  PeerOnQ.Infrastructure     Persistence, cloud client and updates
  PeerOnQ.Transport          WebRTC, ICE, STUN and TURN transport
  PeerOnQ.Media              Capture, codecs, audio and adaptive quality
  PeerOnQ.Platform.*         Windows, Linux, Android and Apple integrations
  PeerOnQ.App*               Native clients (WinUI 3, Avalonia, Android, Apple)
  PeerOnQ.Service.Windows    Windows session broker service
  PeerOnQ.Signaling.Server   Real-time signaling service
  PeerOnQ.Presence.Server    Device presence service
  PeerOnQ.Cloud.*            Cloud API and domain
  PeerOnQ.Admin.Api          Administration API
  PeerOnQ.Downloads.Service  Release downloads
  PeerOnQ.*.Deployment       Compose, Nginx, TLS and monitoring definitions
tests/                       .NET test projects
installer/                   WiX installer for Windows (x64, ARM64)
packaging/                   Linux and Android packaging
eng/                         Build of the maintained SIPSorcery derivative from pinned source
artifacts/
  peeronq/                   Website and offline web prototype (React 19, Vite, Tailwind CSS 4)
  peeronq-admin/             Admin dashboard
  peeronq-portal/            Account portal
  api-server/                Minimal Express API
lib/                         OpenAPI specification and generated TypeScript clients
scripts/                     Build, release, quality and operations scripts
docs/                        Architecture, security, deployment and operations documentation
```

## Getting started

### Prerequisites

- Windows 11 or a supported Windows 10 release to build and run the Windows client.
- [.NET SDK 10.0.402](https://dotnet.microsoft.com/download/dotnet/10.0) (pinned in `global.json`).
- [Node.js 24](https://nodejs.org/) and pnpm 10.33.0 through Corepack. The workspace accepts only
  pnpm; `npm install` and `yarn` are rejected.
- Docker with Linux containers for the signaling, TURN and control-plane stacks.
- Visual Studio Build Tools and the Windows SDK to build installers.

### Build and test

```powershell
corepack enable
pnpm install --frozen-lockfile

# Build the maintained SIPSorcery derivative from its pinned source (needed before restore)
dotnet run --project eng/PeerOnQ.DependencyBuilder -c Release -- --output eng/sipsorcery/packages

dotnet restore PeerOnQ.slnx
dotnet build PeerOnQ.slnx -c Release --no-restore
dotnet test PeerOnQ.slnx -c Release --no-build

pnpm run typecheck
pnpm run lint
pnpm run test
pnpm run build
```

Run the website and web prototype locally:

```powershell
$env:PORT = '5555'
$env:BASE_PATH = '/'
pnpm --filter @workspace/peeronq run dev
```

The [local development guide](docs/LOCAL_DEVELOPMENT.md) covers the local signaling and TURN
stack, the complete control-plane workspace, testing on two Windows computers on a trusted LAN, and
the frontend environment variables.

### Windows installers

Development installers are built with `scripts/windows/build-phase5-development.ps1`. The
public-pilot builder produces an unsigned, test-only MSI whose updater trusts only the compiled
manifest key and publisher fingerprint. Supplying all three update-trust values enables verified
updates; omitting all three produces an update-disabled build:

```powershell
.\scripts\windows\build-peeronq-public-pilot.ps1 `
  -Architectures x64 `
  -UpdateChannel beta `
  -UpdatePublicKeySpkiBase64 $env:PEERONQ_UPDATE_PUBLIC_KEY_SPKI `
  -UpdateKeyId $env:PEERONQ_UPDATE_KEY_ID `
  -PublisherCertificateSha256 $env:PEERONQ_PUBLISHER_CERTIFICATE_SHA256 `
  -IUnderstandThisIsNotProductionSigned
```

Installer behavior, upgrades and enterprise deployment are described in
[Installer](docs/INSTALLER.md) and [Enterprise deployment](docs/ENTERPRISE_DEPLOYMENT.md).

### Changing the API contract

Edit `lib/api-spec/openapi.yaml`, then regenerate the clients. Do not edit generated files by hand.

```bash
pnpm --filter @workspace/api-spec run codegen
```

## Deployment

The self-extracting Linux server bundle (`peeronq-server-<version>.run`) installs and upgrades the
complete server. Docker Engine with Compose, public DNS and firewall or NAT rules are host
prerequisites. See the [Deployment guide](docs/DEPLOYMENT.md) for the exact commands.

To run only the signaling and TURN stack, start from `.env.production.example`, keep the populated
file outside the repository, and validate the configuration before starting it:

```bash
cd src/PeerOnQ.Realtime.Deployment
docker compose --env-file /secure/peeronq.env -f docker-compose.production.yml config
docker compose --env-file /secure/peeronq.env -f docker-compose.production.yml up -d --build
```

Day-to-day operation is covered by [Operations](docs/OPERATIONS.md), [Runbooks](docs/RUNBOOKS.md),
[High availability and disaster recovery](docs/HA_AND_DISASTER_RECOVERY.md) and the
[realtime deployment notes](src/PeerOnQ.Realtime.Deployment/README.md).

## Security

Please report vulnerabilities privately as described in [SECURITY.md](SECURITY.md). Do not open
public issues for security problems.

- [Security model](docs/SECURITY.md)
- [Threat model](docs/THREAT_MODEL.md)
- [Secure transport](docs/SECURE_TRANSPORT.md)
- [Update security](docs/UPDATE_SECURITY.md)
- [Privacy](docs/PRIVACY.md)

## Code signing

Windows release installers are built by GitHub Actions from a tagged commit
([release-windows.yml](.github/workflows/release-windows.yml)) and signed through SignPath: free code
signing provided by SignPath.io, certificate by SignPath Foundation. Release signing needs manual
approval. The [code signing policy](CODE_SIGNING_POLICY.md) lists what is signed, the team roles and
what the app sends over the network. The signing program application is pending until the
SIPSorcery license restriction described in [docs/signing/APPLY.md](docs/signing/APPLY.md) is
resolved. Local and test builds are unsigned.

## Documentation

| Topic | Documents |
| --- | --- |
| Overview | [Architecture](docs/ARCHITECTURE.md), [Platform capabilities](docs/CROSS_PLATFORM_CAPABILITIES.md), [Known limitations](docs/KNOWN_LIMITATIONS.md) |
| Development | [Local development](docs/LOCAL_DEVELOPMENT.md), [Performance](docs/PERFORMANCE.md), [Protocol compliance](docs/PROTOCOL_COMPLIANCE.md) |
| Security and privacy | [Security](docs/SECURITY.md), [Threat model](docs/THREAT_MODEL.md), [Privacy](docs/PRIVACY.md), [Data classification](docs/DATA_CLASSIFICATION.md), [Data retention](docs/DATA_RETENTION.md) |
| Releases | [Installer](docs/INSTALLER.md), [Update security](docs/UPDATE_SECURITY.md), [Release checklist](docs/RELEASE_CHECKLIST.md), [Release notes](src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md), [Changelog](CHANGELOG.md) |
| Operations | [Deployment](docs/DEPLOYMENT.md), [Operations](docs/OPERATIONS.md), [Runbooks](docs/RUNBOOKS.md), [Incident response](docs/INCIDENT_RESPONSE.md), [Support diagnostics](docs/SUPPORT_DIAGNOSTICS.md) |
| Web | [Website and prototype](artifacts/peeronq/README.md), [Frontend architecture](artifacts/peeronq/FRONTEND_ARCHITECTURE.md), [Design system](artifacts/peeronq/DESIGN_SYSTEM.md), [Accessibility](artifacts/peeronq/ACCESSIBILITY.md) |
| Licensing | [Third-party notices](THIRD_PARTY_NOTICES.md), [Dependencies](DEPENDENCIES.md), [License audit](docs/THIRD_PARTY_LICENSE_AUDIT.md) |

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening an issue or a
pull request. Everyone taking part in the project is expected to follow the
[Code of conduct](CODE_OF_CONDUCT.md).

## License

PeerOnQ is released under the [MIT License](LICENSE). Third-party components remain under their
own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Some bundled dependencies carry
additional terms, in particular the SIPSorcery license's geographic use restriction, which is
documented there.
