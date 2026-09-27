# PeerOnQ Phase 6 runbooks

## Common incident guardrails

For every alert, confirm environment, region, service, start time, and whether the signal is telemetry
loss rather than service loss. Check deployment/audit changes and correlated traces using sanitized
identifiers. Do not copy passwords, tokens, private keys, raw Device IDs, IP addresses, request bodies,
screen/clipboard/file content, or diagnostic objects into chat or tickets. Do not disable
authentication, MFA, TLS, rate limits, signature checks, audit, or retention to recover service.

Acknowledge the alert, assign the listed owner, and open an incident when the critical threshold is
sustained. Recovery requires the alert to clear for a full evaluation window and the affected health
and smoke tests to pass. Document commands and actual results in the incident record.

## API high error rate

- **Owner/severity/window:** Platform Operations; critical; over 5% 5xx rate for 10 minutes.
- **Triage:** Compare by service/region/route template, readiness, database pool, Redis, object store,
  and the last deployment. Inspect exemplars by trace ID, not request body.
- **Mitigate:** Remove unready instances, stop the current rollout, shift traffic to a healthy region,
  or roll back the application image while retaining forward-compatible schema.
- **Recover/escalate:** Run health and authenticated smoke tests. Escalate Platform lead then Incident
  commander; include Database/Security if dependency or auth failures correlate.

## Authentication failure spike

- **Owner/severity/window:** Security Operations; critical; over 100 failures in 10 minutes,
  sustained for five minutes.
- **Triage:** Separate invalid credentials, expired challenge, replay, fingerprint mismatch, MFA,
  lockout, and token-validation codes. Review source risk aggregates without exporting raw addresses.
- **Mitigate:** Preserve rate limits/lock protection, revoke affected admin sessions or device tokens,
  block confirmed abusive sources at the edge, and rotate a key only on evidence of compromise.
- **Recover/escalate:** Verify legitimate device/admin authentication and audit entries. Escalate
  Security owner then Incident commander; start breach assessment if credential exposure is possible.

## Signaling unavailable

- **Owner/severity/window:** Realtime Operations; critical; no successful signaling metrics scrape
  for three minutes.
- **Triage:** Confirm `/health/live`, `/health/ready`, `/metrics`, proxy/WebSocket routing, certificate,
  coturn dependency, host capacity, and the last deployment. Distinguish telemetry loss from process
  loss before shifting traffic.
- **Mitigate:** Drain the failing instance, route to a healthy signaling region, restore proxy timeout
  and heartbeat compatibility, and keep remote input disabled during uncertain reconnect.
- **Recover/escalate:** Complete two-device reconnect and resume tests with unchanged permission scope.
  Escalate Realtime on-call, Platform lead, then Incident commander.

## Signaling attestation validation failure

- **Owner/severity/window:** Security Operations with Realtime Operations; critical; valid Cloud
  enrollment attempts rejected or missing/invalid attestation accepted for five minutes.
- **Triage:** Compare the exact HTTPS issuer, audience, maximum lifetime, clock skew, mounted public
  key fingerprints, and Cloud/Signaling deployment versions. Confirm Cloud has only the PKCS#8
  private PEM and Signaling has only SPKI public PEMs. Never copy key material into the incident.
- **Mitigate:** Stop enrollment traffic, keep `Required=true` and TOFU fallback disabled, roll Cloud
  back to the last key whose public half is still trusted, or restore the approved public-key overlap.
  Do not mount a private key in Signaling or weaken issuer/audience/replay checks.
- **Recover/escalate:** Prove one fresh attestation succeeds and missing, replayed, expired,
  wrong-issuer, and wrong-audience attestations fail. Wait the maximum token lifetime plus clock skew
  before removing the old public key. Escalate Security lead, Realtime lead, then Incident commander.

## coturn unavailable

- **Owner/severity/window:** Realtime Operations; critical; coturn metrics or TCP listener probe
  unavailable for three minutes.
- **Triage:** Check the STUN health command, exporter on 9641, shared-secret parity, realm,
  certificate, public IP mapping, UDP/TCP/TLS listeners, relay range, quotas, and firewall. Do not
  create static client credentials.
