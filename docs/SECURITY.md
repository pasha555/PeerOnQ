# Security model

## Trust anchors

- The signaling directory identity remains ECDSA P-256. It binds a hybrid ML-DSA-65 + Ed25519
  device identity. Windows private material is DPAPI-protected; Linux private material is stored in
  the signed-in desktop user's Secret Service keyring through `secret-tool`, namespaced by the
  normalized data-directory hash. Linux startup fails closed when that protected store is unavailable.
  Android private material is AES-GCM ciphertext in private app preferences; the wrapping key is a
  non-exportable Android Keystore key and malformed/decryption-failed state fails closed.
  Apple private material is stored as device-only, non-synchronizing SecItem records in the
  data-protection Keychain; Keychain uncertainty or malformed state fails startup closed.
- Signaling registrations and session attempts prove possession and use replay-protected nonces.
- WebRTC DTLS/SRTP remains defense in depth. A mandatory application layer uses ephemeral
  ML-KEM-768 + X25519 establishment, dual transcript signatures, HKDF-SHA-512 and AES-256-GCM for
  video/input/clipboard/file records; see [SECURE_TRANSPORT.md](SECURE_TRANSPORT.md).
- TURN credentials are server-issued, authenticated, short-lived HMAC credentials; clients contain
  no static TURN password.
- Update manifests use a separately managed ECDSA P-256 key compiled as an SPKI public key. Release
  packages require both the manifest SHA-256 and a trusted Authenticode publisher fingerprint.
- TLS certificate validation is never disabled. Development trust is isolated from release trust.

## Authorization and consent

The accepted permission mask is immutable for a session. Resume and unattended flows can preserve or
reduce scope but cannot expand it. View, control, clipboard, send-file, and receive-file are separate
permissions. Clipboard and file transfers also require their local policy/confirmation. Reconnect
immediately disables input through `IInputSafetyController` and releases all held keys/buttons.

Unattended passwords remain local DPAPI-protected verifiers. A connection uses a short-lived,
identity- and permission-bound HMAC proof instead of transmitting the password. The challenge may
disclose only the configured allowed permission mask: when the viewer selected a broader mode, the
client asks before retrying with the remote-approved narrower mode. The host still enforces the mask,
lockout and one-use challenge; this hint is not authorization and cannot expand permissions.
Remote control requires the immutable `ControlInput` permission, explicit viewer enablement, ordered
bounded commands, and a standard-user Windows input sink. Every input frame carries the session ID,
reconnect generation, focus generation, input-protocol version, and monotonic sequence. The sharer
acknowledges each new focus generation before the viewer forwards input. Replay, cross-session,
pre-reconnect, stale-focus, invalid-coordinate, or rate-abuse input fails closed. The host can revoke
`ControlInput` without ending view; the reduction is audited, signaled to the viewer, and cannot be
restored by reconnect. PeerOnQ does not bypass UIPI, UAC, the secure desktop, or Ctrl+Alt+Delete.

Instant Support tokens contain 256 random bits and are shown only in the invitation link. PeerOnQ
persists a SHA-256 token hash inside the DPAPI store, not the raw token; optional passwords use the
existing PBKDF2-SHA512 600,000-iteration verifier. Validation binds expiry, revocation, maximum uses,
exact mode/permissions and optional requester-device/technician fingerprint. Attempts are bounded,
hash/password comparisons are constant-time, and malformed fingerprints fail closed. Validation
never replaces consent: the standard permission dialog is mandatory and the use counter advances
atomically only after Accept. Audit contains invitation IDs/result categories, never token, password
or support note.

Only one hosted session owns the shared input sink at a time. A focus transfer first invalidates and
releases the previous route, emits a session-bound focus-loss record and rejects later commands from
the old owner. Video, display and collaboration delivery remain keyed by `SessionId`.

