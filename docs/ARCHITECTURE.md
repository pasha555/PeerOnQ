# PeerOnQ architecture

PeerOnQ is split into native Windows/Linux desktop processes, attended native Android and Apple viewers,
an ASP.NET Core signaling service, and coturn.
Media and collaboration data flow over authenticated WebRTC; the signaling service never proxies
screen frames, clipboard text, or file contents.

## Desktop boundaries

- `PeerOnQ.App`: WinUI 3 product shell, explicit consent, session indicators, viewer input surface,
  local stop actions, and update/privacy/audit controls. It runs as the signed-in user and requests
  no elevation.
- `PeerOnQ.App.Linux`: Avalonia viewer/controller shell. It can request an attended Windows-hosted
  view-only or full-control session, render decoded BGR24/I420 frames, and forward explicitly enabled
  keyboard/pointer input. It does not host, capture, accept incoming sessions, transfer files, sync
  clipboard data, or enable unattended access.
- `PeerOnQ.App.Android`: native .NET Android viewer/controller shell. It requests attended view-only
  or control sessions, renders authenticated VP8 through MediaCodec to a Surface, and forwards touch
  or hardware/explicit short-text input only after local enablement and host focus acknowledgment.
  It has no host, capture, input-injection, file, clipboard, unattended or background-service path.
- `PeerOnQ.App.Apple`: one UIKit codebase targeting iPhone/iPad and Mac Catalyst. It requests only
  attended view-only or control sessions, renders authenticated VP8 decoded by the statically linked
  reviewed libvpx archive, and forwards pointer or bounded ASCII input only after explicit local
  enablement and host focus acknowledgment. It declares no background mode and ends an active
  session when the app resigns activation.
- `PeerOnQ.Application`: session state, immutable accepted permission scope, reconnect orchestration,
  clipboard/file-transfer policy, and interfaces for external effects.
- `PeerOnQ.Domain`: identities, session permissions, typed reconnect states, transfer and trusted-device
  invariants.
- `PeerOnQ.Transport`: bounded JSON signaling codec, authenticated WebSocket client, resume protocol.
- `PeerOnQ.Media`: WebRTC peer, bounded frame queue, VP8 software codec or injected native viewer
  decoder sink, frame pacing, bitrate limiter, adaptive FPS/resolution, ICE path inspection, and
  media statistics. Authentication/telemetry unwrap happens before either decoder boundary.
- `PeerOnQ.Platform.Windows`: Windows Graphics Capture, display enumeration, D3D11 capture device,
  DPAPI-backed platform security, hardware capability probing, and permission-scoped `SendInput`.
- `PeerOnQ.Platform.Linux`: least-capability Linux manifest, profile-bound Secret Service storage
  through fixed-path `secret-tool`, and an explicit unsupported capture source for the viewer-only build.
- `PeerOnQ.Platform.Android`: least-capability Android manifest, VP8 key-frame dimension parser,
  hardware-key mapping and an explicit unsupported capture source for the viewer-only build.
- `PeerOnQ.Platform.Apple`: separate macOS/iOS/iPadOS least-capability manifests, bounded frame/input/
  aspect-fit mapping and an explicit unsupported capture source for every viewer-only Apple build.
- `PeerOnQ.Infrastructure`: SQLite stores, local logs, tamper-evident audit, privacy/crash records, and
  signed update verification.

The process contains no Windows service. Unattended authentication therefore does not bypass the
signed-in user's Windows security boundary. A service may be introduced only through a separate
threat model, authenticated local IPC design, ACL review, and explicit installer consent.

Remote input travels only on the accepted session's ordered DTLS/SCTP channel. Input protocol v2
binds every focus, pointer, wheel, button, key, release, acknowledgment, and revocation frame to the
signaling `SessionId`, a reconnect generation, a viewer-focus generation, and a monotonic sequence.
The viewer becomes active only after the sharer acknowledges a fresh focus generation. The sharer
checks permission, binding, ordering, rate, range, and active focus again before mapping normalized
coordinates into the selected display. Live `ControlInput` revocation is a monotonic reduction of
the immutable accepted mask; reconnect restores only the reduced effective mask. Reconnect, monitor
switch, focus loss, invalid input, revocation, end, or disposal releases held buttons/keys. Standard
Windows UIPI and secure-desktop restrictions remain intact.

