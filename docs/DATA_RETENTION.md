# Data retention policy

| Data | Default | Control / deletion |
| --- | --- | --- |
| Security audit | 90 days | User-configurable 1-3650 days; retention re-chains remaining records; export verifies integrity first; clear requires confirmation and leaves a clear event |
| Local application logs | Rolling file policy configured by the application | Delete from `%LOCALAPPDATA%\PeerOnQ\logs` while PeerOnQ is stopped |
| Opt-in local crash reports | 30 days operational target | Crash collection is off by default; delete from `%LOCALAPPDATA%\PeerOnQ\crash-reports` or revoke consent |
| Sanitized diagnostics exports | Until user deletes the chosen export | User-selected file; never automatically uploaded |
| Downloaded update packages | Until installed, superseded, or manually removed | Private `%LOCALAPPDATA%\PeerOnQ\updates`; `.partial` files are never executed |
| Trusted devices/unattended verifier | Until user revokes/removes it | Manage locally; fingerprint or permission changes revoke trust |
| Address book and transfer history | Until user removes it | Local SQLite application data |
| Signaling live ownership/resume state | Reconnect/token lifetime | Ephemeral; not in backup scope |
| Device public-key pin | Until device removal/rotation policy | Durable signaling data; included in encrypted backup scope |
| TURN allocation/credential | Credential/allocation lifetime | Ephemeral; never backed up |

Uninstall intentionally leaves `%LOCALAPPDATA%\PeerOnQ` to preserve identity and audit history during
upgrade/reinstall. To perform a full user-data removal, uninstall PeerOnQ, verify no PeerOnQ process is
running, export anything required, then delete that exact directory. Enterprise automation must not
delete it without explicit data-owner authorization.

## Phase 6 cloud defaults

The defaults below apply independently in each environment and region. A deployment may shorten them.
Increasing a period requires Privacy and Security approval and creates an immutable audit event.

| Cloud data | Default detailed retention | After expiry |
| --- | --- | --- |
| Redacted operational application logs | 30 days | Delete from Loki/object tier; retain only non-identifying aggregate metrics; raw signaling files are tmpfs-only |
| Traces | 7 days | Delete trace blocks |
| Prometheus operational metrics | 15 days in development, 30 days in staging; production policy set by operator | Delete time-series blocks; long-term aggregates must not gain identifier labels |
| Security/admin audit events | 365 days | Protected archive or deletion according to legal/security policy; no individual silent deletion |
| Download events and rotating uniqueness digest | 90 days | Delete digest and detailed event; retain non-identifying daily aggregates for 25 months |
| Remote-session metadata and failure records | 90 days | Delete detailed rows; retain monthly non-identifying reliability aggregates for 13 months |
| Presence summaries | 30 days | Delete detailed summaries; Redis leases expire in seconds and are never archival |
| Service-health snapshots and alert events | 30 days / 365 days | Delete health detail; governed alert deletion records append-only batch evidence and honors legal hold |
| Diagnostic bundles | 14 days, or earlier user deletion | Delete object, revoke access token, then mark metadata expired |
| Diagnostic metadata/access audit | 90-day policy target / 365-day audit policy | Delete metadata; retain protected access audit according to audit policy |
| Client crash reports uploaded with consent | 30 days | Delete report; retain aggregate crash rate by bounded version/channel |
| Admin access/refresh sessions | Access token 5 minutes, refresh session 8 hours; completed session records 30 days | Delete/revoke token verifier; audit logout/revocation |
| Registration/auth challenges | 5 minutes maximum | Expire from Redis; never back up |
| PostgreSQL backups | 14 daily copies by default | Cryptographic erasure and deletion after restore-test window/legal hold check |
| Managed Redis snapshots containing Admin Data Protection keys | Operator policy, maximum 14 daily copies unless legal hold applies | Encrypted deletion after isolated restore/MFA proof; expired TTL records are not reactivated |
| Loki local store | 30 days (`720h`) | Compactor deletion |
| Tempo local store | 7 days (`168h`) | Compactor deletion |

The public reverse proxy does not retain request access logs. Raw request IP addresses may be used in
memory for rate limiting, coarse country derivation, and risk evaluation, but are not written to the
download table or routine logs. Download uniqueness uses an environment-specific rotating HMAC digest
that expires with the detailed download event.

## Retention execution and evidence

Retention jobs run in bounded, retry-safe batches and emit only aggregate counts, duration, outcome,
environment, and region. Failures alert after a sustained window. Diagnostic deletion verifies object
removal before completion. Legal holds are scoped, authorized, time-bounded, reviewed monthly, and
audited; they do not silently disable an entire retention job.

Backups do not extend normal business retention indefinitely. Restore procedures re-enable expiry
workers before traffic, and operators verify that already-expired diagnostic objects and token
verifiers are not made accessible by a restore.

The reference retention worker enforces presence, download, session, diagnostic-object,
service-health, completed-admin-session, audit, and alert expiry in bounded batches. Audit and alert
deletion is available only through governed database procedures: minimum periods are enforced,
legal holds and explicit policy enablement are checked, individual administrative deletion is
blocked, and an append-only evidence row is written in the same transaction. Final deletion of
expired diagnostic metadata and its protected access-audit chain still requires an approved archival
controller before production. A configured policy value alone is not evidence of enforcement;
operations must record worker, evidence, legal-hold review, and restore-test results.

For audit and alert records, the protected `RetentionPolicies` rows and PostgreSQL clock are the
enforcement authority. The procedure deliberately ignores caller-supplied policy version, cutoff,
and execution time. Runtime roles cannot read or modify policy rows, and receive an exact procedure
`EXECUTE` grant only while that record class is enabled and not under legal hold. The controlled
permissions job atomically synchronizes reviewed deployment policy and those grants before Cloud
starts.
