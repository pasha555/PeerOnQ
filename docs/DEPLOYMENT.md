# PeerOnQ Phase 6 deployment

## Deployment boundary

`src/PeerOnQ.Infrastructure.Deployment` is the Phase 6 management-plane reference deployment. It
starts the public website, Cloud API, Presence Server, Admin API, Downloads Service, real Admin UI,
the separate customer Account Portal, signed-update artifact origin, PostgreSQL, Redis,
the existing Phase 3 signaling and coturn implementations, and an
OpenTelemetry/Prometheus/Alertmanager/Loki/Tempo/Grafana stack. The data-plane code is reused rather
than forked: this deployment adds common TLS routing, health scraping, redacted log forwarding, and
central metrics around the existing services.

The Compose files are suitable for development and a single-node staging environment. Production
must use externally managed secrets, encrypted backups, highly available PostgreSQL, and highly
available Redis before carrying public traffic. Compose is not presented as a multi-region
orchestrator.

## Portable Support development package

The no-install support package reuses the native desktop codebase and public pilot endpoints:

```powershell
.\scripts\windows\build-phase11-portable-support.ps1 `
  -Architectures x64,arm64 `
  -IUnderstandThisIsNotProductionSigned
```

The script publishes a self-contained app, writes `PeerOnQ.PortableSupport.json`, a usage/readiness
notice and per-file SHA-256 manifest, then emits one checksum-protected ZIP per architecture. Running
`PeerOnQ.exe` directly activates portable mode: no MSI, service or startup entry; unattended access
is disabled and local state is ephemeral. These packages are development/unsigned. Never publish
them as official support media until Authenticode, clean-VM lifecycle and the release trust pipeline
pass. The installed MSI separately registers `peeronq://support` and still requires explicit remote
Accept; URI activation never auto-connects.

## Services and trust boundaries

| Service | Internal port | Public route | Required state |
| --- | ---: | --- | --- |
| Reverse proxy | 443 | All management-plane public hosts | TLS certificate/private key |
| Public website | 8080 | `peeronq.com` and `www.peeronq.com` | Static files only |
| Cloud API | 8080 | `api*.peeronq.*` | PostgreSQL, Redis, diagnostic object service |
| Account Portal | 8080 | `portal*.peeronq.*` | Static SPA; same-origin `/portal/v1/*` proxies to Cloud API |
| Presence Server | 8080 | `presence*.peeronq.*` at `/presence/v1/hub` | Redis lease state, PostgreSQL summaries |
| Admin API | 8080 | same-origin `/admin/v1/*` under admin host | PostgreSQL, Redis, Data Protection keys |
| Admin UI | 8080 | `admin*.peeronq.*` | Static files only; no embedded credentials |
| Signed update origin | static | `updates*.peeronq.*` | Admin-writable, proxy-read-only release volume |
| Downloads Service | 8080 | `download*.peeronq.*` | PostgreSQL and read-only signed release store |
| Signaling Server | 8080 | `signal*.peeronq.*` at `/ws` | Redis cluster state, attestation public key, durable device pins and coturn configuration |
| Signaling metrics proxy | 8081 | Never public | Read-only loopback bridge to signaling `/metrics` |
| coturn | 3478, 5349, 9641 | TURN/STUN listeners; metrics never public | REST secret, realm, relay ports, TLS certificate in staging/production |
| PostgreSQL | 5432 | Never public | Durable Phase 6 records/audit |
| Redis | 6379 | Never public | Challenges, leases, revocation/rate-limit coordination |
| OTel Collector | 4317/4318 | Never public | Telemetry routing |
| Prometheus / Loki / Tempo | 9090 / 3100 / 3200 | Direct ports stay loopback/internal; Prometheus has a CIDR-restricted HTTPS host | Metrics/logs/traces under retention |
| Grafana | 3000 | Direct port stays loopback; CIDR-restricted HTTPS host | Administrator secret, provisioned data sources |
| Blackbox exporter | 9115 | Never public | Internal TCP health probes only |

Redis is justified for short-lived, cross-instance coordination and shared Admin Data Protection
keys. It is not the source of truth for devices, installations, releases, audit, or historical
analytics, but loss of the Data Protection key set makes stored administrator MFA secrets
undecryptable; production Redis persistence and backup are therefore mandatory.

## DNS and certificates

Create environment-specific records before issuing certificates:

| Environment | Web | API | Account portal | Admin | Download | Updates | Presence | Signaling | TURN |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Development | `dev.localhost` | `api.dev.localhost` | `portal.dev.localhost` | `admin.dev.localhost` | `download.dev.localhost` | `updates.dev.localhost` | `presence.dev.localhost` | `signal.dev.localhost` | `turn.dev.localhost` |
| Staging | `staging.peeronq.com` | `api-staging.peeronq.com` | `portal-staging.peeronq.com` | `admin-staging.peeronq.com` | `download-staging.peeronq.com` | `updates-staging.peeronq.com` | `presence-staging.peeronq.com` | `signal-staging.peeronq.com` | `turn-staging.peeronq.com` |
| Production | `peeronq.com` | `api.peeronq.com` | `portal.peeronq.com` | `admin.peeronq.com` | `download.peeronq.com` | `updates.peeronq.com` | `presence.peeronq.com` | `signal.peeronq.com` | `turn.peeronq.com` |

The `api` host serves the client Cloud API. The `portal` host serves the customer SPA and proxies
only `/portal/v1/*` to Cloud, preserving same-origin customer cookies. The `admin` host serves both
the Admin SPA and its same-origin `/admin/v1` API through the management proxy; do not point the
Admin SPA at the Cloud API host. Publish the client-facing names to the TLS ingress only after it
accepts TCP 443 and presents a certificate covering them. `peeronq.com`, `www`, `portal`, `api`,
`download`, `updates`, `presence`, `signal`, and the required TURN endpoints are public user/client
surfaces. Keep only `admin`, `grafana`, and `prometheus` access private with split DNS and the
shared proxy source-CIDR allowlist. Production internal DNS should map these operator names to
the server LAN address:

| Internal name | LAN target | Public HTTPS behavior |
| --- | --- | --- |
| `admin.peeronq.com` | PeerOnQ server LAN IP | `403` outside the configured internal CIDR |
| `grafana.peeronq.com` | PeerOnQ server LAN IP | `403` outside the configured internal CIDR; Grafana login still required |
| `prometheus.peeronq.com` | PeerOnQ server LAN IP | `403` outside the configured internal CIDR; proxied access is read-only |

A DNS record by itself does not deploy or expose a service.

**DNS operator requirement:** publish `portal.peeronq.com` as an A record to the production public
TLS ingress IPv4 address, or a CNAME to a hostname on that ingress. Do not leave it as a LAN-only
record. Publish an AAAA record only when the same ingress is reachable and verified over IPv6.
Set `PEERONQ_PORTAL_HOST=portal.peeronq.com`; route public TCP 443 to Nginx and deploy the current
configuration. These source changes do not update public DNS, issue a certificate, or deploy a server.

Public "Portal" and "Sign in" links must target `https://portal.peeronq.com` without a port.
Release 0.9.71 overrides the development `web-ui` build arguments in staging/production, including
the download host. Previously, Compose inherited `:8443` even though the proxy published 443;
the page could load directly on HTTPS while its navigation links timed out. Rebuild `web-ui` through
the server installer to replace the compiled URLs; changing only its runtime environment is not
enough. Do not open public 8443 or publish the portal container port as a workaround. Development
retains `https://portal.dev.localhost:8443`. From a source checkout, run
`node scripts/test-peeronq-compose-contract.mjs` to validate the real merged Compose models with
synthetic configuration and no container startup.

Customer requests stay at `https://portal.peeronq.com/portal/v1/*`. The existing
`__Host-peeronq_customer_access`, `__Host-peeronq_customer_refresh`, and `__Host-peeronq_customer_csrf`
cookies remain Secure, SameSite=Strict, Path=/ and host-only; access/refresh remain HttpOnly.
The public website only navigates to the portal and neither reads nor shares those cookies.
Customer/Admin identities, CSRF validation and rate limits remain separate and unchanged. No parent
domain cookie, broad CORS rule or direct application port is needed. Public ingress does not change
`Closed`, `InvitationOnly` or `Open` registration policy: existing accounts can sign in while the
configured registration/mail/startup validation still applies.

