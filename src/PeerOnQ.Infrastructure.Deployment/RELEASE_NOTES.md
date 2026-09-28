# PeerOnQ server and clients 0.9.76 - release candidate

The Windows packages retain the unsigned-public-pilot classification. The server bundle has no
detached GPG signature. Checksums establish integrity, not production signing approval.
Physical-device/WAN performance and release-signing gates remain open.

## Package contents and version truth

| Surface | Version / status |
| --- | --- |
| Full Linux x86_64 server upgrade | 0.9.76 |
| Embedded Windows x64 MSI | 0.9.76; unsigned public pilot |
| Matching Windows ARM64 MSI | 0.9.76; separate local website package |
| Linux / Android / Apple | Matching source version only; no newly published packages |

Directory.Build.props remains the single server/client version source. Android/Apple source codes
advance to 9076. Native behavior/protocols are unchanged; matching Windows packages only advance
the shared version stamp. Installing the server updates offered downloads, not installed clients.
Earlier immutable artifacts remain unchanged. Matching MSI checksums accompany the package pair.

## Website change

- Remove the prominent amber pilot warning and the testing-only sentence from the download hero.
- Keep the existing unsigned classification and factual digital-signature status in a collapsed,
  keyboard-operable native Installer details disclosure.
- Keep the selected platform/architecture, canonical version, download link, integrity checks,
  publication gates and actual installer signing status unchanged.
- Successful full-server activation makes this bundled website authoritative and deactivates
  any older website overlay through the existing validated installer transaction.

## Preserved behavior and boundaries

- Existing production/staging Cloud API SMTP egress via customer-mail-egress. Control and
  observability networks stay internal; database/cache/application ports are not published.
- Real customer registration, password recovery, verification and login routes, with host-only
  __Host- cookies, CSRF, rate limits, account/organization authorization and current MFA settings.
- Production customer recovery, separate email verification and subsequent sign-in were confirmed
  by the operator for one account on 2026-09-28. This is not a fresh test of this package on the host.
- Password reset does not mark an account's email verified. Use the separate verification email.
- Admin identity and mandatory privileged-role MFA remain separate from customer Portal identity.
- Public portless Portal, operator CIDRs, unknown-host rejection and TLS/security headers.
- Prior installer Node/version/cache/header-parsing fixes and validated embedded Windows downloads.
- No native behavior, physical sharpness/4K/latency or remote-control protocol change is included.

## Existing production configuration

Preserve /etc/peeronq/peeronq.env and the existing Resend SMTP API key/sender. Run the normal upgrade
without --bootstrap, --disable-customer-mail or registration/MFA overrides. Do not replace working
values with placeholders. This update does not require new DNS, firewall, SMTP or Admin credentials.
The initial Admin onboarding record, if retained, is /etc/peeronq/admin-onboarding.txt (root-only).
Use its Email/Password plus an authenticator code or unused recovery code; never share that file.

## Validation and limits

See AI_CHANGELOG.md and docs/CURRENT_STATE.md for the actual completed checks and exact hashes.
Release validation covers the website regression/build, canonical paired client packaging,
embedded MSI integrity, exact server payload, merged Compose and installer/ingress contracts.
No production installation, real-browser visual acceptance or physical-device test is claimed.
Existing media timing failures remain outside this UI/package change; the full media suite was
not rerun and its thresholds were not changed.

## Operator install command

Copy peeronq-server-0.9.76.run to /home/peeronq. First verify its exact published SHA-256 using the
inline handoff checksum or its matching .run.sha256 sidecar if copied. Then run in Linux Bash:

```bash
(
set -e
cd /home/peeronq
chmod 700 peeronq-server-0.9.76.run
sudo ./peeronq-server-0.9.76.run --env-file /etc/peeronq/peeronq.env --dry-run
sudo ./peeronq-server-0.9.76.run --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.76.run --status --env-file /etc/peeronq/peeronq.env
)
```

After activation, reload the public website with Ctrl+F5. Confirm the amber notice is gone and
Installer details opens; confirm the offered Windows package version is 0.9.76. Existing customer
and Admin accounts remain unchanged. Test their normal sign-in paths without changing mail policy.
If the server cannot reach its own public IP, retain TLS verification with this local probe:

```bash
curl --noproxy '*' --connect-timeout 5 --max-time 15 \
  --resolve portal.peeronq.com:443:127.0.0.1 \
  --fail --silent --show-error https://portal.peeronq.com/portal/v1/auth/capabilities
```

The existing portal.peeronq.com DNS must continue to resolve to the HTTPS ingress with valid TLS
and reachable TCP 443. No public 8443 port is required. Source code does not alter public DNS.

## Rollback

Use the retained verified server release with --rollback --env-file /etc/peeronq/peeronq.env.
Rollback to 0.9.75 restores the old website notice while retaining its SMTP egress fix. Preserve
all data volumes, forward migrations and protected SMTP configuration. Server rollback does not
downgrade installed clients. Do not re-bootstrap the existing installation.
