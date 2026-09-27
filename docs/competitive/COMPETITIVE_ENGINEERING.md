# Competitive engineering baseline

Competitors are used only as public outcome references. PeerOnQ does not copy DeskRT, TeamViewer
protocols, binaries, assets or undocumented behavior. Marketing numbers are not PeerOnQ benchmark
results.

Official public references reviewed on 2026-08-18:

- [AnyDesk performance](https://anydesk.com/en/performance)
- [AnyDesk screen sharing](https://www.anydesk.com/en/features/screen-sharing)
- [AnyDesk file transfer](https://www.anydesk.com/en/features/remote-file-transfer)
- [AnyDesk trace files](https://support.anydesk.com/what-are-trace-files)
- [TeamViewer security and connection routing](https://www.teamviewer.com/en-us/global/support/knowledge-base/teamviewer-remote/security/security-statement/)
- [TeamViewer file-transfer session](https://www.teamviewer.com/en/global/support/knowledge-base/teamviewer-remote/remote-control/open-a-file-transfer-session/)
- [TeamViewer policy management](https://www.teamviewer.com/en-us/global/support/knowledge-base/teamviewer-remote/remote-management/remote-management-policies/)

| Public capability category | PeerOnQ current implementation | PeerOnQ measured result | Gap | Test method | Status |
| --- | --- | --- | --- | --- | --- |
| Desktop-oriented codec/high frame rate | Desktop-tuned software VP8 with real-time settings | Real 1080p WebRTC flow exists; sustained physical 60 FPS is unmeasured | No proprietary codec claim, hardware path or equivalent benchmark | Same content/hardware, capture-to-render FPS and latency distributions | partial |
| Low-latency LAN | Direct ICE and bounded latest-frame pipeline | No synchronized physical input-to-photon value | Need two-device instrumentation | High-speed camera or shared-clock marker plus input transport/injection segments | blocked |
| Low-bandwidth survival | Five-rung loss/RTT/jitter/drop adaptation | Deterministic controller tests, no full impairment matrix | Need controlled bandwidth/loss/jitter runs | Reproducible network emulator with identical content/duration | partial |
| Direct or routed connectivity | Direct LAN/Internet and authenticated TURN fallback | Local coturn UDP/TCP/TLS evidence exists | Public/symmetric-NAT and relay subtype UI remain open | Nominated pair plus independent packet path verification | partial |
| Large file transfer | Direct authenticated SCTP, streaming, resume and hashes | Local bounded test artifacts only | 1/10/100 GB and simultaneous-control benchmarks | Byte-identical files, process/network/disk metrics and input latency | partial |
| Trace/support evidence | Sanitized JSON logs and consented diagnostic ZIP | Redaction and archive tests exist | Human session timeline and Network Doctor absent | Fault-injection test with exact sanitized event sequence | partial |
| Simple device connection | Twelve-digit ID, saved aliases and three explicit modes | UI and end-to-end signaling tests exist | Physical usability study not run | Timed two-person attended flow | implemented, external UX evidence open |
| End-to-end protected traffic | Mandatory authenticated application record layer over WebRTC | Tamper/replay/channel separation tests pass | Independent review and hostile network test open | Capture relay/signaling view and prove no plaintext; cryptographic review | partial |
| Enterprise device/policy management | Phase 6/7 Cloud, Admin, organizations and signed policy attestations | API/tenant tests and local Compose acceptance exist | Multi-host/public production evidence open | Cross-tenant, revoke, policy and audit acceptance | partial |

## Metric definitions

- Network RTT: transport round-trip estimate only.
- Input transport latency: viewer send to host receive, excluding Windows injection and display.
- Input injection latency: host receive to successful Windows injection boundary.
- Input-to-photon: physical input event to visibly rendered changed pixel on the viewer.
- Video pipeline latency: capture timestamp to successful remote render; local segment P95 values
  are not a substitute.
- FPS support: capture, preprocess, encode, transport, decode and render must all sustain the target
  without a growing queue.

No “better than AnyDesk” or “better than TeamViewer” statement is supported by current evidence.
