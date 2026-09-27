# PeerOnQ data classification

## Handling levels

| Class | Meaning | Examples | Minimum handling |
| --- | --- | --- | --- |
| Public | Approved for unrestricted distribution | Public release version, public download URL, public documentation | Integrity control; TLS for distribution |
| Internal | Low-impact operational information not intended for public release | Aggregate service health, non-sensitive runbooks, bounded metric names, deployment topology without secrets | Authenticated staff access; approved collaboration systems |
| Confidential | Metadata that could identify activity, installations, administrators, or security posture | Masked Device ID, installation reference, session metadata, download event, presence summary, alert detail, sanitized logs | Least-privilege access, TLS, encryption at rest, retention, audited exports |
| Restricted | Secrets, authentication material, diagnostic content, or data whose misuse can compromise users or systems | Private keys, passwords, refresh-token hashes, MFA seeds, recovery-code hashes, signing keys, raw Device ID resolution, diagnostic bundles | Dedicated secret/object store, strongest least privilege, audited access, no routine logging, explicit rotation/deletion |

Classification follows the highest-sensitivity field in a record, archive, log line, export, backup,
or message. Removing one sensitive field does not lower classification unless the remaining data has
been reviewed for linkability.

## Phase 6 inventory

| Data | Class | System of record | Permitted consumers |
| --- | --- | --- | --- |
| Public app release metadata and signed installer | Public | Release service/object store | Public download service and update client |
| Aggregate online/session/download/install metrics | Internal | Prometheus/Admin API aggregates | Operations and approved analysts |
| Device identity fingerprint and keyed server-assigned routing-alias hash | Confidential | PostgreSQL | Cloud authentication service; limited security administrators |
| Raw 12-digit routing-alias resolution, when operationally required | Restricted | Isolated lookup boundary | Explicitly authorized security operation only |
| Installation and presence metadata | Confidential | PostgreSQL/Redis | Cloud, presence, authorized support/operations roles |
| Remote-session lifecycle metadata | Confidential | PostgreSQL | Operations/support within assigned scope |
| Download event and rotating uniqueness digest | Confidential | PostgreSQL | Downloads service and approved analysts |
| Structured technical logs and traces | Confidential | Loki/Tempo | Operations/security within environment |
| Immutable administrator audit event | Confidential | PostgreSQL/protected archive | Owner, security administrator, authorized auditor |
| Diagnostic bundle | Restricted | Restricted object storage | Consent-bound support/security workflow |
| Diagnostic metadata without bundle content | Confidential | PostgreSQL | Authorized support/security roles |
| Password hash, MFA seed, recovery-code hash, refresh-token hash | Restricted | PostgreSQL/secret-protected columns | Admin authentication service only |
| Device private key, release-signing private key, Cloud signaling-attestation private key | Restricted | Device OS secret store, offline signer, or deployment secret manager | Owning process only; never Cloud database or Signaling |
| PostgreSQL backup | Restricted | Encrypted backup store | Backup/restore service account and database owners |
| Redis snapshot containing Admin Data Protection keys | Restricted | Encrypted managed Redis backup | Backup/restore service account and security-approved database owners |

## Rules by class

### Public

Public status is an explicit approval, not a default. Public release artifacts remain subject to
signature, checksum, provenance, and rollback controls.

### Internal

Do not include identifiers, free-form client messages, request bodies, or infrastructure secrets.
Internal dashboards require staff authentication even when their individual data points are low risk.

### Confidential

Use TLS, encryption at rest, server-side authorization, bounded pagination/export, environment
separation, and the documented retention period. Do not place high-cardinality identifiers in metric
labels. Mask Device IDs before returning them to the Admin UI.

### Restricted

Restricted data is never committed, printed, included in routine telemetry, sent to client analytics,
or copied into tickets/chat. Inject secrets at runtime; encrypt backups with a key held separately;
rotate after suspected exposure. Diagnostic bundle reads and raw identifier resolutions require a
reason and an immutable audit event.

## Prohibited collection

PeerOnQ analytics, logging, metrics, traces, diagnostics, and audit systems must not collect screen
contents, clipboard contents, keystrokes, mouse coordinates, transferred-file contents, user
documents, passwords, private keys, temporary access passwords, access tokens, full authentication
payloads, or filenames unless a separately approved feature has a documented necessity and privacy
review. Phase 6 has no such approved exception.

## Ownership and review

Security owns this classification policy; service owners classify new fields before schema or API
review. Privacy and Security jointly approve any downgrade. Operations reviews storage access every
quarter, and the incident commander treats an unknown classification as Restricted until assessed.
