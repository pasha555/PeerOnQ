# PeerOnQ competitive differentiation

This document defines measurable product targets, not unverified superiority claims.

| Differentiator | Evidence required before claim |
|---|---|
| Accountless LAN remote access | Two clean physical Windows devices pair, consent, view and disconnect without an account or internet service. |
| Fully self-hostable internet path | Public signaling/TURN/cloud deployment passes separate-ISP, UDP-blocked, TURN TCP/TLS and symmetric-NAT cases with published runbook. |
| Local-first privacy | No session media, clipboard, file content or credentials in logs/telemetry; telemetry remains off by default; diagnostics are user initiated and redacted. |
| Fail-closed control | Permission scope cannot expand on reconnect; uncertain state releases input; replay/rate/range/path/tamper tests pass. |
| Honest open source | Clean build/test/start scripts work locally, core operation has no activation, and signed distribution trust is not confused with licensing. |
| Operator-grade observability | Health/readiness, bounded-cardinality metrics, traces, audit retention, backup/restore and alert ownership are exercised in deployment tests. |
| Efficient desktop streaming | Bounded queues and measured CPU/memory/latency/quality outperform an agreed comparison workload on identical hardware/network. No result exists yet. |

## Required comparison method

Use identical machines, displays, network impairment, codec mode, content corpus and warm-up. Record
artifact hashes, versions, CPU/GPU/driver, bandwidth/loss/RTT/jitter, capture-to-render latency,
input latency, frame correctness, CPU/GPU/memory, reconnect time and file throughput. Publish raw
samples and confidence intervals. Do not use marketing screenshots, loopback-only measurements, or
different quality settings as evidence of superiority.

R0 establishes instrumentation and honesty boundaries. It does not establish a competitive win.
