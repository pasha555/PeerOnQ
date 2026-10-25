# PeerOnQ 0.9.89 - license-clean WebRTC stack, source-built libvpx, TURN over TLS

This synchronized server/client release replaces 0.9.88. Directory.Build.props is the sole
product-version source; Windows, Portable Support, Linux, Android and Apple derive 0.9.89 and the
Android/Apple bundle codes advance to 9089. Signaling messages are unchanged.

Upgrade the server and every client together. The WebRTC transport moved from SIPSorcery 10.0.17 to
the 8.0.14-based derivative, so mixed 0.9.88/0.9.89 media sessions have not been tested.

## Changed in 0.9.89

- License: SIPSorcery added a non-OSI "Additional Restriction" to its license on 2025-05-18. The
  maintained derivative is now built from SIPSorcery 8.0.14 (tag v8.0.14, commit
  08d3a0271dded6aaebc3183f963bc820b099c552), the last release without it, with
  SIPSorceryMedia.Abstractions 8.0.10 and SIPSorceryMedia.Encoders 8.0.7. The 23 PeerOnQ patches and
  one added file are listed with their hashes in eng/sipsorcery/upstream.json and ship beside the
  license in ThirdParty/SIPSorcery.
- Ported from the 10.0.17 derivative: SCTP sender flow control and receiver credit, bounded SACK
  history, single-owner UDP receive admission and first-packet RTCP transit initialization. The
  DTLS receive wakeup patch is not needed: the 8.0.14 DTLS receive path already blocks on its queue.
- Security fixes written by PeerOnQ for advisories that affect 8.0.14: malformed UDP/STUN/RTP/RTCP
  packets are dropped (GHSA-28gm-jrmw-xx93 class), SCTP zero-length chunks and oversized SACK counts
  are rejected (GHSA-qmvg-569h-hqrh, GHSA-jwjp-4649-v8jp), DTLS handshakes run on a dedicated thread
  (GHSA-mc26-5x96-8x52) and the ICE-over-TCP receiver survives bad packets (GHSA-mwf8-6m4x-pgmm class).
- Also fixed in 8.0.14: SRTP/SRTCP replay window and rollover counter (a forged packet could make
  genuine packets fail; packets after a rollover were decrypted with the wrong counter), DTLS
  datagrams larger than the record buffer aborted the handshake, one ICMP "connection reset" stopped
  receiving for good, and the SCTP receive loop ended on a single malformed packet.
- TURN over TLS: 8.0.14 skipped `turns:` servers. PeerOnQ's patch adds TLS 1.2/1.3 to the TURN
  server on 5349 with full certificate validation (trusted chain and host name); TLS connections
  close with the session so coturn frees the allocation.
- libvpx: vpxmd.dll is built in CI from libvpx 1.17.0 (commit
  6df3ec34557879fff673706f4a1d9fbd0f3a6f0e) for x64 and ARM64. libvpx 1.17 raised the encoder ABI;
  the VP8 encoder supports both the old and the new layout.
- Windows installers: the unused Windows App SDK AI, ML (ONNX Runtime, DirectML), Imaging/Vision and
  Widgets binaries are removed (x64 app payload 235.8 MB to 191.6 MB).
- Known limitation: the 8.0.14 DTLS code depends on Portable.BouncyCastle 1.9.0, which is no longer
  maintained; see docs/KNOWN_LIMITATIONS.md.

Not verified in the field yet: a two-device session across networks on the 8.0.14 stack (direct,
UDP relay and TLS relay), and long sessions across an SRTP sequence-number rollover.

# PeerOnQ 0.9.88 - private unsigned candidate

This synchronized full-server/client candidate replaces 0.9.87. Directory.Build.props is the sole
product-version source. Windows, Portable Support, Linux, Android and Apple derive 0.9.88;
Android/Apple bundle codes advance to 9088. Older artifacts retain their original bytes/version.
Install the server and both clients together: the new optional signaling fields (display rate,
busy decline, resume media state) are ignored by 0.9.87 peers and servers, which then behave as
before.

## Fixed in 0.9.88 (server and every client)

From the joint field test of 0.9.87 (server 10.1.10.1, LNK-157 on Windows 11, LNK-363 on
Windows 10 Pro N 22H2 without the Media Feature Pack, i5-5300U 2C/4T):

