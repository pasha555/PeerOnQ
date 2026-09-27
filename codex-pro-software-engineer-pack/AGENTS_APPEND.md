<!-- BEGIN PRO-SOFTWARE-ENGINEER -->
## Codex engineering contract

- Prefer the smallest correct change that satisfies the task.
- Preserve working behavior unless the requested change requires otherwise.
- Inspect before editing; do not guess repository structure, APIs, schemas, commands, or configuration.
- Reuse existing architecture, utilities, components, conventions, and documentation before creating new ones.
- Do not create mock/demo/fake implementations for a requested production feature unless explicitly requested.
- Do not claim a feature is complete or production-ready when validation is missing or failing.
- Never hard-code, print, commit, or expose secrets, credentials, tokens, private keys, or production connection strings.
- Do not silently weaken authentication, authorization, encryption, OS security, validation, auditing, rate limits, or safety controls.
- Keep scope tight. Avoid unrelated refactors, dependency churn, formatting sweeps, and mass renames.
- For nontrivial changes: inspect -> plan -> implement -> targeted verify -> review diff -> broader verify when justified.
- Prefer targeted search and relevant file ranges over reading entire large files.
- Avoid dumping large logs or source files into context. Filter to the failing section or relevant symbols.
- Run the narrowest useful checks first. Run broader suites for cross-cutting, release-critical, security-sensitive, or shared-core changes.
- Use existing `PROJECT_MAP.md`, `AI_CHANGELOG.md`, ADR/decision files, or equivalent project-state docs if present; do not duplicate them.
- Final handoff should be concise: changed behavior, important files, verification performed, unresolved risks/blockers.
<!-- END PRO-SOFTWARE-ENGINEER -->
