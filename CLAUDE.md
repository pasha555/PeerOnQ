# Claude Project Instructions

Follow AGENTS.md exactly. This file exists specifically for Claude / Claude Code agents.

## Reading Order

Before starting any task, read only these files first:

1. AGENTS.md
2. CLAUDE.md
3. PROJECT_MAP.md
4. ROUTES_MAP.md
5. AI_CHANGELOG.md

For frontend work, add the relevant doc from `artifacts/peeronq/`
(DESIGN_SYSTEM.md, FRONTEND_ARCHITECTURE.md, INTEGRATION_CONTRACT.md, ACCESSIBILITY.md).

Do not scan the full repository.

## What This Repo Is

- pnpm workspace monorepo. **`pnpm` only** — `npm`/`yarn` are blocked by the root `preinstall`.
- `artifacts/peeronq` — the product: React + Vite remote-access frontend prototype,
  data in `localStorage`, no live backend.
- `artifacts/api-server` — Express 5 skeleton, only `/api/healthz`.
- `artifacts/mockup-sandbox` — sandbox, not the product.
- `lib/api-client-react`, `lib/api-zod` — generated from `lib/api-spec/openapi.yaml`.

## Main Rules

- Use PROJECT_MAP.md and ROUTES_MAP.md to locate relevant files.
- Inspect only files directly related to the task.
- Make the smallest safe change.
- Do not refactor unrelated code.
- Do not change architecture unless explicitly requested.
- Do not introduce new dependencies unless explicitly approved.
- Preserve backward compatibility.
- Prefer targeted search by route, component, hook, storage key, or exact error text.
- Use design tokens from `artifacts/peeronq/src/index.css` — never raw hex colors.
- Keep the PeerOnQ prototype network-free.

## Forbidden Paths

Do not read or scan these unless explicitly required:

```text
node_modules/
dist/
build/
.next/
coverage/
logs/
backups/
runtime/
app-updates/
attached_assets/
vendor/
.git/
.local/
lib/api-client-react/src/generated/
lib/api-zod/src/generated/
artifacts/*/src/components/ui/
artifacts/mockup-sandbox/
```

Do not perform:

- full repository analysis
- broad refactors
- mass formatting
- unrelated cleanup
- destructive database changes
- security bypasses
- silent error ignoring

## Required Work Process

Before editing, state:

```text
Files I will inspect:
- <file> — <reason>
```

After editing, return exactly:

```text
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

Then update the maps that the change affects (see "Keeping the Maps Correct" in AGENTS.md)
and append an AI_CHANGELOG.md entry.

## Validation

Run only relevant checks:

```bash
pnpm run typecheck
pnpm --filter @workspace/peeronq run typecheck
pnpm --filter @workspace/peeronq run test
pnpm run build
```

If a check cannot be run, say why.

## Token Saving Behavior

- Stop reading once enough context is found.
- Do not open unrelated files.
- Do not summarize the entire repository.
- Do not duplicate explanations.
- Prefer a direct patch and a concise report.
