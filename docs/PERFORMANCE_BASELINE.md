# PeerOnQ performance baseline

## R0 measurements — 2026-08-12

Environment: Windows x64, .NET SDK 10.0.302, Node 25.2.1, pnpm 10.33.0, Docker 29.7.2. These are
developer-workstation measurements, not release service-level objectives.

| Measurement | Result |
|---|---:|
| `pnpm install --frozen-lockfile` | 2.529 s |
| `dotnet restore --locked-mode` | 5.287 s pre-change |
| full .NET Release build | 44.211 s pre-change; 0 warnings/errors |
| full .NET tests | 32.592 s pre-change; 452 pass, 2 skip |
| root TypeScript typecheck | 6.771 s pre-change |
| root lint | 32.100 s pre-change |
| root frontend tests | 43.115 s pre-change; 67 pass |
| product production JS | 668.87 KiB minified / 202.64 KiB gzip |
| Admin production JS | 276.44 KiB minified / 85.15 KiB gzip |
| Phase 6 warm-cache rebuild, migration and ready gate | 157.3 s final |

The product bundle exceeds Vite's 500 KiB warning threshold and is a P2 optimization item. Vendored
UI sourcemap warnings are visible and not suppressed.

## Historical Phase 5 runtime evidence

The repository's 2026-08-10 measured idle file records cold startup 2,929.762 ms, CPU median 1%/p95
4%, working-set median 168.465 MiB/p95 169.289 MiB, private-memory p95 100.086 MiB, and dedicated-GPU
p95 46.438 MiB. It is historical evidence for its exact artifact/environment, not an R0 remeasurement.

R0 has no synchronized physical measurements for capture-to-render latency, input latency, frame
correctness under motion, public-network file throughput, active-session CPU/GPU, or reconnect time.
Those values remain null/unclaimed. Use `scripts/windows/measure-phase5-performance.ps1` and the
method in `docs/PERFORMANCE.md`, recording artifact hash, machines, drivers, display and network.
