# Accessibility

PeerOnQ is designed to be usable by everyone. This file records what is implemented, what is
tested, and what is still missing.

## Implemented

1. **Semantic HTML** — each surface layout owns its landmarks: public header/nav/main/footer,
   portal header/nav/main, and desktop preview main/header/aside.
   - Public pages include a keyboard-visible skip link to the main content.
   - The compact public menu reports `aria-expanded`, has explicit open/close labels, and mounts a
     separately labeled mobile navigation landmark.
2. **Keyboard navigation**
   - All interactive elements are reachable via `Tab`.
   - Dialogs and sheets (Radix/shadcn) trap focus and close on `Escape`.
   - The sidebar collapse toggle is a real `<button>` with `aria-label="Toggle sidebar"`.
   - Collapsed sidebar items expose their label through a Radix tooltip.
3. **ARIA attributes**
   - `role="status"` on `StatusBadge` (`components/StatusBadge.tsx`) and on the `Spinner`
     primitive, so status changes are announced.
   - Toasts announce through the live region built into the Radix Toast primitive.
   - `aria-label` on icon-only buttons — `"Copy device ID"` in `components/DeviceId.tsx`,
     `"Toggle sidebar"` in `components/Sidebar.tsx`.
4. **Forms** — fields are associated with labels via the shadcn `Form` primitives
   (`id`/`htmlFor` handled internally); errors render through `FormMessage`.
5. **Color and theme** — all colors come from the tokens in `src/index.css`, so contrast is
   controlled in one place for both light and dark themes.
6. **Reduced motion** — public CSS honors `prefers-reduced-motion` globally; a separate
   `reducedMotion` flag also exists in desktop-preview `AppSettings` and is togglable in Settings.
7. **Test IDs** — `data-testid` is used where tests need a stable handle; currently
   `components/DeviceId.tsx` owns the device-ID display hook.

8. **Development services** - the desktop-preview-only Phase 6 toolbar is a labeled complementary
   landmark; its service links sit in a labeled navigation region, retain visible keyboard focus,
   and always include text instead of relying on icons alone. Public routes never render it.

## Tested

`src/test/accessibility.test.tsx` covers roles, labels, and test hooks.
`src/test/permissionDialog.test.tsx` covers dialog keyboard behaviour: the dialog has an
accessible name, focus moves into it on open, Tab never escapes it, and Escape closes it.
Run them with:

```bash
pnpm --filter @workspace/peeronq run test
```

## Known Gaps

- The desktop sidebar is an `<aside>`; its current link groups do not yet have inner `<nav>`
  landmarks.
- No screen reader pass across major browsers yet (NVDA / VoiceOver / Narrator).
- Contrast ratios have not been measured against WCAG AA for every token pair; re-check if the
  brand palette shifts.
- The desktop-preview `reducedMotion` setting is not yet wired into every Framer Motion animation;
  public CSS independently honors the operating-system preference.
- Focus-visible styling relies on the default `--ring` token and has not been audited per component.

## Rules for New UI

- Every icon-only control needs an `aria-label`.
- Every async region needs a visible loading and empty state (see the state table in
  [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md)).
- Never remove a landmark element when restructuring layout.
- Never convey status with color alone — pair it with text or an icon.
