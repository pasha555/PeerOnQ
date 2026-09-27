# Codex Pro Software Engineer Pack

A repo-scoped Codex skill designed for professional software development with low context/token waste.

## Install

1. Extract this package into the **root of your repository**.
2. Keep the `.agents/skills/pro-software-engineer/` path exactly as-is.
3. If your repository already has `AGENTS.md`, do **not** overwrite it. Ask Codex to merge the contents of `AGENTS_APPEND.md` into the existing file.
4. Start a new Codex session if the skill is not immediately visible.
5. Invoke it with:

   `$pro-software-engineer`

Codex can also select the skill implicitly when a task matches its description.

## Recommended first run

Paste the contents of `FIRST_RUN_PROMPT.txt` into Codex.

## Why this pack is token-efficient

- The main `SKILL.md` stays compact.
- Longer rules are split into `references/`.
- Codex is explicitly told **not** to read every reference.
- Repository inspection is search-first and scope-first.
- Large logs/files should be sliced, filtered, or summarized instead of dumped.
- Existing project maps/changelogs are reused instead of creating duplicate documentation.
- Targeted tests run before full-suite tests unless the change is broad or release-critical.

## Important

This skill improves process consistency; it cannot guarantee that code is bug-free, secure, or production-ready. Production claims must be backed by successful verification and evidence.
