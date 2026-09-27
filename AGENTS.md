# AI Agent Instructions

You are working inside the **PeerOnQ** repository as a senior engineer.

Primary goals:

- Understand the project using the maps first.
- Use minimum tokens.
- Avoid full repository scans.
- Make the smallest safe change.
- Preserve existing architecture.
- Do not refactor unless explicitly requested.

## Project Quick Facts

Read these before deciding which files to open:

- Monorepo of pnpm workspaces. **Use `pnpm`, never `npm` or `yarn`** — the root `preinstall`
  script rejects other package managers.
- `artifacts/peeronq` is the product: a React + Vite frontend prototype of a remote-access app.
  All data is in `localStorage`; there is no live backend.
- `artifacts/api-server` is an Express 5 skeleton with a single `/api/healthz` route.
- `artifacts/mockup-sandbox` is a throwaway sandbox — not the product, do not read it.
- `lib/api-client-react` and `lib/api-zod` are **generated** from `lib/api-spec/openapi.yaml`.
- `src/components/ui/` folders are vendored shadcn primitives — do not modify them.

Details: PROJECT_MAP.md (structure, flows, env vars) and ROUTES_MAP.md (routes, pages,
repositories, storage keys).

## Mandatory Reading Order

Before doing any task, read only these files first:

1. AGENTS.md
2. PROJECT_MAP.md
3. ROUTES_MAP.md
4. AI_CHANGELOG.md

For frontend work, also read the relevant doc in `artifacts/peeronq/`:
DESIGN_SYSTEM.md, FRONTEND_ARCHITECTURE.md, INTEGRATION_CONTRACT.md, or ACCESSIBILITY.md.

Do not scan the full repository.

## Token Control Rules

- Do not run broad operations such as:
  - read all files
  - inspect entire repo
  - summarize whole project
  - grep everything without reason
- Use PROJECT_MAP.md to locate likely files.
- Open only files directly related to the task.
- Prefer targeted search by exact route, function, component, hook, storage key, or error text.
- Stop reading once enough context is found.
- Do not read generated or vendored paths:

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

## Change Rules

Before editing:

- Identify the exact bug or requirement.
- List the files you need to inspect.
- Explain why each file is needed.

While editing:

- Make the smallest safe change.
- Do not rewrite unrelated code.
- Do not rename files, functions, routes, types, or storage keys unless required.
- Do not change formatting across whole files.
- Do not introduce new dependencies unless explicitly approved.

Project-specific rules:

- Never hardcode colors in components — use the tokens in `artifacts/peeronq/src/index.css`.
- Never add a real network call to the PeerOnQ prototype; it must stay offline
  (`src/test/noNetworkRequest.test.ts` enforces this).
- Never hand-edit files under `src/generated/`; change `lib/api-spec/openapi.yaml` and run
  `pnpm --filter @workspace/api-spec run codegen`.
- Adding a page means a page file in the matching surface directory, a route in
  `src/app/router/index.tsx`, a nav entry in that surface's layout when required, and a row in
  ROUTES_MAP.md.

After editing, return this exact format:

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

## Keeping the Maps Correct

The maps only save tokens if they stay true. In the same change:

- New/renamed route, page, endpoint, repository, or storage key → update ROUTES_MAP.md.
- New directory, env var, critical file, or flow → update PROJECT_MAP.md.
- New design token or component → update `artifacts/peeronq/DESIGN_SYSTEM.md`.
- Any non-trivial change → append an entry to AI_CHANGELOG.md.

## Versioned Development Package Publication

When the user asks to create a "patch", prepare the versioned full server upgrade bundle as part
of the deliverable; a client MSI or website-only ZIP alone does not satisfy that request unless
the user explicitly narrows the scope. The user applies server patches to production.

- Include operator-facing release notes in the bundle at
  `src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md`, and provide them alongside the artifact.
- Always provide copy-paste Linux CLI commands with each server patch: verify the exact artifact
  checksum, set its executable permission, run the installer preflight, apply the upgrade and check
  status. Use the delivered version/filename and the known server directory/environment path;
  verify the flags against the current installer. Existing installations reuse their environment
  without `--bootstrap`. State remaining signing/release gates; never bypass them. Do not wait for
  the user to request installation commands separately.
- State the server version, embedded client version/classification, server/web/portal changes,
  whether clients changed and their changes, validation results, remaining blockers and rollback.
- Follow the canonical paired Windows publication rules below for every new product release and
  embed the exact validated x64 client. Distinguish client behavior changes from a version-only
  rebuild in the notes. Server installation does not automatically upgrade installed clients.
- Preserve signature and release gates. Never describe an unsigned candidate, a failing release
  gate or an untested production deployment as ready for production.

The server patch and clients share one product release version. `Directory.Build.props` property
`PeerOnQWindowsClientVersion` is its single source of truth; do not introduce a separate server
version line. For each new product patch/release, increment the canonical version once and advance
the server and all derived client versions together, including server-only or web-only changes in
that release. This is a release rule, not a requirement to bump versions on every source commit.
Rebuild and validate matching client packages even when client behavior has not changed. Never
rename an older binary or bundle to claim it has the new version.

Never place a default client version in an individual project or build script. The server bundle's
version, client release, public-pilot package, Portable Support package, server-embedded MSI, and
local website package must use that exact canonical version; reject a mismatched set before
publication. An explicitly scoped website-only patch remains version-neutral and must not select a
client version; a new versioned product release still requires the full synchronized package set.

