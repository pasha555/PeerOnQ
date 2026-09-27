# PeerOnQ test matrix

| Layer | Command/evidence | R0 status | What it does not prove |
|---|---|---|---|
| .NET build/discovery | `dotnet build/test PeerOnQ.slnx` | 30 projects build; discovery succeeds | runtime on clean/unsupported hardware |
| .NET unit/integration | 13 TRX-producing test assemblies | final: 452 passed, 2 explicit opt-in skips, 0 failed | physical/public topology |
| Offline web product | product Vitest, typecheck, lint, Vite build | final: 49 passed; network primitive invariant included | native capture/control |
| Admin SPA | Admin Vitest/typecheck/build | final: 19 passed | external SSO/browser matrix |
| API skeleton | TypeScript build | pass | production API functionality (only health skeleton) |
| Signaling/TURN loopback | Phase 3 controller and opt-in Docker tests | implemented; prior real coturn evidence exists | separate ISP/symmetric NAT |
| Phase 6 Compose | development/staging config, health/readiness | development config passes; 19 services running and health-checked services healthy; staging needs external values | production DNS/TLS/multi-host failover |
| Installer/update | extraction, hash, update/tamper/audit tests | automated/local paths exist | production Authenticode and clean VM/ARM64 lifecycle |
| Security | secret scan, pnpm audit, .NET vulnerable packages, fuzz/replay/redaction | R0 scans pass | full-history scan, CodeQL host run, penetration test |
| Brand | `scripts/quality/test-brand-purity.ps1` | pass | third-party historical text outside repository |
| Accessibility | component/route tests and manual checklist | partial | complete keyboard/screen-reader/zoom audit |
| Physical Windows | two machines, capture/render/control/reconnect/file/clipboard | `EXTERNALLY_BLOCKED` in R0 | cannot be substituted |
| Public downloads | public MSI bytes/content headers/hash/install | `EXTERNALLY_BLOCKED` in R0 | local route mocks are insufficient |

## Phase 6.5 local validation (2026-08-18)

| Layer | Command/evidence | Result | What it does not prove |
|---|---|---|---|
| Full .NET solution | `dotnet test --configuration Release --no-build` | 622 passed, 5 explicit external/opt-in skips, 0 failed | physical/public topology or long soak |
| Release build | `dotnet build --configuration Release --no-restore` | 30 projects, 0 warnings, 0 errors | clean-VM install and signed distribution |
| Windows capture E2E | WGC collection in `PeerOnQ.EndToEnd.Tests` | 49 passed, 0 failed | a second physical display/device or sustained 4K/60 FPS |
| Offline frontends | pnpm lint/typecheck/test/build | 81 tests passed; lint/typecheck/build passed | native screen capture/control |
| Dependency security | .NET vulnerable packages, pnpm production audit/licenses, repository secret scan | no known vulnerability; license inventory and 985-file secret scan passed | exact release-native SBOM and legal compatibility |
| Formatting | scoped Phase 6.5 C# formatter; full solution formatter | scoped pass; full check blocked by two pre-existing whitespace findings in unchanged `CustomerOrganizationService.cs` | unrelated formatter debt |

The five .NET skips are four opt-in Docker/distributed signaling tests and one live TURN media test.
No skipped external test is counted as passed.

Machine-readable baseline is under `artifacts/test-results/r0-baseline/`; final TRX/JUnit output is
under `artifacts/test-results/r0-final/`. A skipped external test is never counted as passed.
