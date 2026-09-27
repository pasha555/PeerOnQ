# Privacy policy draft

This draft describes the implemented PeerOnQ desktop behavior. It must be reviewed against the legal
entity, distribution countries, support process, and public website before broad release.

## Data processed

PeerOnQ processes the screen frames and user-approved clipboard/file data needed for an active remote
session. Media and collaboration content travel over WebRTC and are not intentionally stored by the
signaling service. TURN may relay encrypted packets but cannot decrypt WebRTC content.

The desktop stores device identity material, trusted-device/unattended settings, address-book entries,
transfer state, application logs, and security audit records locally. Device/private integrity keys
are protected with Windows DPAPI for the signed-in user. Security audit identifiers are masked.

Rolling logs are structured JSON with bounded file size/count. They retain message templates and
sanitized structured properties, not rendered secret values. Sensitive property names are redacted;
IDs, IP addresses, Windows paths, JWT-like tokens, and key/value secrets are sanitized. Exceptions are
recorded only by type, without message or stack content.

## Crash reporting

Crash reporting is disabled until the user opts in. The implemented local report contains UTC time,
application version, process architecture, OS description, exception type, and a one-way error ID. It
does not contain screen frames, clipboard content, exception messages, stack traces, file names, user
documents, passwords, tokens, device secrets, or full device ID. No upload endpoint is implemented;
the user chooses whether to share a local report with support.

## User control

The settings UI exposes crash consent, audit retention, sanitized audit export, confirmed audit clear,
and the exact local-data folder. Users can delete local crash reports and diagnostics from
`%LOCALAPPDATA%\PeerOnQ`. Uninstall
preserves local data to prevent accidental identity/audit loss; removal instructions are in
[DATA_RETENTION.md](DATA_RETENTION.md).

## Server data

Signaling stores only live connection/session ownership and durable device public-key pins required to
prevent identity substitution. Resume tokens and TURN credentials are short-lived. Metrics contain
counts/durations/status categories, not tokens, addresses, screen/file/clipboard content, or device
identifiers. Backup scope is documented in the deployment guide.

The optional self-hosted customer portal stores account, revocable session, organization/team,
policy, protected invitation/recovery, claimed-device metadata, append-only security event and
privacy-request records. It does not store remote screen, input, clipboard or file content. The exact
fields, access purpose and lifecycle are in
[ACCOUNT_DATA_INVENTORY.md](ACCOUNT_DATA_INVENTORY.md). Customer portal telemetry is not sent to an
external SaaS provider.

## Requests

Local-only data can be exported or deleted by the user without contacting PeerOnQ. The self-hosted
portal accepts explicit account export/delete requests; deletion fails until owned organizations are
transferred/deleted and immediately revokes account sessions when accepted. The self-hosted operator
must publish its legal identity, contact channel, lawful basis, retention, regions/subprocessors, and
request-completion process before enabling the portal for real people.
