# Integration Contract

## Purpose

This document describes the future customer-portal contract for the offline prototype. The browser
frontend uses `localStorage` repositories plus the `src/services/apiClient.ts` stub. **No API,
WebSocket, or background network request may be made by the browser bundle** —
`src/test/noNetworkRequest.test.ts` enforces that. An explicitly configured public download anchor
may navigate to the Phase 6 Downloads Service only after the user clicks it; it is not a fetch or a
portal integration. Separately, the managed local Vite server may report bounded lifecycle telemetry
for an actual exact-version development-MSI GET; that server-only behavior adds no browser API call.

## Current State

| Piece | Where | Status |
| --- | --- | --- |
| Frontend stub client | `src/services/apiClient.ts` | Returns `NotConfigured` unless `VITE_PEERONQ_API_BASE_URL` is set; only exposes `ping()` |
| API server | `artifacts/api-server` | Express 5, only `GET /api/healthz` |
| Contract source of truth | `lib/api-spec/openapi.yaml` | Health check only |
| Generated React client | `lib/api-client-react` | Orval output — never edit by hand |
| Generated Zod schemas | `lib/api-zod` | Orval output — never edit by hand |
| DB schema | `lib/db/src/schema/index.ts` | Empty |
| Tracked public download origin | `VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL` | Optional absolute HTTPS origin; invalid/insecure values fail closed to unavailable |
| Local Windows release selection | `VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION` + `VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER` | Controller-only canonical version and verified package classification; both are required when local downloads are enabled |
| Published non-Windows install links | `VITE_PEERONQ_MACOS_DOWNLOAD_URL`, `VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL`, `VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL`, `VITE_PEERONQ_ANDROID_DOWNLOAD_URL`, `VITE_PEERONQ_IOS_DOWNLOAD_URL` | Optional HTTPS-only destinations selected after local device detection; remain unset until the matching signed/reviewed platform release is actually published |
| Local static-download telemetry | `server/localDownloadTelemetry.ts` | Managed-dev-server-only start/completion reporting for exact MSI GETs; disabled when its server environment is absent |

When real endpoints land, add them to `lib/api-spec/openapi.yaml`, run
`pnpm --filter @workspace/api-spec run codegen`, and consume the generated hooks from
`@workspace/api-client-react` instead of hand-writing fetch calls.

## Phase 6 boundary

The production device/install/session/diagnostics APIs are implemented separately under
`src/PeerOnQ.Cloud.Api`; the production operator SPA is `artifacts/peeronq-admin`. They do not turn
this design preview into a network client and are not routed through the legacy Express/OpenAPI
skeleton. The Windows application uses the versioned C# contracts in `PeerOnQ.Shared.Contracts` and
build-fixed HTTPS/WSS endpoints. See the root `ROUTES_MAP.md` for the exact production routes.

## Planned Endpoints (conceptual)

### Devices

- `GET /api/v1/devices` → `Device[]`
- `POST /api/v1/devices` → `Device`
- `DELETE /api/v1/devices/:id` → `204 No Content`

### Sessions

- `GET /api/v1/sessions` → `Session[]`
- `POST /api/v1/sessions/connect` → `Session`
- `POST /api/v1/sessions/:id/approve` → `Session`

### Contacts

- `GET /api/v1/contacts` → `Contact[]`
- `POST /api/v1/contacts` → `Contact`

### Settings

- `GET /api/v1/settings` → `AppSettings`
- `PUT /api/v1/settings` → `AppSettings`

Payload shapes correspond to the types in `src/types/index.ts`. Those types are the frontend's
source of truth today; when the OpenAPI spec covers them, the generated types replace them.

## Authentication

Planned: HTTP-only cookies carrying JWTs or secure session identifiers. The client will send
`credentials: "include"` on standard requests. Nothing is implemented — the API server has no
auth middleware yet.

## Websocket / Real-time

Planned for live device status and incoming connection requests:
`wss://api.peeronq.app/ws/events`. Not implemented; do not add a socket to the prototype.

## Migration Notes

Replacing localStorage with the API means swapping the repository implementations in
`src/features/*` — pages and hooks should not need to change if the repository signatures hold.
Keep `isPrototypeRecord` handling until real records exist.