- **Mitigate:** Shift relay issuance to a healthy TURN server/region, restore the permitted UDP range,
  or favor authenticated TCP/TLS relay while UDP is unavailable.
- **Recover/escalate:** Run TURN-only UDP, TCP, and TLS acceptance with time-limited credentials.
  Escalate Network provider and Incident commander if regional.

## TURN packet drop rate

- **Owner/severity/window:** Realtime Operations; critical; coturn packet drops above 5% of processed
  plus dropped packets for 10 minutes.
- **Triage:** Confirm exporter continuity, packet processed/dropped rates, listener transport, relay
  port exhaustion, host/network drops, quotas, and suspected abuse. Compare with an external
  authenticated allocation instead of relying on the dashboard alone.
- **Mitigate:** Remove an unhealthy relay, repair the exact firewall/routing fault, enforce existing
  quotas, or shift new credentials to a healthy relay region.
- **Recover/escalate:** Require the ratio below threshold for one full window and pass TURN-only UDP,
  TCP, and TLS acceptance. Escalate Network provider then Incident commander.

## TURN bandwidth saturation

- **Owner/severity/window:** Realtime Operations; warning; above 85% configured capacity for 15 minutes.
- **Triage:** Validate the capacity metric, active allocations, bitrate distribution, regional skew,
  egress limits, and abuse/quota signals.
- **Mitigate:** Add approved relay capacity, rebalance new allocations, enforce existing quotas, and let
  normal adaptive quality reduce bitrate. Never change security settings for quality.
- **Recover/escalate:** Keep utilization below 70% through a peak interval; escalate Capacity owner.

## Presence unavailable

- **Owner/severity/window:** Realtime Operations; critical; no successful Presence metrics scrape for
  three minutes.
- **Triage:** Check liveness/readiness, connections, event-loop/CPU/memory, Redis latency, heartbeat
  rate, duplicate loops, reconnect storms, and per-instance skew.
- **Mitigate:** Add Presence instances, rebalance, apply documented backpressure, and retain the lease
  timeout. Do not lengthen leases to mask overload or report uncertain devices online.
- **Recover/escalate:** Verify duplicate resolution and online/offline timeout; escalate Platform
  capacity owner.

## Database pool pressure

- **Owner/severity/window:** Database Operations; critical; above 90 of the configured 100 connections
  for 10 minutes.
- **Triage:** Inspect pool wait, long transactions, blocked queries, connection leaks, query latency,
  database max connections, and deployment changes. Capture query fingerprints, not parameter values.
- **Mitigate:** Stop the offending rollout/job, bound concurrency, cancel proven runaway queries under
  database-owner control, and shed noncritical reads. Do not blindly raise connection limits.
- **Recover/escalate:** Verify pool below 70%, readiness, migration state, and request latency. Escalate
  Platform lead then Incident commander.

## Redis unavailable

- **Owner/severity/window:** Platform Operations; critical; required dependency readiness below one for
  three minutes.
- **Triage:** Check network/TLS/auth, memory eviction policy, persistence error, failover state, latency,
  and which services are affected.
- **Mitigate:** Fail over to the configured healthy Redis node. Treat presence as uncertain/offline,
  reject replay-sensitive authentication if certainty is lost, and keep PostgreSQL durable data intact.
- **Recover/escalate:** Verify challenges, revocation, duplicate presence, heartbeat expiry, and rate
  limiting. Escalate Database on-call and Incident commander.

## Queue backlog

- **Owner/severity/window:** Platform Operations; warning; more than 1000 queued items for 10 minutes.
- **Triage:** Identify the exact owning service, enqueue/dequeue rates, dependency latency, retries,
  poison work, and the last deployment. Do not inspect payload contents in telemetry.
- **Mitigate:** Stop the faulty producer/rollout, restore the blocked dependency, apply documented
  backpressure, and add approved worker capacity only after confirming work is safe to retry.
- **Recover/escalate:** Confirm the queue drains without duplicate side effects and remains below the
  threshold for one window. Escalate Capacity owner.

## Update failure spike

- **Owner/severity/window:** Release Engineering; critical; more than 25 failed update events in 30
  minutes, sustained for 15 minutes.
