# PeerOnQ Phase 7 completion report

## 1. Phase and final gate

**Phase:** 7 — customer accounts, organizations, teams, policy, audit and a real portal without
commercial licensing.

**Final gate:** `PASS_WITH_EXTERNAL_BLOCKERS`.

The self-hosted account/API, tenant/RBAC boundary, managed signaling policy, real portal, migration,
mail/key persistence and multi-organization API acceptance pass locally. The contract also requires
recorded real-browser evidence. The in-app Browser runtime could not initialize because its MCP
bootstrap omitted `sandboxPolicy`; DOM tests and HTTPS/API probes do not replace that visual gate.

## 2. Repository snapshot

- Branch: `feat/phase1-remote-view`; base `HEAD`:
  `086631df7d95e71b5e13ce563de55b2a9e844e63`.
- Validation date: 2026-08-17; Windows host in Asia/Baku.
- The worktree contained cumulative uncommitted Phase 1–6 work. It was preserved in place.
- No commit, push, tag, public deploy, DNS, router, firewall, database deletion or volume deletion
  was performed.

## 3. Areas inspected

- Existing Cloud/Admin device identity, authentication, database, Redis and audit boundaries.
- Customer account lifecycle, cookies, CSRF, MFA/recovery, reset, sessions and rate limiting.
- Organization membership, invitations, teams, RBAC, policy, device/session visibility and audit.
- Cloud-signed signaling attestation and server-side managed-session policy enforcement.
- Public website, separate customer portal, Admin SPA and Nginx host separation.
- PostgreSQL migrations/triggers/runtime grants, customer key/mail volumes and SMTP configuration.
- Phase 7 tests, runtime logs, HTTPS headers and deployment controller behavior.

## 4. Components reused

- ASP.NET Core Identity password hashing, authentication/authorization, Data Protection and rate
  limiting; no external identity SaaS is required.
- Existing Cloud EF Core/PostgreSQL model and forward-only migration workflow.
- Existing proof-bound device enrollment and asymmetric Cloud-to-Signaling attestation.
- Existing Nginx TLS proxy, Compose controller, observability, mail/configuration patterns and
  PeerOnQ visual language.
- Existing accountless/unmanaged LAN path; it was not converted into an account activation flow.

## 5. Confirmed defects and proven root causes

1. The old public `/app` surface was a localStorage preview, not an authenticated portal. Active
   routes were retired and account links now target the separate real portal host.
2. A portal container attempted a network-dependent Alpine Nginx reinstall and used the wrong
   configuration path. It now uses the pinned official image, writable PID and correct conf.d file.
3. Customer login rejected valid active accounts whose lock timestamp was null. The null/no-lock
   case is now explicitly allowed and tested.
4. Customer roles and connection modes serialized as numbers while the portal sent names. Domain
   enums now use bounded string JSON contracts; the live role/policy flow passes.
5. Customer tables initially lacked explicit runtime grants and persistent key/mail directories
   could be root-owned. Least-privilege grants and a scoped storage-init service fix both.
6. Refresh-token concurrency could leave a winning replacement active after replay. The complete
   token family is atomically revoked after a concurrency conflict.
7. MFA setup protection was not time-limited. The setup challenge now expires after ten minutes and
   remains decryptable across a Cloud restart through the persistent key ring.
8. Authenticated customer rate limits used one shared `authenticated` partition. The partition now
   uses customer account ID (falling back to installation ID only for device principals), preventing
   one tenant from consuming another tenant's authenticated limit.
9. The supported controller treated Grafana's successful multi-line CLI output as failure because
   PowerShell `-notmatch` returned every nonmatching array element. The predicate now tests whether
   any output line matches `successfully`; the exact command and predicate pass.

## 6. Files created and modified

Created:

- `artifacts/peeronq-portal/` — separate React/Vite/Nginx customer portal.
- `CustomerPortalOptions.cs`, `CustomerPortalAuthentication.cs`, `CustomerMail.cs`,
  `CustomerAccountService.cs`, `CustomerPortalEndpoints.cs`, `CustomerOrganizationService.cs`, and
  `CustomerOrganizationEndpoints.cs` in `PeerOnQ.Cloud.Api`.
