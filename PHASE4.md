# PeerOnQ Phase 4: Collaboration and unattended access

Status date: 2026-08-17
Gate: `PASS_WITH_EXTERNAL_BLOCKERS`

This document describes the real .NET desktop product under `src/`. The offline React prototype
under `artifacts/peeronq` is not runtime evidence.

## Implemented scope

- A version 2 collaboration protocol inside mandatory `PNQE` AES-256-GCM records on the ordered
  WebRTC DTLS/SCTP channel. Every frame is bound to the exact signaling session, direction, channel,
  key epoch, sequence and permission generation.
- Hybrid ML-KEM-768 + X25519 establishment and ML-DSA-65 + Ed25519 transcript authentication must
  reach `SECURE` before encoded video or collaboration data flows.
- File and recursive folder transfer with explicit offer/approval, destination and collision policy,
  streaming 256 KiB chunks, progress/speed/ETA, pause/resume/cancel, exact offsets, per-chunk and final
  SHA-256, reconnect resume, `.partial` cleanup, disk preflight and atomic same-volume finalization.
- No commercial or arbitrary product transfer-size cap. Administrators may configure positive
  file/transfer quotas; defaults use the protocol/filesystem `long` range. Manifests remain bounded
  to 100,000 entries and protocol frames remain bounded.
- When both endpoints negotiate `file.relay.v1`, FileTransfer records use the authenticated TLS
  signaling socket as opaque binary relay frames. The server checks permission, session and current
  connection ownership but cannot read metadata/content. Non-negotiated peers retain the direct-P2P
  fallback and a relayed or not-yet-nominated ICE path fails closed. There is no product bandwidth
  throttle; bounded records, controlled workers and receiver/network backpressure prevent unbounded
  local memory growth.
- Plain-text clipboard synchronization, default off per session, requiring both accepted
  `ClipboardText` permission and local enablement on both peers. It is size-bounded and loop-safe.
- Encrypted address book/group CRUD, search/filter/sort, tags, favorites and notes. User input cannot
  forge OS or last-seen data; those fields are displayed only when authenticated presence supplied
  them. Saving a device never makes it trusted.
- Trusted-device records bound to exact registered-key fingerprint and allowed scope, with expiry,
  revocation, last use, fingerprint-change invalidation, scope-expansion rejection and audit.
- Unattended access off by default, explicit setup/disable, password or single-use recovery proof,
  PBKDF2-HMAC-SHA512, one-use challenges, replay/scope/identity binding, lockout and visible session
  disclosure. Trusted-device authentication is separately fingerprint/scope checked.
- Windows AMSI scanning before a received partial file receives its final name. This is a scan
  contract, not a statement that a received file is safe.
- SQLite security audit events with content-free metadata; protected profile secrets use Windows
  DPAPI `CurrentUser`.

The WinUI connect surface exposes View Only, Full Control and File Transfer as the three primary
choices. Optional file transfer and text clipboard permissions are explicit additions to an
attended request. Unattended mode is a separate default-off choice and accepts a password or
recovery code; a stored trusted-device record may satisfy it only within its exact scope.

## Protocol and compatibility

`CollaborationProtocolCodec.CurrentVersion` is `2` and `SignalingProtocol.CurrentVersion` is `3`.
Signaling also requires `security.hybrid-pq-v1` on both peers, so legacy/unsupported clients receive
`capability_mismatch` before a session can partially connect.

Collaboration frames retain the existing `PNQ4` magic and carry:

- frame kind and bounded header length;
- protocol version;
- signaling session ID;
- permission generation;
- bounded JSON control data or a JSON transfer header followed by raw chunk bytes.

The channel label is `peeronq.secure.v1`; its subprotocol is `peeronq.hybrid-pq.v1`. `PNQH`
handshake frames use strict version/state/transcript checks; application records use `PNQE`. Unknown
messages/members, mismatched frame kinds, wrong session/generation, malformed offsets/hashes,
control frames over 768 KiB, chunks over 64 KiB and total frames over 1 MiB fail closed.

Interactive input messages use the foreground lane. File chunks use a bounded 16 MiB in-flight
window, with no rate cap; foreground waiters pre-empt bulk sends. The media encoder also keeps a
bounded latest-frame queue. Full details are in `docs/SECURE_TRANSPORT.md`.

## File-transfer security and lifecycle

- Sources are selected locally and recursive enumeration refuses symlinks, junctions and reparse
  points.
- Every file record has a transfer-ID-derived AEAD key. A negotiated opaque relay accepts only the
  FileTransfer channel after session/permission ownership validation; otherwise direct LAN or direct
  internet ICE is required.
- Relative paths reject traversal, roots, UNC/drive syntax, reserved or invalid Windows names,
  trailing dots/spaces, control characters and segment/total-length overflow.
- The destination root and all parents are containment/reparse checked during preparation, before
  every chunk and before finalization. Destination disappearance and disk-full conditions produce
  stable failure codes.
- Receiver-selected collision behavior is overwrite, rename or skip. `Ask` never reaches writes;
  skipped files report their full received offsets so the sender transmits no skipped content.
