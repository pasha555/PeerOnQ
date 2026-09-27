# Architecture reference

Read only for architectural or cross-cutting work.

## Rules
- Identify existing boundaries before adding new layers.
- Keep domain logic out of presentation and transport code when the current architecture supports separation.
- Prefer one source of truth for configuration, state, schemas, and business rules.
- Avoid cyclic dependencies and hidden global state.
- Public interfaces should be small and stable; internal details stay internal.
- Make ownership clear for long-lived state, concurrency, caches, connections, and background tasks.
- Preserve backward compatibility unless the task explicitly authorizes a breaking change.
- For a breaking schema/API/config change, include migration/compatibility handling and document the impact.
- Do not add abstractions before there are concrete callers/duplication that justify them.
- Avoid "god" services/components. Split by responsibility, not arbitrary file size.
- Prefer explicit data flow over implicit side effects.

## Architecture review checklist
1. Where does the behavior belong today?
2. Is there already an abstraction for it?
3. What other modules depend on the changed interface?
4. What state is persisted and who owns migration?
5. What happens on restart/retry/partial failure?
6. Does the change introduce a second source of truth?
7. Can it be tested at the owning boundary?

When recommending a redesign, distinguish required changes from optional improvements.