- Freeze when LNK-363 shares (critical): one libvpx VP8 call did not return while a lock shared
  with every rate change, picture request and Dispose was held. Capture, quality changes and
  picture requests then waited on it; the picture request waited on the RTP receive thread, so
  LNK-363 stopped answering ICE checks and dropped its media 8-11 s after connecting, and closing
  the session waited forever (End Task, 4 Application Hang events). Now:
  - one dedicated thread owns each libvpx context; rate, frame-rate and key-frame requests only
    record the new values, which the encoder thread applies before the next frame with one
    native call (logged with old/new values and its duration);
  - an encode, reconfigure or destroy that does not return within 2 s is logged (call, size,
    threads, kbps, fps); that encoder is abandoned (never destroyed from another thread) and a
    single-threaded encoder continues with a key frame (at most 3 replacements per session);
  - machines with 4 or fewer logical processors encode with one libvpx thread;
  - picture requests refresh the capture on the thread pool, never on the RTP receive thread;
  - closing never blocks: the peer connections close off the caller's thread with a 3 s bound,
    every session release step is bounded (5 s, logged when it is left behind), the session
    always logs "closed: {Reason}" and removes its indicator, and closing the app exits after at
    most 15 s of cleanup.
  - Diagnostics: the sharer summary runs in every media state and includes VP8 encode p50/p95,
    libvpx threads and encoder state; the first encoded frame is logged; "sharer video stalled
    (encoder): frames queued but none encoded for N s" is logged with the gate (secure session,
    media state, encoder busy for X s); the process stall watchdog has a "Screen-sharing video
    pipeline" probe. A local encoder stall never starts an ICE restart or a MediaLost.
  - The viewer logs when it answers an ICE-restart offer while its own media stayed connected
    (0.9.87 answered silently, which read as "the viewer never answered").
- Windows N without the Media Feature Pack: no session path needs Media Foundation. The H.264
  encoder/decoder factories are null without mfplat.dll/msmpeg2vdec.dll, the hardware probe
  returns at once, and both roles use D3D11 conversion, libvpx VP8 and the D3D11/bitmap presenter.
  The dashboard says "hardware video is not available; PeerOnQ uses software video" instead of
  asking to install the pack. On such a sharer with 4 or fewer threads the frame rate starts at
  30 fps (software VP8 cannot encode 60 fps there).
- Connect panel: the "Use a support invitation" switch is gone (a peeronq://support link still
  fills the invitation fields, with a "use a standard connection" link to clear them), and so is
  the "Request system sound" box. Sound is automatic: dashboard requests always include system
  sound, the permission dialog says "audio and voice can be used during the session", and the
  sharer's sound starts on connect (the host can still stop it). Either side can turn on its
  microphone at any time; the other side hears it automatically, and both sides show "Sound on",
  "Mic on" and "... talking". Where an organization policy forbids audio or a device cannot
  capture/play it, the server removes sound from the request instead of refusing the session, and
  the indicator says why. Explicitly scoped requests keep the previous refusal.
- Frame rate and picture quality: Automatic now runs at 60 fps with a 10 Mbps ceiling (was 30 fps,
  6 Mbps) and may climb to the viewer's display refresh rate, at most 120 fps, on a healthy link
  with hardware H.264 when the shared display refreshes that fast. Viewers send their highest
  display refresh rate with each request (older viewers count as 60 Hz); native 4K stays at 30 fps.
  The VP8 safety-net key frame moved from every 2 s to every 5 s (loss recovery still requests key
  frames on demand) and key frames are bounded to nine frame budgets, which removes the periodic
  burst that stalled Wi-Fi links.
- Network Doctor: STUN is a real Binding request to the TURN host on UDP 3478; Relay UDP/TCP/TLS
  send an unauthenticated Allocate on UDP 3478, TCP 3478 and TLS 5349 and report "reachable
  (allocation needs session credentials)" on the expected 401 (no TURN credentials are minted
  outside a session). All timings exclude DNS (resolved first and shown on its own row); a slow
  but measured latency is "High (N ms)", never "Unavailable"; packet loss comes from an active
  session or reads "Not measured"; unprobed rows read "Not tested (needs a session)". The relay
  host is derived from the signaling name (`signal.<domain>` -> `turn.<domain>`).
- One screen-sharing session at a time: a request that arrives while this device shares (or is
  connecting a share) waits for a session that is already ending, otherwise it is declined as
  busy, and the requester sees "already sharing its screen in another session". A queued dialog
  accepted after another share started is declined the same way.
- Resume: a peer that resumes on signaling with its media still connected says so, and the other
  peer keeps its connection without an ICE restart (0.9.87 restarted ICE and refused the viewer's
  input focus right after). When only the sharer restarts ICE, the viewer now moves to the same
  input-session generation, so remote input is not rejected afterwards.
