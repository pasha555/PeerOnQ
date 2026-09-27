# Remote desktop / remote access reference

Read only for legitimate remote-support or remote-administration application work.

## Trust and consent
- Attended remote-control sessions require clear user consent and an obvious active-session state.
- Unattended access must be explicitly enabled by the device owner and protected with strong authentication/authorization.
- Do not add stealth, hidden persistence, security-software evasion, credential capture, or mechanisms that bypass operating-system protections.
- Privileged/UAC/secure-desktop behavior must use supported OS mechanisms rather than bypasses.

## Identity and pairing
- Device identifiers should be generated from a large random space or another collision-resistant design; server-side uniqueness must still be enforced.
- Keep public device IDs separate from authentication secrets.
- Pairing/session tokens must be short-lived or revocable as appropriate and generated with a cryptographically secure RNG.
- Bind session capabilities to the authenticated principal/device and requested permissions.

## Transport
- Protect signaling/control/data transport with authenticated encrypted channels using established TLS/DTLS/WebRTC/platform mechanisms.
- Do not invent custom cryptography.
- Direct peer connectivity may be attempted when appropriate, with authenticated relay fallback.
- Relay services must enforce session authorization and must not become open proxies.

## Reliability
- Model connection state explicitly: idle, requesting, authenticating, connecting, connected, reconnecting, ended/failed.
- Use keepalive/health signals appropriate to the transport.
- Reconnect with bounded exponential backoff + jitter; avoid reconnect storms.
- Handle network changes, suspend/resume, peer exit, relay failure, and version incompatibility.

## Screen/input pipeline
- Separate capture, encode, transport, decode/render, and input-control concerns.
- Bound frame/input queues so latency does not grow without limit.
- Prefer dropping obsolete frames over displaying seconds-old frames.
- Input injection is permitted only for an authorized active session and requested capability.
- Clipboard/file-transfer are separate capabilities with explicit permission/policy.

## Operations
- Audit security-relevant events: pairing changes, session start/end, auth failures, permission changes, unattended-access changes.
- Avoid logging clipboard contents, file contents, secrets, or unnecessary screen-derived data.
- Installer/update paths must verify package authenticity using the platform's standard signing/update mechanisms when available.
