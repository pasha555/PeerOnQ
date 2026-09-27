# PeerOnQ Design System

This document describes the visual language, color tokens, typography scale, and component inventory used throughout the PeerOnQ frontend prototype.

---

## Brand Identity

PeerOnQ uses a compact Q-link mark: a continuous connection ring with two equal peer endpoints.
The mark must remain readable at 16 px and must not gain gradients, fine detail, or text inside the
icon. The canonical source is `scripts/windows/generate-peeronq-brand.ps1`, which deterministically
produces the web SVG set, Windows ICO frames, favicon, and WiX artwork.

| Asset | Usage |
| --- | --- |
| `public/brand/peeronq-mark.svg` | Light-surface navigation and product mark |
| `public/brand/peeronq-mark-light.svg` | Dark-surface navigation |
| `public/brand/peeronq-wordmark.svg` | Light-surface wordmark; outlined paths, no font dependency |
| `public/brand/peeronq-wordmark-light.svg` | Dark-surface wordmark |
| `public/brand/peeronq-lockup*.svg` | Horizontal marketing lockups |
| `public/favicon.svg` | Navy app tile with white/green Q-link mark |

The identity colors are secure green `#198754`, trust navy `#0F2E46`, and white. UI components must
continue to use semantic CSS tokens instead of these literal asset colors.

---

## Color Tokens

All colors are defined as CSS custom properties in `src/index.css` using HSL space-separated values (no `hsl()` wrapper). Tailwind consumes them via `@theme inline`.

### Palette — Light Mode

| Token | Value (HSL) | Usage |
| --- | --- | --- |
| `--background` | `0 0% 98%` | Page background |
| `--foreground` | `222 14% 14%` | Primary text |
| `--card` | `0 0% 100%` | Card surface |
| `--card-foreground` | `222 14% 14%` | Card text |
| `--card-border` | `220 13% 92%` | Card border |
| `--primary` | `155 50% 38%` | Brand green — buttons, links, active states |
| `--primary-foreground` | `0 0% 100%` | Text on primary |
| `--secondary` | `220 14% 95%` | Subtle surface alternative |
| `--secondary-foreground` | `222 14% 24%` | Text on secondary |
| `--muted` | `220 13% 96%` | Muted surfaces |
| `--muted-foreground` | `220 9% 48%` | Secondary/caption text |
| `--accent` | `155 50% 38%` | Same as primary (accent interactions) |
| `--destructive` | `0 78% 58%` | Errors, delete actions |
| `--success` | `153 56% 42%` | Success states, confirmations |
| `--warning` | `38 90% 52%` | Warnings, caution messages |
| `--border` | `220 13% 92%` | Default borders |
| `--input` | `220 13% 92%` | Input borders |
| `--ring` | `155 50% 38%` | Focus ring |
| `--sidebar` | `220 14% 97%` | Sidebar background |
| `--sidebar-primary` | `155 50% 38%` | Active nav item |

### Palette — Dark Mode

| Token | Value (HSL) | Usage |
| --- | --- | --- |
| `--background` | `222 20% 9%` | Dark page background |
| `--foreground` | `210 14% 89%` | Primary text |
| `--card` | `222 18% 13%` | Dark card surface |
| `--card-border` | `222 14% 20%` | Dark card border |
| `--primary` | `153 52% 46%` | Brighter green for dark contrast |
| `--muted` | `222 14% 16%` | Dark muted surface |
| `--muted-foreground` | `220 9% 54%` | Dark secondary text |
| `--sidebar` | `222 22% 11%` | Dark sidebar |
| `--border` | `222 14% 20%` | Dark borders |

### Semantic Color Usage

- **Success**: Confirmations, security checks, online status → `text-success`, `bg-success/10`
- **Warning**: Prototype notices, pending approvals → `text-warning`, `bg-warning/10`
- **Destructive**: Delete, disconnect, blocked → `text-destructive`, `bg-destructive/10`
- **Muted foreground**: Secondary labels, captions, timestamps

### Public Marketing Tokens

The public website uses a light surface with navy text, green actions and dark navy client/security
panels; `.dark .public-site` supplies its dark variant. Overrides live on `.public-site` in
`src/index.css`, so the desktop preview retains its existing theme. Components consume semantic
Tailwind tokens rather than literal colors.