Bootstrap HTTP-01 requests `portal.$BASE_DOMAIN` in its SAN list; certificate import and renewal
validate `PEERONQ_PORTAL_HOST` with every other configured name. An imported certificate must cover
`portal.peeronq.com` explicitly or through the valid `*.peeronq.com` wildcard. Do not bypass TLS
verification. After deployment, test from a separate Internet connection outside the operator CIDR:

```bash
dig +short portal.peeronq.com
curl --fail --show-error --head https://portal.peeronq.com/
curl --silent --show-error --output /dev/null --write-out '%{http_code}\n' \
  https://portal.peeronq.com/portal/v1/account/profile
```

Verify the DNS answer belongs to the intended ingress, certificate trust/name/expiry pass, the SPA
returns 200, and the unauthenticated profile API returns 401. Then test login/logout, the configured
registration mode, CSRF rejection and MFA with a controlled customer account. Confirm Admin,
Grafana and Prometheus still return 403 from that external connection. Local fixture tests are not
proof of public DNS, certificate issuance, Internet reachability or a real account login.

Production also needs `www.peeronq.com`. Diagnostics use the authenticated Cloud API at
`api.peeronq.com`; signed artifacts use `updates.peeronq.com`. Use short DNS TTLs during first
rollout, then raise them after failover has been exercised.

Official Windows releases accept only public-style DNS names for every compiled HTTPS/WSS service
endpoint. `build-phase5-release.ps1` rejects IP literals, single-label names, localhost/private
development suffixes, `sslip.io`, and `nip.io`; the unsigned LAN development builder remains the
separate path for physical-laptop testing. The desktop Settings page never exposes these deployment
addresses, while the client continues to resolve and use them internally from signed release metadata.

The proxy accepts TLS 1.2/1.3 only, disables session tickets, sends HSTS, and never offers an insecure
application fallback. Mount the certificate chain as `/certs/fullchain.pem` and inject the private key
from a file outside the repository. Development certificates must include every development hostname
in Subject Alternative Name and must be trusted only on development devices. Never add a
certificate-ignore switch to a client or browser build.

## Firewall

- Forward public TCP 443 to the management-plane proxy. The website, customer Account Portal, Cloud API, Presence,
  Signaling WebSocket, downloads, and updates share this listener through TLS SNI/HTTP host routing.
- Forward public TCP 80 to the same host when using the bundled Let's Encrypt HTTP-01 bootstrap and
  renewal flow. The production Compose stack does not publish this port continuously; standalone
  Certbot binds it only during certificate issuance/renewal, so it is not an application route.
- Keep PostgreSQL 5432, Redis 6379, OTel 4317/4318, Prometheus 9090, Loki 3100, Tempo 3200, Grafana
  3000, and all application port 8080 listeners on internal networks.
- Keep direct Grafana/Prometheus ports on loopback; use their authenticated/CIDR-restricted HTTPS
  virtual hosts from the internal network.
- Allow signaling through the proxy on TCP 443. Allow coturn UDP/TCP 3478, TLS/DTLS 5349, and the
  configured TURN UDP relay range (currently 49160-49200 in the single-node reference). Keep coturn
  metrics TCP 9641 internal.
- Set `PEERONQ_TURN_EXTERNAL_IP` to the public IPv4 address only for the single-node Docker
  deployment. Coturn's relay interface is inside its Docker bridge, so the host LAN address must not
  be supplied as the private side of a `public-ip/private-ip` mapping. The installer migrates that
  legacy form while retaining the LAN-derived `PEERONQ_ADMIN_ALLOWED_CIDR` separately.
- For the reference server at `10.20.10.46`, the router mappings are TCP 80 -> 80, TCP 443 -> 443,
  UDP/TCP 3478 -> 3478, UDP/TCP 5349 -> 5349, and UDP 49160-49200 -> the identical internal range.
  A single public TCP 443 NAT rule cannot simultaneously terminate HTTPS in Nginx and TURN-TLS in
  coturn. More importantly, a TURN allocation still needs reachable relay ports. Do not advertise
  an all-on-443 deployment unless a separately designed and tested L4 multiplexer plus compatible
  relay architecture is in place.
- A router rule label such as `peeronq.com` is descriptive unless the firewall explicitly provides
  an application-aware host/SNI filter. Ordinary NAT matches the public IP, protocol and port, so
  every HTTPS hostname on TCP 443 reaches Nginx. Nginx routes only the configured virtual hosts and
  rejects `admin`, `grafana`, and `prometheus` requests whose direct source is outside
  `PEERONQ_ADMIN_ALLOWED_CIDR`.
- Unknown HTTP Host headers are closed without an application response; unknown TLS SNI names
  are rejected during the handshake. Public web/portal/API/presence/download hosts reject `/metrics`
  (including trailing paths and case variants); signaling/updates allow no such route. Internal
  service metrics remain available only on the isolated service/observability networks.
- Website-only exposure is not a functional remote-access deployment. Native clients require the
  public API, Presence, Signaling, Downloads/Updates and TURN endpoints; customer accounts need
  the public Account Portal. Admin, Grafana, Prometheus, PostgreSQL, Redis and the remaining direct
  observability/application listeners stay private.
- A timeout when the Ubuntu host itself curls `https://*.peeronq.com` does not prove that the public
  NAT rule is closed: many routers do not support NAT hairpin/loopback. Test public DNS/TLS from a
  separate Internet connection (for example a phone hotspot). On the server, use the installer's
  loopback `--resolve` probes or the direct container health checks documented below.
- Limit database and Redis access by workload identity/security group and the per-service roles/ACLs
  below, not only by network reachability or one shared password.

The pilot edge and embedded web origin use automatic workers, an 8,192-connection ceiling per
worker, kernel `sendfile`, and unbuffered MSI proxying. This removes the previous temp-file failure
and permits controlled high-concurrency load testing, but it does not create bandwidth or high
availability: 1,000 downloads of the current 78.4 MB pilot consume about 78.4 GB at the origin.
Before a broad release, put the versioned artifact in object storage behind a CDN, keep the origin
private, load-test the real uplink, and retain the SHA-256/Authenticode publication checks.

## Configuration and secrets

Copy `.env.example` to a file outside source control and populate it from the target environment.
The `.gitignore` rejects `.env`, `secrets/`, `certificates/`, backups, and the real Alertmanager
configuration. Required secret classes are:

- independent PostgreSQL runtime-role and Redis ACL-user credentials per service, plus isolated
  migration, bootstrap, backup, restore, and health credentials;
- a dedicated `peeronq_signaling` Redis credential restricted to `peeronq:{signaling}:*` keys and
  channels; each Signaling process also needs a unique cluster instance ID;
- three independent Base64 HMAC keys: Cloud public-ID lookup, Admin privacy/audit hashing, and
  Downloads uniqueness hashing;
- administrator JWT signing and refresh-token hash keys (separate keys);
- a separate customer JWT signing key and persistent customer Data Protection directory;
- customer registration mode, verification policy, portal HTTPS origin, mail provider, and optional SMTP credentials;
- download completion-token key and exact artifact-origin allowlist;
- diagnostic storage bearer credential;
- proxy and TURN TLS private keys;
- Cloud-only signaling-attestation private PEM and Signaling-only public PEM;
- coturn REST shared secret;
- Grafana administrator credential;
- separate PostgreSQL `.pgpass` files for the read-only backup role and CREATEDB-only isolated
  restore-test role;
- Admin Data Protection PFX and its separate password file in staging/production; and
- Alertmanager-to-Admin-API webhook token file plus notification receiver credentials in the
  externally mounted staging configuration.

Generate independent random values per environment. Do not reuse TURN, update signing, admin token,
refresh hashing, any of the three HMAC purposes, download completion, database, or Redis secrets.
Store release-signing private keys offline or in an HSM/KMS;
they are not injected into the Admin API. The platform must reject unsigned release publication.

