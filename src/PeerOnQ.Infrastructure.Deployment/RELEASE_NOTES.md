# PeerOnQ server and clients 0.9.70 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate has no detached server GPG signature and
contains an unsigned public-pilot Windows client. Previously observed WebRTC CI latency failures
remain unresolved. A checksum establishes byte integrity, not authenticity or production readiness.

## Package contents

| Surface | Version / status |
| --- | --- |
| Full server upgrade | 0.9.70; Linux x86_64 deployment bundle |
| Embedded Windows x64 client | 0.9.70; unsigned public pilot |
| Matching Windows ARM64 client | 0.9.70; separate local website MSI, not embedded in the server |
| Linux / Android / Apple clients | Source previews; no newly published packages |

This supersedes candidate 0.9.69. The operator's log showed healthy services followed by
`Public Windows client response is cacheable.` during `verify Windows client proxy streaming`.
The installer rejected activation and entered its existing rollback flow. This new candidate has
not been deployed to that server. Older immutable artifacts retain their original bytes.

## Server, public website and customer portal

- Corrects one shell-quoting error in the installer's nested proxy command. The old CR filter
  deleted literal r characters, turning Cache-Control into an unrecognized header name. The
  corrected filter preserves the name and removes CR as intended.
- Keeps the no-store, complete download hash, embedded version, TLS certificate and current website
  checks mandatory. No proxy cache policy was weakened and no failed release is manually activated.
- Adds regression coverage executing the actual nested command: valid headers pass, while missing
  or cacheable headers fail. The complete publication function is also exercised through the real
  pinned Nginx TLS fixture. Both Linux validation suites now run in GitHub Quality.
- Retains the Node 24.21.0 Docker build fix, frozen dependencies, strict engines and package-age policy.
- Public website and customer portal behavior is unchanged. Customer/Admin identities, host-only
  secure cookies, CSRF, registration modes, rate limits and operator-only hosts remain unchanged.

## Client changes: Windows 0.9.69 to 0.9.70 is a version-only rebuild

- Fresh Windows x64 and ARM64 packages share the canonical Directory.Build.props version with the
  server. Android/Apple source bundle codes advance to 9070; their publication gates remain separate.
- Native features, permissions, protocols, accountless LAN use, account portal discovery and public
  endpoint metadata are unchanged. Customer login is still separate from device enrollment.
- Clients remain unsigned public pilots; Verified Updates remains unconfigured. Installing the server
  updates its offered download, not clients already installed on devices.

Windows x64 MSI SHA-256:
`d72f617c028ee3b435f3106fa6ce8a06420ed50c1a123f46655752a0a696a2bb`

Matching Windows ARM64 MSI SHA-256:
`c0c1b9197c565b741410d36b4b2dfd19b9157190381b609e32445819173ca47e`

## Validation and remaining gates

- The original installer reproduced the reported cache-policy error in both the shell regression
  and the real TLS proxy fixture; the corrected code passed. Missing/cacheable policies stay rejected.
- Linux bootstrap/Compose contracts and Nginx ingress checks passed in the production-pinned image:
  trusted local TLS, public portal and same-origin API, private operator surfaces, read-only
  Prometheus, denied public metrics and rejected unknown hosts. These are local fixture tests.
- All 21 deployment configuration tests, 86 public-site tests, public-site typecheck, workflow
  formatting, native UI guard and Windows/Linux/Android/Apple/server version guards passed.
- Fresh self-contained x64/ARM64 builds and MSI extraction/payload validation passed. MSI versions
  are 0.9.70 and app assemblies are 0.9.70.0. The local website restarted and selected the matching
  unsigned-public-pilot pair; both complete HTTP downloads returned 200 with matching SHA-256.
- The Git Bash contract attempt stopped in the existing TLS fixture; the complete contract passed
  in Linux. Full .NET, Admin/Portal frontend and transport-performance suites were not repeated.
- Previous WebRTC CI latency gate failures are not fixed here. No production DNS/TLS/reachability,
  database migration, device installation or physical 4K/latency validation was performed.
- Production approval still requires resolving release gates and approved client/server signatures.
  Do not bypass Admin signature validation or relabel pilot artifacts as production-signed.

## Rollback

Use the existing retained, verified server release through the approved rollback process. Preserve
all data volumes and backups; database migrations remain forward-only. The installer retains its
rollback behavior if publication checks fail. Server rollback does not downgrade installed clients.