- **Triage:** Segment by signed release/channel/architecture/stage/error code; verify signatures,
  checksums, manifest, CDN object, disk/runtime prerequisites, and rollout audit.
- **Mitigate:** Pause rollout. Never republish altered bytes under the same version or bypass signature
  enforcement. Activate a verified rollback only through the signed release process.
- **Recover/escalate:** Test fresh install/update/rollback on supported x64 and ARM64 devices. Escalate
  Security owner and Incident commander for signing/provenance anomalies.

## Verified download cache unavailable

- **Owner/severity/window:** Release Engineering; critical; verified-cache write/eviction failures or
  download capacity unavailable for five minutes.
- **Triage:** Check dedicated-volume free bytes/inodes, mount ownership (Downloads UID 1654), configured
  cache/artifact limits, active leases, origin strong ETag, immutable object size, and SHA-256 failures.
  Do not copy unverified origin bytes directly to clients or move the cache into `/tmp`.
- **Mitigate:** Pause download routing, drain active streams, expand or replace the dedicated volume,
  and fix origin immutability. Never delete cache entries while the service is using them.
- **Recover/escalate:** Prove a full artifact is verified before its first byte is returned, confirm one
  `.artifact` and no stale `.partial`, then prove a range cache hit does not contact origin and its hash
  matches the source slice. Escalate Security for integrity failures and Platform Capacity for space.

## Client crash rate increase

- **Owner/severity/window:** Client Reliability; critical; crashes above 1% of active installations for
  a version for 30 minutes.
- **Triage:** Compare version/OS/architecture/error ID and rollout; request sanitized diagnostics only
  with explicit user consent. Never request raw memory that may contain secrets.
- **Mitigate:** Pause release/update rollout, publish support guidance, and use a verified signed
  rollback where safe.
- **Recover/escalate:** Validate the fixed signed build and watch a canary window. Escalate Release
  manager and Incident commander.

## Telemetry pipeline unavailable

- **Owner/severity/window:** Platform Operations; warning; OpenTelemetry Collector metrics target
  unavailable for five minutes.
- **Triage:** Confirm collector process/config, exporter backpressure, Loki/Tempo reachability, disk,
  and whether services remain healthy independently. Treat missing telemetry as unknown state.
- **Mitigate:** Roll back the collector configuration, restore bounded queues/storage, and keep
  realtime Fluent Forward input loopback-only. Do not bypass the realtime redaction processor.
- **Recover/escalate:** Verify metrics, traces, and a sanitized test log arrive without identifiers;
  clear only after one evaluation window. Escalate Observability owner.

## Unsupported client spike

- **Owner/severity/window:** Release Engineering; warning; unsupported versions above 5% of active
  installations for 30 minutes.
- **Triage:** Verify minimum/security-floor policy, update availability, channel/architecture coverage,
  and whether the rise follows offline devices returning.
- **Mitigate:** Correct signed release availability and communication; enforce the security floor
  server-side. Do not weaken the floor to hide adoption delay without Security approval.
- **Recover/escalate:** Confirm adoption trend and successful updates; escalate Product owner.

## Regional outage

- **Owner/severity/window:** Platform Operations; critical; regional health below one for five minutes.
- **Triage:** Confirm multiple independent probes, DNS/load balancer, TLS, proxy, APIs, Presence,
  PostgreSQL/Redis, signaling/TURN, provider status, and last change.
- **Mitigate:** Stop new regional traffic, fail over to a tested region, preserve immutable audit, and
  avoid split-brain presence/ownership. Communicate degraded relay/latency honestly.
- **Recover/escalate:** Restore dependencies, reconcile stale sessions/leases, then canary traffic.
  Escalate Platform lead and Incident commander immediately.

## Certificate expiration

- **Owner/severity/window:** Security Operations; warning; less than 14 days remaining for one hour.
- **Triage:** Verify the measured hostname, certificate chain, SAN, issuer/renewal job, DNS challenge,
  and key ownership from two networks.
- **Mitigate:** Issue a new certificate/key through the approved CA and secret manager; stage it on a
  canary proxy. Never disable client validation or reuse an exposed key.
