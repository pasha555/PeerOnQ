# PeerOnQ signaling v3 test vectors

These UTF-8 JSON lines are canonical byte vectors for independent native-client implementations.
Property order, casing and the final LF are intentional. Consumers remove the final LF before
passing one WebSocket text frame to the signaling codec.

`hello-linux-viewer.json` is a protocol interoperability vector only. It does not claim that a
Linux desktop client exists or has passed a native build/runtime gate. `unsupported-version-v2.json`
is the exact machine-readable response shape used to keep an old client out of partial registration.
