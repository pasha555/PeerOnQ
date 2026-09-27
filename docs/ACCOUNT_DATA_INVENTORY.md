# PeerOnQ customer account data inventory

This inventory covers Phase 7 customer/account data in the self-hosted Cloud database. It does not
cover the separate internal Admin identity domain or local desktop SQLite/DPAPI data. PeerOnQ does
not send customer portal telemetry to an external SaaS provider.

| Data class | Stored values | Purpose and access | Lifecycle |
| --- | --- | --- | --- |
| Customer account | normalized email, display name, versioned password hash, status, verification/MFA flags, protected MFA secret, timestamps | authentication/profile; current account and authorized organization members only | disabled/deletion-pending accounts cannot authenticate; deletion request revokes sessions |
| Customer session | account/family IDs, SHA-256 refresh hash, bounded user-agent summary, created/expiry/revocation/replacement timestamps | rotating browser login and explicit revocation; account owner only | access JWT 10 min by default; refresh 14 days; replay revokes family |
| Account token/recovery | purpose, SHA-256 token/code hash, issue/expiry/use timestamps | email verification, password reset and one-use recovery | email/reset 30 min by default; used/expired values cannot authorize |
| Organization/membership/team | organization name/owner, account membership and customer-only role, team name/membership | tenant collaboration and RBAC | owner transfer explicit; removal revokes membership; deletion guarded by members/devices |
| Invitation | organization, normalized recipient email, role, SHA-256 token hash, issue/expiry/accept/revoke timestamps | short-lived tenant onboarding | 24 h by default; one use; revocable; guessing/replay fails closed |
| Organization policy | connection modes, unattended/clipboard, mandatory MFA, trusted-device/audit retention days, relay regions, client version, hybrid requirement | server and signaling authorization | versioned update; effective for subsequent evaluation/attestation |
| Claimed device/session metadata | organization owner link, masked device ID, status/timestamps, permission/path/lifecycle | organization device and support/session views | no screen, clipboard, file, key or pointer content is stored |
| Trusted-device record | account, SHA-256 device key hash, name, issue/expiry/revocation | explicit account sign-in trust inventory/revocation | policy-bounded expiry; expired/revoked records cannot represent active trust |
| Customer security event | account/organization, action/result, UTC time, correlation ID, bounded safe metadata | account security and organization audit/export | append-only in EF/PostgreSQL; organization policy exposes 1–3650 day retention configuration |
| Data request | account, export/delete kind, status and timestamps | operator-visible privacy workflow | one equivalent pending request; delete requires owner transfer and revokes sessions |
| Customer Data Protection keys | ASP.NET Data Protection key ring | encrypt MFA secrets and time-limited setup challenges | dedicated persistent Cloud volume; backup/restore with DB; never served by API |
| Development mail | recipient, subject, bounded body containing one-time link | local verification/reset/invitation testing | dedicated development volume only; provider rejected outside Development/Testing |

Passwords, raw refresh/invitation/reset/recovery/trusted-device tokens, private keys, remote screen
frames, clipboard text, file names/paths/content, pointer/keyboard input, and full device IDs are not
customer audit fields. Organization audit JSON export contains only the bounded event shape above.

Retention settings are transparent through organization policy. Expiry is enforced for
authorization even if an expired row remains for audit/operations. Destructive database cleanup of
customer security events must use an approved bounded maintenance path that preserves legal/audit
requirements; Phase 7 does not silently grant the Cloud runtime direct UPDATE/DELETE on the
append-only customer audit table.
