# PeerOnQ server and clients 0.9.68 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate has no detached server GPG signature and
contains an unsigned public-pilot Windows client. Two WebRTC latency checks remain failing on
GitHub. A checksum establishes byte integrity, not release authenticity or production readiness.

## Package contents

| Surface | Version / status |
| --- | --- |
| Full server upgrade | 0.9.68; Linux x86_64 deployment bundle |
| Embedded Windows x64 client | 0.9.68; unsigned public pilot |
| Matching Windows ARM64 client | 0.9.68; separate local website MSI, not embedded in the server |
| Linux / Android / Apple clients | Source previews; no newly published packages |

This supersedes the local 0.6.46 candidate, which embedded Windows 0.9.67. The server and rebuilt
clients now share the canonical Directory.Build.props version and advance together. Older artifacts
retain their original versions and bytes. This does not establish which version is installed in
production or on a user's computer, and it does not authorize a production deployment.

## Server, public website and customer portal

- The server builder rejects a server/client version mismatch before staging. MSI validation also
  compares the package's internal ProductVersion with the release version; renaming an old MSI
  cannot satisfy the check. The server/client version invariant now runs in GitHub Quality.
- Includes the current public website and real customer portal with consistent PeerOnQ branding,
  product terminology, GitHub/download/portal entry points and account-specific sign-in sessions.
- Customer portal production ingress is public HTTPS with same-origin /portal/v1 API routing.
  Admin, Grafana and Prometheus remain restricted operator surfaces. Customer/Admin identities,
  host-only secure cookies, CSRF, registration modes and rate limits retain their existing boundaries.
- Local website MSI downloads survive an unavailable telemetry service; telemetry failures are
  reported without terminating the website while a package is streaming.
- Build-tool security patches advance fast-uri to 3.1.6 and js-yaml to 4.3.2, resolving the five
  high audit findings. Four existing moderate findings in qs and Vitest/mocker remain documented.

## Client changes: Windows 0.9.67 to 0.9.68 is a version-only rebuild

- Rebuilds Windows x64 and ARM64 installers as 0.9.68; no native feature or protocol change in this
  release. Linux/Android/Apple source versions derive from the same value; Android/Apple codes
  advance to 9068. Their physical-device and publication gates remain separate and unfulfilled.
- Retains the optional Settings/About Open Account Portal action in the system browser, using
  the fixed HTTPS portal endpoint without transferring credentials or device tokens.
- Aligns View Only, Full Control, File Transfer, Remote Device ID and Verified Updates language;
  removes stale version presentation and binds installer version selection to the canonical source.
- LAN use remains accountless. Cloud device enrollment remains separate from customer login.
  Remote-control protocols, permissions and update trust policies are unchanged by this patch.
- This MSI retains the previous production endpoint metadata but is not Authenticode-signed.
  Verified Updates remains unconfigured. Installing the server bundle updates the offered download;
  an existing desktop client still requires its own approved MSI installation or signed update.

Windows x64 MSI SHA-256:
`8dcae098f4c17dfb4490a7b94c9c6b2c91a4a07607d9fb188bd5f8ade0fcd825`

Matching Windows ARM64 MSI SHA-256:
`c6a6bd810812db4f7c241862ee3c59eac3f747ea30e62a27b8a950d128bae08f`

## Validation and remaining gates

- Server/client mismatch regression reproduced before the fix and passed afterward. The actual
  0.9.67 MSI was rejected for release 0.9.68. Mandatory MSI, signature and publication guards passed.
- Canonical Windows/Linux/Android/Apple version checks, native UI validation, 18 existing deployment
  tests, 86 public website tests, website typecheck/build and Quality workflow formatting passed.
- Both Windows MSI administrative extractions matched their fresh self-contained publish payloads
  by file path and SHA-256. Both MSI ProductVersion values are 0.9.68; compiled app versions are
  0.9.68.0. The restarted local website selects 0.9.68 unsigned-public-pilot; both complete HTTP
  downloads returned 200 and matched SHA256SUMS.txt. Repository secret scanning passed.
- Prior GitHub Quality run 36335866350: web and secret-history passed; Windows had 873 passed, 2 failed,
  5 skipped. The two WebRTC input tests measured baseline p95 37.4/37.2 ms against the unchanged
  35 ms gate. This version-alignment change does not resolve those failures. The complete .NET,
  Admin/portal and transport performance suites were not rerun for this packaging-only change.
- No production DNS, TLS, external reachability, database migration, device upgrade or physical
  4K/latency validation was performed for this candidate. DNS must already route portal.peeronq.com
  to the production ingress and its TLS certificate must cover that host.
- Before production approval: resolve failing release gates, obtain the approved signed Windows
  client and rebuild a new immutable matching server/client release, then sign with the server's
  trusted GPG key.
  Do not bypass Admin signature validation or relabel pilot artifacts as production-signed.

## Rollback

Use the existing retained, verified server release through the approved rollback process. Preserve
database/cache volumes and backups; database migrations are forward-only and must remain compatible
with the retained release. A server rollback does not downgrade clients already installed on devices.
