# Frontend Architecture

## Overview

The PeerOnQ frontend is a React 19 application on Vite, serving as a prototype that demonstrates
the UX and UI of the planned desktop agent. It has no backend: every feature is either static or
backed by `localStorage`.

## Directory Structure

```text
src/
  App.tsx           Providers (QueryClient, Theme, Tooltip, Toaster) + router mount
  app/router/       Route table + legacy redirects
  layouts/          PublicLayout, PortalLayout, DesktopPreviewLayout
  main.tsx          Root render
  index.css         Design tokens and utility classes (single source of truth)

  components/       Shared components used across pages
    ui/             shadcn/Radix primitives — vendored, do not edit
  features/         Feature-sliced domain logic (repository + hooks per feature)
    address-book/   contactRepository.ts
    connections/    useConnectionState.ts
    devices/        deviceRepository.ts, useDevices.ts
    files/          useFileTransfer.ts
    sessions/       sessionRepository.ts
    settings/       useSettings.ts
  pages/            Route components grouped by public / portal / desktop-preview
  repositories/     index.ts — barrel re-exporting the feature repositories
  services/         apiClient.ts — stub client for the future backend
  hooks/            useTheme, useLocalStorage, use-toast, use-mobile
  lib/              utils.ts (cn helper), validation.ts (Zod schemas)
  types/            index.ts — all domain types
  test/             Vitest suites + setup.ts
```

`src/repositories/index.ts` deliberately contains no logic — it re-exports
`features/*/xRepository.ts` so consumers have one import path while features stay self-contained.

## State Management

- **localStorage**: all persistent state (devices, sessions, contacts, groups, settings, theme)
  goes through the repository pattern in `src/features/*` and the `useLocalStorage` hook.
  Keys are listed in [ROUTES_MAP.md](../../ROUTES_MAP.md).
- **Component state**: `useState` / `useReducer` for ephemeral UI state.
- **Forms**: `react-hook-form` + `zod` via `@hookform/resolvers`; shared schemas in
  `src/lib/validation.ts` (notably `deviceIdSchema`, which normalizes and validates
  `XXX-XXX-XXX-XXX`, digits only).
- **TanStack Query**: the provider is mounted in `App.tsx` for future server state; no queries
  are issued today.

## Routing

`wouter`, with the route table in `src/app/router/index.tsx` and the base path taken from
`import.meta.env.BASE_URL`.

- Public routes use `PublicLayout` and never mount the desktop sidebar.
- `/app…` routes use `PortalLayout` and expose only account-level navigation.
- `/desktop-preview…` routes use `DesktopPreviewLayout`, including its permanent development
  banner, desktop sidebar, topbar, and `ErrorBoundary`.
- Dashboard is the only desktop-preview connection entry. The retired Remote Access URLs redirect to
  `/desktop-preview/dashboard` and are not shown in navigation.
- Legacy desktop paths redirect into `/desktop-preview…`.

Adding a page: create it under the matching `src/pages/<surface>/` directory, add a `<Route>` in
`src/app/router/index.tsx`, update only that surface's navigation if needed, and add a row to
`ROUTES_MAP.md`.

`Phase6DevToolbar` is rendered only inside `DesktopPreviewLayout` in Vite development when the
workspace controller supplies validated Admin, Cloud health, Grafana and Prometheus URLs. Public
marketing routes never expose operational links. The preview links are ordinary user-clicked links,
not background requests.

## Theming

- Tokens are CSS custom properties in `index.css` (`:root` and `.dark`), stored as
  space-separated HSL triples without the `hsl()` wrapper, exposed to Tailwind v4 via
  `@theme inline`.
- `src/hooks/useTheme.ts` syncs the preference (`system | light | dark`) with the
  `peeronq_theme` key and toggles the `.dark` class on `document.documentElement`.
- Components must use tokens (`bg-card`, `text-muted-foreground`, …), never raw hex values.
  See [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md).

## Network boundary

The browser preview does not call Phase 6 APIs. `VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL` may configure
the user-clicked Windows download anchor to the production Downloads Service. Optional macOS,
Linux x64/ARM64, Android and shared iOS/iPadOS install URLs may configure the same single action for
the locally detected platform after that platform's release gates pass. Every remote destination must
be absolute HTTPS and fails closed when absent or invalid; unknown devices never fall back to Windows.
The API-backed production operator frontend is
the separate `artifacts/peeronq-admin` workspace. In development, the desktop-preview-only Phase 6
toolbar links to that separately hosted SPA and loopback observability tools without weakening the
no-network boundary.
The managed Vite development server may independently report a real exact-version local MSI GET's
start and completion state to the local Downloads Service. This is server-side, user-action-triggered,
disabled without controller-provided settings, and does not add a browser fetch or background request.
The controller exposes a local download only when `Directory.Build.props`' canonical Windows client
version has one checksum-verified x64/ARM64 pair with a single explicit `unsigned-development` or
`unsigned-public-pilot` classification. Version/classification metadata is injected together; the
page fails closed instead of deriving a link from stale files.
Non-HTTPS remote tool URLs and malformed URLs fail closed and are not rendered.

## Testing

Vitest with jsdom (`src/test/`):

| Suite | Guards |
| --- | --- |
| `noNetworkRequest.test.ts` | The browser prototype makes no API/background network calls |
| `accessibility.test.tsx` | Landmarks, labels, roles |
| `demoMode.test.tsx` | `VITE_ENABLE_DEMO_DATA` behavior |
| `deviceId.test.ts` | PeerOnQ ID parsing/validation |
| `emptyState.test.tsx` | Zero-state rendering |
| `permissionDialog.test.tsx` | Connect flow only opens the notice dialog; Escape closes it; focus stays trapped |
| `routeSeparation.test.tsx` | Layout isolation, legacy redirects, native-app fallback, and safe Phase 6 dev links |
| `settings.test.ts` | Settings persistence |
| `theme.test.ts` | Theme resolution and persistence |

Run with `pnpm --filter @workspace/peeronq run test`.

`src/test/setup.ts` loads jest-dom and installs an in-memory `localStorage` when the jsdom
environment exposes an incomplete one, so persistence tests behave the same on every machine.

## Linting

`eslint.config.js` (flat config) runs `typescript-eslint` plus the React Hooks and React Refresh
plugins. Vendored shadcn output (`src/components/ui/`, `src/hooks/use-toast.ts`,
`src/hooks/use-mobile.tsx`) is ignored. The React Compiler rules
(`react-hooks/set-state-in-effect`, `react-hooks/purity`) are warnings, since the prototype loads
data inside effects by design.

Run with `pnpm --filter @workspace/peeronq run lint`.
