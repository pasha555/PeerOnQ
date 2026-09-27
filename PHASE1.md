# PeerOnQ Phase 1 — two Windows devices, view-only screen sharing

Phase 1 is a working remote-**view** product: a persistent device identity, an authenticated
signaling server, an explicit permission prompt, real screen capture, and a real WebRTC video
connection. There is no mouse or keyboard control, no unattended access, and no hidden capture.

## What is in the box

```text
src/
├─ PeerOnQ.Domain             PeerOnQ ID, device identity, typed session state machine
├─ PeerOnQ.Application        Ports and the SessionCoordinator (timeouts, lifecycle, cleanup)
├─ PeerOnQ.Infrastructure     SQLite, DPAPI secret store, Serilog with ID masking
├─ PeerOnQ.Transport          Signaling protocol contracts + WebSocket client
├─ PeerOnQ.Media              Bounded frame pipeline, statistics, WebRTC (VP8) peer
├─ PeerOnQ.Platform.Windows   Windows.Graphics.Capture + D3D11 readback + BGRA→I420
├─ PeerOnQ.Signaling.Server   ASP.NET Core WebSocket signaling
└─ PeerOnQ.App                WinUI 3 desktop app (unpackaged, asInvoker)

tests/
├─ PeerOnQ.Domain.Tests            45 tests
├─ PeerOnQ.Infrastructure.Tests    18 tests
├─ PeerOnQ.Application.Tests       21 tests
├─ PeerOnQ.Media.Tests             17 tests (includes a real peer-to-peer video test)
├─ PeerOnQ.Signaling.Tests         21 tests (real server over a real socket)
└─ PeerOnQ.EndToEnd.Tests          14 tests (real capture + real WebRTC + real signaling)
```

## Running it on two computers

### 1. Start the signaling server (one machine, or any host both can reach)

```bash
dotnet run --project src/PeerOnQ.Signaling.Server --urls http://0.0.0.0:5080
```

Health check: `http://<host>:5080/health`.

### 2. Allow the port through the Windows firewall on the server machine

```powershell
New-NetFirewallRule -DisplayName "PeerOnQ signaling" -Direction Inbound `
    -Protocol TCP -LocalPort 5080 -Action Allow -Profile Private
```

WebRTC media flows directly between the two devices over UDP host candidates. On a private
network profile Windows normally allows this for an `asInvoker` desktop app; if media never
connects, allow the PeerOnQ executable itself:

```powershell
New-NetFirewallRule -DisplayName "PeerOnQ media" -Direction Inbound `
    -Program "C:\path\to\PeerOnQ.exe" -Action Allow -Profile Private
```

### 3. Run the app on both machines

```bash
dotnet run --project src/PeerOnQ.App
```

Point both at the same server, either in the app's signaling box or with an environment
variable before launch:

```powershell
$env:PEERONQ_SIGNALING_URL = "ws://192.168.1.10:5080/ws"
```

### 4. Connect

1. Both apps show their own PeerOnQ ID (`LNK-483-921-756-204`) after registering.
2. On the sharer, pick the display to share. Nothing is captured until you do.
3. On the viewer, type the sharer's ID and request a view-only session.
4. The sharer sees a dialog: requester name, masked ID, requested permission, local time, and a
   countdown. No answer within 30 seconds is a decline.
5. On accept, the sharer shows a red always-on-top banner with the viewer's name, a VIEW ONLY
   badge, the session duration and a Stop button. The Windows capture border stays on.
6. Either side can end the session; capture, encoder and peer connection are all released.

## Transport security

- `wss://` is required for anything except loopback. The client refuses plain `ws://` to a
  non-loopback host unless `AllowInsecureTransport` is explicitly set, and certificate
  validation is never disabled.
- For a LAN deployment, terminate TLS in front of the signaling server (reverse proxy or a
  Kestrel HTTPS endpoint with a certificate both machines trust).

## Identity and secrets