- Telemetry: session heartbeats start only after the cloud accepted the session's Connected event
  (a heartbeat sent earlier was rejected with invalid_request and stopped that session's
  heartbeats for good).
- Logs: "media established via `<path>` (codec pending)" until a sharer report carries the codec,
  then "video: `<encoder>`, WxH"; a request that times out is reported as one consistent timeout by
  the dialog and the coordinator; WARP is logged with a justification (no PeerOnQ device requests
  it on a machine with hardware adapters; the Windows UI framework most likely loads it), and
  PEERONQ_TRACE_WARP=1 records the loading thread and callers on a test machine.

Not verified in the field yet: 10-minute LNK-363 share with 5 clean closes, self-connect refusal,
redeploy downtime with a connected client, resume after a real Wi-Fi drop.

### 0.9.88 candidate artifacts (private, unsigned)

| Artifact | Bytes | SHA-256 |
| --- | --- | --- |
| `peeronq-server-0.9.88.run` | 164,032,796 | `2a1d44e24e6d6d5733e771e52ad11510c2315af43aee44e4448c14edec382653` |
| `PeerOnQ-0.9.88-unsigned-public-pilot-x64.msi` (embedded) | 85,569,536 | `61a54c92bcef8a8b4c00eb6b3f7621093b2f1a06a3aa7489c587d5890cf6568a` |
| `PeerOnQ-0.9.88-unsigned-public-pilot-arm64.msi` (embedded) | 78,057,472 | `21c6a8829e711e5f9ea663c297be1955ce9cf22f72d119816b1d379e9957f5bc` |

Upgrade (existing installation, no migration, online by default):

```bash
cd /home/peeronq
printf '%s
' '2a1d44e24e6d6d5733e771e52ad11510c2315af43aee44e4448c14edec382653  peeronq-server-0.9.88.run' | sha256sum --check -
chmod 700 peeronq-server-0.9.88.run
sudo ./peeronq-server-0.9.88.run --env-file /etc/peeronq/peeronq.env --dry-run
sudo ./peeronq-server-0.9.88.run --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.88.run --status --env-file /etc/peeronq/peeronq.env
```

Rollback: `sudo ./peeronq-server-0.9.88.run --rollback --env-file /etc/peeronq/peeronq.env`, or reinstall
0.9.87. Installing the server does not upgrade installed clients: install the 0.9.88 MSI on both
Windows PCs.

## Fixed in 0.9.87 (server and every client)

From the joint field test of 0.9.86 (server plus two Windows clients, live signaling restart):

- Signaling restart, server: a session can be resumed after signaling comes back. Resume tokens
  were issued once, at accept, and expired two minutes later, so any session older than that
  could never resume; a live session now receives a fresh token with its relayed messages before
  half its lifetime has passed, and the token it replaced stays valid until its own expiry. A
  server that shuts down no longer starts the 30 s resume window of every session (a 43 s restart
  outlived it): the sessions and their owner leases stay in Redis, the restarted server waits one
  resume window before it handles owner leases that expired while it was down, and a device that
  registered again still gets the resume window for a session it has not resumed yet. Every refused
  resume is logged with its reason (token missing, expired, mismatch, protocol, disconnect window,
  state) and the lease owner.
- Signaling restart, client: the resume result and its reason are logged; a refused resume is
  retried up to three times with re-authentication and backoff while media is up. Until the
  session is owned again, quality reports, display lists, display requests and ICE candidates are
  held instead of being sent (0.9.86 produced about 2.5 not_session_participant errors a second).
  A session whose media is connected is no longer ended because signaling could not take it back
  (or swept it): it continues peer-to-peer, the dashboard says so, and it ends when its media ends
  or the peer closes the connection.
- TURN: closing a session closes its TLS connections to the TURN server (turns:, port 5349).
  SIPSorcery stopped refreshing them but never closed them, so each session left its coturn
  allocations alive against user-quota=4. coturn deletes the allocation of a TCP/TLS client when
  its connection closes. UDP allocations are no longer refreshed and expire on their lifetime; a
  lifetime-0 Refresh would need SIPSorcery's internal TURN nonce and is not sent. The result is
  logged once per session.
- Quality reasons: the server accepts the client's capture_restart reason, relays any well-formed
  reason code it does not know (logged once) and drops only a malformed one, keeping the rest of
  the report, so clients and server can ship independently.
- Quality on a static screen: jitter is judged against the measured frame interval (a desktop at
  2 fps made 35-88 ms of jitter on a clean LAN) and follows the soft rules: 3 consecutive windows,
  frame rate only, one level per 10 s, never a lower scale for jitter alone.
