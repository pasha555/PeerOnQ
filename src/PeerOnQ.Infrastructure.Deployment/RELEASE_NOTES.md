# PeerOnQ server and clients 0.9.75 - release candidate

Status: NOT APPROVED FOR PRODUCTION. Windows clients are unsigned public-pilot packages and the
server bundle has no detached GPG signature. Checksums prove integrity, not authenticity.
Live recipient delivery, physical-device/WAN performance and release-signing gates remain open.

## Package contents and version truth

| Surface | Version / status |
| --- | --- |
| Full Linux x86_64 server upgrade | 0.9.75 |
| Embedded Windows x64 MSI | 0.9.75; unsigned public pilot |
| Matching Windows ARM64 MSI | 0.9.75; separate local website package |
| Linux / Android / Apple | Matching source version only; no newly published packages |

Directory.Build.props remains the single server/client version source. Android/Apple source codes
advance to 9075. Native behavior/protocols are unchanged; matching Windows packages only advance
the shared version stamp. Installing the server updates offered downloads, not installed clients.
Earlier immutable artifacts remain unchanged. Matching MSI checksums accompany the package pair.

## SMTP connectivity fixed

- Production/staging Cloud API previously joined only internal control/observability networks,
  leaving no route to an external SMTP provider despite enabled auth capabilities.
- Only Cloud API now additionally joins customer-mail-egress, a dedicated non-internal bridge.
  Control/observability remain internal. PostgreSQL, Redis, telemetry and every other service
  retain their existing network attachments. No application/container port is published.
- The bridge supplies outbound routing; it is not a SMTP host/port firewall allowlist. Operators
  retain responsibility for their provider-specific egress/firewall rules. Development FileSink
  networking is unchanged. SMTP uses the existing TLS-validated sender and configured credentials.
- Password recovery/resend retain generic responses to avoid account enumeration. A success card
  does not prove delivery: unavailable accounts, the one-minute cooldown and SMTP errors can all
  produce an accepted response. Existing mail failures record account.mail_delivery_failed.

## Preserved behavior and boundaries

- Real auth routes at /, /register, /forgot-password, /resend-verification, /verify-email and
  /reset-password; persistent success guidance, keyboard/history/focus and capability gates.
- Existing registration modes, password rules, verified email, session rotation/revocation,
  customer MFA setting, Admin MFA, host-only __Host- cookies, CSRF and rate limits.
- Account/organization authorization, native device identity and accountless LAN access.
- Public portless portal routing, operator CIDRs, unknown-host rejection, TLS/security headers,
  private database/cache/metrics endpoints, validated embedded downloads and prior cumulative fixes.
- No physical sharpness/4K/latency acceptance or new native behavior is claimed by this patch.

## Existing Resend configuration

The operator already configured Resend. Preserve the protected SMTP API key, verified sender,
Smtp provider, current valid registration mode and required email verification on upgrade.
DO NOT pass --disable-customer-mail; that flag intentionally clears SMTP credentials/configuration.
The existing Resend connection uses smtp.resend.com:587 with STARTTLS, username resend and the
sending API key as the password. Never paste the key into chat, shell arguments or source control.
Do not replace a valid provider configuration with placeholders.

## Validation and limits

The regression originally failed because the merged model had no outbound Cloud API route.
Merged Compose guards now preserve exclusive mail-network membership, private networks/ports,
operator boundaries and development isolation. Bootstrap/version/native UI guards passed.
A local Docker before/after probe failed on internal-only networking and reached the real Resend
220 banner after adding an outbound bridge. A separate container STARTTLS probe verified the real
smtp.resend.com certificate chain and hostname on port 587. Neither probe authenticated or sent mail.
Existing deployment, Cloud and Admin tests passed; portal/public-site regression tests passed.
See AI_CHANGELOG.md and docs/CURRENT_STATE.md for exact counts and final package validation.
The production host's SMTP credentials, sender authorization and inbox delivery remain untested here.
No production installation or physical-device acceptance is claimed. Earlier media timing failures
remain outside this deployment fix; their tests/thresholds were not changed or reported as passing.

## Operator install command

Copy peeronq-server-0.9.75.run to /home/peeronq. First verify its exact published SHA-256, using the
sidecar if copied (sha256sum -c peeronq-server-0.9.75.run.sha256) or the inline handoff checksum.
These Linux Bash commands preserve the existing Resend/registration/MFA configuration:

```bash
(
set -e
cd /home/peeronq
chmod 700 peeronq-server-0.9.75.run
sudo ./peeronq-server-0.9.75.run --env-file /etc/peeronq/peeronq.env --dry-run
sudo ./peeronq-server-0.9.75.run --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.75.run --status --env-file /etc/peeronq/peeronq.env
)
```

After activation, wait at least one minute since the previous request, then request verification
or password recovery once for the existing approved account. Check Resend Emails and the actual
mailbox, including spam. SMTP acceptance and inbox delivery must be verified independently.
If the server cannot reach its own public IP, a local check retaining TLS validation is:

```bash
curl --noproxy '*' --connect-timeout 5 --max-time 15 \
  --resolve portal.peeronq.com:443:127.0.0.1 \
  --fail --silent --show-error https://portal.peeronq.com/portal/v1/auth/capabilities
```

DNS operator requirement: portal.peeronq.com must resolve to the HTTPS ingress with valid TLS and
reachable TCP 443. No public 8443 port is required. Source changes do not change DNS or host firewalls.

## Rollback

Use the retained verified server release via --rollback --env-file /etc/peeronq/peeronq.env.
Preserve all data volumes, forward migrations and protected SMTP configuration. Rolling back to
internal-only Cloud API networking can reintroduce this SMTP issue. Server rollback does not
downgrade installed clients. Never open database/cache/application ports as a workaround.
