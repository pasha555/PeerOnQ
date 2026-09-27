# PeerOnQ open-source strategy

PeerOnQ is source-available under the repository's MIT `LICENSE` and is intended to remain usable
without a runtime commercial entitlement.

## Runtime freedom

- No license key, activation, subscription, payment, seat, or online-entitlement check is required.
- LAN pairing works accountlessly. Internet operation can be self-hosted with the provided
  signaling, TURN, and cloud components.
- There is no mandatory external AI service or proprietary cloud dependency.
- Accounts are allowed only where identity, organizations, shared address books, managed support,
  or administration logically require them; an account must never become hidden activation.
- Telemetry is default-off/local-first for the native product. Diagnostics require explicit user
  action and use documented redaction/retention controls.
- PeerOnQ imposes no artificial product-level session duration, resolution, speed, or file-size
  restrictions. Resource/security limits may protect systems and are documented as such.

## Legal license versus distribution trust

The MIT license grants source-code rights; it does not make an arbitrary binary trustworthy.
Unsigned local development builds are supported and must identify themselves as development
artifacts. Production distribution requires Authenticode, timestamping, signed update manifests,
checksums, SBOMs, and provenance. Those controls authenticate an artifact; they do not activate or
commercially gate the software.

## Contributor and dependency policy

- Contributions use Developer Certificate of Origin sign-off (`Signed-off-by`) unless maintainers
  adopt a different public governance decision.
- Dependencies require a compatible license, pinned/reviewed version, vulnerability review, and
  notice/SBOM coverage.
- Local scripts are authoritative. GitHub Actions may automate them but cannot be the only way to
  build, test, scan, or package the project.
- Enterprise services may be sold around support, hosting, or operations, but the repository must
  not contain dormant activation or feature-lock paths.