Customer registration is `Closed`, `InvitationOnly`, or `Open` under the self-hosted operator's
configuration; it is never an activation or licensing switch. Production uses the `Smtp` provider
with an approved sender and TLS policy. `FileSink` writes development messages only to the dedicated
customer-mail volume and is rejected at non-development startup. Customer Data Protection keys must
be on the dedicated persistent volume, backed up and restored with the database; replacing them
invalidates MFA ciphertext and in-flight protected setup material. Protect the production volume
with host/storage encryption and access limited to Cloud API UID 1654.

For public onboarding in 0.9.73, use `sudoedit /etc/peeronq/peeronq.env` and provide the real,
operator-approved mail values. Keep this file root-protected; do not paste secrets into commands:

```dotenv
PEERONQ_CUSTOMER_REGISTRATION_MODE=Open
PEERONQ_CUSTOMER_REQUIRE_EMAIL_VERIFICATION=true
PEERONQ_CUSTOMER_MFA_ENABLED=false
PEERONQ_CUSTOMER_MAIL_PROVIDER=Smtp
PEERONQ_CUSTOMER_SMTP_HOST=<real-smtp-host>
PEERONQ_CUSTOMER_SMTP_PORT=587
PEERONQ_CUSTOMER_SMTP_USERNAME=<operator-value>
PEERONQ_CUSTOMER_SMTP_PASSWORD=<operator-secret>
PEERONQ_CUSTOMER_MAIL_FROM_ADDRESS=<approved-sender-address>
```

The sender defaults to `peeronq@<PEERONQ_WEB_HOST>`; configure an address the SMTP provider
allows. Production Compose retains SMTP TLS. Portal/email links use `https://portal.peeronq.com`
and same-origin `/portal/v1/*`; the public website needs no portal cookies. Admin MFA, operator
CIDRs, private ports and accountless LAN access are unchanged. Configure SMTP before choosing
Open/InvitationOnly; there is no production FileSink fallback or guessed mail service.

Installer/bootstrap options: `--customer-registration-mode Closed|InvitationOnly|Open`,
`--enable-customer-mfa`, `--disable-customer-mfa`. Upgrades preserve each valid existing
registration mode unless explicitly overridden; unknown/empty configured modes fail. A missing
mode defaults to Closed. Customer MFA defaults false when missing, preserving valid existing values
unless explicitly overridden. Disabling mail with Open/InvitationOnly fails unless Closed is
explicitly selected. Stored customer MFA/recovery/organization policy data is preserved while off.

After copying the exact **0.9.73** bundle into `/home/peeronq`, first verify its release-specific
SHA-256 sidecar (or the handoff hash if only `.run` was copied), then run in order:

```bash
cd /home/peeronq
chmod 700 peeronq-server-0.9.73.run
sudo ./peeronq-server-0.9.73.run --env-file /etc/peeronq/peeronq.env --customer-registration-mode Open --disable-customer-mfa --dry-run
sudo ./peeronq-server-0.9.73.run --env-file /etc/peeronq/peeronq.env --customer-registration-mode Open --disable-customer-mfa
sudo ./peeronq-server-0.9.73.run --status --env-file /etc/peeronq/peeronq.env
```

Continue only if the preceding command succeeds. This requires configured SMTP; the installer does
not provision an SMTP account. Checksums prove byte integrity, not authenticity; unsigned-pilot
release gates remain. Rollback: `--rollback --env-file /etc/peeronq/peeronq.env`; separately restore
prior registration/MFA/mail settings from the protected backup when reverting policy. Preserve data.

### SMTP connectivity and generic recovery responses (0.9.75)

Cloud API in staging/production joins a dedicated `customer-mail-egress` bridge in addition to the
internal control and observability networks. Earlier releases attached it only to internal networks,
which prevented reaching external SMTP even when `/auth/capabilities` advertised enabled mail.
Only Cloud API joins the added bridge; database/cache/metrics remain isolated and no application
port is published. The bridge provides outbound routing; it is not a destination/port allowlist.
Apply any required egress restrictions at the host/firewall for the configured SMTP provider.
Development FileSink networking remains unchanged.

The operator uses an existing Resend account. Preserve its protected SMTP/API key and verified
sender configuration during upgrades; do not pass `--disable-customer-mail`. Resend SMTP uses
`smtp.resend.com:587` with STARTTLS, username `resend` and the sending API key as its password.
Never print that key, include it in command arguments or commit it. SMTP/verification settings and
the current valid registration mode survive a normal installer upgrade without mail-policy flags.

The generic recovery success page does not prove SMTP delivery. Missing/inactive accounts, a
one-minute per-account token cooldown and SMTP failure can all return the same accepted response
to preserve enumeration protection. SMTP failure records `account.mail_delivery_failed` in the
customer security audit. Check Resend Emails and the approved recipient's mailbox after one request;
wait at least a minute before retrying. An SMTP 220 banner proves connectivity only, not key/sender
authorization or inbox delivery. See [Resend SMTP](https://resend.com/docs/send-with-smtp).

Local acceptance: `scripts/windows/test-phase7-customer-portal.ps1` uses trusted
`https://localhost:8443` and the portal virtual host. It modifies only the local development stack,
creates unique test accounts, ages only their tokens for expiry/cooldown tests, cycles registration
and MFA policies, restores the original MFA value, and tests real rate limits without weakening them.
It uses Development FileSink, not SMTP. Production verification/reset delivery needs an approved
mailbox and an actual SMTP test; until executed, record `LIVE EMAIL TEST = BLOCKED`.

DNS operator requirement: `portal.peeronq.com` must resolve to the production HTTPS ingress, with
valid TLS and reachable TCP 443. Source code does not change DNS. The URL must not contain `:8443`;
Admin/Grafana/Prometheus remain operator-only.


The Admin Releases page accepts only the Authenticode-signed MSI and detached ECDSA manifest produced
by the offline Phase 5 release scripts. The API verifies the trusted manifest key, exact update origin,
version/channel/architecture, validity window, size, and MSI SHA-256 before atomically publishing to
the release volume. A rollout change is a newly offline-signed manifest, never a mutable database-only
percentage. The proxy mounts this volume read-only and exposes only canonical stable/beta manifest and
versioned MSI paths. Set `PEERONQ_RELEASE_ARTIFACT_HOST` and `PEERONQ_UPDATE_HOST` to the same exact
host. Keep `PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE=false` until at least one signed release has passed
the clean-VM and canary gates.

Cloud-to-signaling device enrollment uses a separate ECDSA P-256 attestation key pair. Mount only the
PKCS#8 `BEGIN PRIVATE KEY` PEM in Cloud and only the SPKI `BEGIN PUBLIC KEY` PEM in Signaling. Set one
exact HTTPS issuer and audience on both services; production, staging, and the reference development
stack require attestation and disable TOFU fallback. The normal lifetime is five minutes, Signaling
accepts at most fifteen minutes, and clock skew is thirty seconds. Keep old and new public keys
concurrently during a bounded rotation overlap, but never mount the private key in Signaling. The
standalone Phase 3 development flow may use TOFU only with both explicit Development environment and
`Required=false`/`AllowDevelopmentTofuFallback=true`; this exception is forbidden in staging/production.
The secret provider must expose each PEM read-only to the service's effective UID (1654 in the
reference .NET images). A file that exists but is owned by another UID with mode `0600` is deliberately
rejected at startup. With production Docker/Swarm or Kubernetes secrets, set the target UID/GID and
mode `0400` or `0440`. Local Compose file-backed secrets retain host ownership and mode; the PeerOnQ
bootstrap and installer therefore keep the parent directory root-only while assigning each mounted
file to only its consuming runtime group. Never make these files world-readable or solve this by
running a service as root.

Enrollment is proof-first: Cloud issues a one-use challenge for the presented public key, verifies
the signature, atomically creates the proof-bound device/installation, assigns the 12-digit routing
alias, and returns the device token plus signaling attestation. Only then may the authenticated
installation-confirm endpoint update installation metadata. A previously enrolled device may survive
a brief Cloud interruption only while its already-issued credentials remain valid; Cloud outage does
not authorize a fresh enrollment, a new alias, or an unsigned/expired signaling assertion.

