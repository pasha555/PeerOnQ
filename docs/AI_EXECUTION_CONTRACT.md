# PeerOnQ AI execution contract

This repository uses evidence, not roadmap prose, as its source of truth. The recovery contract
applies to R0 and every later phase unless a newer, explicit maintainer decision replaces it.

## Non-negotiable rules

- PeerOnQ is the only active product identity. Persisted former identifiers may survive only in a
  documented compatibility boundary with migration tests.
- The source license and runtime access are separate concerns. PeerOnQ has no license key,
  activation, subscription, payment, seat, or online-entitlement gate.
- Inspect before changing, preserve existing architecture, make the smallest safe patch, and never
  weaken authentication, authorization, encryption, validation, auditing, or supply-chain controls.
- Do not add production mocks, fake success, hidden network calls, or claims unsupported by runtime
  evidence.
- Use `pnpm`; generated clients come only from `lib/api-spec/openapi.yaml`; vendored UI primitives
  and the mockup sandbox are out of product scope.
- A physical-device or public-endpoint check cannot be replaced by a mock. Report it as
  `EXTERNALLY_BLOCKED` when the required environment is unavailable.
- Preserve machine-readable evidence under `artifacts/test-results/` when the runner supports it.

## Evidence states

Use `PASS`, `PASS_WITH_EXTERNAL_BLOCKERS`, `FAIL`, or `BLOCKED` for gates. Use `IMPLEMENTED`,
`PARTIAL`, `NOT_IMPLEMENTED`, and `EXTERNALLY_BLOCKED` for feature truth. Documentation, mockups,
test names, and a successful compile do not independently prove runtime behavior.

Every completion report records the snapshot, inspected areas, defects and root causes, changes,
migrations, protocol/security impact, exact commands and results, runtime evidence, performance,
purity, dependency/provenance impact, P0/P1/P2 issues, limitations, compatibility, blockers, and the
next-phase decision.

## Phase gates

- No phase passes with an unresolved P0 in its primary objective.
- Earlier working behavior and tests remain green.
- A feature is not advertised as complete until implementation, integration, security controls,
  UI wiring, and applicable tests exist.
- R0 precedes Phase 1 repair. Phase 7 expansion is prohibited until prior gates are truthful and
  complete.

The full operational R0 record is [R0_COMPLETION_REPORT.md](phase-reports/R0_COMPLETION_REPORT.md).