- `CustomerIdentityEntities.cs`, the Phase 7 EF migration/designer, and
  `OrganizationSessionPolicy.cs`.
- `CustomerPortalSecurityTests.cs`, `OrganizationSessionPolicyTests.cs`,
  `scripts/windows/test-phase7-customer-portal.ps1`, and this report.
- `artifacts/peeronq/src/lib/accountPortal.ts` and `docs/ACCOUNT_DATA_INVENTORY.md`.

Modified:

- Cloud API wiring/options, domain enums/device ownership, EF model/snapshot/bootstrap and
  attestation issuer.
- Shared attestation contracts and Signaling validation/registry/session routing.
- Development/staging/production Compose, Nginx, environment contract, runtime database grants,
  controller and production image-digest regression.
- Public website account links/router, route tests, design/map/security/privacy/deployment/operations
  documentation and changelog.

## 7. Migrations

- Added forward-only migration
  `20260817060948_AddCustomerIdentityOrganizationsAndPolicy`.
- Added customer account/session/token/recovery, organization/membership/invitation, team,
  organization policy, trusted device, security event and data-request tables with indexes/FKs.
- Added PostgreSQL `TR_CustomerSecurityEvents_AppendOnly`; EF also rejects update/delete before write.
- Real database proof returned `t|t` for customer table plus append-only trigger presence.
- Migration job reported the database current; runtime permission bootstrap completed. Existing
  named volumes were preserved and no down migration ran.

## 8. API/protocol/schema changes

- Added `/portal/v1/auth/*`, `/portal/v1/account/*`, and tenant-scoped
  `/portal/v1/organizations/*` routes documented in `ROUTES_MAP.md`.
- Customer and Admin schemes/claims/cookies/roles remain unrelated.
- Managed attestation carries organization ID, policy flags, minimum client version and approved
  relay regions. Signaling enforces both participants before routing.
- The unmanaged/accountless attestation shape remains supported; signaling protocol version remains
  2 and no media/collaboration payload schema changed.

## 9. Security/privacy changes

- Secure Identity password hashing; enumeration-safe reset; email verification; TOTP; one-use
  recovery; ten-minute MFA setup; DB-backed revocable sessions; one-use rotating refresh families.
- Secure/HttpOnly/SameSite=Strict root `__Host-` cookies and double-submit CSRF.
- Cryptographically random protected/hash-only account, reset, invitation, refresh and recovery
  material; raw values are not stored or logged.
- Tenant filter and customer-role check on every organization command/query; explicit owner transfer
  and deletion/device guards; append-only redacted audit.
- Dedicated persistent customer Data Protection/mail storage; production rejects the file mail sink.
- No licensing key, activation, billing, pricing, payment, seat, subscription or entitlement model,
  API or active portal route was introduced.

## 10. Exact tests and commands

```powershell
dotnet test tests/PeerOnQ.Cloud.Domain.Tests/PeerOnQ.Cloud.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~VerifiedActiveCustomerWithoutALockCanLogin|FullyQualifiedName~CustomerInvitation|FullyQualifiedName~OrganizationOwnershipTransfer|FullyQualifiedName~OrganizationDeletion|FullyQualifiedName~OrganizationPolicy|FullyQualifiedName~DisabledCustomerCannotLogin"
dotnet test tests/PeerOnQ.Cloud.Infrastructure.Tests/PeerOnQ.Cloud.Infrastructure.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CustomerPortalSecurityTests|FullyQualifiedName~ModelEnforcesIdentityIdempotencyAndAuditIndexes|FullyQualifiedName~GeneratedPostgreSqlScriptContainsCompleteForwardSchema|FullyQualifiedName~ContextRejectsAuditAndDiagnosticAccessMutationBeforeDatabaseWrite|FullyQualifiedName~MigrationAppliesToExplicitPostgreSqlTestDatabase"
dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OrganizationSessionPolicyTests|FullyQualifiedName~Validator_preserves_signed_organization_policy_claims"
dotnet test tests/PeerOnQ.Observability.Tests/PeerOnQ.Observability.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WindowsDevelopmentController_ManagesOperationsSecretsOutsideTheEnvironmentFile|FullyQualifiedName~Phase7CustomerPortal_HasIsolatedStorageAndLeastPrivilegeDatabaseAccess|FullyQualifiedName~ProductionContainerInputs_ArePinnedToImmutableDigests"
dotnet build src/PeerOnQ.Cloud.Api/PeerOnQ.Cloud.Api.csproj -c Release --no-restore
pnpm --filter @workspace/peeronq-portal run typecheck
pnpm --filter @workspace/peeronq-portal run test
pnpm --filter @workspace/peeronq-portal run build
pnpm --filter @workspace/peeronq run typecheck
pnpm --filter @workspace/peeronq run test -- --run src/test/routeSeparation.test.tsx
pnpm --filter @workspace/peeronq run build
.\scripts\windows\test-phase7-customer-portal.ps1
.\scripts\security\scan-repository-secrets.ps1
.\scripts\quality\test-brand-purity.ps1
git diff --check
```

