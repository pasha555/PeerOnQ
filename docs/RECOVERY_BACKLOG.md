# PeerOnQ recovery backlog

Priority means user/security/release impact, not an assertion that an unobserved defect exists.

## P0 — next gate / release blockers

| Item | Evidence now | Smallest closure |
|---|---|---|
| Two-device remote image correctness (wrong colors, tiling, bands, tearing, lifetime) | build/E2E/loopback pass; no R0 physical run | instrument and run deterministic visual corpus on two physical devices; fix any reproduced defect before Phase 1 passes |
| Physical reconnect/renegotiation stability and permission non-expansion | automated state/replay/restart coverage passes; public path unverified | separate-network interruption matrix with logs, nominated pair and bounded reconnect measurement |
| Native install/run gate | native Release build passes; R0 did not install current artifact on clean machines | build current x64 MSI, clean install/repair/upgrade/uninstall/launch; physical ARM64 install/launch |
| Public MSI route exact bytes | local Downloads validation exists; no public endpoint | fetch without browser automation, assert status/content type/content length/hash/MSI parse, then install exact bytes |
| Secret/session-content absence | working-tree scan and redaction tests pass | hosted full-history scan plus independent log/telemetry inspection during physical sessions |

There is no unresolved internally reproducible P0 build/test defect at the R0 gate. Any failed case
above becomes an active P0 with its observed evidence and root cause.

## P1 — required before an official beta

- Establish one version source for native assembly, MSI, update manifest, website/download metadata,
  API and package surfaces; add a drift invariant.
- Obtain production signing/timestamp inputs and execute exact signed artifact, SBOM, checksum,
  provenance, rollback and hosted-update verification.
- Complete public Phase 3/6 DNS, TLS, NAT, staging, backup/restore, alert and multi-host tests.
- Reconcile historical phase reports with `docs/CURRENT_STATE.md` without rewriting historical facts.
- Upgrade/evaluate SPDX 3.0 output; add CycloneDX only when a consumer/use case is defined.
- Complete WCAG 2.2 AA manual verification and remediate failures.
- Run CodeQL/full-history scanning and independent security/privacy review.

## P2 — quality and optimization

- Resolve scoped formatting debt and teach Prettier how to exclude/parse WiX without a formatting sweep.
- Code-split the web product and measure the change; do not tune only to silence a warning.
- Standardize Node 24 in developer/CI setup and remove the Node 25 test-runner warning.
- Evaluate OpenAPI 3.1.2/3.2 generator compatibility before changing `openapi.yaml`.
- Add production hardware codec/GPU-loss/dirty-region/occlusion support only with measurements.
- Design a versioned dual-prefix device-ID protocol only if the benefit outweighs migration and
  prefix-free-input ambiguity; do not mutate deployed IDs.
- Produce a fair, reproducible competitive benchmark before making superiority claims.