Production endpoint selection is fixed by release configuration. Operators may set
`VITE_PEERONQ_ADMIN_API_BASE_URL` at Admin UI build time, but the normal UI contains no endpoint
selector. The default Admin UI uses its same-origin `/admin/v1` reverse-proxy route.

Development can query the internal HTTP Prometheus service. Staging and production set
`PEERONQ_ADMIN_PROMETHEUS_ENDPOINT` to an internal **HTTPS** query endpoint and
`PEERONQ_ADMIN_PROMETHEUS_ALLOWED_HOST` to its exact host. The Admin API intentionally returns an
unavailable metric state when that endpoint is absent; never disable certificate validation to make
the dashboard green.

The allowlisted HTTPS release origin must return a strong (non-weak) `ETag`, honor `If-Match`, and
keep the artifact immutable for its versioned URL. Downloads fail closed if size/SHA-256 verification
or this origin contract fails. Origin bytes are first written to the dedicated
`/var/lib/peeronq/download-cache` volume and are never released until exact size and SHA-256
verification succeeds. Only then can a single client byte range be served from the verified immutable
entry; invalid or multipart ranges return 416. Size the volume above `PEERONQ_DOWNLOAD_CACHE_MAX_BYTES`
plus filesystem overhead, keep it writable only by the Downloads container UID 1654, and never map it
to `/tmp` or a shared application volume. The reference proxy streams without response buffering or
temp-file staging, limits each source IP to four concurrent download connections, and applies bounded
inter-read/inter-send timeouts. Production must put a capacity-planned CDN/WAF in front of the
download host; the application concurrency gate is not a substitute for edge slow-client and
volumetric-abuse protection.

## Development startup

1. Install Docker Desktop with Linux containers and Compose.
2. On Windows, let `peeronq-start.bat` create and trust the repository-scoped local CA and
   `*.dev.localhost` leaf under ignored `.peeronq-phase6/`. Other development hosts must provide
   equivalent locally trusted material without committing its private key.
3. Create two owner-only `.pgpass` files. Backup uses
   `postgres:5432:peeronq_cloud:peeronq_backup:<generated-password>`. Restore proof uses
   `postgres:5432:*:peeronq_restore_operator:<generated-password>` because it connects to a newly
   generated isolated database. Neither credential is an application runtime or schema-owner role.
4. Create a writable backup directory and separate owner-only files for the proxy private key,
   Alertmanager webhook token, Grafana administrator password, and Cloud/signaling attestation private
   and public PEMs outside the repository.
5. Populate all required `.env` fields. The controller creates an ignored random customer JWT key;
   development uses the explicit file mail sink. Development administrator bootstrap requires an email, a
   unique password of at least 14 characters, a Base32 TOTP secret decoding to at least 20 bytes, and
   eight independent recovery codes of at least 12 normalized characters. Remove these values after
   the first owner exists.
6. On Windows, run `.\peeronq-start.bat`. It validates configuration, starts the state stores,
   applies migrations and least-privilege runtime grants, rebuilds the services, waits for the real
   Admin console and Account Portal, and opens the Admin UI together with the offline preview. The database remains on the private
   control network; do not publish PostgreSQL merely to run migrations. The equivalent manual flow is:

```powershell
docker compose --env-file <path-to-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml config --quiet

docker compose --env-file <path-to-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  up -d postgres redis

docker compose --env-file <path-to-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  run --rm --build migrations

docker compose --env-file <path-to-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  run --rm database-permissions

docker compose --env-file <path-to-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml up -d --build
```

The reserved `.localhost` names resolve to loopback without a hosts-file edit. Open
`https://admin.dev.localhost:8443`, `https://portal.dev.localhost:8443`, and
`https://grafana.dev.localhost:8443`; the generated CurrentUser-trusted certificate matches every
`*.dev.localhost` management host. Prometheus and Grafana direct ports bind to loopback only. A container is not
ready merely because it is running; verify the health endpoints below or run `peeronq-status.bat`.

`peeronq-phase6-dev.ps1` creates stable random Grafana and Alertmanager credentials plus separate
backup/restore `.pgpass` files below the gitignored `.peeronq-phase6` state directory. It never puts
these values in Compose arguments or source-controlled files. On an existing Grafana volume the
controller synchronizes the generated administrator password through binary standard input; it does
not expose the password in the process command line. PostgreSQL archives and isolated restore-test
state also remain below this ignored directory unless the operator explicitly configures another
protected destination.

After the full stack is healthy, run the bounded proof-first acceptance client. It creates an
ephemeral device key, confirms installation and Presence, performs the attested Signaling WebSocket
registration, and proves missing, different-key, wrong-audience, and expired attestations are
rejected. The private-key path is read only to create deliberately invalid signed test cases and is
never printed; run this only in the isolated development/staging acceptance environment:

```powershell
$env:PEERONQ_ACCEPTANCE_ATTESTATION_PRIVATE_KEY_FILE = '<cloud-attestation-private-pem>'
$env:PEERONQ_ACCEPTANCE_ALLOW_UNTRUSTED_DEVELOPMENT_CERTIFICATE = 'true'
dotnet run --project `
  src/PeerOnQ.Infrastructure.Deployment/acceptance/PeerOnQ.Phase6.Acceptance.csproj `
  --configuration Release
```

The certificate bypass refuses non-loopback or non-`.localhost` hosts. Omit it in staging, where
the certificate chain must validate normally.

Run the real Phase 7 customer acceptance after the portal and Cloud API report ready. It creates
unique development-only accounts in two organizations, verifies mail, persists session/Data
Protection state through a Cloud restart, checks CSRF and cross-tenant denial, exercises invitation
replay, roles, teams, policy, ownership, audit, and refresh replay, and leaves its audit rows for
inspection:

```powershell
.\scripts\windows\test-phase7-customer-portal.ps1
```

The development TURN listeners also bind to `127.0.0.1` by default. For a deliberate two-device LAN
test, give the Phase 3 controller its reserved LAN bind address together with DNS names, for example
`-SignalHost signal.peeronq.com -TurnHost turn.peeronq.com`, rather than changing the Phase 6
management stack. The controller creates TLS material for those names and opens only the
signaling/TURN ports on the Windows `Private` profile and `LocalSubnet` when explicitly run as
Administrator with `-ConfigureFirewall`. `build-phase5-development.ps1` must compile the canonical
`wss://signal.peeronq.com:5443/ws` endpoint and the matching development root into an isolated
unsigned MSI kit; it never replaces the website's generic downloads. Do not use `0.0.0.0`, a router
public address, or port forwarding on a development host. The Phase 6 development stack exposes
loopback TURN TLS on 5349 with its generated certificate to prove configuration and handshake
readiness; the Phase 3 controller remains the source for physical-LAN TLS/DTLS relay allocation
acceptance. The complete laptop workflow is documented in the repository README.

## Staging rollout

The staging file extends the development topology, removes development bootstrap defaults and every
MFA-bypass behavior, but permits an explicit audited one-time bootstrap. It requires public
hostnames, region, proxy and TURN TLS material, secrets, an HTTPS Prometheus query
endpoint for Admin Infrastructure metrics, and an external Alertmanager notification configuration.
That Alertmanager configuration must retain the authenticated Admin API webhook receiver from the
development example, use the mounted token file, and add the approved paging receiver.

1. Back up the database and run the restore test.
2. Verify migration SQL in a disposable copy of the current schema.
3. Run `docker compose ... config --quiet` with the staging environment.
4. Apply forward-compatible migrations before new application instances receive traffic by running
   `docker compose ... -f docker-compose.staging.yml run --rm --build migrations`, followed by
   `docker compose ... -f docker-compose.staging.yml run --rm database-permissions`. Both jobs must
   exit zero; the second job revokes stale grants, applies the reviewed per-service matrix, and fails
   on unsafe ownership/role attributes.
5. Start stateful dependencies, telemetry, APIs, Presence, Downloads, signaling, coturn, Admin UI, public website,
   then proxy.
