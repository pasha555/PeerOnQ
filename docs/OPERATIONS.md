# PeerOnQ Phase 6 operations

## Ownership

| Area | Primary owner | Secondary |
| --- | --- | --- |
| Cloud/Admin/Downloads APIs and reverse proxy | Platform Operations | Security Operations |
| Presence, signaling, and TURN | Realtime Operations | Platform Operations |
| PostgreSQL, Redis, backup/restore | Database Operations | Platform Operations |
| Signed releases and client adoption | Release Engineering | Client Reliability |
| Admin authentication, audit, diagnostics access | Security Operations | Privacy owner |
| Metrics, logs, traces, alert delivery | Observability owner | Platform Operations |

Every production alert has a primary on-call, an escalation path, and an immutable runbook URL.
The Alertmanager-to-Admin-API token and paging receiver credentials live in externally mounted secret
files/configuration, never in this repository. The authenticated webhook is what makes current and
resolved alerts visible in the real Admin UI.

## Health and service objectives

These are initial engineering objectives, not claims of measured production performance:

| Service indicator | Initial objective | Measurement |
| --- | --- | --- |
| Cloud/Admin/Downloads successful request availability | 99.9% monthly | Non-5xx requests excluding client validation/auth denial |
| Presence authenticated connection availability | 99.9% monthly | Accepted connections and lease continuity by region |
| Admin overview freshness | 99% under 90 seconds | `generatedAtUtc` age at response |
| Download proxy availability | 99.95% monthly | Successful hash/size-verified signed-artifact streams |
| Diagnostic deletion timeliness | 99% within 24 hours of expiry | Expiry worker completion evidence |
| Daily PostgreSQL backup | One verified archive every 24 hours | Textfile success timestamp plus object evidence |
| Restore proof | Monthly | Isolated restore command output and incident/change record |

Error-budget policy is established only after at least one complete production measurement window.
Do not change alert thresholds merely to make a dashboard green.

## Telemetry contract

Phase 6 API services emit OpenTelemetry traces, metrics, and structured logs with environment,
region, service, severity, event name, correlation/trace/request identifiers, optional session
reference, error identifier, and sanitized message. Existing signaling/coturn logs enter the same
Loki pipeline through Fluent Forward only after collector redaction; their native metrics endpoints
are scraped internally. Signaling metrics pass through the read-only loopback bridge on port 8081 so
its non-TLS application-traffic rejection remains enabled. Free-form request bodies are not
telemetry.

Signaling and coturn use separate internal Fluent Forward receivers (24224 and 24225) so the
collector assigns bounded `peeronq-signaling` and `coturn` service names without deriving labels
from untrusted log text.

Prometheus labels are limited to bounded service, environment, region, result, failure stage,
connection path, release channel, architecture, platform, and version values. Never label a metric
with Device ID, installation ID, session ID, administrator ID/email, download ID, diagnostic ID,
correlation ID, trace ID, request ID, IP address, or user agent.

Loki retention is 30 days in the reference config; Tempo is 7 days. Query access follows environment
and role. The reverse proxy does not write request access logs, preventing default raw-IP retention.
The collector removes IP literals, formatted Device IDs, and TURN REST usernames from realtime logs
before export. A redaction processor error or sensitive sample is handled as an incident, not ignored
as routine noise.

## Minimum dashboards

The reference stack provisions the source-controlled **PeerOnQ Operations** Grafana dashboard from
`src/PeerOnQ.Infrastructure.Deployment/observability/grafana/dashboards`. Its panels query real
Prometheus series for target/dependency readiness, online devices, active sessions, API traffic and
errors, database/queue pressure, TURN outcomes, downloads, updates, backup, and restore activity.
Missing series remain missing rather than being replaced with demo values. Treat this as the
single-node operational baseline; the broader role-specific dashboards below remain required before
a production pilot.

1. **Executive operations:** online devices, active sessions, downloads, installations, active
   installations, session success, crash rate, TURN usage, stable release, region health.
2. **API reliability:** request volume, p50/p95/p99 duration, 4xx/5xx, rate limits, database pool,
   queue depth, readiness by service/region.
3. **Realtime:** presence connections/leases, signaling online/active counts, connection opens,
   resume outcomes, coturn current allocations, packet drops, and relay bandwidth.
4. **Client/release:** version and Windows distribution, unsupported clients, update
   offered/downloaded/installed/failed/rollback by signed release.
5. **Security:** administrator login/MFA outcomes, lockouts, revocations, challenge replay rejection,
   blocked identities, privileged audit events. No secret or full identifier panel.