| Scoped token | Public light | Public dark |
| --- | --- | --- |
| `--background` | `210 25% 98%` | `222 20% 9%` |
| `--foreground` | `207 65% 17%` | `210 25% 94%` |
| `--primary` | `153 69% 31%` | `153 52% 58%` |
| `--primary-foreground` | `0 0% 100%` | `222 20% 9%` |
| `--muted-foreground` | `215 16% 42%` | `215 16% 72%` |
| `--border` | `214 20% 88%` | `215 18% 25%` |

| Token | Usage |
| --- | --- |
| `--marketing-hero` | Dark client illustration and security section (`207 62% 13%` on `.public-site`) |
| `--marketing-foreground` | Light primary copy on dark panels; inherited from the base theme |
| `--marketing-muted` | Secondary panel copy (`210 22% 76%`) |
| `--marketing-panel` | Client illustration surfaces (`207 46% 18%`) |
| `--marketing-line` | Dark panel separators and outlines (`207 30% 28%`) |

The landing page combines an explicitly captioned client-workflow illustration, four numbered native
access-mode rows, direct/relay/recovery/diagnostics/update copy, the real customer-portal entry,
security boundaries, MIT/public-source links, device-aware downloads and native `<details>` FAQ.
Section layouts use dividers, editorial rows and split columns rather than repeated feature cards.
The illustration uses the canonical Q-link mark and native
Segoe font family; its sidebar and permission choices are explanatory, not working remote controls
or a fabricated live session. Effects are limited to a low-opacity radial hero wash, subtle borders,
shadows and color transitions.

`.public-container` caps content at 78rem with fluid side padding; `.public-section`,
`.public-heading` and `.public-eyebrow` define responsive spacing/type. `.public-button` supplies
46px minimum action height and token-based primary/secondary variants. Public links, buttons and
FAQ summaries have an explicit `:focus-visible` outline. The mobile menu exposes expanded state,
closes on Escape and returns focus to its toggle; it scrolls within short viewports and exposes the
same Portal/Sign in and download anchors. Full navigation starts at `xl` to leave room for header
actions. The skip-link target is programmatically focusable; the viewport permits browser zoom.

The hero contains the one device-selected package action alongside Open Portal. Header/mobile
Download PeerOnQ links navigate to `#client-download`; `#download` explains platform status and links
back to that selector. `DownloadsPage` displays detected device/architecture, only known release versions and
explicit unsigned classifications, or an honest unavailable state. It never invents a signed badge
or a current version for server-owned/tracked releases. Sign in and Portal links navigate to the
separately hosted customer portal; operational Admin/monitoring links remain absent.
`prefers-reduced-motion` reduces transitions and animations globally.

### Customer Portal

The real customer portal keeps its standalone token stylesheet at
`artifacts/peeronq-portal/src/styles.css`. It uses the same canonical light/dark SVG lockups, Segoe
family, green actions and navy structure. Its dark background/foreground/primary match the public
palette. `AuthFrame` shares branding, public/download navigation and theme controls across sign-in,
registration, recovery and verification. `PortalThemeProvider` applies the stored preference before
sign-in or follows the system theme; only theme/organization preferences are persisted, never tokens.

Overview separates account identity/security, a compact count row, and the active organization's
devices/history. Each count has its own loading/error/retry feedback. Account and Organization
navigation use explicit Sign-in sessions, Trusted sign-in devices and Managed devices labels.
Profile lives at `/profile`; the older `/account` URL remains supported.

Policy controls use readable labels and adjacent descriptions while preserving API field names and
server values. Destructive actions use a native modal `ConfirmAction` dialog with initial Cancel
focus, pending/error feedback and focus restoration. The mobile navigation makes background controls
inert, locks page scrolling, closes on Escape and restores focus after removing inert state.
Reduced-motion, keyboard outlines, responsive tables and light/dark semantic statuses remain active.

---

## Typography

Font stack: `'Segoe UI Variable', 'Segoe UI', system-ui, -apple-system, BlinkMacSystemFont, sans-serif`.
Production pages use the system stack and make no third-party font request.

### Utility Classes

| Class | Size | Weight | Usage |
| --- | --- | --- | --- |
| `.text-display` | `3rem` | 700 | Hero headlines |
| `.text-page-title` | `1.875rem` | 700 | Page `<h1>` |
| `.text-section-title` | `1.25rem` | 600 | Section `<h2>` |
| `.text-card-title` | `1rem` | 600 | Card headings |
| `.text-body` | `0.9375rem` | 400 | Body copy |
| `.text-secondary-body` | `0.875rem` | 400 | Descriptions |
| `.text-caption` | `0.75rem` | 400 | Labels, timestamps |
| `.text-button-label` | `0.875rem` | 500 | Button text |
| `.font-device-id` | — | — | Monospace PeerOnQ IDs |