6. Require `/health/startup` success, `/health/live` success, and `/health/ready` success before
   attaching a service to load-balancer traffic.
7. Run device registration/authentication, presence lease expiry, authenticated Admin UI, tracked
   download, metric, alert, and diagnostic-consent smoke tests.
8. Disable any one-time owner bootstrap input immediately after first-owner creation and MFA
   enrollment; redeploy and verify it cannot run again.

Production uses `peeronq_migrator` as the schema/table owner and separate Cloud, Presence, Admin,
and Downloads runtime roles. The pre-provisioned `peeronq_retention_executor` is a NOLOGIN,
non-superuser function-owner role; only the migrator is its member so the forward migration can
transfer the two governed-retention functions to it. Presence can read only device/installation revocation state and
write presence history. Downloads can read releases and create/update download events. Admin can
read its reporting set and mutate only explicit administration/release/diagnostic-access/audit/alert
tables. Cloud owns no objects and receives only its operational table grants plus `EXECUTE` on the
reviewed governed-retention functions. Those functions are `SECURITY DEFINER`, pin
`search_path=pg_catalog,public`, and expose only their exact signatures to Cloud. Backup is read-only;
restore-test is CREATEDB-only and has no
production-table grants. No runtime role may own schema objects, bypass triggers/RLS, inherit the
migration role, hold `BYPASSRLS`, or be a PostgreSQL superuser. The Compose bootstrap superuser is a
one-time local provisioner and is never injected into an application container.

The permissions controller first revokes both retention function grants, then grants each exact
signature only when that data class is enabled and not under legal hold. Re-run the one-shot
`database-permissions` service before starting Cloud after every retention enable/disable or legal-hold
change; audit retention remains disabled by default, while alert retention is enabled by default.
The controller validates deployment values, connects as the one-shot migrator (never a runtime or the
bootstrap superuser), and atomically synchronizes the two database-owned `RetentionPolicies` rows with
the corresponding execute grants. Runtime roles cannot read or mutate this table.

The four .NET runtime images and the one-shot migration image install the minimal
`libgssapi-krb5-2` runtime package required by the PostgreSQL client on the selected Debian base, then
remove package indexes. No compiler, SDK, or package manager cache is copied into final images.

Redis disables the default user. Cloud, Presence, Admin, Downloads, and health checks authenticate
as different ACL users. Key patterns confine Cloud to the Cloud keyspace and Presence to presence
keys plus its required pub/sub channels. A separate Presence token-reader identity receives only
`GET` plus the connection-handshake `PING`/`ECHO` commands on the device-token prefix; it cannot
issue, revoke, enumerate, or mutate tokens. Admin
may write only Admin keys and read presence keys, while Downloads is limited to connection health.
Production managed Redis must reproduce and acceptance-test these ACL boundaries.

Staging/production Admin API startup fails closed unless its Data Protection keys are protected with
the mounted PFX and separate password file. Development may use the explicit development-only
unprotected-key option; never carry that option into staging or production.

Only the reverse proxy at the fixed control-network address `172.29.60.10` is trusted to supply
forwarded client IP/scheme headers. Do not widen this to a subnet or trust all proxies: rate-limit
partitioning and Presence query-token HTTPS enforcement depend on the exact hop.

```powershell
docker compose --env-file <staging-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml config --quiet
docker compose --env-file <staging-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml run --rm --build migrations
docker compose --env-file <staging-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml run --rm database-permissions
docker compose --env-file <staging-env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml up -d --build
```

## Single-node production bundle

The public website, Admin and Portal Docker builds use digest-pinned Node 24.21.0 images.
The frozen dependencies require Node >=24.15.0 and <25 on the supported line. Updating Node on
the Linux host does not change a Dockerfile's build image. Keep `engineStrict` and the package
release-age policy enabled; GitHub Quality builds all three web Dockerfiles to catch drift.

A requested product patch includes the full versioned server bundle, unless explicitly scoped to
a website-only or client-only deliverable. Its operator release notes live at
`src/PeerOnQ.Infrastructure.Deployment/RELEASE_NOTES.md` and are included by the existing source
payload selection. Provide the same notes alongside the bundle. They must identify the server
version, embedded client version/classification, client changes (or no changes), validation,
remaining release blockers and rollback. Client installation/update remains a separate device action.

Server patches and clients now share a single product release version, sourced from
`Directory.Build.props` / `PeerOnQWindowsClientVersion`. For each new versioned release, increment
that value once and advance the server and derived client versions together, even if only server,
website or portal behavior changed. Build and validate matching Windows x64/ARM64 packages and
embed the validated x64 MSI in a server bundle with the same version. Release notes must distinguish
client behavior changes from a version-only rebuild. Reject mismatched versions before publication;
the server builder checks its `-Version` against the canonical version before any payload staging.
It also verifies the MSI's internal `ProductVersion`, so an older MSI with a renamed filename fails.
Do not rename an older artifact to make it appear current. Historical packages retain their original
versions. An explicitly scoped website-only patch remains version-neutral. This policy does not
waive signing, physical-device or other release gates, or require a version bump for each source commit.

`scripts/windows/build-peeronq-server-run.ps1` creates a self-extracting Linux server deployment
bundle and SHA-256 file. This `.run` installs the cloud, website, Admin, download, signaling, TURN,
and observability stack; it is not a Linux desktop client. Every server bundle must explicitly embed
one verified x64 Windows MSI so a healthy public Downloads route can never be deployed without its
client. Authenticode verification is required by default; `-IncludeUnsignedWindowsPilot` is accepted
only with an actually unsigned MSI for an authorized controlled pilot. For a production artifact,
pass an approved offline GPG key and `-RequireSignature`, publish the `.run`, `.sha256`, and `.asc`
together, and verify the detached signature on the Linux host before invoking `sudo`. The builder
stages all three files on the output filesystem, publishes the signature and checksum first, and
makes the `.run` visible last as the atomic commit marker. A failed signed build removes the entire
trio.

```powershell
$ReleaseVersion = ([xml](Get-Content ./Directory.Build.props -Raw)).Project.PropertyGroup.PeerOnQWindowsClientVersion
pwsh ./scripts/windows/build-peeronq-server-run.ps1 `
  -Version $ReleaseVersion `
  -OutputDirectory ./dist/server `
  -WindowsClientMsiPath "./dist/release/$ReleaseVersion/PeerOnQ-$ReleaseVersion-x64.msi" `
  -GpgKeyId <offline-release-key-id> `
  -RequireSignature
```

For the first single-node public pilot, create the client-facing DNS records in the production table,
reserve the server LAN address, and forward TCP 443 plus the documented TURN ports. Docker Engine and Compose v2 must already be
installed. The
bootstrap path creates independent random secrets, Data Protection and signaling keys, a pilot update
signing key, a separate customer JWT key, a root-only Admin MFA onboarding record, and the production
environment file. Customer mail starts safely disabled, with closed registration and no email
verification dependency. Bootstrap detects the LAN address or accepts `--local-ip`, then configures coturn with the required
`public-IP/private-IP` NAT mapping. No credential or private key is printed.

```bash
release_version='<same canonical version used to build the release>'
gpg --verify "peeronq-server-${release_version}.run.asc" "peeronq-server-${release_version}.run"
sha256sum -c "peeronq-server-${release_version}.run.sha256"
chmod 0755 "peeronq-server-${release_version}.run"
sudo "./peeronq-server-${release_version}.run" --bootstrap --dry-run \
  --acme-email <real-certificate-notification-email> \
  --admin-email admin@peeronq.com \
  --public-ip 31.171.38.28 \
  --local-ip 10.20.10.46 \
  --admin-allowed-cidr 10.20.10.0/24 \
  --platform-upgrade-keyring /secure/peeronq-platform-release-keys.gpg \
  --platform-upgrade-signer-fingerprint <exact-40-or-64-hex-fingerprint>
