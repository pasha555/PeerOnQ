# PeerOnQ privacy architecture

## Scope and principles

The Phase 6 control plane processes operational metadata needed to register installations, establish
presence, diagnose failures, distribute signed releases, and administer the service. It does not
collect remote screen frames, clipboard data, keyboard or mouse input, filenames, transferred-file
contents, user documents, passwords, private device keys, recovery codes, or raw authentication
payloads.

PeerOnQ applies five defaults:

1. collect only data required for an identified operational purpose;
2. keep authorization and redaction decisions on the server;
3. separate device identity, installation, download, presence, session, diagnostic, and audit data;
4. aggregate or delete detailed events as soon as their operational purpose ends; and
5. make diagnostic upload an explicit, reviewable user action.

This document is an engineering policy. Deployment owners remain responsible for legal basis,
regional notices, processor agreements, cross-border transfer controls, and data-subject procedures
for the jurisdictions in which they operate.

## Data-flow boundaries

| Flow | Accepted data | Explicitly rejected or stripped | Storage |
| --- | --- | --- | --- |
| Device enrollment | Installation reference, public key and signed one-use challenge, fingerprint, platform/version metadata | Private keys, passwords, access tokens in bodies, or a client-selected routing alias | Redis challenge; proof-bound PostgreSQL device/installation and keyed server-alias hash |
| Presence | Authenticated installation reference, state, region, app version, lease timestamps | Every heartbeat as an indefinite history, remote-session content | Redis lease; periodic PostgreSQL summary |
| Session lifecycle | Masked/hashed endpoints, permission mode, selected path, timing, failure codes, TURN use, reconnect count | Frames, input events, clipboard, filenames, file content | PostgreSQL metadata only |
| Download | Version, platform, architecture, channel, normalized source/campaign, timing/result, user-agent family | Persistent raw IP, precise location, invasive browser fingerprint | PostgreSQL event; aggregate queries |
| Diagnostics | User-approved sanitized archive, version/OS/architecture, bounded statistics and error identifiers | Secrets, tokens, full Device ID, screen/clipboard/file content, raw memory dump | Restricted object storage plus PostgreSQL metadata and expiry |
| Administration | Account/session state, roles, MFA state, privileged action audit | Password value, recovery code value, refresh token value | Password/recovery hashes, encrypted MFA seed, and refresh-token hashes in PostgreSQL; Data Protection keys in Redis |
| Telemetry | Bounded service/region/result labels, traces with correlation identifiers, sanitized messages | Device/user/session IDs as metric labels, request bodies, secrets | Prometheus, Loki, Tempo under retention policy |

The public reverse proxy disables request access logs because the default Nginx format contains the
client IP address. Applications emit allow-listed structured events instead. Operational security
systems may inspect a request address in memory for rate limiting and risk evaluation, then retain
only coarse risk metadata or a short-lived keyed digest according to the retention policy.

Existing signaling and coturn stdout reaches separate Fluent Forward receivers on the isolated
observability bridge. The collector assigns fixed bounded service names and removes IP literals,
formatted Device IDs, time-limited TURN usernames, and peer-address attributes before export.
Signaling's local file sink is mounted on volatile tmpfs in the Phase 6 deployment; it is not a
durable raw-log store.

## Identifier protection

- A 12-digit routing alias is never authentication. Cloud assigns it only after the one-use
  challenge is signed by the presented device key; the successful proof creates the proof-bound
  device and installation before an authenticated installation-confirm call is accepted.
- Cloud signs a short-lived signaling attestation that binds that server-assigned alias to the
  identity fingerprint, device, and installation. Signaling validates the exact issuer, audience,
  lifetime, signature, and replay state; clients cannot self-assert or replace the alias.
- Administrative lists receive only masked routing aliases. Raw resolution is performed only inside a
  specifically authorized server operation and is audited.
- Cloud routing-alias lookup, Admin audit/IP privacy, and Download uniqueness use three independent
  environment-specific HMAC keys. Download uniqueness also uses a rotating time bucket. Digests are
  not reversible or shared across purposes/environments. Cloud retains at most three explicitly
  configured prior key versions during a controlled alias migration; Admin/download key rotation
  deliberately starts new correlation epochs.
- Metric labels use bounded values such as service, environment, region, result, channel, and version.
  Device, installation, session, administrator, diagnostic, trace, and download identifiers are not
  metric labels.
- Correlation and trace IDs support incident analysis but are not identity credentials.

## Consent-based diagnostics

The client must show the included diagnostic categories before upload. Consent records bind the
diagnostic reference, approved category allowlist, client version, approval state, and expiry; they
never contain archive content. Upload uses a short-lived single-purpose token. The current server
verifies consent, token scope/expiry, exact size, SHA-256 integrity, ZIP signature, and object-origin
allowlist before marking the bundle available. Antivirus/content scanning is an object-storage
ingress responsibility and must be configured before production diagnostic access. Opening a
diagnostic is privileged and audited. Expired objects are deleted by the retention worker and become
unavailable even if stale metadata remains in a cache.

## Administrative privacy controls

- Admin authentication is separate from device authentication. Privileged roles require MFA.
- Access tokens are short-lived and remain in browser memory. Rotating refresh tokens use
  `HttpOnly`, `Secure`, `SameSite=Strict`, `__Host-` cookies and double-submit CSRF protection.
- Support roles cannot access raw credentials, unrestricted device identifiers, diagnostic objects,
  or unbounded exports.
- The UI renders only fields returned by the authorized Admin API and contains no local mock data.
- Every diagnostic read, device block, release or rollout change, role change, retention change, and
  security policy change creates an immutable audit event.

## Regional processing and isolation

Each service emits its environment and region. Development, staging, and production use separate
databases, Redis namespaces, keys, log stores, metrics stores, object stores, and administrator
accounts. Production regions may keep ephemeral presence leases local while replicating only the
minimum control-plane records required for ownership and recovery. A new region requires a documented
data-residency decision before traffic is enabled.

## Deletion and anonymization

Retention workers select expired records in bounded batches, record counts and completion status,
and are safe to retry. Restricted object deletion is verified against object storage before metadata
is finalized. Where legal hold applies, the hold is separately authorized and audited; it does not
silently disable global retention. Aggregate operational statistics remain only when they cannot be
used to single out a person or installation.

Data-subject or customer deletion requests follow this order: verify authority, place an auditable
request, identify applicable records, preserve required security/legal records, delete or anonymize
eligible detailed records, verify object deletion, and issue a completion reference. Individual audit
entries cannot be silently removed.

## Privacy verification gates

- automated log-redaction and diagnostic-sanitization tests;
- schema review for new identifiers or free-text fields;
- metric-label cardinality and sensitive-label review;
- quarterly role/access review for diagnostics and audit data;
- monthly expiry-job evidence and sampled object-deletion verification;
- restore tests that confirm retention markers and access controls survive backup recovery;
- repository secret scan before release.
