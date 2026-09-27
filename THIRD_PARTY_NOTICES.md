# Third-party notices

PeerOnQ incorporates open-source components. Copyright and license terms remain with their
respective authors. This file is an orientation aid; the machine-generated SBOM and the license
files shipped with an exact release artifact are authoritative for that artifact.

Major dependency families include Microsoft .NET/ASP.NET Core/Windows App SDK, Avalonia, and SDK tooling;
SQLite, PostgreSQL/Npgsql, Redis/StackExchange.Redis; Serilog and OpenTelemetry; SIPSorcery and
Vortice; React, Vite, Tailwind CSS, Radix/shadcn-derived components and Vitest; Express; xUnit and
Microsoft test tooling; WiX; and container images/services for Nginx, coturn, PostgreSQL, Redis,
OpenTelemetry Collector, Prometheus, Grafana, Loki and Tempo.

Each component is governed by the license attached to its pinned package/image/source. The PeerOnQ
MIT license does not replace those terms, trademark rules, platform SDK terms, or notices.

The 2026-08-18 Phase 6.5 audit found unresolved distribution terms in the restored SIPSorcery
10.0.15 license, incomplete notice/source provenance for the `vpxmd.dll` embedded by
SIPSorceryMedia.Encoders 10.0.4, and WiX UI extension terms that require release-owner review.
These are release blockers, not implied grants. Details and exact versions are recorded in
`DEPENDENCIES.md` and `docs/competitive/THIRD_PARTY_LICENSE_AUDIT.md`.

## Release requirement

The release builder generates and validates an SPDX 2.2 SBOM from the complete runtime tree.
`scripts/windows/new-phase5-local-evidence.ps1` also emits machine-readable .NET dependencies,
production pnpm licenses, vulnerability reviews, checksums, and SLSA v1-shaped local provenance.
Hosted automation is optional and must reproduce these local gates. Before distribution, release
owners must:

1. resolve package/image metadata and hashes from lockfiles/manifests;
2. review new or changed licenses for compatibility and notice/source obligations;
3. archive the validated SBOM, license/notice bundle, checksums and provenance with the artifact;
4. reject unknown, missing, disallowed, or unverifiable license metadata rather than guessing;
5. update this file when a material dependency family or distribution obligation changes.

Current dependency manifests are `Directory.Packages.props`, `pnpm-lock.yaml`, workspace
`package.json` files, .NET project files, Dockerfiles and Compose definitions. Unknown or
`NOASSERTION` license metadata remains a release-review blocker until the package's authoritative
license is verified; the generator never guesses a grant.
