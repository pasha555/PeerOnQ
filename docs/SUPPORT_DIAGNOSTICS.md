# Support diagnostics guide

Start with the in-app connection diagnostics: connection path category, relay server identifier, RTT,
packet loss, bitrate, capture/encode/decode/render FPS, dropped frames, reconnect attempts, and last
interruption reason. Normal UI intentionally omits private IP addresses.

Use sanitized export for escalations. Review the file before sharing and remove unrelated records.
Audit export first verifies the HMAC chain and masks device IDs and IPv4/IPv6 addresses. Never request
or share passwords, tokens, private keys, unattended verifiers, TURN credentials, full device IDs,
screen frames, clipboard/file content, user documents, or unredacted network captures.

Useful evidence includes UTC reproduction time, app/OS/architecture, role (viewer/sharer), accepted
permission names, path category (direct LAN/direct internet/relay), relay server ID, quality profile,
sanitized error category, and exact release SHA-256. For installer problems, add verbose MSI logs after
reviewing them for user paths. For performance, use `scripts/windows/measure-phase5-performance.ps1`
and state whether the app was idle or in a real active session.

Crash reports are local and opt-in. They intentionally exclude messages/stacks and therefore provide
an error ID/type for correlation, not a full dump. Full memory dumps are disabled by policy because
they may contain screen, clipboard, keys, or file data. Collecting a dump requires a separate,
case-specific user consent and secure handling plan.
