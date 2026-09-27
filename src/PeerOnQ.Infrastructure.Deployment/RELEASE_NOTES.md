# PeerOnQ server and clients 0.9.72 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate has no detached server GPG signature and
contains an unsigned public-pilot Windows client. Physical-device/WAN performance is unverified.
A checksum establishes byte integrity, not authenticity or production readiness.

## Package contents

| Surface | Version / status |
| --- | --- |
| Full server upgrade | 0.9.72; Linux x86_64 deployment bundle |
| Embedded Windows x64 client | 0.9.72; unsigned public pilot |
| Matching Windows ARM64 client | 0.9.72; separate local website MSI, not embedded in the server |
| Linux / Android / Apple clients | Source previews; no newly published packages |

This cumulative patch includes 0.9.71's portal navigation fix and a separate native fix for stale
latency measurements repeatedly degrading a quiet desktop. Install it directly over the retained
0.9.70 release; no intermediate 0.9.71 installation is needed. This candidate has not been deployed
to that server. Older immutable artifacts retain their original bytes.

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

## Client changes: long-session quality recovery

- Frame/render latency percentiles previously kept the last 120 samples indefinitely. Periodic
  feedback could therefore repeatedly report one old delay as current pressure, lowering a quiet
  desktop to the minimum quality rung. Sparse input samples could also keep earlier input spikes.
- Each latency sample now expires after five seconds, still bounded to 120 samples. Old samples
  expire individually even when a few fresh frames or clicks arrive. Session totals/dimensions stay
  intact; real fresh congestion and the existing healthy-network recovery requirements are retained.
- Update both endpoint clients to 0.9.72. A server-only installation cannot replace their running
  media code. Protocols, encryption, permissions and accountless LAN use are unchanged.
- Windows x64/ARM64 and server share the canonical version. Android/Apple source codes advance to
  9072; non-Windows publication gates remain separate. No new non-Windows binary is published.
- Clients remain unsigned public pilots; Verified Updates remains unconfigured. Installing the server
  updates its offered download, not clients already installed on devices.

Windows x64 MSI SHA-256:
`7353746e062a1dd116128ae40594ca02097fdeb3d0d96d01ebdd4bc60aede3d0`

Matching Windows ARM64 MSI SHA-256:
`1680c5ff92ce99b9115975d0a35e4bd2410bc858745443c90bb2c7d4b3affb3e`

## Validation and remaining gates

- Three new reproductions failed before the media fix: stale quiet-desktop latency, sparse-sample
  recovery and a simulated session stuck at quality rung 4. The corrected full Media suite passed
  143 tests; one existing live-TURN test was skipped because that external fixture was unavailable.
  The additional sustained-delay case confirms fresh slow frames still lower quality.
- The merged Compose regression reproduced the old portal port bug and passed with the correction.
- Linux bootstrap/Compose contracts and Nginx ingress checks passed in the production-pinned image:
  trusted local TLS, public portal and same-origin API, private operator surfaces, read-only
  Prometheus, denied public metrics and rejected unknown hosts. These are local fixture tests.
- All 21 deployment tests, 12 customer security tests, 86 public-site tests, 47 portal tests,
  both frontend typechecks, portal build, workflow formatting, native UI and version guards passed.
- Fresh 0.9.72 x64/ARM64 builds, MSI ProductVersion and extracted-payload checks passed. The local
  website restarted and selected the matching unsigned-public-pilot pair; both full HTTP downloads
  returned 200 with matching checksums. Both app assemblies report 0.9.72.0.
- The preceding portal commit's GitHub Quality run passed ingress, web images/workspace and secret
  checks, but failed two existing input-latency tests at 36.9/37.1 ms against the unchanged 35 ms
  gate. Local Media passed as recorded above; remote CI and physical performance remain separate gates.
- Direct live HTTPS returned portal 200 and anonymous account profile 401 with valid TLS. Real-account
  login and browser interaction were not tested: no browser connection was available. Frontend tests
  use mocked API responses; they do not establish successful production account authentication.
- Full solution and separate Transport suites were not repeated for this change. No production
  installation of this candidate, database migration, device installation or physical 4K/latency
  validation was performed. Do not infer zero latency or fixed image quality on an impaired link.
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
