# Enterprise deployment guide

Enterprises should deploy only a versioned MSI whose Authenticode chain, publisher certificate
SHA-256, release manifest signature, artifact checksum, and SBOM have been approved. Preserve the
original evidence with the software-distribution record.

## Desktop policy

- Prefer per-user scope for interactive deployments. Machine scope is permitted only by an approved
  administrator and does not install a service.
- Do not enable the optional startup shortcut by default. Users must know when PeerOnQ starts.
- Allow outbound HTTPS/WSS to signaling/update domains and the documented STUN/TURN ports. Do not
  create inbound desktop firewall rules for PeerOnQ.
- Do not create antivirus exclusions. If endpoint protection quarantines a file, validate the signed
  package and work with the security vendor.
- Keep Windows, GPU drivers, WebView/runtime dependencies supplied by Windows, and root certificates
  current. Standard users must not receive local administrator rights merely for PeerOnQ.
- Unattended access stays disabled unless policy explicitly identifies devices, immutable permission
  ceilings, owners, review dates, and revocation procedure.

## Server topology

Use separate public names for signaling and TURN, trusted public certificates, a restricted TURN UDP
allocation range, a reverse proxy with WebSocket support, and external secret files or a secret
manager. Run at least two signaling instances only after an atomic shared connection-ownership store
has been configured; otherwise use a single active instance with health-gated replacement.

The authoritative DNS, port/firewall, TLS, TURN, reverse-proxy, health, metrics, secret rotation, and
backup instructions are in the
[`PeerOnQ.Realtime.Deployment` guide](../src/PeerOnQ.Realtime.Deployment/README.md).

## Monitoring and audit

Alert on readiness failures, authentication/replay/rate-limit spikes, reconnect failures, TURN
allocation failures, update rejection, and certificate/key expiry. Metrics must remain aggregate and
secret-free. Local audit can be exported after integrity verification. Enterprise central audit is a
future optional sink; do not claim centralized retention until its authenticated transport, tenant
isolation, integrity, deletion, and privacy controls are implemented.

## Rollout

Deploy to an internal canary ring, validate real two-network WebRTC/TURN/reconnect and installer
lifecycle, then increase the signed manifest rollout percentage. Pause by publishing a newly signed
manifest with the previous percentage; revoke a bad version by shipping a higher signed corrective
version. Never republish different bytes under an existing version or weaken the security floor.
