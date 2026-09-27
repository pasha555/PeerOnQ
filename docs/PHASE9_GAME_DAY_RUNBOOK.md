# Phase 9 single-region game-day runbook

This runbook covers the local single-host HA validation profile. It never authorizes destructive
restore, volume removal, public deployment, DNS/firewall changes or secret rotation.

## Preconditions and stop conditions

1. Record branch, commit, dirty-tree state, operator, UTC start, host hardware and Docker version.
2. Confirm Phase 6 and Phase 7 gates are green and a recent verified PostgreSQL backup exists.
3. Confirm the current `.peeronq-phase6` certificate/secret paths are files, not stale directories.
4. Merge Compose configuration with both development and HA files and require `config --quiet`.
5. Stop immediately on tenant-boundary failure, secret exposure, database corruption, missing audit,
   both replicas unhealthy, or any need to remove a volume.

## Start and baseline

```powershell
$env:PEERONQ_TLS_CERT_DIR = (Resolve-Path .peeronq-phase6\certs).Path
$env:PEERONQ_TLS_PRIVATE_KEY_FILE = (Resolve-Path .peeronq-phase6\certs\privkey.pem).Path

docker compose --env-file src/PeerOnQ.Infrastructure.Deployment/.env `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.ha.yml `
  up -d --build

.\scripts\windows\test-phase9-single-region-ha.ps1 `
  -RequestCount 200 -Concurrency 8 -IncludeSingleNodeDataRestart
```

The script writes a secret-free JSON evidence bundle under `.peeronq-phase9/evidence`, restores every
service it stops in `finally`, and never removes volumes. Review error rate, p50/p95/p99/max latency,
resource samples, recovery observations and the explicit data-loss measurement field.

## Decision points

| Observation | Decision |
| --- | --- |
| Primary stops; backup stays healthy; zero test errors | Continue to the next stateless service. |
| Any `5xx`, timeout or backup unhealthy | Abort; start the primary; preserve proxy/service logs and evidence; do not promote the profile. |
| Redis/PostgreSQL restarts and readiness returns | Record as single-node interruption only. Do not call it failover. |
| Redis restart loses Admin Data Protection/MFA ability | Severity 1 rollback; isolate Redis evidence and restore only into a separate rehearsal instance. |
| Migration needs a down migration or destructive SQL | Abort. Roll application forward or restore into isolation after approval. |
| One Signaling instance is lost | Require the second instance to remain ready. Existing clients reconnect through the proxy and reclaim only a valid, one-use session resume token within the bounded grace window. Preserve failure evidence if routing, permissions or ownership diverge. |

## Backup and isolated restore drill

Use the existing `backup` and `restore-test` Compose profiles. Identify the exact new archive and
sidecar, verify SHA-256, set `PEERONQ_RESTORE_TEST_FILE` to its `/backups/...` container path, and run
the acceptance restore. The restore job must create and drop only its generated test database. Never
point the drill at the live database.

## Alert delivery drill

Submit a uniquely named warning through Alertmanager, wait no longer than the configured group wait
plus 40 seconds, and prove the alert reached governed `AlertEvents`. A token mount that is a directory,
an authentication rejection, or a missing row is a failed drill. Do not print the webhook token.

## Rollback and recovery

1. Start every stopped primary and wait for its own health check.
2. Reapply the previously validated Compose model; do not use `down -v` or remove named volumes.
3. Verify Cloud API, Presence, Downloads, Signaling and Admin readiness through HTTPS.
4. Verify audit/alert rows and one non-destructive database read.
5. Preserve evidence and exact logs; record whether any write was in flight and whether data loss was
   actually measured.
6. If PostgreSQL/Redis is corrupt, keep writers stopped and rehearse restore in isolation. Production
   failover or destructive restore requires the database, security and incident owners.

## Production HA prerequisites

The local game day cannot approve production HA. Required external evidence includes a real
PostgreSQL leader failure, Redis failover with Admin MFA recovery, object-store continuity, physical
active-session restart across the two Signaling nodes, sustained soak,
real download throughput, TURN capacity/failover, and an off-host backup/PITR drill.
