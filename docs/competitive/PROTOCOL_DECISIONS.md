# Phase 6.5 protocol decisions

Signaling remains v3. Additive optional fields preserve legacy decoding; no existing field or wire
meaning was replaced.

| Contract | Current version/decision | Rationale |
| --- | --- | --- |
| Signaling | v3 bounded JSON over authenticated WSS | Capability/version rejection occurs before partial session creation |
| ICE | Standard SIPSorcery ICE/STUN/TURN | Do not invent competitor routing or send media over signaling |
| Route truth | Nominated candidate pair only | Candidate discovery alone does not prove P2P or relay selection |
| Collaboration | v2 ordered SCTP data channel | Binds session and permission generation; separates interactive/normal/bulk priority |
| File transfer | Direct-path-only policy | Current product chooses confidentiality/capacity policy over TURN transfer fallback |
| Input | v2 ordered, session/reconnect/focus/sequence-bound frames | Prevent replay, stale focus and permission expansion |
| Secure handshake | `PNQH` v1 | ML-KEM-768 + X25519, dual authenticated transcript and explicit confirmation |
| Secure records | `PNQE` v1 | Independent direction/channel/transfer/epoch AES-256-GCM state |
| Quality setup | `automatic`, `office`, `balanced`, `performance`, `quality`, `low-bandwidth` | Exact allowlist; omission maps only to the legacy Automatic default |
| Resolution setup | optional `automatic`, `720p`, `1080p`, `1440p`, `2160p`, `native` | Sharer receives an explicit non-upscaling preference; omission retains the selected profile default |
| Quality telemetry | additive dimensions and encoder state | Source/requested/encoded dimensions and actual encoder/hardware state are bounded and address-free; legacy peers omit them |

Future content classifiers, TURN-region candidates or transfer transports must be
versioned only when they cross a peer/server boundary. Local diagnostics additions do not justify a
protocol bump. A QUIC transfer path or new codec requires measurements, compatibility negotiation
and a migration plan; it must not silently replace the current SCTP behavior.