- Incoming data is written beside the destination as
  `<name>.peeronq.<transfer-id>.partial` using exact offset and monotonic chunk-index checks.
- Completion verifies size and whole-file SHA-256, scans the partial through the configured malware
  scanner and atomically renames on the same filesystem. PeerOnQ never launches received files.
- Cancel, reject, validation/protocol failure, session end and app shutdown remove partial files and
  empty directories created for the transfer. A connection interruption pauses the transfer and
  keeps verified partial data for exact-offset resume.

Folder hierarchy and empty files/directories are preserved. Atomicity is per file; an entire
multi-file folder transfer cannot be finalized as one filesystem transaction.

## Clipboard policy

Only Unicode plain text is supported. Images, files, HTML, RTF and custom/binary formats are not
implemented. The default UTF-8 cap is 120 KiB, chosen so worst-case JSON escaping remains inside the
bounded control frame.

Clipboard starts disabled for every session, including trusted and unattended sessions. Both peers
must enable it. Origin/change identifiers and SHA-256 equality suppress echo loops. Reconnect or any
protocol uncertainty pauses delivery. On session end, remotely supplied text is cleared only if the
clipboard has not since changed locally. Text, length and hashes are absent from logs and audit.

## Address book, trust and unattended access

The address book and groups support create/update/delete, tags, favorites, notes and group cleanup.
Presence is derived from authenticated signaling. OS/last-seen values from legacy or UI input are
not trusted or displayed until authenticated provenance exists.

Trust requires PeerOnQ ID, exact registered-key SHA-256 fingerprint, exact allowed permission mask,
`Trusted` state and an unexpired approval. Fingerprint change revokes eligibility. Expiry is derived
and persisted. Scope expansion fails and is audited.

Unattended password proof uses a random 256-bit nonce, short-lived challenge ID and a 64-byte
PBKDF2-HMAC-SHA512 key with at least 600,000 iterations. The HMAC binds challenge, nonce, requester
identity/fingerprint and requested scope. Recovery codes are normalized, hashed at rest, used as
one-time proof keys and zeroed after success. Five failures lock credential authentication for
15 minutes. Passwords, recovery codes and verifier-equivalent data never traverse signaling or
enter logs/audit.

Unattended access does not install a Windows service, bypass UAC/UIPI/Secure Desktop, promise
pre-logon or lock-screen control, hide the local indicator, or silently persist. Those scenarios
remain external/unsupported until separately implemented and physically verified.

## Local verification on 2026-08-17

Environment: Windows 11 Pro build 26200, .NET SDK 10.0.303, x64 Release, local loopback WebRTC peers.
Relevant results:

- Full Application tests: 117/117 passed, including hybrid handshake/record-layer regressions.
- Signaling tests: 86 passed, 4 explicit live-Docker/HA tests skipped.
- Domain tests: 61/61 passed.
- Media tests: 62 passed, 1 explicit live-TURN test skipped.
- Phase 4 Infrastructure tests: 4/4 passed.
- End-to-end tests: 44/44 passed; the final security change also reran file/shutdown 2/2.
- Full solution and WinUI x64 Release builds: 0 warnings, 0 errors.
- Brand purity: passed.
- Local v2 WSS/TURN stack: rebuilt and healthy on `10.0.0.10`.
- Unsigned x64 Development MSI `0.8.2`: ICE03/extract/self-contained/exact-payload validation passed;
  78,438,400 bytes; SHA-256
  `366AF8F62A09E8ABA3EA0852205E97F767CA4D9DD84B69E1EB540E58FDC425C9`.

Verified conditions include 16 MiB synthetic streaming with 64 KiB chunks; an 8 MiB file through
the real local WebRTC data channel with 1 KiB chunks while interactive pointer input is delivered;
folder/empty-file/collision handling; exact resume offsets; corruption, duplicate/out-of-order,
destination removal, disk preflight, cancellation, traversal and real Windows junction rejection;
clipboard default-off/bilateral enable/disable/loop prevention/redaction; trust mismatch/scope/expiry;
unattended replay/recovery/lockout; DPAPI persistence and sanitized audit.

In the isolated local WebRTC contention test, the interactive pointer message arrived in 260.4 ms
while the 8 MiB/1 KiB-chunk transfer was deliberately backpressured. The full test completed in
34 seconds. This is a repeatable loopback measurement on the declared desktop, not physical-LAN
input latency or transfer-throughput evidence.

## External evidence still required

Loopback and synthetic tests do not substitute for two physical devices. The required external
matrix remains: 1 GB and larger-safe-data transfer, interruption/resume, simultaneous real control
and transfer, recursive folder transfer, clipboard enable/disable, trusted revoke, unattended after
app restart, and documented locked/elevated behavior. Record devices, OS, network/NAT/path,
candidate pair, size/hash, interruption duration, timing and result. Until that evidence exists,
the phase gate is `PASS_WITH_EXTERNAL_BLOCKERS`, not `PASS`.