### Tailwind Heading Conventions

| Level | Classes |
| --- | --- |
| Page title | `text-3xl font-bold tracking-tight` |
| Section | `text-xl font-semibold` |
| Card | `text-base font-semibold` |
| Caption | `text-xs text-muted-foreground` |

---

## Border Radius

`--radius: 0.5rem` (8 px base)

| Tailwind token | Value | Use |
| --- | --- | --- |
| `rounded-sm` | 4 px | Badges, tags, small chips |
| `rounded-md` | 6 px | Inputs, small buttons |
| `rounded-lg` | 8 px | Buttons, cards |
| `rounded-xl` | 12 px | Large cards, panels |
| `rounded-2xl` | 16 px | Modals, drawers |
| `rounded-full` | 9999 px | Avatars, status dots |

---

## Shadows

Light mode uses green-tinted low-opacity shadows for warmth:

| Token | Use |
| --- | --- |
| `--shadow-sm` | Subtle card lift |
| `--shadow` | Hover elevation |
| `--shadow-md` | Dropdown/popover |
| `--shadow-lg` | Modal/sheet |

Tailwind: `shadow-sm`, `shadow`, `shadow-md`, `shadow-lg`.

---

## Spacing

Tailwind's default 4 px grid. Key layout values:

| Pattern | Value |
| --- | --- |
| Page content max-width | `max-w-7xl mx-auto` |
| Section vertical gap | `space-y-8` |
| Card internal padding | `p-5` or `p-6` |
| Card grid gap | `gap-4` or `gap-6` |
| Topbar / sidebar header height | `h-16` (64 px) |
| Sidebar expanded width | `w-64` (256 px) |
| Sidebar collapsed width | `w-16` (64 px) |

---

## Component Inventory

### Layout

| Component | File | Description |
| --- | --- | --- |
| `PublicLayout` | `layouts/PublicLayout.tsx` | Scoped public theme, responsive in-page/portal navigation, keyboard menu, skip link, content landmark, GitHub and legal footer |
| `PublicPageHero`, `SectionHeading`, `MarketingCta` | `components/PublicMarketing.tsx` | Retained public primitives; legal pages use `PublicPageHero`, while the landing page owns its current section composition |
| `ClientPreview` | `pages/LandingPage.tsx` | Captioned static native-workflow illustration using existing brand assets and permission terminology |
| `DownloadsPage` | `pages/DownloadsPage.tsx` | One device-matched package action with known release details, unsigned warning and explicit unavailable state |
| `DesktopPreviewLayout` | `layouts/DesktopPreviewLayout.tsx` | Desktop sidebar/topbar shell with permanent preview banner |
| `Sidebar` | `components/Sidebar.tsx` | Desktop-preview-only collapsible nav with icon+label items |
| `Topbar` | `components/Topbar.tsx` | Search, theme, notifications, user menu |
| `PageHeader` | `components/PageHeader.tsx` | Consistent `<h1>` + description per page |
| `Phase6DevToolbar` | `components/Phase6DevToolbar.tsx` | Desktop-preview-only, token-based development links to the real Admin, Cloud health and observability surfaces; never rendered on public routes or in production |

### Data Display

| Component | File | Description |
| --- | --- | --- |
| `DeviceId` | `components/DeviceId.tsx` | Monospace PeerOnQ ID with copy button |
| `DeviceCard` | `components/DeviceCard.tsx` | Device summary card (name, status, OS) |
| `StatusBadge` | `components/StatusBadge.tsx` | online / offline / unknown pill |
| `SecurityCard` | `components/SecurityCard.tsx` | Security feature with planned badge |
| `SessionTable` | `components/SessionTable.tsx` | History table with direction/mode/state |
| `PlatformCard` | `components/PlatformCard.tsx` | Legacy platform-status card; the home download section owns the current device-matched action |
| `EmptyState` | `components/EmptyState.tsx` | Consistent zero-state with icon + CTA |
| `DeviceDetailDrawer` | `components/DeviceDetailDrawer.tsx` | Sheet with device details, connect and remove actions |

### Forms & Actions

