# Token/context efficiency reference

Read when a task is large, multi-phase, or context is becoming expensive.

## Goal
Spend context on evidence needed for the current decision, not on repository narration.

## Repository inspection
- Search by task terms/symbols first.
- Use directory depth limits and Git-tracked files rather than recursive dumps.
- Read interfaces/call sites before entire implementations when possible.
- Read only the relevant region of very large files.
- Ignore generated/vendor/build output unless it is the bug source.
- Check the diff instead of rereading all edited files.

## Logs
- Start with the first meaningful error and a small surrounding window.
- Filter by request/session/error ID or component.
- Do not paste thousands of successful lines.
- If repeated errors are identical, keep one representative example plus count.

## Planning
Keep plans to 3-7 concrete steps. Do not repeat requirements already encoded in the skill or AGENTS instructions.

## Project knowledge
Reuse existing concise project maps and decision logs. Verify high-impact facts against current code.

When a long project has no useful map and repeated rescanning is demonstrably costly, propose or create a concise map only if the user asks or the repository convention supports it. Keep it factual and cheap to maintain.

## Work partitioning
For a wide task:
- split by independently verifiable subsystem;
- finish and verify one slice before expanding;
- avoid loading unrelated subsystem context simultaneously.

## Output
Do not paste code that already exists in the working tree unless asked.
Prefer:
- changed behavior;
- file paths;
- test evidence;
- blocker/risk.
