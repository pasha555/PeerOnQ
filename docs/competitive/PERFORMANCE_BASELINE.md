# Phase 6.5 performance baseline

Baseline was recorded before Phase 6.5 source changes. The artifact is the existing unsigned
LAN-development `0.9.20.0` x64 executable. It was launched with an isolated data directory so the
already running installed PeerOnQ instance and identity were not displaced.

## Environment

- OS: Windows 11 Pro 10.0.26200, x64
- CPU: AMD Ryzen 7 3700X, 8 cores / 16 logical processors
- GPU: AMD Radeon RX 5600 XT, driver 32.0.21045.1000
- Display: 1920x1080
- Active physical network: Intel Wi-Fi 6 AX200 160 MHz, reported link 1.3 Gbps
- Duration: 15 seconds, idle main window
- Evidence: `benchmarks/phase6.5-idle-local-2026-08-18-v0.9.20.json`

## Measured idle result

| Metric | Result |
| --- | ---: |
| Window-observed startup | 837.765 ms |
| Normalized CPU median / P95 | 0% / 3% |
| Working set median / P95 | 200.340 / 201.027 MiB |
| Private memory P95 | 129.551 MiB |
| Dedicated GPU memory P95 | 52.469 MiB |
| Samples | 10 |

These results describe one short local idle run, not an active remote session and not a competitor
comparison. The older `0.5.0.0` idle artifact remains unchanged for history but is not treated as an
equivalent A/B benchmark because version, startup state and implementation differ.

## Not measured at baseline

- active capture/encode/decode/render CPU and GPU;
- sustained capture/encode/decode/render FPS;
- input transport, injection or input-to-photon latency;
- true capture-to-remote-render latency;
- controlled bandwidth/loss/jitter behavior;
- reconnect duration on two physical devices;
- file-transfer throughput and control latency during transfer;
- 1440p/4K 30/60 FPS physical support;
- memory/GPU/handle trend during an 8- or 24-hour soak.

Missing results remain missing; they are not estimated from target settings or link speed.