```

The default production mail provider is `Disabled`; the file-system development sink is still
rejected. Password reset and invitation endpoints return an explicit service-unavailable response
without creating tokens while mail is disabled. To enable email later, supply an approved resolvable
host with `--customer-smtp-host` and optionally `--customer-smtp-port`; place credentials only in the
root-owned `/etc/peeronq/peeronq.env`, never on the command line.

After the bootstrap dry-run, start the verified release without repeating bootstrap:

```bash
sudo "./peeronq-server-${release_version}.run" --env-file /etc/peeronq/peeronq.env
```

Those last two options are the one-time trust bootstrap for Admin platform upgrades. Supply an
exported **public-only** OpenPGP keyring and an exact fingerprint verified through an independent
release-authority channel. The installer rejects secret-key packets, multi-purpose path aliases,
unsafe ownership, and fingerprints not present in the keyring, then copies only the public trust and
fingerprint to root-owned files. Existing installations must perform one manually downloaded,
SHA-256-checked, GPG-verified bundle install with both options before the Admin upgrade page is
enabled; the Admin API cannot bootstrap or rotate its own host trust.
The pinned value may be the exact primary-key fingerprint or the exact signing-subkey fingerprint;
the host accepts only a successful single `VALIDSIG` whose signing fingerprint or reported primary
fingerprint is that enrolled value.

For automatic split-DNS TLS, create a Spaceship API key with only `dnsrecords:read` and
`dnsrecords:write`, then stage its values in a root-only directory. The values are never put in
`peeronq.env`, a Compose variable, a command line, or a systemd unit:

```bash
sudo install -d -o root -g root -m 700 /etc/peeronq/acme-spaceship
sudo install -o root -g root -m 400 /secure/spaceship-api-key /etc/peeronq/acme-spaceship/api-key
sudo install -o root -g root -m 400 /secure/spaceship-api-secret /etc/peeronq/acme-spaceship/api-secret
```

For a new bootstrap, pass `--spaceship-dns-credentials /etc/peeronq/acme-spaceship` together with
`--acme-email`. The bundled Certbot container adds and removes only the exact
`_acme-challenge.peeronq.com` TXT value, waits for authoritative propagation, and does not stop
Nginx or bind TCP 80. Renewal runs weekly and keeps the current certificate active on DNS/API failure.
On a later signed Admin upload, the installer discovers this canonical directory automatically.

```bash
sudo ./peeronq-server-0.6.40.run --env-file /etc/peeronq/peeronq.env \
  --spaceship-dns-credentials /etc/peeronq/acme-spaceship \
  --acme-email admin@peeronq.com --dry-run
sudo ./peeronq-server-0.6.40.run --env-file /etc/peeronq/peeronq.env \
  --spaceship-dns-credentials /etc/peeronq/acme-spaceship \
  --acme-email admin@peeronq.com
```

If a trusted SAN/wildcard certificate already exists, replace `--acme-email` with
`--tls-cert /secure/fullchain.pem --tls-key /secure/privkey.pem`. The certificate must cover the apex,
web, API, Account Portal, Admin, Grafana, Prometheus, Downloads, Updates, Presence, Signaling and TURN
names. HTTP-01 bootstrap installs a weekly systemd renewal timer; TCP 80 must remain reachable for
renewal. The
standalone HTTP-01 flow stops only the running edge proxy long enough to bind TCP 80 and restores
that exact existing proxy on both success and failure before the upgrade or renewal continues. A
successful in-place ACME-managed upgrade also atomically refreshes the command used by the existing
systemd renewal timer; imported-certificate deployments remain unmanaged and do not gain a timer.

An existing HTTP-01 installation whose management names are now split-DNS must be converted once
from a root shell; the Admin upload route never accepts a TLS private key. Put a DNS-01/public-CA SAN
or apex-plus-wildcard pair in absolute root-owned, non-symlink paths (certificate mode `0644` or
narrower, private-key mode `0600`), then validate and apply the same immutable bundle:

```bash
sudo ./peeronq-server-0.6.35.run --env-file /etc/peeronq/peeronq.env \
  --tls-cert /secure/peeronq-fullchain.pem --tls-key /secure/peeronq-privkey.pem --dry-run
sudo ./peeronq-server-0.6.35.run --env-file /etc/peeronq/peeronq.env \
  --tls-cert /secure/peeronq-fullchain.pem --tls-key /secure/peeronq-privkey.pem
```

The installer requires at least 24 hours of remaining validity, every configured DNS name, and an
exact certificate/private-key match. It backs up the active pair and known PeerOnQ renewal units,
stops HTTP-01 renewal, publishes both files atomically, recreates proxy/TURN, and retires the old
timer only after public verification. Any failed deployment restores the previous certificate,
renewal state, and edge services. After this one-time conversion, normal later platform bundles use
the signed Admin upgrade flow without carrying the private key.

The bundled HTTP-01 flow validates every certificate name, including the three operator surfaces;
those names must therefore resolve to `31.171.38.28` while issuance and renewal run. External HTTPS
requests to those three operator hosts are still denied by the source-CIDR rule. The customer Portal
remains public. If the management names must never exist in public
DNS, import a trusted SAN/wildcard certificate issued through DNS-01 or another external certificate
workflow; then keep only their internal split-DNS records pointing to `10.20.10.46`.

After a healthy first install, the installer atomically disables and blanks all Admin bootstrap
values from `/etc/peeronq/peeronq.env`, recreates Admin API, and leaves the credentials/TOTP/recovery
codes only in `/etc/peeronq/admin-onboarding.txt` with mode `0600`. Read it as root, enroll the TOTP
secret, verify login at `https://admin.peeronq.com`, protect the recovery codes offline, then delete
the onboarding file. The generated pilot update private key is not mounted into any container; move
it to the offline Windows release workstation and remove the server copy before a real launch.
The Admin, Grafana, and Prometheus virtual hosts evaluate the direct TCP source address and
return `403` outside `PEERONQ_ADMIN_ALLOWED_CIDR`; they never trust `X-Forwarded-For`. Configure split
LAN DNS (or local hosts entries) so all three names resolve to `10.20.10.46` on internal devices.
Public web, Account Portal, API, Presence, Signaling, Downloads, Updates, and TURN hosts remain reachable because the
clients need them. Grafana remains authenticated, and the Prometheus proxy permits only GET/HEAD.

The installer verifies its embedded payload, rejects symlink/path-traversal entries, and fails before
bootstrap or Compose if the mandatory client version/release-type metadata, canonical MSI, unique
lowercase SHA-256 record, or matching artifact bytes are missing or invalid. It then validates the
merged staging+production Compose model without printing interpolated secrets, runs persistent
storage initializers in a bounded preflight and repeats them during normal startup, applies migrations
and database permissions, waits for container health, and verifies the embedded client's HTTP hash,
accurate signed/unsigned Downloads UI state, complete final TLS-proxy stream, public API liveness,
and Signaling readiness before recording `current`/`previous` release links
under `/opt/peeronq`. Before retrying an incomplete first installation, it removes partial containers
and networks while preserving all named data volumes. A failed release remains available for
investigation; rerunning the identical bundle moves
that unreferenced root-owned directory under `/opt/peeronq/failed-releases` and extracts a pristine
copy. Active, rollback, symbolic-link and non-root-owned targets are never replaced.

The edge resolves Docker service names continuously. This matters during an in-place upgrade:
recreated API or Signaling containers can receive new internal addresses while the proxy remains
running. The Signaling metrics sidecar is also recreated whenever its shared network-namespace owner
is replaced, preventing a healthy-looking edge from remaining pinned to a dead container instance.

On failure, the installer records the failed step, Compose state and the last 200 container-log lines
before cleanup in a root-only `0600` file under `/var/log/peeronq/installer` and prints its exact path.
These logs can contain operational details and must not be published. Use `--status` for read-only
service state and `--rollback` to restore the previous application bundle. Database migrations remain
forward-only and therefore must be backward compatible with the previous application release.

The 0.9.69 installer could falsely report `Public Windows client response is cacheable.` after
healthy startup: nested shell quoting made its header filter remove the letter `r` instead of CR.
Release 0.9.70 corrects that filter; it still requires `Cache-Control: no-store` and verifies TLS,
client hash and version before activation. Use the corrected immutable bundle instead of disabling
publication checks. Quality exercises the actual nested command and the TLS ingress fixture.

