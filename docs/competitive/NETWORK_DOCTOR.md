# Network Doctor

Status: partially implemented and locally tested on 2026-08-18.

`NetworkDoctorService` is an on-demand deterministic diagnostic. It is not run before every
connection and it never changes certificate, authentication, routing or permission policy.

## Standalone probes

| Probe | Evidence | Normal UI exposure |
| --- | --- | --- |
| Internet | local interface plus successful DNS and signaling TCP reachability | status only |
| DNS | bounded resolution of the configured signaling hostname | no returned addresses |
| PeerOnQ API | bounded HEAD request when a fixed API endpoint exists | status only |
| Signaling | authenticated client state plus TCP reachability | state/status only |
| UDP/direct prerequisite | ability to bind a local UDP socket | status only |
| Approximate RTT | signaling TCP-connect duration | rounded milliseconds |
| Region | compiled/validated region label | configured label |
| Protocol | successful signaling registration/version negotiation | status only |
| Clock skew | last authenticated signaling server-time offset | absolute milliseconds |

Each network operation has a three-second default deadline. Raw exception messages, hostnames,
resolved addresses and candidate addresses are excluded from both normal and technical output.
Technical output contains allowlisted codes such as `POQ-NET-TIMEOUT` and exception type only.

## Session-scoped probes

STUN, TURN/UDP, TURN/TCP, TURN/TLS and packet-loss sampling require an approved live session and
short-lived session credentials. The standalone doctor reports them as `NotConfigured`; it does
not mint TURN credentials or create a hidden media session. Adding these probes requires a bounded
session-aware contract and must preserve explicit consent.

The last report can be added to a support ZIP only after the existing export/send confirmation.
AI is not required and receives no data from this implementation.
