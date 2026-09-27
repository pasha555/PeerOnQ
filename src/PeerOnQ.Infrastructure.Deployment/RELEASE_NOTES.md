# PeerOnQ server and clients 0.9.74 - release candidate

Status: NOT APPROVED FOR PRODUCTION. This candidate contains unsigned public-pilot Windows
clients and has no detached server GPG signature. A checksum proves byte integrity, not authenticity.
Production SMTP delivery, physical-device/WAN performance and release-signing gates remain open.

## Package contents and version truth

| Surface | Version / status |
| --- | --- |
| Full Linux x86_64 server upgrade | 0.9.74 |
| Embedded Windows x64 MSI | 0.9.74; unsigned public pilot |
| Matching Windows ARM64 MSI | 0.9.74; separate local website package |
| Linux / Android / Apple | Matching source version only; no newly published packages |

Directory.Build.props remains the canonical server/client version source. Android/Apple source
codes advance to 9074. Native behavior/protocols are unchanged in this patch; clients retain the
0.9.72 stale-latency expiry fix and accountless LAN behavior. Server installation updates offered
downloads; it does not replace already installed clients. Earlier immutable artifacts are unchanged.

Windows x64 MSI SHA-256:
`9a9a16e062d818433f8ca376f290da18c996d26b8fdbb5c9c938d23d5daa2e14`

Windows ARM64 MSI SHA-256:
`477d090835db532efa49c3ee2c5da252156df0e467a481196545b5453f9a6d19`

## Auth navigation fixed in this patch

- Sign in remains at `/`. Real links open `/register`, `/forgot-password` and
  `/resend-verification`; direct entry and browser history select the corresponding form.
- Successful registration shows verification guidance. Recovery/resend stay on their own route
  with generic success text and an explicit Back to sign in link. No account-existence response
  is introduced for recovery/resend.
- First fields and success headings receive focus. Navigation links are separate from form
  submission; late requests cannot replace the result on another auth route.
- Existing capabilities/password rules/invitation tokens govern availability, including direct
  route entry. `/verify-email` and `/reset-password` retain their real backend operations.
- Existing register/login/reset/resend endpoints, cookie/CSRF/rate-limit behavior and MFA policies
  are unchanged. Client packages advance only their shared version; no native behavior change.
- The operator reported no SMTP provider. Leave registration Closed and mail Disabled until real
  SMTP is configured. This fix cannot send mail or enable safe Open registration without it.

## Retained customer account portal capabilities

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

## Retained installer and production configuration

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

Portal typecheck/build and 82/82 tests passed; Cloud.Infrastructure 56/56 and Admin 49/49 passed.
Public-site typecheck and 86/86 tests, deployment configuration 21/21, merged Compose/bootstrap
contracts and native UI/version invariants passed. Matching x64/ARM64 packages were built and
payload-validated. Artifact validation evidence is recorded in AI_CHANGELOG.md and docs/CURRENT_STATE.md.
Local FileSink/HTTPS tests are not live SMTP proof. Existing media input-latency gates still fail in
isolated execution; no thresholds or native code were changed to hide those failures. No physical
Windows/ARM64, 4K/WAN session, production installation, or browser visual acceptance is claimed.

## Operator install command

Copy peeronq-server-0.9.74.run into /home/peeronq and verify the exact published SHA-256. If the
sidecar was copied, use sha256sum -c peeronq-server-0.9.74.run.sha256; otherwise use the handoff hash.
For the operator who has no SMTP service, run the following in Linux Bash after checksum validation.
This explicitly keeps registration Closed, mail Disabled and customer MFA off; existing account sign-in
is retained. Do not use these policy flags on a deployment that already has working SMTP/Open
registration; omit them there to preserve the existing configuration.

```bash
(
set -e
cd /home/peeronq
chmod 700 peeronq-server-0.9.74.run
sudo ./peeronq-server-0.9.74.run --env-file /etc/peeronq/peeronq.env --customer-registration-mode Closed --disable-customer-mail --disable-customer-mfa --dry-run
sudo ./peeronq-server-0.9.74.run --env-file /etc/peeronq/peeronq.env --customer-registration-mode Closed --disable-customer-mail --disable-customer-mfa
sudo ./peeronq-server-0.9.74.run --status --env-file /etc/peeronq/peeronq.env
)
```

DNS operator requirement: portal.peeronq.com must resolve to the production HTTPS ingress with
valid TLS and reachable TCP 443. Do not expose port 8443 publicly. Source code does not change DNS.
Verify portal routes and any existing approved account login/overview/logout. After SMTP setup,
perform real approved-mailbox registration, verification and reset acceptance. Do not call this candidate a completed production deployment before those checks.

## Rollback

Use the retained verified server release through --rollback --env-file /etc/peeronq/peeronq.env.
Restore prior protected registration/MFA/mail settings separately when reverting policy. Preserve
all data volumes and forward migrations. Server rollback does not downgrade installed clients.