`PeerOnQLinuxClientVersion` derives from that canonical source version. The Linux viewer project and
portable builder must consume the derived property and reject explicit mismatches; they must not
carry independent default versions. Linux preview artifacts remain unpublished until their separate
physical-Linux capability gate passes.

`PeerOnQAndroidClientVersion` also derives from the canonical source version. Android additionally
uses the monotonic `PeerOnQAndroidClientVersionCode`; the native project and APK builder must consume
both properties. Development-signed APKs remain unpublished until physical-device, Windows-host
interop, accessibility, lifecycle/reconnect, ten-minute-session and production-signing gates pass.

`PeerOnQAppleClientVersion` also derives from the canonical source version. Apple additionally uses
the monotonic `PeerOnQAppleClientVersionCode`; the shared iPhone/iPad and Mac Catalyst project and
macOS-only builder must consume both properties and reject explicit mismatches. Apple source
previews remain unpublished until matching Xcode/.NET Apple workloads, reviewed static libvpx,
physical Mac/iPhone/iPad interop, accessibility/lifecycle, ten-minute-session, approved signing,
notarization and App Store gates pass.

When any product release bumps the canonical version, treat server and client publication as one
transaction:

- Build and payload-validate matching x64 and ARM64 installers from the same source/version.
- Publish the exact classified pair and its matching `SHA256SUMS.txt` to every intended download
  surface in the same task. Never rename a public-pilot artifact as a development or signed artifact.
- Restart each affected website/runtime so build-time version metadata is refreshed; copying a new
  MSI without a restart is incomplete.
- Verify the selected website version/classification equals the canonical version, both package URLs
  return HTTP 200, and their bytes match the published checksums.
- Build the full server bundle with that same canonical version and embed the exact validated x64
  client. Provide release notes covering both server and client changes and any remaining gates.

Do not report a client version as current when any intended consumer surface still selects an older
version. If a complete verified package pair is unavailable, fail closed and show downloads as
unavailable instead of falling back to stale files.

When creating a new Windows development MSI version, do not treat the work as complete until all
of the following have happened in the same task:

- Build and payload-validate matching x64 and ARM64 installers.
- Publish both MSI files and their matching `SHA256SUMS.txt` to
  `artifacts/peeronq/public/downloads/` when the version is meant for the local website.
- Restart the local website preview and verify both new package URLs return HTTP 200.
- Confirm the preview selects the new checksum-verified version and record the result in
  `AI_CHANGELOG.md`.

Never report a new installer version as ready if the website still exposes a previous version.

## Confirmed Fix Preservation

- The operator confirmed successful server 0.9.70 activation on 2026-09-27. Preserve the compatible
  Node 24 Docker pins and the correctly nested CR/header parsing in the installer. Keep their
  executable regression checks; do not bypass download hash/cache/TLS checks to make upgrades pass.
  See docs/CURRENT_STATE.md for the dated deployment evidence and its validation limits.
- Treat user-confirmed fixes and approved page sections as protected behavior.
- Do not redesign, revert, remove, or otherwise alter an already corrected area unless the user
  explicitly asks for that area to change or the change is strictly required to keep the requested
  new work correct and compatible.
- Every new website-distributed version must retain those confirmed fixes and publish its matching
  versioned downloads before it is reported as ready.

## GitHub synchronization

- The public, MIT-licensed source repository is `https://github.com/pasha555/PeerOnQ`.
- The user has authorized publishing this project and pushing subsequent commits to GitHub.
  After each completed commit, push the active publication branch to `origin` and verify the
  remote commit matches locally. If a push fails, report it explicitly; never claim synchronization.
- Public development starts from a clean source snapshot on `main`. The older
  `feat/phase1-remote-view` history remains local because it includes generated artifacts.
  Never push all branches, mirror the repository, or publish that archived history implicitly.
- Keep credentials, private keys, local state, logs, test reports and installers out of source
  commits. Source publication does not authorize releasing unvalidated binaries or deploying services.

## Safety Rules

- Never remove existing data migration logic without explicit approval.
- Never delete database tables or production data.
- Never change authentication, licensing, payment, or encryption behavior unless the task is
  exactly about that.
- Never weaken security checks.
- Never disable `minimumReleaseAge` in `pnpm-workspace.yaml` — it is supply-chain protection.
- Never bypass validation silently.
- Never ignore TypeScript/build errors.
- Never hide failed tests.

## Validation

Run only the checks relevant to the change:

```bash
pnpm run typecheck                            # whole workspace
pnpm --filter @workspace/peeronq run typecheck
pnpm --filter @workspace/peeronq run test     # vitest
pnpm --filter @workspace/peeronq run dev      # local preview
pnpm run build                                # typecheck + build everything
```

If a check cannot be run, say why.

## Project-Specific Priorities

This project values:

- Stability over speed
- Small patches over big rewrites
- Backward compatibility
- Visual consistency with the design system
- Honest prototype behavior (no fake "working" features)
- Clear rollback path
- Production-safe behavior

## When Unsure

Do not guess blindly. Use this process:

1. Read the nearest relevant map.
2. Inspect the smallest related file set.
3. Make a conservative change.
4. Clearly state uncertainty.

Ask a question only when the task cannot be completed safely without missing information.

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
