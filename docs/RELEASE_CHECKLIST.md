# Release checklist

Record commands, UTC timestamps, environment, artifact hashes, and reviewer identity. A check is not
complete without attached evidence.

## Source and quality

- [ ] Approved commit/tag; clean review of all changes and generated inputs.
- [ ] Pinned .NET SDK, pnpm, NuGet/npm dependencies and GitHub Actions restored from trusted sources.
- [ ] Format, build with warnings-as-errors/analyzers, unit/integration/E2E tests, CodeQL, dependency
  scan, full-history secret scan, protocol fuzz, rate-limit/replay/hijack/path traversal/redaction tests.
- [ ] No unresolved critical/high vulnerability without written risk owner, compensating controls,
  expiry, and release approval.

## Product behavior

- [ ] Visible view/control/file/unattended state, controller identity, and local stop verified.
- [ ] View-only and immutable permission scope verified; reconnect releases all input and cannot expand
  permission; unattended access off by default and visibly indicated.
- [ ] Same-LAN, two different public networks, mobile hotspot, TURN UDP/TCP/TLS, UDP blocked,
  symmetric-NAT where available, signaling restart, sleep/wake, and network transitions recorded.
- [ ] File traversal/overwrite/resume/hash/permission tests and clipboard non-logging verified.
- [ ] Repeatable idle and active-session benchmarks attached; unsupported metrics marked unavailable.

## Installer and update

- [ ] x64 clean standard-user install, launch, repair, previous-version upgrade, downgrade rejection,
  uninstall, optional shortcuts, no hidden persistence/service, and user-data preservation on clean VM.
- [ ] ARM64 package build plus install/launch lifecycle on ARM64 Windows.
- [ ] First-party binaries and MSI Authenticode signatures/trusted timestamp/publisher verify.
- [ ] ECDSA manifest verifies; tamper, wrong key/certificate/product/channel/architecture/hash/size,
  expiry and downgrade cases reject.
- [ ] Failed MSI transaction restores prior version; higher corrective release tested.
- [ ] SPDX SBOM, SHA256SUMS, signed manifests and provenance attestations archived.
- [ ] Linux server `.run` detached signature and SHA-256 verified before root execution; dry-run,
  health wait, previous-release rollback, and forward-compatible database migration tested.
- [ ] Both persistent storage initializers pass twice with existing files preserved; a forced failed
  install retains root-only bounded diagnostics, and the identical bundle retries from a quarantined
  pristine release while `--status` performs no deployment operation.
- [ ] Public-pilot bootstrap DNS/NAT preflight, TLS issuance/import, root-only Admin onboarding scrub,
  coturn public/private IP mapping, ACME renewal timer and diagnostics-volume backup tested on host.
- [ ] Local Compose file-backed secrets remain under a root-only directory; every non-root production
  consumer can read only its mounted secret, including the two explicitly shared-group files.
- [ ] Every Compose service static IP is contained by the matching subnet in the final merged
  staging+production model; daemon-level network/container creation tested on the release host.
- [ ] Every .NET Docker build stage uses the exact SDK selected by `global.json`; floating SDK/runtime
  tags are absent and the migration bundle plus all production service images build from a clean pull.

## Operations and approval

- [ ] Public DNS/TLS/firewall/TURN quotas, health/readiness/metrics, alerts, backups and rotations tested.
- [ ] Staging smoke and canary acceptance passed; initial rollout percentage and rollback thresholds set.
- [ ] Signing/update key rotation and revocation drill completed.
- [ ] Privacy/retention/support/vulnerability disclosures reviewed and public security channel staffed.
- [ ] Independent penetration test findings resolved or formally accepted.
- [ ] Manual production environment approval recorded before distribution.

The release workflow intentionally stops at an approved artifact boundary when no distribution/CDN
provider is configured. Operators must not bypass that boundary with ad-hoc unsigned uploads.
