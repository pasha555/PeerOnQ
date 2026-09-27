# PeerOnQ server and clients 0.9.71 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate has no detached server GPG signature and
contains an unsigned public-pilot Windows client. Previously observed WebRTC CI latency failures
remain unresolved. A checksum establishes byte integrity, not authenticity or production readiness.

## Package contents

| Surface | Version / status |
| --- | --- |
| Full server upgrade | 0.9.71; Linux x86_64 deployment bundle |
| Embedded Windows x64 client | 0.9.71; unsigned public pilot |
| Matching Windows ARM64 client | 0.9.71; separate local website MSI, not embedded in the server |
| Linux / Android / Apple clients | Source previews; no newly published packages |

This supersedes 0.9.70. That release installed successfully, but its public website compiled Portal
and Sign in links with `:8443` inherited from development. Direct HTTPS on port 443 worked while
the navigation links timed out. This new candidate has not been deployed to that server. Older
immutable artifacts retain their original bytes.

## Server, public website and customer portal

- Staging/production explicitly build public Portal and Sign in links as `https://portal.peeronq.com`,
  without a development port. The inherited download URL is corrected in the same build contract.
- Adds a regression that renders the actual Compose inheritance/override model. It rejects the
  original `:8443` bug, tests omitted/8443/443 bind settings, verifies private port boundaries and
  preserves the explicit localhost development port. GitHub Quality runs it before ingress tests.
- The real customer portal and authentication/API implementation are retained. Same-origin
  `/portal/v1/*`, host-only secure cookies, CSRF, registration modes and rate limits are unchanged.
  Admin identity stays separate; Admin, Grafana and Prometheus remain operator-only.
- Retains the Node 24.21.0 build fix and 0.9.70 installer header-parsing fix, including no-store,
  full download hashes, frozen dependencies, strict engines, package-age and TLS validation.

## Client changes: Windows 0.9.70 to 0.9.71 is a version-only rebuild

- Fresh Windows x64 and ARM64 packages share the canonical Directory.Build.props version with the
  server. Android/Apple source bundle codes advance to 9071; their publication gates remain separate.
- Native features, permissions, protocols, accountless LAN use, account portal discovery and public
  endpoint metadata are unchanged. Customer login is still separate from device enrollment.
- Clients remain unsigned public pilots; Verified Updates remains unconfigured. Installing the server
  updates its offered download, not clients already installed on devices.

Windows x64 MSI SHA-256:
`1528dbbd357d1d2d6cb4800c233c0be95f87879ac04363d02bdc77fe956cb4ca`

Matching Windows ARM64 MSI SHA-256:
`9eea7ed7639fc9c89d7f1e2468dda2d4965448a22e7a395415ec7a0b6b09feb6`

## Validation and remaining gates

- The merged Compose regression reproduced the old portal port bug and passed with the correction.
- Linux bootstrap/Compose contracts and Nginx ingress checks passed in the production-pinned image:
  trusted local TLS, public portal and same-origin API, private operator surfaces, read-only
  Prometheus, denied public metrics and rejected unknown hosts. These are local fixture tests.
- All 21 deployment tests, 12 customer security tests, 86 public-site tests, 47 portal tests,
  both frontend typechecks, portal build, workflow formatting, native UI and version guards passed.
- Fresh self-contained Windows x64/ARM64 builds, MSI ProductVersion and extracted-payload checks
  passed. Both packages are published to the local website, whose restarted preview selects
  0.9.71 unsigned-public-pilot and returns 200 for both package HEAD requests.
- Direct live HTTPS returned portal 200 and anonymous account profile 401 with valid TLS. Real-account
  login and browser interaction were not tested: no browser connection was available. Frontend tests
  use mocked API responses; they do not establish successful production account authentication.
- Full .NET, Admin frontend and transport-performance suites were not repeated. Previous WebRTC CI
  latency gate failures are not fixed here. No production installation of this candidate, database
  migration, device installation or physical 4K/latency validation was performed.
- Production approval still requires resolving release gates and approved client/server signatures.
  Do not bypass Admin signature validation or relabel pilot artifacts as production-signed.

## Operator action

Copy the new `.run` file to `/home/peeronq`, verify the published checksum, then use its existing
`--env-file /etc/peeronq/peeronq.env --dry-run`, install and `--status` commands. Reuse the existing
configuration and operator CIDR; do not bootstrap again or open public 8443. The installer rebuilds
the website with corrected URLs. Open `https://portal.peeronq.com` and verify a real account sign-in.
Registration still follows the configured Closed/InvitationOnly/Open and mail requirements.

DNS requirement: `portal.peeronq.com` must resolve to the existing public HTTPS ingress; its TLS
certificate must cover that host. Source changes do not modify DNS, NAT or the live server.

## Rollback

Use the existing retained, verified server release through the approved rollback process. Preserve
all data volumes and backups; database migrations remain forward-only. The installer retains its
rollback behavior if publication checks fail. Server rollback does not downgrade installed clients.