Concurrent technician sessions are keyed by `SessionId` through the coordinator and native viewer
maps. Frames, display lists, collaboration contexts and transfer callbacks cannot use the active
window as an implicit routing key. The shared Windows input sink has one process-local focus owner;
switching ownership synchronously releases the previous session before enabling the next route.

`ConnectionPolicyProvider` is the central deterministic boundary for media health, adaptive quality
and file-transfer allocation. It consumes aggregate loss/RTT/jitter/bitrate/queue measurements and
cannot change authentication, consent, permissions, cryptography or relay trust. The optional ML
interface has no implementation in the default product.

Support invitations are target-, mode- and permission-bound capabilities. The issuing device stores
only a token hash and optional password verifier inside its DPAPI secret profile. Signaling relays a
bounded credential only to the authenticated target; the target validates it before creating the
permission request and atomically consumes a use only after local Accept. URI activation loads the
request but does not start a session.

The viewer exposes one accessible display-scaling menu: Fit preserves the full remote aspect ratio,
Fill preserves aspect ratio while cropping centered edges, Stretch uses the complete local viewport,
and Actual size maps one source pixel to one local physical pixel with DPI-aware scrolling. Remote
pointer normalization uses the selected rendered rectangle rather than window dimensions.

## Server and relay boundaries

`PeerOnQ.Signaling.Server` authenticates device registrations, owns live connections, enforces
message limits/rate limits, routes offers/answers/candidates, issues short-lived resume tokens and
authenticated TURN REST credentials, and exposes secret-free health/metrics. The client detects a
half-open signaling path with bounded WebSocket/app-heartbeat PONG deadlines. After authenticated
resume, the server notifies the participant that stayed online; both sides fail closed and complete
a fresh ICE negotiation with the sharer as deterministic offerer before restoring the session.
Signaling protocol v3 is checked in `hello` before the challenge and echoed in `registered`; missing,
older, or newer versions fail with structured `unsupported_version` bounds and cannot partially
connect. The v3 hello also carries a bounded native-platform capability manifest. The server echoes
the accepted set, negotiates only implemented optional protocol features, and rejects a session
before creation when the viewer or host lacks its required screen/input/file/clipboard/unattended
ability. Syntactically valid unknown message types receive `unsupported_message` and do not kill the
connection; malformed data remains rejected.
When `Signaling:Cluster:Enabled` is set, Redis stores bounded device-owner leases, the live session
state machine, one-use resume-token hashes, replay nonces and unattended challenges. Each process
keeps only its local WebSocket objects; a Redis pub/sub backplane routes signaling frames to the
instance that owns the target socket. Lease expiry converts sessions owned by a hard-crashed process
to the existing bounded reconnect state. Redis failure therefore makes clustered readiness fail and
new routing/session operations fail closed.
Session content is not stored. `PeerOnQ.Turn.Configuration` uses coturn; PeerOnQ does not implement
TURN.

Collaboration protocol v2 is carried on the same authorized ordered DTLS/SCTP data channel and binds
each file, clipboard, or control frame to the signaling session ID and permission generation. Input
uses the interactive lane, metadata uses the normal lane, and file chunks use a bounded bulk lane so
transfer backpressure cannot occupy the foreground send budget. Signaling never proxies payloads.

Production deployment is described in
[`src/PeerOnQ.Realtime.Deployment/README.md`](../src/PeerOnQ.Realtime.Deployment/README.md).
Connection ownership is abstracted for horizontal scaling. A multi-node deployment must enable the
Redis ownership/session implementation, give every process a unique instance ID and pass its own
in-flight resume/failover gate before claiming cross-node continuity.

