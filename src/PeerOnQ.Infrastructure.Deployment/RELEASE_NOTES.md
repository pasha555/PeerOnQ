# PeerOnQ server 0.6.46 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate has no detached server GPG signature and
contains an unsigned public-pilot Windows client. Two WebRTC latency checks remain failing on
GitHub. A checksum establishes byte integrity, not release authenticity or production readiness.

## Package contents

| Surface | Version / status |
| --- | --- |
| Full server upgrade | 0.6.46; Linux x86_64 deployment bundle |
| Embedded Windows x64 client | 0.9.67; unsigned public pilot |
| Matching Windows ARM64 client | 0.9.67; separate local website MSI, not embedded in the server |
| Linux / Android / Apple clients | Source previews; no newly published packages |

This supersedes the local 0.6.45 candidate, which embedded Windows 0.9.66. It does not establish
which server version is currently installed in production. This historical candidate predates the
unified release-version policy and does not satisfy it: future server patches and clients must share
the canonical Directory.Build.props version and advance together. Do not promote or rename this
mismatched candidate as a release satisfying that policy; create a new validated matching package set.

## Server, public website and customer portal

- Includes the current public website and real customer portal with consistent PeerOnQ branding,
  product terminology, GitHub/download/portal entry points and account-specific sign-in sessions.
- Customer portal production ingress is public HTTPS with same-origin /portal/v1 API routing.
  Admin, Grafana and Prometheus remain restricted operator surfaces. Customer/Admin identities,
  host-only secure cookies, CSRF, registration modes and rate limits retain their existing boundaries.
- Local website MSI downloads survive an unavailable telemetry service; telemetry failures are
  reported without terminating the website while a package is streaming.
- Build-tool security patches advance fast-uri to 3.1.6 and js-yaml to 4.3.2, resolving the five
  high audit findings. Four existing moderate findings in qs and Vitest/mocker remain documented.

## Client changes: yes, Windows 0.9.66 to 0.9.67

- Includes the optional Settings/About Open Account Portal action in the system browser, using
  the fixed HTTPS portal endpoint without transferring credentials or device tokens.
- Aligns View Only, Full Control, File Transfer, Remote Device ID and Verified Updates language;
  removes stale version presentation and binds installer version selection to the canonical source.
- LAN use remains accountless. Cloud device enrollment remains separate from customer login.
  Remote-control protocols, permissions and update trust policies are unchanged by this patch.
- This MSI retains the previous production endpoint metadata but is not Authenticode-signed.
  Verified Updates remains unconfigured. Installing the server bundle updates the offered download;
  an existing desktop client still requires its own approved MSI installation or signed update.

Windows x64 MSI SHA-256:
`f361e0efb727ae79c645460b8137d840778169b5301012a8e38dab69eeef88f7`

Matching Windows ARM64 MSI SHA-256:
`ae4b38e6cc6279ce68779f65a1591008a76ffff7a137461f6ec19dbe2fc6e4e2`

## Validation and remaining gates

- Both Windows MSI payloads and compiled 0.9.67.0 assembly versions were verified. Both local
  website package URLs returned HTTP 200 with complete byte hashes matching SHA256SUMS.txt.
- Source validation: strict Release build, 875 local .NET tests, 169 frontend tests, workspace
  typecheck/lint/build and repository secret scans passed. Five environment-dependent .NET tests
  were skipped, not passed. The local 1 GiB QUIC test measured 25.2 MiB/s and 0.4 ms input p95.
- GitHub Quality run 36335866350: web and secret-history passed; Windows had 873 passed, 2 failed,
  5 skipped. The two WebRTC input tests measured baseline p95 37.4/37.2 ms against the unchanged
  35 ms gate. Local QUIC figures do not establish physical-device or WAN latency.
- No production DNS, TLS, external reachability, database migration, device upgrade or physical
  4K/latency validation was performed for this candidate. DNS must already route portal.peeronq.com
  to the production ingress and its TLS certificate must cover that host.
- Before production approval: resolve failing release gates, obtain the approved signed Windows
  client and rebuild as a new immutable server version, then sign with the server's trusted GPG key.
  Do not bypass Admin signature validation or relabel pilot artifacts as production-signed.

## Rollback

Use the existing retained, verified server release through the approved rollback process. Preserve
database/cache volumes and backups; database migrations are forward-only and must remain compatible
with the retained release. A server rollback does not downgrade clients already installed on devices.
