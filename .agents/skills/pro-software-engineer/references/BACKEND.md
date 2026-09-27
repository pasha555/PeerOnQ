# Backend reference

Read for APIs, databases, services, workers, queues, migrations, and external integrations.

## API/service rules
- Validate input at trust boundaries.
- Keep authorization close to protected operations.
- Use explicit error contracts; do not leak stack traces or secrets to clients.
- Apply timeouts to external calls where the stack supports them.
- Retry only transient/idempotent operations, with bounded exponential backoff and jitter.
- Do not retry permanent validation/auth errors.
- Make idempotency explicit for operations that can be repeated by clients/workers.
- Propagate cancellation/deadlines where appropriate.

## Database rules
- Use migrations for schema changes.
- Consider existing data, defaults, nullability, index cost, and rollback/forward compatibility.
- Avoid N+1 and unbounded queries on user-facing or repeated paths.
- Keep transactions as short as correctness allows.
- Enforce important invariants at the strongest practical layer.

## Workers/background jobs
- Jobs must tolerate restart and duplicate delivery when the queue semantics require it.
- Bound concurrency and resource use.
- Capture actionable failure context without sensitive payloads.
- Define retry/dead-letter/final-failure behavior.

## External integrations
Treat external data as untrusted and unavailable-by-default. Handle rate limits, malformed responses, auth expiry, partial outages, and version changes without silently manufacturing data.
