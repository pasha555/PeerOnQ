# Phase 6.5 performance report

Status date: 2026-08-18. This report separates measured results from targets.

## Measured before source changes

The `0.9.20.0` x64 idle client was measured for 15 seconds on Windows 11 Pro, Ryzen 7 3700X,
Radeon RX 5600 XT and a 1920x1080 display. Startup was 837.765 ms; CPU median/P95 was 0%/3%;
working-set median/P95 was 200.340/201.027 MiB; private-memory P95 was 129.551 MiB; dedicated GPU
memory P95 was 52.469 MiB. Evidence is
`benchmarks/phase6.5-idle-local-2026-08-18-v0.9.20.json`.

## Implemented controls

- one-frame newest-state encoder queue and one pending render frame;
- capture pacing before 4K GPU readback/conversion;
- RTCP/backpressure adaptation that lowers FPS before resolution;
- four healthy windows before recovery and ten seconds between resolution changes;
- explicit 720p/1080p/1440p/4K non-upscaling targets;
- actual stage FPS, dimension provenance, codec/hardware truth and local latency segments.

## Automated evidence

The complete Release .NET run passed 622 tests, skipped five explicitly external/opt-in tests and
failed none. This includes Media 66 passed plus one live-TURN skip, Application 126 passed,
Signaling 107 passed plus four distributed/Docker skips, Infrastructure 80 passed and Windows
end-to-end 49 passed. The complete frontend run passed 81 tests. All 30 Release projects build with
zero warnings/errors; frontend lint, typecheck and build pass.

## Missing measurements

No current physical two-device artifact proves active CPU/GPU, input-to-photon latency, sustained
1440p/4K or 60 FPS, WAN/relay behavior, impairment recovery, file throughput or 8/24-hour soak.
No performance comparison with AnyDesk or TeamViewer is claimed.