| Item | Where it lives |
| --- | --- |
| Internal UUID, PeerOnQ ID, name, created, version, public key | `%LOCALAPPDATA%\PeerOnQ\peeronq.db` (SQLite) |
| Device secret (32 random bytes) | `%LOCALAPPDATA%\PeerOnQ\secrets\device-secret.dpapi` (DPAPI, current user) |
| ECDSA P-256 private key | `%LOCALAPPDATA%\PeerOnQ\secrets\device-signing-key.dpapi` (DPAPI, current user) |
| Logs | `%LOCALAPPDATA%\PeerOnQ\logs\` |

The PeerOnQ ID comes from `RandomNumberGenerator` only. It is never derived from a MAC
address, user name, computer name, IP address, disk serial or a timestamp. Private material
never enters SQLite (there is a test that greps the database file for it) and IDs are masked in
logs as `LNK-483-***-***-204`.

## Signaling protocol

JSON frames over one WebSocket per device, capped at 64 KB. The server relays control-plane
messages only — it never sees a video frame or a private key.

| Message | Direction | Purpose |
| --- | --- | --- |
| `hello` | client → server | Announce the PeerOnQ ID, request a challenge |
| `challenge` | server → client | 32 random bytes, valid 30 s |
| `register` | client → server | ECDSA signature over the challenge + public key |
| `registered` | server → client | Short-lived connection token (10 min) + heartbeat interval |
| `session.request` | client → server | Session id, target, mode, single-use nonce, timestamp |
| `session.incoming` | server → target | Requester name, masked handling, permission deadline |
| `session.permission` | target → server | accept / decline / block / timeout |
| `session.permission.result` | server → requester | The decision |
| `sdp`, `ice` | relayed between the two participants only |
| `session.end` | either → server → peer | Ends the session |
| `ping` / `pong` | client ↔ server | Heartbeat |
| `error` | server → client | Typed code (`target_offline`, `rate_limited`, …) |

The server pins each PeerOnQ ID to the public key seen at its first registration (trust on
first use), rate limits every connection (token bucket), rejects replayed nonces and stale
timestamps, drops malformed or oversized frames, refuses relays from non-participants, and
sweeps abandoned sessions.

## Media

- One VP8 video track, send-only on the sharer and receive-only on the viewer.
- No audio track and no data channel are ever created, so there is no transport for input.
- Capture → bounded queue (capacity 3, newest wins) → frame-rate limiter → VP8 → RTP.
- Real counters: frames captured/encoded/dropped/rendered, bytes, measured fps and kbps.

Defaults: 30 fps target, automatic resolution, cursor visible, audio disabled.

## Known limitations

- Window capture is refused; Phase 1 captures a display only.
- Hardware encoding is not wired up yet: VP8 encoding is the libvpx software path.
- The server's public-key pinning is in memory, so restarting the server clears the pins.
- Reconnect is best-effort: the client retries registration five times, and a session that
  loses media waits out `ReconnectTimeout` before ending.
- The WinUI 3 app builds and is wired to the real services, but its windows have not been
  exercised on two physical machines yet — see the acceptance status below.

## Acceptance status

The full acceptance flow runs green in `PeerOnQ.EndToEnd.Tests` using real components on one
machine: two provisioned identities, the real signaling server over a real socket, an explicit
permission decision, `Windows.Graphics.Capture` on the sharer, and a real WebRTC connection
that delivers decoded 1920×1080 frames to the viewer.

The two-physical-computer run required by the specification has **not** been performed. The runtime
scope is locked to attended view-only at the WinUI, coordinator, signaling, and WebRTC-session
boundaries; control, file transfer, clipboard, and unattended requests are rejected rather than
merely hidden. Therefore the Phase 1 gate is `BLOCKED`, not passed. See
`docs/phase-reports/PHASE_01_COMPLETION_REPORT.md` for exact local evidence and the required
external test matrix.

## Current automated result

The test-count listing above reflects the original Phase 1 guide. On 2026-08-12, the current
Release suite completed with 455 passed and 2 opt-in external tests skipped; the current
Phase-1-relevant projects reported 61 domain, 59 application, 56 media, 63 signaling, and 37
end-to-end passing tests. This is local automated evidence only and does not change the blocked
physical-device gate.