An in-place upgrade first stops the retained application stack with `docker compose down` without
the volume-removal option. This releases old network endpoints before the new release reconciles its
networks, while PostgreSQL, Redis, Grafana and other named-volume data remain intact. Database/cache
and telemetry prerequisites then start in separate health-waited steps with three bounded attempts.
If any step fails, the installer starts the retained release again and leaves `current` unchanged.

Install a later server bundle without `--bootstrap`; it reuses the protected environment and records
the old application bundle for `--rollback`. It also repairs installer-managed TLS and file-backed
secret ownership before containers start. Shared files use narrow supplementary groups only where two
different non-root services consume the same secret; the secret directory remains root-only:

```bash
release_version='<canonical version of the new verified release>'
sudo "./peeronq-server-${release_version}.run" --env-file /etc/peeronq/peeronq.env --dry-run
sudo "./peeronq-server-${release_version}.run" --env-file /etc/peeronq/peeronq.env
sudo "./peeronq-server-${release_version}.run" --status --env-file /etc/peeronq/peeronq.env
```

Server 0.6.20 migrates environments created before customer mail and public-host settings were
introduced. No SMTP service is required; the installer selects the bounded disabled-mail policy and
preserves any already configured SMTP provider:

```bash
chmod 0755 peeronq-server-0.6.20.run
sudo ./peeronq-server-0.6.20.run --dry-run
sudo ./peeronq-server-0.6.20.run
```

The migration fills missing mail-policy, key, signaling and public-host values without replacing a
valid existing SMTP configuration or custom hostname. A dry-run never mutates the protected file,
so repeat the command without `--dry-run` to apply the migration and release. Grafana, Prometheus,
Admin, Portal, Signaling and the remaining bundled services are installed and started by the same
server package; no separate SMTP package is installed.

If a failed older migration left an unwanted SMTP provider or stale host in the protected env,
recover without manually editing secrets by making the policy change explicit:

```bash
sudo ./peeronq-server-0.6.20.run --disable-customer-mail --dry-run
sudo ./peeronq-server-0.6.20.run --disable-customer-mail
```

The explicit disable option clears the SMTP host, port override, username and password, requires Closed registration (explicitly select it when changing an open installation),
and disables email verification. It cannot be combined with SMTP options.

Server 0.6.26 also includes the customer Portal SPA in the complete bundle. Its embedded Windows MSI
publication check streams the payload directly into SHA-256 instead of copying the roughly 76 MiB
file into the web container's 16 MiB temporary filesystem. Upgrade and verify it with:

```bash
chmod 0755 peeronq-server-0.6.26.run
sha256sum -c peeronq-server-0.6.26.run.sha256
sudo ./peeronq-server-0.6.26.run --disable-customer-mail --dry-run
sudo ./peeronq-server-0.6.26.run --disable-customer-mail
sudo ./peeronq-server-0.6.26.run --status
```

Admin **Upgrade** accepts only a complete canonical stable `major.minor.patch` server trio (no
prerelease, fourth segment, or leading-zero component; `.run`, exact `.sha256`, and detached `.asc`),
with a 256 MiB bundle limit. The exact upload route alone has a
260 MiB ingress ceiling and disabled proxy request buffering; Admin API's 272 MiB temporary
filesystem accommodates ASP.NET multipart form spooling plus bounded metadata. This is a privileged,
single-upload memory bound, so operators must serialize platform uploads and monitor host memory.

Admin writes a fixed request contract into
`/var/lib/peeronq/platform-upgrade/inbox` and can read only bounded, sanitized `status.json`. It has
no Docker socket, root process execution, trusted keyring, archive, installer log, `/opt`, or `/etc`
mount. A root-owned hardened `systemd.path`/oneshot agent claims one atomically published request,
rejects unsafe owners, modes, links, hardlinks, extra files, replay, stale-current, and downgrade
attempts, recomputes the exact checksum, and requires `gpgv` `VALIDSIG` to match the pinned root-owned
fingerprint. Stage performs no bundle execution: it only verifies and retains the signed archive.
Owner-authorized apply reverifies that root-only archive, invokes the fixed installer `--dry-run`,
then applies it. The full apply also transactionally updates the root-owned updater agent and units,
self-checking the new agent and restoring the previous trio and enablement state on failure. Rollback
is limited to the agent-selected retained verified release. The root updater and its pinned trust are
forward-only across an application rollback: rollback restores the previous server stack but never
reinstalls an older, potentially vulnerable host agent or unit. Every updater release must therefore
continue to accept the fixed schema-1 spool/status contract used by the retained previous stack.
Raw host logs never cross the Admin boundary.

The sticky inbox prevents Admin from removing another owner's active gate. After a complete ready
request is published, the agent replaces the gate with a root-owned marker and keeps it for the full
operation. Every atomic claim creates a root-only operation log before status references it; terminal
contract rejections retain that sanitized reference while the untrusted claimed payload is removed.
A service failure restarts once per bounded interval and converts an uncertain claimed operation to
`manual_recovery` without replay. Producer requests without a ready marker expire after 30 minutes.
Archive retention is bounded to the verified active release plus the staged/previous release; only
strictly validated root-owned release or stale staging directories are eligible for removal.

Owner role, MFA, and API audit are governance controls, not cryptographic isolation from a fully
compromised Admin container. Because Admin owns the inbox, such a compromise could schedule an
already valid offline-signed stage/apply/rollback request outside the API audit trail; it still cannot
forge unsigned code, the pinned keyring, or root-published status. Treat that event as critical, stop
the updater path and service, and investigate the root-only evidence. Removing this residual trust
would require a separate asymmetric Owner-authorization broker enforced by the host agent.

A full-platform apply deploys the complete server stack, forward-only database migrations, and the
bundle's checksum-verified Windows client/public-download payload. It does not force-update desktop
applications already installed on laptops. Those continue to use the separate signed
**Releases**/AppRelease self-update channel: Admin Releases accepts the Authenticode MSI and
ECDSA-signed manifest, verifies them, and publishes that package atomically.

Admin **Releases > Website upgrades** accepts only a complete static ZIP plus a manifest signed by
the separate website P-256 key. It rejects traversal, links, executable modes, unknown extensions,
duplicate paths, oversized/expanded archives and expired or mismatched signatures, then atomically
switches `current` while retaining `previous`. The builder includes a byte-identical, Admin-verified
Downloads UI compatibility entry; old retained overlays without it fall back to the server-bundled
Downloads page. Website releases cannot contain or select an MSI. Build the pair offline and upload
both generated files:

```powershell
.\scripts\windows\build-peeronq-website-patch.ps1 `
  -Version 0.6.12 `
  -PrivateKeyPemPath .\secure\website-private-key.pem `
  -ExpectedPublicKeySpkiBase64 '<PEERONQ_WEBSITE_SIGNING_PUBLIC_KEY_SPKI_BASE64>'
```

The website signing private key is generated under `/etc/peeronq/pilot-website-signing` during
bootstrap or the first 0.6.11 upgrade. Transfer it over a protected channel to the offline build
workstation and delete the server private-key copy after verification. Never upload a private key,
loose shell script, or arbitrary package through the Admin Panel; only the fixed signed platform
trio is accepted by the dedicated Upgrade route.

For a controlled connectivity test, build the explicit x64 public-pilot client. Its production
API/Presence/Signaling/Downloads/Updates endpoints
are compiled in. The pilot MSI itself remains unsigned, but its Verified Updates panel is enabled
only with the complete public manifest key/key ID and Authenticode publisher fingerprint; it can
therefore move forward only to a normally signed release published through Admin **Releases**. It may
be embedded in a full server bundle only with the explicit unsigned-pilot build switch; the Downloads
page labels it as a controlled pilot and never presents it as a signed stable release. Do not publish
the unsigned pilot MSI itself through the Windows Admin Releases/update channel.
If the manifest public key/key ID and publisher fingerprint do not exist yet, omit all three update
trust arguments from the command below; the connectivity test still builds, but Verified Updates
correctly remains unavailable until a later client is built with the complete public trust set.

Every navigation link to the public `/downloads` page performs a document load instead of an SPA
transition. A current-format signed website patch can update that page through its verified UI-only
compatibility entry; an older overlay falls back to the server-bundled page. The canonical MSI,
SHA-256, version and signing classification always remain owned by the full server release.

A successful full-server upgrade makes its bundled website authoritative. After the complete stack
is healthy, the installer transactionally deactivates any previously active website overlay, proves
that the public `/downloads` document is byte-identical to the new base web image, and only then
commits the deployment. A failed verification reports the exact failed step and restores the prior
overlay together with the prior application release. Website release directories are retained and
can be selected later through Admin, but an older patch must not be reactivated when the full server
release supplies the newer UI.

```powershell
.\scripts\windows\build-peeronq-public-pilot.ps1 `
  -Architectures x64 `
  -UpdateChannel beta `
  -UpdatePublicKeySpkiBase64 $env:PEERONQ_UPDATE_PUBLIC_KEY_SPKI `
  -UpdateKeyId $env:PEERONQ_UPDATE_KEY_ID `
  -PublisherCertificateSha256 $env:PEERONQ_PUBLISHER_CERTIFICATE_SHA256 `
  -IUnderstandThisIsNotProductionSigned