Runtime probes used the current rebuilt Cloud image, `https://127.0.0.1:8443` with the exact
`portal.dev.localhost` Host header, Cloud logs, controller status, PostgreSQL catalog queries and the
real Grafana CLI stdin synchronization command.

## 11. Exact build/test results

- Phase 7 .NET targeted tests: **24 passed, 0 failed, 0 skipped** — Domain 7,
  customer/migration security 9, Signaling policy/attestation 5, deployment/controller 3.
- Customer portal: typecheck passed; **9/9 tests** passed; production build passed (224.75 kB JS,
  9.95 kB CSS before gzip reporting).
- Public website: typecheck passed; route separation **28/28** passed; production build passed.
- Cloud API Release build: **0 warnings, 0 errors**.
- Current-image real HTTPS/API acceptance: exit 0 in **12.3 s** including its Cloud restart/health wait;
  all named flows pass. The final password-randomization rerun plus an explicit 12-second pre-wait,
  secret scan and brand scan completed in **28.0 s**.
- Secret scan: **933 files**, pass. Brand purity: pass.
- Unchanged earlier-phase suites were not rerun from the beginning, per user instruction.

## 12. Runtime and physical/external evidence

- Current rebuilt `cloud-api` and `portal-ui` images are healthy behind the existing TLS proxy.
- `https://portal.dev.localhost:8443` returns the real portal HTML and HTTP 200 with HSTS, CSP,
  `nosniff`, `DENY` framing, restrictive Permissions Policy and no external script origin.
- The acceptance creates three accounts across independent organizations and multiple roles, uses
  the development mail sink, restarts Cloud, then proves session and MFA setup key persistence.
- Real TOTP activation, MFA-required denial, one-use recovery, password-reset replay denial, CSRF,
  tenant isolation, self-escalation denial, invitation replay, team/policy/ownership/audit and refresh
  family replay all passed.
- Final Cloud log window had no Error/Fatal/Unhandled/Exception lines.
- A real automated browser walkthrough could not be recorded because the Browser MCP bootstrap
  failed before navigation. No physical-laptop test is required for the new portal code and none is
  claimed here.

## 13. Feature truth matrix

| Capability | State | Evidence |
| --- | --- | --- |
| Self-hosted customer accounts | PASS locally | Real register/verify/login/reset/MFA API flow |
| Organizations/teams/invitations | PASS locally | Multi-organization acceptance and domain tests |
| Customer/Admin identity separation | PASS | Separate schemes/entities/routes/cookies/roles |
| Cross-tenant/RBAC fail-closed | PASS | Live read/mutation/self-escalation denials |
| Server-side organization policy | PASS locally | API evaluation + signed claims + Signaling tests |
| Accountless LAN compatibility | PASS targeted | Unmanaged policy test remains allowed |
| Real portal/API | PASS locally | Separate healthy SPA; real HTTPS/API state |
| Browser visual/accessibility runtime | EXTERNALLY_BLOCKED | Browser MCP lacked sandbox metadata |
| Commercial/licensing surfaces absent | PASS | Portal route regression + bounded source scan |
| Privacy/audit/data requests | PASS with operator workflow | Inventory, append-only audit, export/delete requests |
| Trusted-device inventory/revoke | BOUNDED | Real list/revoke/expiry model; persistent trust issuance is not exposed |
| External SMTP delivery | EXTERNALLY_BLOCKED | Local sink and production-startup validation pass; no approved SMTP endpoint supplied |