Every live session has a visible window/indicator, tray revoke/end commands, and a global
Ctrl+Alt+Shift+F12 emergency end shortcut when Windows hot-key registration succeeds. The viewer
keeps approved Full Control active while focused and re-requests host acknowledgment when focus
returns. Unattended access is off by default, bound to a
device fingerprint and exact permission ceiling, and visibly indicated when used.

The Linux build is a viewer only. Its capability manifest excludes host/capture/input-injection,
file, clipboard and unattended abilities. Input forwarding is initially off, requires a
Windows-approved `ControlInput` scope plus a fresh focus acknowledgment, and is disabled on focus
loss, session interruption, session end, or remote-owner revocation.

The Android build follows the same least-capability viewer boundary. It accepts only WSS endpoints
ending in `/ws`; an optional embedded development CA remains scoped to that client and hostname
validation stays mandatory. Input starts off, requires the accepted `ControlInput` scope and fresh
host focus acknowledgment, and is disabled on focus/background loss, revocation, interruption, end
or disposal. `FLAG_SECURE` protects the remote Surface from ordinary screenshots/recents capture.
The application has only the Internet permission, disables backup and cleartext traffic, and has no
MediaProjection, AccessibilityService, foreground service, host or unattended capability.

The iPhone/iPad and Mac Catalyst builds advertise the same least-capability viewer boundary under
their distinct protocol platform names. They accept only credential-free WSS endpoints ending in
`/ws`; input starts off and requires accepted `ControlInput` plus fresh host focus acknowledgment.
Resigning activation/backgrounding disables input, releases held state and ends the attended session;
no Apple background mode is declared. The build requires a reviewed architecture-matching static
libvpx archive and approved Apple signing identity. Mac distribution still requires hardened-runtime
Developer ID signing and notarization. The separate iPhone development path accepts only an Apple
Development identity and verifies that its embedded development profile authorizes the requested
physical-device UDID, certificate team and effective bundle identifier. A development-only Personal
Team bundle-ID override never changes the canonical distribution ID; its output is never a
website/App Store artifact. iPhone distribution still requires its distribution profile and App
Store/physical-device gates.

## Data minimization

Security audit and exported diagnostics reject sensitive metadata keys and sanitize device IDs and IP
addresses. Passwords, private keys, temporary secrets, tokens, clipboard contents, file names/paths,
and file contents are forbidden. Crash reporting is disabled by default and records neither exception
messages nor stack traces; see [PRIVACY.md](PRIVACY.md).

## Operational controls

- Signaling applies a 64 KiB frame limit, bounded queues, per-connection rate limits, client/server
  heartbeat deadlines, Windows network-change detection, dead-connection cleanup, ownership
  validation, authenticated resume, peer resume notification, explicit protocol-v3
  compatibility/capability rejection, and graceful readiness draining. Input remains disabled until
  the fresh ICE negotiation completes. Live clients re-register on the same authenticated socket
  before their short-lived connection token expires, fetching a fresh device attestation without
  interrupting media; interruption recovery does the same and fails closed on revocation.
- Clustered Signaling uses a dedicated Redis ACL restricted to `peeronq:{signaling}:*` keys and
  channels. Resume tokens are stored only as SHA-256 hashes and consumed atomically; replay nonces,
  unattended challenges, permission transitions and per-device session limits are atomic across
  replicas. Redis/backplane uncertainty fails readiness and routing closed.
- `security.hybrid-pq-v1` is required on both peers before signaling creates any session. The hybrid
  handshake rejects downgrade, wrong identity, transcript tampering and application data before
  `SECURE`; record epochs reject replay and rekey on volume/time/record bounds and reconnect.
- Collaboration v2 rejects cross-session or stale-permission-generation frames. File writes enforce
  exact monotonic offsets/chunk indexes, re-check destination containment/reparse points, verify
  per-chunk and final SHA-256, scan before same-volume rename, and clean partials on terminal paths.
  File sessions exclude TURN candidates before peer creation; frames are also prohibited on
  TURN/unknown ICE paths, have per-transfer AEAD contexts, no default
  byte ceiling or bandwidth throttle, and use bounded 16 MiB backpressure plus controlled parallel
  workers. Clipboard text remains default-off, bilateral, bounded, and excluded from logs/audit.