6. **Privacy/diagnostics:** consent outcomes, upload validation, bundle expiry/deletion, access audit,
   retention-job outcomes.
7. **Backup:** last success, archive size trend, restore-test age/result, storage free capacity.

## Routine schedule

### Per shift / daily

- Review firing critical/warning alerts and unacknowledged security audit events.
- Verify all configured regions are ready and Admin overview freshness is below 90 seconds.
- Verify a PostgreSQL backup completed and its SHA-256 sidecar exists.
- Verify the managed Redis snapshot/persistence job containing Admin Data Protection keys completed.
- Check diagnostic expiry and retention jobs for bounded successful completion.
- Review release/update failure and crash-rate changes before increasing rollout.

### Weekly

- Review database/Redis capacity, pool utilization, queue depth, TURN capacity, and disk trend.
- Sample sanitized logs for redaction regressions; never paste raw logs into tickets without review.
- Reconcile active sessions and stale presence leases.
- Review unsupported client share and security-floor enforcement.

### Monthly / quarterly

- Run and record isolated PostgreSQL and Redis restore tests monthly; prove administrator MFA still
  decrypts after the Redis restore.
- Test a regional failover and Presence reconnection without expanding permissions.
- Review administrator roles, active sessions, MFA enrollment, diagnostics access, service accounts,
  secret age, and legal holds quarterly.
- Verify data-store retention against `DATA_RETENTION.md` and sample expired object deletion.
- Verify the runtime database role can execute only the approved governed-retention functions. It
  must not inherit the migration-owner role, own protected tables, bypass triggers/RLS, or hold
  superuser privileges; review explicit function `EXECUTE` grants after every migration. Verify both
  functions remain owned by the NOLOGIN `peeronq_retention_executor`, are `SECURITY DEFINER`, and pin
  `search_path=pg_catalog,public`; only the migrator may be a member of that owner role.
- Treat retention enablement and legal holds as database authorization changes: stop the retention
  worker, change policy, run `database-permissions`, verify the expected function `EXECUTE`
  grant/revocation and exact database policy row, then restart. A disabled or held data class must
  have no Cloud execute grant. The database function ignores caller-supplied policy/cutoff/time and
  derives them from the protected row and database clock.

## Administrator security

Privileged roles require MFA. Access tokens remain short-lived and in browser memory; refresh tokens
rotate and can be revoked. A 401 causes at most one controlled refresh attempt. Permanent 401/403,
stale 409, and rate-limited 429 responses remain distinct operational states. The UI hides unavailable
navigation for usability, but the Admin API is the sole authorization authority.

One-time owner bootstrap is permitted only during an authorized deployment, with email, strong
password, Base32 TOTP secret, and eight recovery codes injected from the secret manager. Verify MFA
and audit creation, disable bootstrap, remove every bootstrap value, and redeploy before routine use.

## Customer account operations

- Treat `portal.*` customer identities as a separate domain from `admin.*`; never repair a customer
  by granting an Admin role or copying an Admin session/cookie.
- Review registration mode, SMTP delivery, verification/reset failures, account lockouts, refresh
  replay, invitation abuse, MFA/recovery events, and pending export/delete requests daily during a
  pilot. Logs and tickets must not contain raw tokens or MFA material.
- To disable an account, update it through an authorized operator workflow and revoke all customer
  sessions. Do not delete organization owners; transfer or delete every owned organization first.
- Process exports from the documented tables in [ACCOUNT_DATA_INVENTORY.md](ACCOUNT_DATA_INVENTORY.md),
  redact protected hashes/ciphertext/internal identifiers, and record completion. Deletion requires
  owner/device safeguards and a recoverable, audited operator procedure; never delete append-only
  security evidence ad hoc.
- Back up the dedicated customer Data Protection volume with PostgreSQL. Restore both into an
  isolated environment and verify an existing customer session and MFA secret before accepting the
  proof. Loss of the key ring is an authentication incident, not a reason to disable MFA checks.
- Development mail files are local test evidence only. Production must use configured SMTP and
  provider-level delivery/abuse monitoring; never expose the mail volume through Nginx.
- Organization policy changes affect managed policy evaluation and newly issued short-lived
  attestations. For urgent revocation, revoke the affected device/session in addition to changing
  policy, then verify signaling rejects reauthentication.

## Release operation

The management plane stores release metadata and rollout, but it cannot sign an artifact. Release
Engineering verifies installer signatures, update-manifest signatures, checksums, SBOM, malware scan,
minimum/security-floor policy, and architecture before activating a release record. Roll out in
bounded steps, pause on crash/update/session regressions, and audit every change. Unsigned or
unverifiable artifacts remain unavailable regardless of administrator role.