## 14. Measured performance results and environment

- Windows/Docker Desktop local single-node environment; current Phase 6 stack and preserved volumes.
- Current-image Phase 7 HTTPS acceptance completed in approximately **12.3 s**, including its Cloud
  restart and health wait. The final combined restart/wait/acceptance/secret/brand command completed
  in approximately **28.0 s**.
- Portal production build completed in approximately **2.5 s**; affected frontend validation/build
  group completed in approximately **27.0 s**.
- These are development observations, not browser render, WAN, load, HA or production SLO evidence.

## 15. Brand-purity result

- Repository brand-purity gate passed.
- Portal title, service names, cookies, schemes and new contracts use PeerOnQ-owned identifiers.
- No commercial entitlement or former-product identity was added.

## 16. Dependency/SBOM/provenance impact

- No new external .NET package was added; ASP.NET Core framework facilities are reused.
- The portal uses existing workspace React, Vite, Wouter, Lucide and testing dependencies; the pnpm
  lockfile records that workspace and no paid/external account service is required.
- Portal Nginx and all production container inputs are covered by the immutable-digest regression.
- No Windows release artifact, SBOM, MSI or update manifest was produced by Phase 7.

## 17. P0/P1/P2 issues

- **P0:** none known in locally exercised Phase 7 account/tenant/policy/API paths.
- **P1 external:** real browser runtime evidence and approved external SMTP delivery are missing.
- **P2:** persistent trusted-device issuance is intentionally not exposed; organization audit
  retention is configurable/transparent but append-only cleanup remains an approved operator path;
  policy updates affect evaluation/new short-lived attestations rather than force-ending every
  already-connected media session.

## 18. Known limitations

- The portal has real list/revoke support for trusted-device records but does not yet issue a
  persistent browser-trust credential; this avoids silently weakening MFA.
- Account export/delete endpoints create guarded requests; the self-hosted operator must complete
  export/pseudonymization under the documented workflow.
- Customer security audit is append-only. Expiry affects authorization, while destructive physical
  cleanup requires a reviewed bounded maintenance path rather than Cloud runtime DELETE grants.
- The portal is locally TLS/API tested, not visually approved in the actual Browser tool.
- Single-node Compose is not HA/multi-region and no public production deployment is claimed.

## 19. External blockers

1. Restore Browser MCP with valid sandbox metadata and record registration/login, organization
   switch, member/team/invitation/policy/security/audit/privacy pages, responsive layout, keyboard
   navigation, loading/error/empty states and absence of commercial routes.
2. Configure an approved staging SMTP service, verify TLS/sender/delivery/bounce behavior and repeat
   verification/reset/invitation flows without the development file sink.
3. Before real-user use, publish the self-hosted operator privacy identity/process and prove an
   isolated database + customer Data Protection restore.

No public infrastructure, DNS, firewall or production credential was modified automatically.

## 20. Compatibility impact

- Existing accountless/unmanaged clients remain allowed; customer accounts are not activation.
- Managed attestation adds optional bounded organization-policy claims and remains rejected safely by
  incompatible/invalid validators; signaling protocol version is unchanged.
- The migration is additive and forward-only; existing Phase 6 rows and volumes remain compatible.
- Public `/app*` preview URLs are retired. Account links now require the configured separate HTTPS
  portal origin.
- Production operators must add the portal host, customer JWT key, persistent Data Protection
  storage and SMTP configuration.

## 21. Exact readiness decision for the next phase

**Decision:** Phase 7 is `PASS_WITH_EXTERNAL_BLOCKERS`. Core local account, tenant, policy and API
dependencies are ready for the next phase, but do not describe the portal as fully browser-accepted
or production-ready until section 19 is closed with real evidence.

When the Browser runtime is restored, run only the Phase 7 visual/accessibility matrix and affected
portal/API checks; do not rerun every earlier phase unless that evidence exposes a shared regression.
