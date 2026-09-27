# Client observability

Client logs are bounded structured JSON with UTC time, service/version/environment/region fields.
Device IDs are masked; IPv4/IPv6 addresses, paths, credentials, tokens, JWTs and sensitive field
names are sanitized. Screen, input, clipboard and file content are never diagnostic fields.

`SessionTimelineStore` retains at most 128 events for each of 50 recent sessions. Entries come from
real coordinator actions: request, online routing, permission, identity/security negotiation,
selected path, first render, interruption, reconnect attempt/completion and end. Descriptions are
fixed/address-free and survive local runtime release for troubleshooting.

Connection details and consented support bundles can include the timeline. Bundles remain bounded,
allowlist-only and local unless the user separately confirms a configured first-party upload.
Network Doctor statuses are appended as health-check codes; raw addresses and exception messages
are excluded.

Input-to-photon latency, encoder/decoder CPU/GPU load and distributed client/admin correlation are
not currently measured. Existing cloud metrics do not receive session media or user content.