```

This single-node bundle is suitable for an explicitly accepted production pilot, not Phase 9 global
high availability. Managed multi-zone PostgreSQL/Redis, off-host encrypted backups, an independent
penetration test, and a practiced disaster-recovery run remain public-production gates.
When the same verified public-pilot pair is intentionally needed on the local preview, build both
architectures and pass `-PublishLocalWebsiteDownloads`. The wrapper then publishes the exact
classification/checksums, restarts the preview and verifies the selected canonical version; it does
not change the signed production Downloads Service.
Pilot diagnostics use the private `diagnostic-artifacts` volume with an explicit production opt-in;
move that storage to an encrypted managed object store before leaving the single-node pilot.

## Health contract

- `/health/live`: process/event-loop health only; temporary database, Redis, object-store, or peer
  failures do not kill a healthy process.
- `/health/ready`: checks every dependency required to accept that service's traffic.
- `/health/startup`: remains unhealthy until configuration validation, migration compatibility, and
  startup initialization complete.
- `/metrics`: internal network only and contains no device, installation, session, user, diagnostic,
  trace, or download identifier labels.
- coturn readiness uses its STUN client health check; Prometheus scrapes port 9641 and also probes the
  TCP 3478 listener. This does not replace external UDP/TCP/TLS allocation acceptance.
- Signaling continues to reject non-TLS application traffic outside loopback. A non-root, read-only
  Nginx sidecar sharing only the signaling network namespace exposes `/metrics` on internal port
  8081; every other path returns 404. This lets Prometheus scrape metrics without weakening the
  signaling TLS policy or exposing the endpoint publicly.

The development node exporter uses a read-only root mount without recursive slave propagation so it
also starts on Docker Desktop. The staging override restores `rslave`, which is required on the
supported Linux host to observe mounts created after exporter startup.

Signaling and coturn stdout are forwarded through the collector's fixed address on the isolated
observability bridge; the Fluent Forward listener is additionally bound only to host loopback for
local troubleshooting. The collector redacts IP literals, formatted Device IDs, and time-limited
TURN usernames before Loki ingestion. Reverse-proxy access logs remain disabled. Treat an
unredacted telemetry sample as a security/privacy incident.

## Backups and restore proof

Run a verified custom-format PostgreSQL backup daily:

```powershell
docker compose --env-file <env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml `
  --profile operations run --rm backup
```

The controller uses `pg_dump`, validates the archive with `pg_restore --list`, writes SHA-256 evidence,
publishes a textfile metric, and removes only matching backup files older than the configured period
inside the exact mounted backup directory. Backup and restore jobs mount different `.pgpass` secrets,
copy each to an owner-only `0600` file on their private `/tmp` tmpfs, and remove that copy on exit;
their root filesystems remain read-only. This preserves libpq permission checks even where a local
Compose bind mount cannot expose the requested secret mode.

At least monthly, set `PEERONQ_RESTORE_TEST_FILE` to an existing container path such as
`/backups/peeronq-cloud-20260811T010000Z.dump`, then run:

```powershell
docker compose --env-file <env> `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.staging.yml `
  --profile acceptance run --rm restore-test
```

The restore test creates a uniquely named isolated database, restores the archive, verifies public
tables exist, and drops only that test database. A real restore is destructive and requires the exact
`PEERONQ_CONFIRM_RESTORE=RESTORE_PEERONQ_CLOUD` confirmation; follow the Backup Failure runbook while
application writers are stopped.

PostgreSQL backups contain Restricted data and must be encrypted with a separately held key. Back up
signed release artifacts and diagnostic object storage according to their own service policies.
Production Redis contains short-lived leases/challenges **and** the shared
`peeronq:{admin}:data-protection-keys` key set used to decrypt administrator MFA secrets. Use managed
encrypted Redis persistence/snapshots and test restoration into an isolated Redis instance. Limit
snapshot access as Restricted data; expired TTL records remain expired after recovery. A restore is
accepted only after an administrator can complete MFA with the restored Data Protection keys.
Prometheus blocks, Loki logs, and Tempo traces are operational evidence under their retention policy,
not business-record backup. Preserve them separately only for an active incident or legal hold.

## Scaling and regions

Cloud, Presence, Admin, Downloads and Signaling hosts keep shared state in PostgreSQL/Redis and can scale
horizontally behind a regional load balancer. Presence instances coordinate leases and duplicate
connections in Redis. PostgreSQL remains the durable authority. Clustered Signaling requires a
unique `Signaling__Cluster__InstanceId` per process, the dedicated `peeronq_signaling` Redis ACL,
the shared hash-tagged prefix, and one private metrics sidecar per process. Do not mix the local
in-memory session store with clustered replicas. Use separate regional connection pools, bounded
queries, and a controlled global ownership/routing record rather than cross-region Redis as a hidden
source of truth.

For the local Phase 9 management-plane gate, merge the opt-in HA overlay and run the bounded harness:

```powershell
docker compose --env-file src/PeerOnQ.Infrastructure.Deployment/.env `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.development.yml `
  -f src/PeerOnQ.Infrastructure.Deployment/docker-compose.ha.yml config --quiet

.\scripts\windows\test-phase9-single-region-ha.ps1 `
  -RequestCount 200 -Concurrency 8 -IncludeSingleNodeDataRestart
```

The overlay adds warm backups for Cloud API, Presence and Downloads plus two active Signaling
instances. Separate Downloads cache
volumes prevent cross-process cache corruption. Nginx's explicit backup peer avoids the concurrent
first-failure `504` window observed with passive-active management peers on Docker Desktop; the
WebSocket upstream uses `least_conn` across both Signaling nodes. This profile does not add
PostgreSQL, Redis or coturn replicas and must not be called production data-tier HA, global or
multi-region. The ownership matrix and production data-service integration
contract are in [HA_AND_DISASTER_RECOVERY.md](HA_AND_DISASTER_RECOVERY.md).

For production, replace Compose PostgreSQL and Redis with multi-zone managed services, object storage
with lifecycle policies, and durable telemetry storage. Deploy each region with the same immutable
image digests and a unique `Region__Id`. Do not send traffic until regional health and failover tests
pass. Kubernetes manifests may later map health endpoints to startup/readiness/liveness probes,
external secrets to CSI, and services to disruption budgets; Phase 6 deliberately does not require a
Kubernetes control plane.

Every image reference in `docker-compose.production.yml` and every `FROM` line used by a production
build is pinned to an immutable manifest digest. Upgrade by resolving and reviewing new digests,
running the Compose/build/security gates, and changing the tag-plus-digest pair together; never
silently retag an approved deployment.

## Rollback

Application rollback uses the last verified image while retaining forward-compatible schema. Never
automatically run a down migration on a live database. If a migration prevents rollback, stop traffic,
restore into an isolated database, verify it, and obtain database/security owner authorization before
the destructive restore. Release rollout changes and administrator actions remain audited throughout
rollback.
