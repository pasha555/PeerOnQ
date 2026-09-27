# Post-quantum security status

PeerOnQ uses a mandatory hybrid application security layer in addition to WebRTC transport:

- key establishment: ML-KEM-768 plus X25519;
- identity authentication: ML-DSA-65 plus Ed25519;
- transcript binding and explicit confirmation;
- record protection: channel/direction/epoch-separated AES-256-GCM with HKDF-SHA-512 derivation;
- protected application paths: video, control, input, clipboard and file transfer.

The handshake and record protocols are `PNQH` v1 and `PNQE` v1. Missing algorithms, invalid
transcript/signature/confirmation, downgrade or replay conditions fail closed. Rekey and reconnect
do not expand the accepted permission mask. Signaling and relay infrastructure carry ciphertext
and routing metadata, not plaintext session records.

This is an implementation statement, not a claim of formal proof or independent cryptographic
certification. Independent review, physical hostile-network testing and long-duration rekey/soak
evidence remain external gates. The viewer UI does not need a marketing badge for this property;
sanitized diagnostics retain the negotiated security facts.
