# PeerOnQ server and clients 0.9.69 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate has no detached server GPG signature and
contains an unsigned public-pilot Windows client. Previously observed WebRTC CI latency failures
remain unresolved. A checksum establishes byte integrity, not authenticity or production readiness.

## Package contents

| Surface | Version / status |
| --- | --- |
| Full server upgrade | 0.9.69; Linux x86_64 deployment bundle |
| Embedded Windows x64 client | 0.9.69; unsigned public pilot |
| Matching Windows ARM64 client | 0.9.69; separate local website MSI, not embedded in the server |
| Linux / Android / Apple clients | Source previews; no newly published packages |

This supersedes candidate 0.9.68. The operator's installation log showed ERR_PNPM_UNSUPPORTED_ENGINE
while building Admin/Portal: Node 24.4.1 did not satisfy jsdom 30.0.1's Node 24.15.0 minimum. The
installer did not activate 0.9.68; the supplied status still pointed to server 0.6.28.
Server and rebuilt clients share the canonical Directory.Build.props version. Older artifacts
retain their original bytes. This candidate has not been deployed to that production server.

## Server, public website and customer portal

- Fixes the reported deployment build failure by pinning all three web builders to official Node
  24.21.0 images by digest, retaining their existing Debian/Alpine variants. The root engine range
  is now >=24.15.0 <25. Strict engine checks, frozen dependencies and minimum package age stay enabled.
- Adds three Docker-version regression cases and a GitHub Quality matrix that builds the actual
  public website, Admin and Portal images, covering the gap in host-only frontend validation.
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

## Client changes: Windows 0.9.68 to 0.9.69 is a version-only rebuild

- Rebuilds Windows x64 and ARM64 installers as 0.9.69; no native feature or protocol change in this
  release. Linux/Android/Apple source versions derive from the same value; Android/Apple codes
  advance to 9069. Their physical-device and publication gates remain separate and unfulfilled.
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
`06ffcbe0cab04e7c3c9ccdc1b964bc4617fbbb707c51750e6bacd1bf1aad3848`

Matching Windows ARM64 MSI SHA-256:
`f08b557895aea8d48f4c06a5dbecb3fbe8dd1863a114c2a0cbec604b57aadd90`

## Validation and remaining gates

- All three Node regression cases failed on 24.4.1 before the fix; all 21 deployment tests passed
  afterward. All three complete Docker image builds passed with frozen dependencies. Each image
  started in an isolated container, passed HTTP /health/live and ran as the non-root nginx user.
- Canonical Windows/Linux/Android/Apple version checks, server publication guard, native UI guard,
  169 frontend tests (86 website, 36 Admin, 47 Portal), workspace typecheck and workflow formatting
  passed. The deployment test project also built with strict static analysis and no warnings/errors.
- Fresh self-contained Windows x64/ARM64 builds and MSI administrative extraction/payload SHA-256
  validation passed. Both MSI versions are 0.9.69 and app assemblies are 0.9.69.0. The restarted
  local website selects the matching unsigned-public-pilot pair; both full HTTP downloads returned
  200 and matched SHA256SUMS.txt. Repository secret scanning and git diff --check passed.
- Prior GitHub Quality run 36335866350: web and secret-history passed; Windows had 873 passed, 2 failed,
  5 skipped. The two WebRTC input tests measured baseline p95 37.4/37.2 ms against the unchanged
  35 ms gate. This Docker build fix does not resolve those failures. The complete .NET and transport
  performance suites were not rerun for this packaging change.
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