- **Recover/escalate:** Verify TLS 1.2/1.3, chain, hostname, expiry metric, admin cookie, WebSocket, and
  TURN TLS where applicable. Escalate Certificate owner.

## Disk space pressure

- **Owner/severity/window:** Platform Operations; warning; below 15% free for 15 minutes.
- **Triage:** Identify exact filesystem and growth source: database, backup, Loki, Tempo, Prometheus,
  container log, diagnostic object cache, or release store. Verify retention worker health.
- **Mitigate:** Stop uncontrolled writers, expand approved storage, or run the owning retention policy
  against its exact validated directory. Do not recursively delete broad paths or audit/database files.
- **Recover/escalate:** Verify above 25% free and writer/readiness health; escalate Storage owner.

## Interrupted platform upgrade

- **Owner/severity/window:** Platform Operations with Release Engineering; critical whenever Admin
  reports `manual_recovery` or the queue gate remains after the updater service exits.
- **Triage:** Stop new rollout actions. From a root shell, record the exact 32-lowercase-hex operation
  ID, inspect the sanitized status, `systemctl status peeronq-platform-upgrade-agent.service`, the
  matching root-only journal/installer log reference, `/opt/peeronq/current` and
  `/opt/peeronq/previous`. Verify with `stat` that only the exact
  `/var/lib/peeronq/platform-upgrade/processing/<operation-id>` directory and matching
  `/var/lib/peeronq/platform-upgrade/inbox/.active-request` are involved. Do not expose raw logs,
  keyrings, archives, environment files, or request payloads through Admin.
- **Fail-safe behavior:** The agent never replays a claimed request after a crash because the
  installer may already have changed containers, migrations, or release links. It marks the status
  `manual_recovery`, retains the root-owned claimed evidence, and leaves the active gate closed.
- **Recover:** Determine the actual active release and complete the normal health/publication checks.
  If it is healthy, close the incident as the observed version; otherwise use only the retained,
  signature-verified bundle's fixed manual `--rollback` workflow. Database migrations are
  forward-only. The root updater and pinned signer trust are also forward-only and must not be
  downgraded with the application; confirm the latest agent still publishes schema-1 status after
  rollback. After the outcome is verified and evidence retained under incident policy, remove
  only the validated exact operation directory and its exact-ID active marker, run
  `/usr/local/libexec/peeronq-platform-upgrade-agent --refresh-idle`, and start the path unit. Never
  recursively delete the platform-upgrade root or blindly rerun the claimed request.
- **Escalate:** Release Engineering and Database Operations must approve an uncertain migration or
  release-link state; Security joins if signer trust or spool ownership is unexpected.

## Backup failure

- **Owner/severity/window:** Database Operations; critical; no verified success within 25 hours for 30
  minutes.
- **Triage:** Check job output, database reachability, `.pgpass` permissions, free capacity, archive
  validation, checksum write, off-host copy, and textfile metric timestamp.
- **Mitigate:** Correct the underlying issue and run `--profile operations run --rm backup`. Do not
  delete the most recent valid archive to create space without Database owner approval.
- **Recover:** Run the isolated `--profile acceptance run --rm restore-test`; record archive checksum,
  table count, start/end, and result. A successful dump without a restore test is not full recovery
  evidence.
- **Destructive restore:** Stop writers and traffic, rehearse against isolation, obtain Database and
  Security owner approval, select the exact checksum-verified archive, then run the restore controller
  with `PEERONQ_CONFIRM_RESTORE=RESTORE_PEERONQ_CLOUD`. Apply compatible migrations, re-enable
  retention, check health/auth/presence/audit, and only then reopen traffic.
- **Escalate:** Database on-call to Platform lead and Incident commander.

## Suspected sensitive-data telemetry exposure

This is an incident even if no service alert fires. Restrict telemetry access, preserve protected
evidence, identify affected stores/retention/backups, stop the emitting code path, rotate exposed
credentials, and involve Security/Privacy. Do not broadly copy or search the sensitive value. Deploy
redaction with regression tests, run scoped deletion where legally permitted, verify all downstream
stores, and document notification decisions.