Monitor the Downloads verified-cache filesystem bytes/inodes, eviction failures, origin fetch
failures, integrity failures, and stream saturation. Capacity must cover the configured maximum cache
plus filesystem overhead and the largest permitted artifact. A cache hit, including a range response,
must not contact origin again. Cleanup is application-governed LRU eviction of unleased verified
entries; operators must not delete `.partial` or `.artifact` files from a running container. If the
volume is full or inconsistent, drain Downloads, preserve logs and release metadata, replace or expand
the dedicated volume, restart, and verify one full artifact and one range before restoring traffic.

## Backup and recovery operation

The PostgreSQL backup controller emits a success metric only after `pg_dump` and
`pg_restore --list` succeed.
Store archives off-host with encryption and immutability appropriate to Restricted data. Failure to
write the monitoring metric is a failed backup run. The restore test always uses a generated isolated
database; normal services continue against the original database.

Redis backup/restore uses the managed service controller because the database mixes ephemeral TTL
state with the critical Admin Data Protection key set. Back up encrypted snapshots without exporting
individual key values. Restore into an isolated instance, confirm expired TTL records are not active,
then run an Admin MFA authentication check before accepting the proof.

A real restore requires an incident/change record, database owner and security owner approval,
writers stopped, exact backup identification/checksum, an isolated rehearsal, explicit confirmation,
and post-restore migrations/readiness/retention verification. Never restore over a live writable
database to test a backup.

## Secret rotation

1. Create the new secret in the manager and record owner/expiry.
2. Where supported, deploy an overlap window with old and new verification keys.
3. Rotate signing, refresh-hash, the three purpose-separated HMAC keys, TURN,
   download-completion, and service webhook keys without reusing a value across purposes. A Cloud
   routing-alias key rotation first adds a new version while retaining the required previous key,
   then migrates stored alias bindings under a reviewed collision-safe procedure before retiring the
   old version; never reinterpret a client-supplied value as the server alias. Admin privacy-key
   rotation intentionally breaks correlation with older audit/IP digests; Downloads privacy-key
   rotation starts a new uniqueness epoch. Record these effects in the change request.
   For signaling attestation, deploy the new public key to every Signaling instance first, canary the
   new Cloud private key, wait no longer than the maximum token lifetime plus clock skew, then remove
   the old public key. A private key never enters Signaling or client configuration.
4. Redeploy one region/canary, verify authentication, presence, downloads, diagnostics, audit, and
   telemetry, then complete the rollout.
5. Revoke the old secret, invalidate affected sessions/credentials, remove old mounts, and audit the
   action.
6. On suspected exposure, skip normal overlap where unsafe and follow the security incident process.

Rotate each database runtime role and Redis ACL user independently, including the read-only Presence
device-token validator, then rerun cross-role denial
checks. Rotation must not grant a runtime schema ownership, `BYPASSRLS`, superuser, another service's
tables, Redis key pattern, or channel pattern. Redis rotation plans for brief coordination
degradation; it must not silently treat uncertain presence as online. Rotate the Admin Data
Protection PFX only after proving old MFA ciphertext can be decrypted during the overlap/migration
window. TLS rotation verifies chain, SAN, OCSP policy, and expiry metric before old certificate
removal.

## Incident handling

Declare an incident when a critical alert sustains its documented window or multiple regions/security
signals indicate material impact. Assign incident commander, operations lead, communications lead,
and scribe. Preserve audit/telemetry evidence without collecting remote content or secrets. Prefer
fail-closed authentication and uncertain/offline presence to unsafe continuation. Record mitigation,
scope, timeline, customer impact, privacy assessment, recovery validation, and follow-up owners.

Use `RUNBOOKS.md` for alert-specific actions. A single isolated client failure does not trigger
a platform alert; support handles it using consent-based sanitized diagnostics.

## Phase 9 game day

Use [PHASE9_GAME_DAY_RUNBOOK.md](PHASE9_GAME_DAY_RUNBOOK.md) for the opt-in local application-tier drill.
Its script records hardware-qualified latency/error/resource evidence, stops only one stateless
primary at a time, restores services in `finally`, and never removes volumes. PostgreSQL/Redis
restart observations are single-node interruptions, not failover. The local overlay now exercises
two Redis-backed Signaling nodes, but production promotion still requires real data-service failover,
physical active-session evidence,
sustained soak/capacity, off-host recovery and operator-approved decision records.
