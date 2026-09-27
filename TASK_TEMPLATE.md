# Task Template

Copy the block below when handing a task to Codex or Claude.

```text
Read AGENTS.md, PROJECT_MAP.md, ROUTES_MAP.md, and AI_CHANGELOG.md first.
For frontend work also read artifacts/peeronq/DESIGN_SYSTEM.md.

Do not scan the full repository. Use the maps to locate files.

Task:
<the concrete problem or feature>

Area:
<peeronq frontend | api-server | lib/db | lib/api-spec | scripts>

Symptoms:
<error text, log output, screenshot description, broken behavior>

Expected behavior:
<what should happen instead>

Constraints:
- Inspect only the files you need.
- Make the smallest safe change.
- Do not refactor unrelated code.
- Do not change architecture.
- Do not introduce new dependencies.
- Use pnpm, never npm or yarn.
- Use design tokens from src/index.css, never raw hex colors.
- Keep the PeerOnQ prototype network-free.
- Preserve backward compatibility.
- Update PROJECT_MAP.md / ROUTES_MAP.md if the change affects the maps.
- Append an AI_CHANGELOG.md entry after the change.

Return exactly:

Root cause:
- ...

Files inspected:
- ...

Changed files:
- ...

What changed:
- ...

Validation:
- ...

Risk:
- ...

Rollback:
- ...
```
