# PeerOnQ

PeerOnQ is a secure remote-access platform prototype built to showcase premium frontend design
for professional applications. "Connect securely. Work anywhere."

## Overview

This is a **frontend-only prototype**. All data is persisted in `localStorage`, and the app makes
no network requests — `src/test/noNetworkRequest.test.ts` fails the build if that changes.
`src/services/apiClient.ts` returns a `NotConfigured` result unless `VITE_PEERONQ_API_BASE_URL`
is set.

## Stack

- React 19 + TypeScript
- Vite + TailwindCSS v4 (`@tailwindcss/vite`)
- Wouter (routing)
- shadcn UI (Radix primitives)
- Framer Motion
- React Hook Form + Zod
- TanStack Query (provider mounted, no live queries yet)
- Vitest + Testing Library

## Commands

Run from the repo root (pnpm workspace — never use npm/yarn):

```bash
pnpm --filter @workspace/peeronq run dev        # dev server
pnpm --filter @workspace/peeronq run build      # production build
pnpm --filter @workspace/peeronq run serve      # preview the build
pnpm --filter @workspace/peeronq run typecheck
pnpm --filter @workspace/peeronq run lint
pnpm --filter @workspace/peeronq run test
```

`vite.config.ts` requires `PORT` and `BASE_PATH` to be set and throws otherwise. The Replit
artifact config supplies `PORT=23586` and `BASE_PATH=/`; outside Replit, set them yourself:

```bash
PORT=23586 BASE_PATH=/ pnpm --filter @workspace/peeronq run dev
```

## Environment

See `.env.example`:

| Variable | Purpose |
| --- | --- |
| `VITE_PEERONQ_API_BASE_URL` | API base URL; empty means `apiClient` reports `NotConfigured` |
| `VITE_ENABLE_DEMO_DATA` | `true` shows hardcoded demo session rows on `/dashboard` and `/sessions` (never written to storage) |
| `VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE` | `true` exposes generated local-test x64/ARM64 MSI files; the dev controller enables it only when both packages and their SHA-256 values match |
| `VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION` | Exact version of the verified local x64/ARM64 pair; required when local Windows downloads are enabled |
| `VITE_PEERONQ_MACOS_DOWNLOAD_URL` / `VITE_PEERONQ_ANDROID_DOWNLOAD_URL` / `VITE_PEERONQ_IOS_DOWNLOAD_URL` | Optional HTTPS-only published install destinations selected for matching Apple/Android devices; keep empty until the corresponding release gate passes |
| `VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL` / `VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL` | Optional HTTPS-only published Linux packages selected by detected architecture; keep empty until the matching physical-Linux gate passes |

## Feature Status

| Area | Route | Status |
| --- | --- | --- |
| Landing page | `/` | Complete (static) |
| Dashboard | `/dashboard` | Complete (single connection form, static highlights; sessions shown only in demo mode) |
| Retired Remote Access URL | `/remote-access` | Redirects to Dashboard; no duplicate navigation/page |
| Devices | `/devices` | Complete (localStorage) — search, status filter, grid/list, detail drawer |
| Sessions | `/sessions` | Complete (localStorage) — direction, mode, and date-range filters |
| File Transfer | `/files` | Layout only — no native file access, controls disabled |
| Address Book | `/address-book` | Complete (localStorage) — groups, tags, favorites, add/edit/remove |
| Security | `/security` | Complete (mocked settings; 2FA is a planned feature) |
| Settings | `/settings` | Complete (localStorage) |
| Downloads | `/downloads` | Selects the matching Windows, macOS, Linux, Android or iOS/iPadOS release when its verified HTTPS destination is configured; otherwise unavailable honestly |
| Pricing / About / Privacy / Terms | see routes | Complete (static) |

Full route table: [ROUTES_MAP.md](../../ROUTES_MAP.md).

## Docs

| File | Contents |
| --- | --- |
| [FRONTEND_ARCHITECTURE.md](FRONTEND_ARCHITECTURE.md) | Directory layout, state, routing, theming |
| [DESIGN_SYSTEM.md](DESIGN_SYSTEM.md) | Color tokens, typography, spacing, component inventory |
| [INTEGRATION_CONTRACT.md](INTEGRATION_CONTRACT.md) | Planned backend contract |
| [ACCESSIBILITY.md](ACCESSIBILITY.md) | Accessibility implementation and gaps |
