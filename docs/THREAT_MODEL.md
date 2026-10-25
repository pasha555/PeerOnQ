# Threat model

## Assets and adversaries

Protected assets are device private keys, unattended verifiers, resume/TURN/update credentials,
screen/clipboard/file data, session permissions, audit integrity, and release signing keys. Relevant
adversaries include an unauthenticated internet client, malicious authenticated peer, on-path actor,
compromised TURN/signaling host, local unprivileged process, malicious update/CDN operator, and stolen
CI credential.

| Threat | Control | Residual risk / required evidence |
| --- | --- | --- |
| Device impersonation | P-256 proof of possession, pinned fingerprint, replay nonce | Endpoint compromise can use the current user's key; OS hardening remains required |
| Permission escalation | Immutable accepted mask; monotonic live revocation; host-boundary authorization; audited changes; fail-closed release | Physical two-device input/UIPI testing and independent review remain required |
| Session hijack/resume/input replay | Connection ownership, short-lived single-use resume tokens, device revalidation; input session/reconnect/focus generations and monotonic sequence | Multi-node continuity needs a shared atomic ownership store; physical fault-injection remains required |
| Signaling flood/oversized frames | 64 KiB cap, bounded outbound queue, backpressure, rate limiting, heartbeat | DDoS absorption also requires edge/network controls |
| Capability spoofing/downgrade | Exact signaling-v3 range; bounded canonical manifest; dependency and directional session gate; client verifies server echo; data-plane authorization remains mandatory | A malicious third-party binary can lie about implemented abilities; trusted package signing and native runtime tests remain required |
| TURN abuse | Authenticated expiring REST credentials, quotas, relay range, peer restrictions | Public relay capacity and regional failover need load/abuse testing |
| Media/content disclosure | WebRTC DTLS/SRTP; data channel permission checks; signaling carries no content | A compromised endpoint can see content the user explicitly shared |
| File traversal/overwrite/corruption | Normalized relative names; repeated root/reparse containment; exact offsets; chunk/final hashes; partial + per-file atomic finalize; explicit conflicts | Link replacement is reduced by repeated checks but not a handle-level no-follow guarantee; scanner verdict and whole-folder atomicity are platform dependent |
| Transfer starvation of control | Interactive/normal/bulk data priority, 256 KiB bulk buffered cap, foreground waiter pre-emption | Physical two-device latency under sustained transfer remains required |
| Clipboard leakage | Text only, 120 KiB cap, accepted permission plus bilateral local enable; reconnect pause; contents never audited | Clipboard is inherently exposed to an accepted peer while enabled |
| Unattended secret replay/guessing | PBKDF2-HMAC-SHA512, scoped one-use nonce proof, constant-time comparison, recovery one-use, failure lockout, DPAPI | No pre-logon/service/Secure Desktop support; physical restart/lock/elevation behavior remains external |
| Update substitution/downgrade | HTTPS, ECDSA manifest, exact hash/size, WinVerifyTrust, publisher allowlist, floor | Signing-key/certificate compromise requires revocation response |
| Audit tampering | HMAC-SHA256 chained local audit and integrity-before-export | Local administrator can delete all local data; central audit is not yet implemented |
| Secret leakage in logs/CI | Structured redaction, DPAPI, protected CI secrets, scans, no committed keys | Independent CI configuration review required before public release |
| Hidden surveillance/persistence | Visible capture border/indicator/stop; startup optional and off by default; no service | UI behavior must be rechecked on every supported Windows build |

## Review triggers

Repeat the model before adding a service, input injection, audio, camera, central audit, new codec,
kernel component, protocol registration, privileged installer action, or a new update/signing provider.
External penetration testing must cover authentication, resume, unattended access, TURN abuse,
collaboration parsers, installer privilege boundaries, and update compromise/revocation.