| Component | File | Description |
| --- | --- | --- |
| `ConnectionForm` | `components/ConnectionForm.tsx` | Remote ID input + mode selector |
| `ThemeSelector` | `components/ThemeSelector.tsx` | Light / Dark / System toggle |
| `SettingsSection` | `components/SettingsSection.tsx` | Settings group container |
| `ConfirmDialog` | `components/ConfirmDialog.tsx` | Destructive action confirmation |

### Feedback

| Component | File | Description |
| --- | --- | --- |
| `PermissionDialog` | `components/PermissionDialog.tsx` | Prototype connection notice modal |
| `OpenAppButton` | `components/OpenAppButton.tsx` | Attempts `peeronq://open`, then offers download/help/portal fallback |
| `PreviewNotice` | `components/PreviewNotice.tsx` | Amber banner explaining prototype state |
| `ErrorBoundary` | `components/ErrorBoundary.tsx` | React error boundary with retry + copyable error ID |
| `ToastProvider` | `components/ToastProvider.tsx` | Wraps the tree and mounts the toast outlet once (used in `App.tsx`) |
| `Toaster` | `components/ui/toaster.tsx` | Toast outlet rendered by `ToastProvider` |

### Production Admin SPA

| Component | File | Description |
| --- | --- | --- |
| `ActionDialog` | `artifacts/peeronq-admin/src/components/ActionDialog.tsx` | Accessible, server-authorized confirmation surface for audited privileged actions |
| `DialogFrame` | `artifacts/peeronq-admin/src/components/ActionDialog.tsx` | Shared native modal frame used for confirmations and read-only diagnostic metadata |
| `WebsiteUpgradePanel` | `artifacts/peeronq-admin/src/components/WebsiteUpgradePanel.tsx` | Responsive active/rollback website cards plus accessible signed-ZIP publication and audited activation dialogs |
| `UpgradePage` | `artifacts/peeronq-admin/src/pages/UpgradePage.tsx` | Responsive full-platform status, native progress, signed bundle staging and exact-version Owner confirmation surfaces driven only by durable host-agent state |

### Production Customer Account Portal

The separate `artifacts/peeronq-portal` SPA mirrors the PeerOnQ navy/green identity without importing
the public prototype or vendored shadcn tree. Its source of truth is `src/styles.css`: semantic HSL
tokens (`background`, `foreground`, `surface`, `subtle`, `border`, `muted`, `primary`, `danger`,
`success`, `info`, `focus`) have light/dark values. It uses the Segoe system font, canonical Q-link
lockup with a Portal label, a navy topbar, a 244 px sidebar and responsive card/form grids. Its
styles and local brand assets are separate from the public-site CSS and offline desktop preview.

| Component | File | Description |
| --- | --- | --- |
| `Shell` / `OrganizationProvider` | `artifacts/peeronq-portal/src/shell.tsx` | Customer-only responsive navigation, organization scope, theme and logout |
| `Page` | `artifacts/peeronq-portal/src/components.tsx` | Consistent page title, description, actions and content region |
| `LoadingState` | `artifacts/peeronq-portal/src/components.tsx` | Accessible `role=status` progress state |
| `ErrorState` | `artifacts/peeronq-portal/src/components.tsx` | Accessible `role=alert` with optional retry |
| `EmptyState` | `artifacts/peeronq-portal/src/components.tsx` | Explicit zero-data state; never substitutes fake records |
| `Notice` | `artifacts/peeronq-portal/src/components.tsx` | Info/success/danger feedback using semantic tokens |
| `Brand` | `artifacts/peeronq-portal/src/brand.tsx` | Canonical lockup and explicit Portal surface label |
| `OverviewPage`, `DevicesPage`, `RemoteSessionsPage` | `artifacts/peeronq-portal/src/workspacePages.tsx` | Organization-scoped account overview, real device data and recorded remote-session history; missing data remains loading/error/empty |

Primary portal buttons and text inputs have 44 px minimum heights; focus rings, reduced-motion
behavior, native labels, focusable table overflow and textual security states remain explicit.
Remote-session history is separate from signed-in account sessions; portal account/device visibility
does not grant native remote control. Billing/pricing and simulated live remote controls are absent.

---

## Icon System

All icons are from `lucide-react`. Key icon conventions:

