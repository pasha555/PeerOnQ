# Security outcome comparison

This is an engineering outcome review, not a claim of certification or superiority.

| Outcome | Public competitor category | PeerOnQ implementation | Status |
| --- | --- | --- | --- |
| Infrastructure cannot decrypt session content | Public TeamViewer security statement describes endpoint-only decryption | PeerOnQ adds authenticated application records for encoded video, input, clipboard and file data over WebRTC | Implemented and tested locally; independent review open |
| Explicit consent and visible control | Both products expose attended/unattended workflows | Incoming permission dialog, visible session indicator, tray stop/revoke, viewer release and host emergency stop | Implemented |
| Direct plus relay path | Public product material describes direct/routed behavior | ICE direct preference and authenticated short-lived coturn fallback | Implemented locally; Internet/NAT evidence open |
| Device/policy management | Public TeamViewer policy material describes centrally assigned settings | Cloud/Admin/organization policy is signed into short-lived device attestations and enforced by Signaling | Implemented locally; production deployment evidence open |
| Post-quantum session protection | No competitor comparison is asserted | ML-KEM-768 + X25519 establishment and ML-DSA-65 + Ed25519 authentication protect every current application data channel | Implemented; non-FIPS package and independent review gates open |

## Downgrade and permission policy

`security.hybrid-pq-v1` is mandatory on both peers. The selected suites, session ID, identities,
permissions and transcript are authenticated; there is no silent `ClassicalOnly` fallback.
Reconnect can preserve or reduce the accepted permission mask but cannot increase it. Any
authentication uncertainty disables/release input and fails closed.

## Claim boundary

The managed Bouncy Castle package implements the selected NIST algorithms but is not the separately
certified Bouncy Castle FIPS distribution. PeerOnQ does not claim quantum communication, FIPS module
certification, penetration-test completion or superiority over another product.
