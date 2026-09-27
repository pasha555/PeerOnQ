# Frontend reference

Read for web/desktop UI work.

## Principles
- Match the repository's existing component system, routing, state management, styling, and accessibility patterns.
- A control that represents a real operation must be wired to the real state/API path or clearly disabled until the capability exists.
- Avoid duplicate pages/components that expose the same action under different decorative layouts.
- Treat loading, empty, error, offline, permission-denied, and success states as first-class behavior.
- Prevent stale async results and updates after unmount/cancellation.
- Keep server authority on server-owned state; do not fake permission/security decisions client-side.
- Validate user input for usability, while retaining authoritative backend validation.
- Preserve keyboard navigation, labels, focus behavior, readable contrast, and responsive layout.
- Avoid unnecessary global state; keep state close to its owner.

## UI completion check
For each new action ask:
1. What real backend/service operation occurs?
2. What does the user see while it runs?
3. What happens on failure/timeout/cancel?
4. Can the action be triggered twice accidentally?
5. Does refreshed/restarted UI show the authoritative state?

Do not call a screen complete if it is only visual scaffolding for a requested functioning feature.
