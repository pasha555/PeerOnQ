# PeerOnQ TURN Configuration

Deployable coturn configuration for PeerOnQ Phase 3. It uses coturn REST authentication:
the signaling server generates a time-limited username and HMAC credential for an already
authenticated, accepted session. No persistent TURN username/password is shipped to clients.

Required runtime values:

- `/run/secrets/peeronq_turn_shared_secret`: preferred production source for at least 32 random
  base64-compatible characters. `PEERONQ_TURN_SHARED_SECRET` is a development-only fallback.
- `PEERONQ_TURN_REALM`: the TURN DNS name.
- `PEERONQ_TURN_EXTERNAL_IP`: required when the container/host is behind 1:1 NAT.
- `/certs/fullchain.pem` and `/certs/privkey.pem`: enable TURN over TLS and DTLS. Set
  `PEERONQ_TURN_REQUIRE_TLS=true` in production so either missing file prevents startup.

The entrypoint validates the realm and secret, then writes a randomly named mode-0600 runtime
configuration under `/tmp`; it never passes the secret in coturn's process arguments. The secret must exactly match the
file configured by `Signaling__Turn__SharedSecretFile` on the signaling service. Rotate it by
running old and new TURN pools during the credential lifetime, switching signaling to the new
pool/secret, waiting for old credentials to expire, and then removing the old pool.