| Context | Icon |
| --- | --- |
| Dashboard / Home | `LayoutDashboard` |
| Devices | `Server` |
| Sessions | `History` |
| File Transfer | `FolderOpen` |
| Address Book | `BookUser` |
| Security | `ShieldCheck` |
| Settings | `Settings2` |
| Download | `Download` |
| Help | `HelpCircle` |
| Success | `CheckCircle2` |
| Warning | `AlertTriangle` |
| Error | `AlertCircle` |
| Copy | `Copy` / `Check` (on success) |

---

## State Patterns

Every async / conditional UI region must handle all six states:

| State | Visual treatment |
| --- | --- |
| **Idle** | Default content or empty state |
| **Loading** | Skeleton placeholders or spinner |
| **Empty** | `EmptyState` component with action |
| **Error** | Error card with retry button |
| **Not configured** | `PreviewNotice` + honest message |
| **Success** | Content + optional success toast |

---

## Theme System

Managed by `src/hooks/useTheme.ts`:

- Persists to `localStorage` key `peeronq_theme`
- Values: `"system"` | `"light"` | `"dark"`
- Applies `.dark` class on `document.documentElement`
- Respects `prefers-color-scheme` when set to `"system"`

### Native Windows mirror

The production WinUI client mirrors the same light surface, green brand accent, 8/10 px card radius,
sidebar navigation, typography hierarchy, and semantic status treatment in
`src/PeerOnQ.App/App.xaml`. It starts in the product's light theme and exposes a keyboard-accessible
theme toggle. Its three connection-mode cards are equal-width, icon-led, and stay on one row at the
supported desktop width. Primary actions and mode cards use a consistent 10 px Fluent corner radius,
and every product page fills a centered 1240 px maximum content container without horizontal drift.
Dashboard is the single connection surface: the device/status/performance
stack balances the session form, and security cards span the full content width below it. Branded
`ContentDialog` forms inherit the active theme, green focus treatment, validation, and safe default
button hierarchy. Native controls remain WinUI controls; all seven `MainWindow.xaml` product pages
bind actions, status, history, security, and diagnostics to the real local agent rather than prototype
data.
The incoming connection dialog uses one compact 400-440 px Fluent layout with theme-aware trust
navy and secure green selection surfaces for light, dark, and high-contrast modes. Requester identity,
concise permission rows, local request time, and timeout remain visible without a decorative hero or
verbose protection card. Color never carries the decision alone: the native radio state, exact
permission summary, and exact approval button label remain visible, and no attended access option is
selected by default.
The native remote viewer groups display sizing under one labeled Fluent toolbar menu with four
mutually exclusive choices: Fit to window, Fill window, Stretch to window, and Actual size. The
active choice remains visible in the toolbar and exposes an updated automation name.
The native connection form keeps View Only, Full Control, and File Transfer as equal primary cards;
the file-transfer surface exposes only local file/folder selection and its transfer queue, while the
plain-text clipboard control is omitted. Unattended access remains a separate default-off
credential/trust workflow. Address-book edit/remove and group
rename/delete use native dialogs, visible labels, keyboard focus, and Cancel as the destructive
default. Saving an address never presents it as trusted, and unauthenticated OS metadata is shown as
unavailable rather than inferred from user input.
Support Invitation is a separate mutually exclusive connection mode with a visible link/password
panel and explicit remote-approval explanation. Security uses the existing card, field, button and
live-status patterns for create/copy/revoke. Portable Support keeps the same native surface but
visibly labels Development / Unsigned and disables unattended controls. The viewer reports measured
resolution/FPS/bitrate/health without inventing unavailable values; file-transfer priority is one
labeled three-option native ComboBox whose help text preserves input/security precedence.
The native NavigationView uses its platform Auto display mode so the pane collapses at narrower
desktop widths, header/status regions grow with system text, and live connection states are exposed
politely to assistive technology. Settings exposes appearance, diagnostics, verified updates, and an
original About/local-data card with the exact runtime version, MIT/no-activation disclosure, and a real
File Explorer action for the current data folder. Deployment-managed service endpoints stay internal
and are not rendered as editable or read-only Settings content. Verified Updates initializes with
its check action disabled, then enables it only when a complete trusted server update configuration
is authoritative; check, download, and install retain distinct progress/error/confirmation states.

### Native Linux viewer mirror