The Phase 9 management-plane HA boundary is narrower and explicit. Cloud API background retention,
presence expiration and stale-session reconciliation each require a renewable Redis operation lease;
lease loss cancels the owned run. Presence sockets use the Redis SignalR backplane, while shutdown
notifications target only connections owned by the stopping instance. Common service drain state
makes readiness fail and rejects new non-probe work during process shutdown. Cloud API, Presence and
Downloads use the opt-in single-region warm-standby validation overlay. Signaling uses two active
instances with Redis-backed ownership, atomic session state and cross-node routing. PostgreSQL,
Redis, TURN and multi-region failover remain outside this local application-tier proof. See
[HA_AND_DISASTER_RECOVERY.md](HA_AND_DISASTER_RECOVERY.md).

## Customer identity and portal boundary

Phase 7 adds a separate customer security domain to `PeerOnQ.Cloud.Api` and a separately deployed
React SPA at `artifacts/peeronq-portal`. It does not reuse internal Admin users, roles, sessions,
cookies, authorization policies, or UI routes. Customer browser sessions use short-lived JWTs backed
by revocable PostgreSQL session rows, rotating one-use refresh tokens, Secure/HttpOnly/SameSite
Strict root cookies, and double-submit CSRF protection.

Organization access starts with an active `(OrganizationId, AccountId)` membership lookup on every
query and command. Owner, Administrator, Technician, Member, and Auditor are customer-only roles;
the server, not the SPA, authorizes role, invitation, team, device, policy, session, and audit
operations. Invitation tokens are random, short-lived, email-bound, single-use, and stored only as
SHA-256. Customer audit rows are append-only in EF and PostgreSQL.

Managed devices may be proof-claimed by an organization. Cloud then signs organization ID and the
effective connection-policy claims into the short-lived signaling attestation; Signaling enforces
cross-tenant, mode, hybrid-security, client-version, and relay-region restrictions before routing a
managed session. Existing accountless/unmanaged LAN mode remains on the v1 trust path and does not
require a customer account.

The public website no longer mounts its static `/app` preview. Its account links target the
validated HTTPS `portal.*` origin. The portal shares the Cloud API origin through Nginx so cookie and
CSRF semantics remain same-origin, while the Admin host stays an independent security surface.

## Update flow

1. CI produces self-contained x64 and ARM64 application trees and an MSI per architecture.
2. First-party PE files and the MSI are Authenticode signed and RFC 3161 timestamped.
3. The release tool verifies MSI trust/publisher, hashes the exact bytes, and signs a bounded JSON
   manifest with an offline ECDSA P-256 release key.
4. The client downloads the manifest over HTTPS, verifies the compiled key ID/public key, product,
   channel, architecture, time window, version floor, and rollout bucket.
5. The MSI is streamed into a private `.partial` file with a signed size cap and incremental SHA-256.
   WinVerifyTrust and the compiled publisher-certificate allowlist are checked before atomic staging.
6. Installation starts only after visible user confirmation and invokes the system `msiexec.exe`.

## Local data

Application state is under `%LOCALAPPDATA%\PeerOnQ`. SQLite contains device/session metadata and the
security audit. DPAPI protects device, audit-integrity, unattended, and privacy secrets for the
current Windows user. Logs, diagnostics, updates, and opt-in crash reports use separate directories.
The installer preserves this directory across repair, upgrade, and uninstall by policy.

The Android viewer keeps its public identity, bounded blocked-device set and last 100 masked session
audit entries in private app preferences. Secret identity material is AES-GCM ciphertext whose
non-exportable wrapping key lives in Android Keystore. Uninstall removes this app-private state;
Android backup is disabled. The Android preview has no updater or background service.

The Apple viewer keeps the equivalent non-secret public identity, blocked-device set and last 100
masked session entries in its application-scoped `NSUserDefaults`. Private identity material uses
SecItem generic-password records in the non-synchronizing data-protection Keychain with
`WhenUnlockedThisDeviceOnly`. iPhone and Mac Catalyst share the source but use platform-specific
capability identifiers. Neither build has host, unattended, file, clipboard, updater or background
execution paths.

Portable Support is the same self-contained desktop publish with an adjacent marker. It selects a
PID-bound temporary data root before service composition, disables unattended access and installs no
service/startup entry. Normal shutdown removes the profile; a later portable start reaps abandoned
profiles only after confirming their owner PID is no longer alive. Development portable ZIPs are
explicitly unsigned and outside official update trust.