- Self-connect: the dashboard refuses this computer's own ID and hides it from saved devices; a
  request that signaling refuses (target_is_self, target_offline, policy, ...) ends at once instead
  of waiting 30 s, and a request that never reached a peer is not reported to the cloud. The
  viewer's cloud Started is sent once the peer answered (or the request window ended).
- Server upgrade: the previous release keeps serving through the image build, prerequisites,
  storage preflight, the forward-only database migrations and the permission grants; the final
  start then replaces each changed container in place, so signaling is away only for its own
  restart instead of 3 min 42 s. A release whose migration is not compatible with the release it
  replaces must be installed with --quiesce-for-migrations (the old stop-first order). 0.9.87 adds
  no migration.
- Client robustness: one signaling reconnect owner (the window retry no longer runs while the
  client's own reconnect loop does); a failed registration closes its socket and reports Faulted;
  a startup failure is retried after 2, 4 and 8 s with its reason logged; the heartbeat ends
  quietly with its closed connection; refreshing the cloud token no longer flips the public state
  to Connecting; the WARP diagnostic uses {Trigger} (it overwrote Component=desktop) and a startup
  sampler names the startup step after which WARP first appeared; data-channel closes during a
  session close are logged at debug, and the TURN check runs once per session.

Not changed: LNK-363 (Windows 10 Pro N) still needs the Media Feature Pack for H.264; without it
every session with it is software VP8 (an operator/user action, not code).

## Fixed in 0.9.86 (server and every client)

From the remaining items of the joint field test of 0.9.85:

- Signaling outage: a signaling disconnect no longer ends a session whose peer-to-peer media is
  still connected. The session keeps running, resumes on signaling once the device registers
  again, and logs how long signaling was away. When media is lost as well, reconnect attempts are
  paced (500 ms doubling to 8 s) and the session ends on the time limits (30 s to resume without
  confirmation, 45 s overall), not after 20 attempts in about 4.6 s.
- Quality: viewer-side signals must persist for 3 consecutive windows before they lower the frame
  rate; viewer input latency now follows the same soft rules. Render latency counts only above
  three frame intervals while render falls behind decode, so a presenter with a steady delay
  (vsync, compositor) no longer lowers the rate. The frame-age allowance uses the measured capture
  interval, so a static screen is not read as late frames. A display switch re-applies the level
  and logs "re-applied (capture_restart)" instead of a fake L1 -> L1 change.
- Session telemetry: a later event whose session the cloud never recorded first sends that
  session's queued start (an outage could leave the start in backoff behind it); a live session
  the cloud lost is started again once; otherwise the session's queued events are dropped and
  logged once instead of being retried on every heartbeat.
- Server upgrade: the installer builds and pulls the new release's images while the previous
  release keeps serving, and only then stops it. The image build caused most of the roughly
  8 minutes without signaling during a redeploy. The previous release still stops for the
  database migrations; containers are not yet replaced one by one.
- Diagnostics: the first load of the WARP software rasterizer (d3d10warp.dll) is logged once with
  the component that preceded it (main window, screen capture device, H.264 decoder or viewer
  presenter) and the default graphics adapter; the capture device logs its adapter.

## Fixed in 0.9.85

From a joint field test of 0.9.84 with two Windows clients (one Windows 10 Pro N):

- Crash: an incoming-request dialog that went unanswered crashed the whole app when its timeout
  fired (and ended any live session). The timeout and a withdrawn request are now handled on the
  UI thread; the app also writes a last-chance log line before an unhandled exception ends it.
- Requests: one permission dialog at a time; a second request waits within its own deadline
  instead of being declined by an error. A request the requester ends closes its dialog and is
  never answered late. A device refuses to send a second request to a device that has not answered
  the first. Every dialog step is logged.
- Session telemetry: clients sent the peer as "LNK-..." and the cloud rejected every session
  event, so no session reached the cloud. Clients send the 12-digit ID and the server also accepts
  the prefix from 0.9.84 clients. Accepted events and cloud online transitions are logged.
- Quality: viewer-side render latency and render backpressure now follow the frame-age rules: at
  most one level per 10 s, frame rate only (never the scale), and an upgrade the viewer cannot hold
  doubles the wait before the next try (up to 2 minutes). Quality never steps up while nothing is
  encoded. Decisions log the measured values and targets. On 1080p sources the "1/2 scale" rungs
  never shrank the picture (capture floor); they lowered rate and bitrate only.
- Viewer: the Direct3D 11 swap chain is the default when the machine has exactly one hardware
  graphics adapter; hybrid/multi-GPU machines and software adapters keep the bitmap presenter.
  The log names the presenter, adapter and reason. Separate windows switch to the bitmap presenter.
- Windows N: missing Media Foundation is detected, logged and shown with a hint to install the
  Media Feature Pack (Settings > Apps > Optional features), without which video is software VP8.
- Signaling: messages to a peer that went offline are held (2-30 s backoff, logged once) instead
  of producing a target_offline error for each ICE candidate.
- Diagnostics: a watchdog on its own thread logs a blocked UI thread, a starved thread pool or a
  stopped cloud loop; open TURN connections still present 5 s after a session closes are logged.

## Fixed in 0.9.84

From the 0.9.83 field review (server logs and one client's log; the other device's log was not
available):

- Cloud connection: a session heartbeat the cloud rejected (`session_not_found`, a 404) tore
  presence down into the 30 s reconnect cooldown, because the client compared uppercase codes and
  required a "permanent" error. Session and update telemetry are now best effort: a rejected
  session heartbeat stops only that heartbeat, and no telemetry failure takes the device offline.
- Frozen picture: when a connected viewer decodes nothing for 3 s it asks the host for a picture
  (backing off to every 10 s), shows "No new picture for N s", and logs when video stops and
  resumes. A host that receives the request while its capture has been idle for 5 s logs the
  capture and encode ages and restarts the capture when the re-emit produces nothing.
- Lost sessions: every reconnect attempt, ICE-restart offer and answer, restore and failure is
  logged, and both users see why a session ended after a connection loss instead of a closed
  window.
- LAN sessions: host candidates of host-only virtual adapters (Hyper-V/WSL, VMware, VirtualBox,
  Docker) are no longer advertised; they delayed the direct LAN pair until a relay won. VPN
  adapters stay advertised. The selected candidate pair and local adapter are logged at connect.
- Quality: frame age alone lowers at most one level per 10 s and, while the viewer renders
  promptly, lowers the frame rate only, never the scale; its target now includes the frame interval
  so a lower rate cannot read as late frames. Loss, RTT, jitter and bandwidth still react at once.
  A display switch starts again from the start level instead of 10 fps. Frame-age decisions log
  the measured P95, target and clock uncertainty; both sides log why H.264 or VP8 was chosen.
- Server: rejected API requests log the exact rule; session participant mismatches say which side
  resolved a device differently. Signaling logs when a session's media connects and its path
  (direct LAN, direct internet or relay; reported by 0.9.84 hosts) and path changes. Presence logs
  each connection's lifetime, registration and end reason, and replaced connections. TURN listens
  on the pinned relay address and loopback only.

Not changed in 0.9.84, by design or pending evidence: the GPU viewer stays opt-in (it crashed in
0.9.79 and is unverified on hybrid-GPU laptops); "All displays" keeps its 4096-pixel encode width
(software VP8 cannot encode 5760-pixel desktops interactively); TURN permissions for private peer
addresses are created inside the maintained SIPSorcery core and are denied harmlessly by the
server; a relay-to-direct ICE restart while connected is not attempted.

## New in 0.9.83: each remote monitor in its own window

- When the shared computer has more than one monitor, the viewer toolbar shows "Separate
  windows". It switches the host to "All displays" and shows the first remote monitor in the
  viewer window and every further monitor in its own window, so each can be placed on a local
  monitor. All windows are cut from the one combined stream: no extra encoder, bandwidth or
  connection.
- Mouse, wheel and keyboard work in every window and go through the same approved Full Control
  channel. Moving between the session's windows keeps control; leaving them for another
  application pauses input as before. Closing the extra windows, or choosing a single monitor,
  returns to one window.
- The host publishes each monitor's position in the display list (`x`/`y`, omitted when unknown).
  The button appears only when the host, the viewer and the signaling server run 0.9.83; older
  peers ignore the positions. The opt-in GPU renderer (`PEERONQ_VIEWER_RENDERER=gpu`) keeps a
  single window.
- The viewer rejects malformed position lists, including out-of-range coordinates.

## Fixed in 0.9.82 (server and every client)

Production logs on 0.9.81 showed devices flapping between online and offline: 2,078
`invalid_initial_presence_state` errors in 8 hours, `heartbeat_rate_limited`, presence negotiate
HTTP 429 and requests for dead presence connections.

- Server, Presence: Register accepts a device that reconnects during a remote session (Busy or
  InSession); 0.9.81 and older clients send exactly that and were rejected on every reconnect.
  Register no longer counts as a heartbeat, so the heartbeat those clients send right after it is
  accepted; later heartbeats on a connection stay at least 3 s apart. Connection attempts are
  limited per authenticated installation (30 per minute; per address when unauthenticated), so
  devices behind one NAT address no longer block each other, and a rejection carries Retry-After.
  Installing the server alone stops the loop for already-installed 0.9.81 clients.
- Client: presence registers Online and then restores Busy/InSession; SignalR automatic reconnect
  is removed because a reconnected connection was never registered (presence_not_registered), and
  the bounded retry loop reconnects and registers as soon as the hub closes. The first hub
  heartbeat waits for the next interval after Register. Rate limits and `invalid_*` presence errors
  wait for the cooldown or Retry-After instead of the fast retry burst. The state returns to
  Online after a token refresh.
- Server, Signaling: the in-place registration renewal a connected device makes before its
  connection token expires is logged as a renewal; it never displaced the connection or a session.
- Server, TURN: on more than one Docker network coturn relayed from each discovered address in
  turn, but Docker publishes the relay ports only to the default-route network, so some relayed
  sessions received nothing. The relay address is now pinned to that network and the external IP is
  mapped to it; `PEERONQ_TURN_RELAY_IP` overrides the detected address. `max-bps` is bytes per
  second (80 Mbit/s per allocation) and does not cap remote-desktop quality. Private peer
  addresses stay denied.

## Fixed in 0.9.81

- Multiple monitors: the sharer publishes its display list right after the offer, before the
  viewer window exists, and that list was dropped. The viewer now keeps it, so the monitor
  picker, the arrows and "All displays" appear on every multi-monitor host.
- First picture: the host can finish its half of the post-quantum handshake first and send the
  first frame before the viewer can authenticate it; that frame was dropped, and a static remote
  desktop then sent nothing new until something changed. The viewer now asks for a key frame as
  soon as it can decrypt and again when its window opens; the host re-emits its unchanged screen
  for it. Frames decoded before the window opened are kept and shown.
- Viewer rendering: the WriteableBitmap presenter is the default again. The Direct3D swap chain
  was verified only on single-GPU desktops; it is opt-in with `PEERONQ_VIEWER_RENDERER=gpu`.
- Quality: the adaptive ladder no longer drops to a quarter-scale (480x270) desktop; half scale
  is the floor. Late frames whose viewer renders promptly lower the frame rate, not resolution.
  Field logs showed 0.9.80 falling from 1080p to quarter scale within 15 s.
- Diagnostics: both sides log a media summary every 10 s (codec, size, frame rates, bitrate,
  RTT/loss/jitter, path, repairs) and every quality change with its policy reason.

## Fixed in 0.9.80

- 0.9.79 viewers crashed as soon as a session opened the viewer window (WER APPCRASH
  0xc000027b, stowed 0x802B000A): the host showed "connected" while the viewer showed nothing.
  The GPU swap chain panel set a Background property that WinUI rejects only at runtime. The
  viewer now opens, renders and keeps all scale modes with the GPU swap chain and the bitmap
  fallback; a real-window runtime gate now runs against the packaged client.
- Frames replaced while ICE and the hybrid post-quantum session negotiate no longer cause a
  quality downgrade (30 to 20 fps) at connection.
- Do not install 0.9.79 or 0.9.80 clients. Replace them with 0.9.85 on both the viewing and the shared
  computer; the server upgrade alone does not update installed clients.

## Client changes since 0.9.78 (first built as 0.9.79)

These are client behavior changes, not a version-only rebuild. Both peers must run 0.9.85 for
the negotiated features; an older peer keeps its previous behavior with this release.

- 4K on 4K displays: a 4K-class source streams native 4K when the viewer has a monitor with a
  short edge of at least 1440 and hardware H.264 is usable; every other display streams a
  1080-pixel short edge. Congestion gives up 4K before frame rate. 4K stays at 30 fps.
- 1080p at 60 fps: Automatic quality may climb to a 60 fps rung (9 Mbps) only on a healthy link
  while hardware H.264 encodes a standard-tier source. Sessions start at 30 fps; software VP8,
  a 4K source or a lost hardware encoder stay at 30 fps.
- Freeze recovery: transient capture failures and graphics device loss restart capture instead of
  ending the session; hardware encoder failures retry up to three times before VP8; the viewer
  requests a key frame on RTP sequence gaps; the safety-net GOP is 5 s.
- GPU capture conversion: scaling and BT.601 NV12 conversion run on the GPU (1.4-4.1 ms instead
  of 38-51 ms CPU box scaling).
- RTP pacing: large frames (4K key frames) are spread at 2.5x the target bitrate after a
  16-packet burst, never beyond a 20 ms per-frame deadline, instead of one line-rate burst that
  overflowed router/socket queues and forced repeated key frames.
- NACK repair: a viewer that negotiates `a=x-peeronq-video-rtx:1` reorders packets, requests each
  missing one by RTCP generic NACK and receives an in-band retransmission (same SSRC, fresh
  sequence number, SRTP replay-safe, 35% bitrate budget). An unrepaired frame is skipped with a
  key-frame request. Older peers keep SIPSorcery's framer; its reassembly limit for older senders
  rises from 1 MiB to 8 MiB so large 4K key frames are no longer dropped.
- GPU viewer: the Windows viewer presents through a Direct3D 11 SwapChainPanel swap chain off the
  UI thread and keeps all four scale modes and pointer mapping. Any device/interop failure, or
  `PEERONQ_VIEWER_RENDERER=bitmap`, uses the previous WriteableBitmap path.
- All displays: a multi-monitor host offers a combined view; viewers step through displays with
  toolbar arrows or the local Ctrl+Alt+Shift+Left/Right shortcut (never forwarded).
- Two-way voice: Opus 48 kHz mono via managed Concentus 2.2.2 with Windows communications-mode
  echo cancellation. Consent is local on each device; nothing is captured or played until that
  device's user enables Voice. Voice uses its own lossy hybrid-PQ record context.
- Files prefer the direct P2P lane, pinned per transfer; the encrypted relay is used only when no
  direct path exists. Bulk traffic yields to measured queueing delay so input and video stay first.
- The hybrid post-quantum session (ML-KEM-768 + X25519, ML-DSA-65 + Ed25519, AES-256-GCM per
  direction and channel) is unchanged and now also protects voice records.

## Server, website, Portal and Admin

Signaling changes in 0.9.87: resume-token rotation for live sessions (with the replaced token valid
until its expiry), sessions kept resumable across a signaling shutdown/restart, resume refusals
logged with their reason, unknown quality reason codes tolerated, and capture_restart accepted.
The installer upgrades online by default (--quiesce-for-migrations restores the stop-first order).
Older 0.9.86 clients keep working with this server; the client-side resume, P2P-only and held-
message behavior needs 0.9.87 clients. The Cloud API, Presence, Portal and Admin are unchanged and
no migration is added. The Cloud API accepts prefixed peer IDs (0.9.85); the 0.9.84 Presence,
Signaling and TURN changes, the 0.9.83 display positions and the 0.9.82 presence fixes are
retained. Media stays peer-to-peer after setup; servers handle presence, signaling and the TURN
relay fallback. Images are rebuilt with this synchronized release so offered downloads select
0.9.87. Server installation updates offered downloads; it does not automatically replace
already-installed clients.

New dependency: Concentus 2.2.2 (Opus BSD-3-Clause notice ships with release artifacts; its
optional native probe is disabled). The maintained PeerOnQ.SIPSorcery 10.0.17.6 core is unchanged.

## Artifacts and validation boundary

Windows x64 and ARM64 installers use unsigned-public-pilot classification. Portable Support
x64/ARM64 use unsigned-development and do not install the optional service broker. Linux remains
a private unsigned attended viewer/controller preview. Android/Apple receive derived source
versions only; no physical-device or distribution approval is implied.

Fresh validation for this patch: full .NET suite with the hardware video gate and a temporary
loopback Redis 1,497 total / 1,473 passed / 2 failed / 22 environment skips. The failures are load
thresholds in code this release does not change: the QUIC goodput test
(`ReceiverConfirmedGoodputEscapesTheWebRtcEstimateWithoutBlockingInput`, load-sensitive since
0.9.79) and an SCTP sender flow-control timing test; both pass 5 of 5 isolated runs and no threshold
was weakened. A run without the hardware gate had 1,486 / 1,448 / 0 failed / 38 skips. New tests: a
signaling restart longer than the resume window keeps the session resumable for both devices (red
on 0.9.86 server code), a live session resumes after its first token expired, a rotated token keeps
the previous one until it expires, resume refusals name their reason, unknown and malformed quality
reasons, the TURN TLS connection is open after the peer closes and closed after the release, a
static screen with field jitter holds L1 for ten minutes (0 changes), sustained jitter lowers only
the rate, refused resumes go peer-to-peer after three tries with held quality reports, a server
sweep keeps connected media until the peer closes, refused and self-targeted requests end at once
without cloud telemetry, and a refused request error names its session. The exact packaged x64
client ran the real viewer window with both renderers, a separate monitor window, and three
permission dialogs (unanswered, concurrent, withdrawn) that all ended as Timeout without a crash.
Public web tests 118/118, typecheck and lint, the native UI gate, five source release contracts and
the installer contract in the CI Linux container passed. All endpoints shared one machine: this is
not physical two-device, Windows N, hybrid-GPU laptop, physical multi-monitor, physical-audio or WAN
evidence.

The Windows pair was built from commit e43b8e1 with 530 frozen client/installer inputs and zero
drift. It passed full extracted-payload, ICE03 and the unchanged 96 MiB size gate with zero
warnings/errors and carries `ThirdParty/Concentus/LICENSE` plus the ten maintained SIPSorcery
notices. Exact embedded inputs and the Portable Support pair:

| Artifact | Bytes | SHA256 |
| --- | ---: | --- |
| x64 MSI | 85,524,480 | 3d170a1b21909794fb66f073a7d0a97500053d0045b80d15aad17738a1cee12b |
| ARM64 MSI | 78,016,512 | c7d69adf28b9c0b7f2140ad6683ec118ff386004f78e33c183e6f501d22e5c6e |
| x64 Portable Support ZIP | 101,655,312 | 570cae8030ddaab1a3242a610a31659e205ab58ea2727600eb0b91043b3cb055 |
| ARM64 Portable Support ZIP | 97,608,137 | 1046eee0d500a3e110dd0b8c82ce0fae4bcaf9bb1f724001f4ab88db7d096b2b |

The Linux preview is not embedded in this bundle; its status is recorded in the accompanying
VALIDATION.json. Post-build acceptance and the whole-bundle hash are external to this immutable
payload; require the explicit current pass in VALIDATION.json.

All existing security/compatibility fixes are retained: maintained PeerOnQ.SIPSorcery 10.0.17.6
provenance/notices, bounded authenticated transport, file integrity, protected optional LocalService
broker, default-off attended audio, Node 24 Docker pins and nested CR/download-header checks.
No encryption, authorization, licensing, quota, TLS, hash, package-size or release gate is relaxed.

## Configuration and coordinated rollback

Reuse /etc/peeronq/peeronq.env and the existing /opt/peeronq installation. Preserve SMTP settings,
TLS, customer registration/MFA, operator restrictions, data volumes and forward migrations.
An existing installation must not use --bootstrap.

No new migration is introduced after 0.9.77. The additive default-false system-audio policy column
remains. Cloud/Signaling must both understand audio flag 64 before enabling that policy. A rollback
to pre-audio parsers requires disabling the policy and refreshing device attestations, or waiting
their lifetime plus verifier clock skew; older parsers reject flag 64 even for non-audio sessions.
Keep the additive column/data and do not run a destructive database downgrade.

This installer upgrades online by default: the previous release keeps serving until each changed
container is replaced. 0.9.87 adds no migration, so --quiesce-for-migrations is not needed. 0.9.87
clients also work against a 0.9.86 server (without token rotation, and capture_restart reports are
refused there as before); 0.9.86 clients work against this server.

## Operator CLI

Copy the exact .run and trusted matching checksum sidecar to /home/peeronq. In Bash:

~~~bash
(
set -eu
cd /home/peeronq
sha256sum --check peeronq-server-0.9.87.run.sha256
chmod 700 peeronq-server-0.9.87.run
sudo ./peeronq-server-0.9.87.run --env-file /etc/peeronq/peeronq.env --dry-run
sudo ./peeronq-server-0.9.87.run --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.87.run --status --env-file /etc/peeronq/peeronq.env
)
~~~

The checksum proves integrity, not publisher authenticity. This private candidate has no approved
detached server signature or Authenticode/timestamp approval. It is not ready for production or
public redistribution. Restricted dependency/license review (including Concentus/Opus), native
source/build/security provenance and exact-artifact SBOM, clean-machine MSI/SCM/LocalService
lifecycle, physical ARM64, two-endpoint media/voice/multi-monitor/WAN and non-Windows
capability/publication gates remain open. No production installation was performed while
preparing the bundle.

For an already installed candidate, after the applicable compatibility preconditions above,
restore the retained previously verified release through the existing installer rollback:

~~~bash
sudo ./peeronq-server-0.9.87.run --rollback --env-file /etc/peeronq/peeronq.env
sudo ./peeronq-server-0.9.87.run --status --env-file /etc/peeronq/peeronq.env
~~~

Rollback does not downgrade installed clients. Retain the earlier release files, data and protected
configuration; preserve immutable 0.9.78-0.9.86 artifacts/evidence for comparison and recovery.