- coturn uses quotas, rate controls, limited relay ports, denied unsafe peer ranges in production,
  and read-only/container capability restrictions.
- CI runs tests, analyzers, dependency scans, repository/history secret scans, and CodeQL.
- Release signing values come only from protected CI environment secrets/variables. Official release
  builds fail when update trust metadata is incomplete.
- Portable Support keeps an ephemeral PID-bound profile, disables unattended access and installs no
  service. Its development builder emits an explicit unsigned label and SHA-256 manifests; official
  distribution still requires Authenticode and the normal release trust gates.
- Singleton Cloud maintenance workers acquire a short renewable Redis lease using a random owner
  token. Compare-and-renew/delete prevents a stale replica from extending or releasing another
  replica's lease; owner values are never logged or written to evidence. Lease uncertainty cancels
  mutation work rather than permitting two owners.
- Service drain fails closed: readiness rejects admission and non-probe HTTP work receives a bounded
  retryable `503`. Probe routes bypass public rate/connection limits so overload controls cannot
  falsely report a healthy warm standby as unavailable.

## Customer account and tenant controls

- Customer identity is a separate `PeerOnQCustomer` scheme. Internal Admin principals cannot satisfy
  customer policies and customer roles cannot satisfy Admin policies.
- Passwords use ASP.NET Core Identity's versioned password-hash format. Login, registration,
  refresh, reset, invitation and sensitive mutations are rate-limited; authenticated customer
  partitions are keyed by account rather than shared across tenants.
- Email verification, password reset, invitations and refresh credentials are random, expiring and
  stored only as protected/hash representations. Reset completion revokes active sessions; refresh
  replay revokes the whole token family.
- MFA uses TOTP with a ten-minute protected setup challenge and one-use hashed recovery codes. MFA
  secret ciphertext depends on persistent ASP.NET Data Protection keys. Development may use a local
  mail file sink; non-development startup rejects it and requires configured SMTP.
- Browser credentials are `__Host-` Secure/HttpOnly/SameSite=Strict cookies at `/`. Unsafe requests
  also require the matching non-HttpOnly CSRF value. Access-token validation rechecks the account and
  session database rows, so disable/revoke takes effect without waiting for JWT expiry.
- Every organization read/mutation starts with active membership and tenant scope. Owner transfer is
  explicit; an owner cannot be removed or demoted through a role endpoint. Organization deletion
  fails while other members or claimed devices remain.
- Managed signaling attestations carry signed organization/policy claims. Signaling denies managed
  cross-tenant sessions and disallowed modes/versions/relay regions; missing or unsupported required
  hybrid security fails closed. Accountless unmanaged LAN behavior remains intentionally available.
- Customer security events are append-only and contain action/result, timestamp, correlation ID and
  bounded safe metadata only. Passwords, tokens, MFA material, full device IDs and remote content are
  never audit fields.

Threats covered by affected tests and live API evidence include cross-tenant reads/mutations,
self-role escalation, invitation guessing/replay/expiry/revocation, disabled accounts/sessions,
refresh replay, CSRF, owner-transfer/deletion guards, policy bypass, and shared rate-limit keys.

## Known security gates

Controlled beta is not broad-production approval. The managed Bouncy Castle ML-KEM/ML-DSA APIs are
not the separately certified Bouncy Castle FIPS distribution even though the algorithms implement
FIPS 203/204, and independent cryptographic review remains mandatory. A trusted public signing
certificate, public TLS
deployment, two-device external-network verification, clean-VM installer lifecycle, actual remote
control validation on two physical devices, and an independent penetration test remain release gates
until their evidence is attached to [PHASE5.md](../PHASE5.md).
