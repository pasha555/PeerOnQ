# PeerOnQ server and clients 0.9.73 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate contains unsigned public-pilot Windows
clients and has no detached server GPG signature. A checksum proves byte integrity, not authenticity.
Production SMTP delivery, physical-device/WAN performance and release-signing gates remain open.

## Package contents and version truth

| Surface | Version / status |
| --- | --- |
| Full Linux x86_64 server upgrade | 0.9.73 |
| Embedded Windows x64 MSI | 0.9.73; unsigned public pilot |
| Matching Windows ARM64 MSI | 0.9.73; separate local website package |
| Linux / Android / Apple | Matching source version only; no newly published packages |

Directory.Build.props remains the canonical server/client version source. Android/Apple source
codes advance to 9073. Native behavior/protocols are unchanged in this patch; clients retain the
0.9.72 stale-latency expiry fix and accountless LAN behavior. Server installation updates offered
downloads; it does not replace already installed clients. Earlier immutable artifacts are unchanged.

Windows x64 MSI SHA-256:
`fac3f3efe2940992d5b26eede0b8803ef53fbfd7270440922a78e5a9c1bdfc50`

Windows ARM64 MSI SHA-256:
`7adf8d55caaad541c168f6f58c1eefada180f709a9f6385ff9ff2cd37119a88b`

## Customer account portal

- Reuses the real customer API, database, identity, password hashing, cookie/CSRF and mail services.
- Adds anonymous, rate-limited, no-store auth capabilities: registration mode, email recovery,
  customer MFA availability and canonical password rules. The UI follows actual server policy.
- Adds generic verification resend, a per-account cooldown, previous-token invalidation and audit.
  Invalid/expired/used verification links fail. SMTP failures expose no provider or credential data.
- Adds authenticated password change: current password + CSRF + policy/rate limits, retained current
  sign-in session, revoked other sessions/reset links, metadata-only audit. Password reset and
  refresh replay keep their existing revocation semantics.
- Customer MFA is off by default. Stored secrets/recovery codes and organization requirements are
  preserved; unavailable MFA actions reject. Organization policy exposes effective requirements
  while retaining the stored value for re-enable. Internal Admin MFA is unchanged.
- Registration/reset/change-password share visible password rules, confirmation and show/hide.
  Security remains useful with MFA off; overview, account/organization devices and separate sign-in
  versus remote-session history remain real existing API screens. Expired access cookies use one
  refresh attempt shared by concurrent requests; failed refresh returns to sign-in.
- No self-service device claim UI: native enrollment does not expose a safe, user-consumable,
  short-lived ownership proof. The existing device access token must not be pasted into a portal.

## Installer and production configuration

- Adds --customer-registration-mode Closed|InvitationOnly|Open and customer MFA enable/disable flags.
  Upgrades preserve valid modes and reject unknown/empty values. Missing mode defaults Closed;
  missing customer MFA defaults false. Disabling mail cannot silently close an open installation.
- Open/InvitationOnly outside Development/Testing require verified email and real SMTP. Production
  FileSink remains forbidden. SMTP TLS remains enabled. An approved sender can be supplied through
  PEERONQ_CUSTOMER_MAIL_FROM_ADDRESS (default: peeronq@<public web host>).
- Portal/email links remain https://portal.peeronq.com with same-origin /portal/v1/* and host-only
  __Host- cookies. Public website cookies, broad CORS and native customer authentication are absent.
- Admin/Grafana/Prometheus CIDRs, private application/database/cache/telemetry ports, unknown-host
  rejection, TLS validation, HTTPS headers and Admin MFA remain unchanged.
- Retains production portless links, pinned Node build images, installer download/header checks,
  frozen dependencies, package-age protection and all prior cumulative fixes.

Before selecting Open, use sudoedit /etc/peeronq/peeronq.env to configure real operator values:

```dotenv
PEERONQ_CUSTOMER_REGISTRATION_MODE=Open
PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION=true
PEERONQ_CUSTOMER_MFA_ENABLED=false
PEERONQ_CUSTOMER_MAIL_PROVIDER=Smtp
PEERONQ_CUSTOMER_SMTP_HOST=<real-host>
PEERONQ_CUSTOMER_SMTP_PORT=587
PEERONQ_CUSTOMER_SMTP_USERNAME=<operator-value>
PEERONQ_CUSTOMER_SMTP_PASSWORD=<operator-secret>
PEERONQ_CUSTOMER_MAIL_FROM_ADDRESS=<approved-sender>
```

Keep the file root-protected. No SMTP account is provisioned by the installer. No real SMTP value
or test mailbox was supplied for this task, so LIVE EMAIL TEST = BLOCKED.

## Validation and limits

Validation evidence and exact commands are recorded in AI_CHANGELOG.md and docs/CURRENT_STATE.md.
Local FileSink/HTTPS tests are not live SMTP proof. Existing media input-latency gates still fail in
isolated execution; no thresholds or native code were changed to hide those failures. No physical
Windows/ARM64, 4K/WAN session, production installation, or browser visual acceptance is claimed.

## Operator install command

Copy peeronq-server-0.9.73.run into /home/peeronq and verify the exact published SHA-256. If the
sidecar was copied, use sha256sum -c peeronq-server-0.9.73.run.sha256; otherwise use the handoff hash.
After configuring actual SMTP, run each command only if the preceding one succeeds:

```bash
cd /home/peeronq
chmod 700 peeronq-server-0.9.73.run
sudo ./peeronq-server-0.9.73.run --env-file /etc/peeronq/peeronq.env --customer-registration-mode Open --disable-customer-mfa --dry-run
sudo ./peeronq-server-0.9.73.run --env-file /etc/peeronq/peeronq.env --customer-registration-mode Open --disable-customer-mfa
sudo ./peeronq-server-0.9.73.run --status --env-file /etc/peeronq/peeronq.env
```

DNS operator requirement: portal.peeronq.com must resolve to the production HTTPS ingress with
valid TLS and reachable TCP 443. Do not expose port 8443 publicly. Source code does not change DNS.
Then perform real approved-mailbox registration, verification, login, overview, logout and reset
acceptance. Do not call this candidate a completed production deployment before those checks.

## Rollback

Use the retained verified server release through --rollback --env-file /etc/peeronq/peeronq.env.
Restore prior protected registration/MFA/mail settings separately when reverting policy. Preserve
all data volumes and forward migrations. Server rollback does not downgrade installed clients.