The Avalonia Linux viewer in `src/PeerOnQ.App.Linux` uses the dark product tokens, green primary
action/status accent, neutral remote canvas, 8/12 px radii and system font stack. Its compact
two-column desktop layout keeps connection controls separate from the remote viewport. Server and
session states always have visible text (not color alone), loading is explicit, failed actions stay
retryable, and unavailable capabilities are omitted rather than displayed as working controls.
Interactive controls expose automation names and keyboard focus. Remote input is an explicit,
default-off toggle with a persistent active badge and `Ctrl+Alt+Shift+Esc` release instruction;
window deactivation also disables forwarding. The Linux viewer does not visually claim Linux host,
file, clipboard, unattended, updater or installer support.

---

## Prototype Conventions

| Pattern | Implementation |
| --- | --- |
| Prototype data badge | `<Badge>Local prototype record</Badge>` |
| Planned feature | `<Badge variant="outline">Planned</Badge>` |
| Preview notice | `<PreviewNotice />` component (amber) |
| No real connections | `<PermissionDialog />` on connect |
| Demo data | `import.meta.env.VITE_ENABLE_DEMO_DATA` |
| No API when unconfigured | `apiClient` returns `NotConfigured` |
## Cross-surface product terminology

Public pages, Account Portal and the Windows client use **View Only**, **Full Control**,
**File Transfer**, **Unattended Access**, **Remote Device ID**, **Verified Updates** and **Diagnostics**
for the same capabilities. **PeerOnQ ID** names the local/public device identifier. Portal account
activity is **Sign-in sessions**; native **Sessions** and portal **Remote sessions** refer to remote
connections. Account sign-in and trusted sign-in devices never imply device enrollment or unattended
permission. **Open Account Portal** is optional external browser navigation from native Settings/About
and the offline preview; LAN use remains accountless.

Keep the existing canonical Q-link assets, Segoe/system typography, green primary action, semantic
status colors and light/dark resources. Native controls retain WinUI spacing, icons, focus and high
contrast behavior. The public and portal layouts retain their existing responsive web spacing and
Lucide icons. MIT describes PeerOnQ's source; dependencies retain their own license terms.
Native version labels use assembly metadata, download labels use verified package metadata, and
offline preview devices show that their installed version is unknown.

## Native Android viewer mirror

`src/PeerOnQ.App.Android/Resources/values/` is the Android-native mirror of the existing PeerOnQ
visual language. It uses system sans typography and native Material controls; no web font or Android
UI dependency is added. Resource tokens, never component-local literals, define dark background,
surface/alternate surface, green primary/focus, primary foreground, main/muted/error text, viewport,
4/8/16/24dp spacing, 48dp minimum touch targets and 14/16/24sp type roles. The launcher vector is
derived from the canonical Q-link mark and uses the documented brand navy/green/white asset colors.

The Activity is mobile-first: portrait uses one control column above an aspect-fit remote Surface;
landscape/tablet uses a two-pane control/viewer layout. Every input has a visible label, status and
errors use text plus color and accessibility live regions, async work has a progress indicator, and
control forwarding is an explicit switch that defaults off. The remote Surface and controls retain
system focus/press behavior; critical actions are visible rather than gesture-only. Touch targets
are at least 48dp and the normal system bars keep controls outside gesture/notch unsafe areas.

The Android surface is intentionally an attended viewer/controller. UI must not expose host,
unattended, file, clipboard, updater or background-service controls until their capability and
native evidence gates are implemented.

### Native Apple viewer mirror

`src/PeerOnQ.App.Apple/AppleTheme.cs` is the semantic native color boundary for the shared iPhone,
iPad and Mac Catalyst viewer. It mirrors background, surface, muted surface, primary, on-primary,
text, muted text, border, destructive and neutral viewport roles with dynamic light/dark providers;
component code does not carry separate color literals. UIKit preferred text styles provide Dynamic
Type and the system font without adding a web font or UI dependency.

The phone layout is safe-area-aware and stacks a scrollable connection surface above an aspect-fit
remote viewport. Landscape, iPad and Mac switch to two panes while the control pane remains
scrollable at large accessibility text sizes. Native controls retain at least 44 pt targets, visible
labels, disabled semantics and pressed feedback. Status/error messages include text, activity is
explicit, and errors are announced to accessibility services. The first session segment is View
only; Full Control still requires remote approval, fresh focus acknowledgment and a separate
default-off local input switch.

Pointer gestures are confined to the remote viewport and mapped against the rendered aspect-fit
rectangle. Leaving the foreground disables forwarding, releases held state and ends the attended
session. The Apple UI must not expose host, unattended, file, clipboard, updater or background-mode
controls until their native capabilities and physical Apple evidence gates exist.
