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

The 2026-08-18 Phase 6.5 findings were rechecked against the restored packages on 2026-09-27:

- SIPSorcery and SIPSorceryMedia.Abstractions 10.0.15 both ship `LICENSE.md` with BSD-3-Clause
  text plus additional geographic/use restrictions. These packages must not be described as
  plain BSD-3-Clause. The package license's separate FFmpeg section does not by itself establish
  that PeerOnQ ships FFmpeg; the final runtime inventory must determine included components.
- SIPSorceryMedia.Encoders 10.0.4 declares BSD-3-Clause, but its NuGet archive omits the separate
  libvpx notice and patent-grant files. Its x64/x86 `vpxmd.dll` files match the examined upstream
  binaries; their exact libvpx source revision, build and security patch status remain unverified.
  Upstream [libvpx license](https://github.com/webmproject/libvpx/blob/main/LICENSE) and
  [patent terms](https://github.com/webmproject/libvpx/blob/main/PATENTS) are reference material,
  not proof of the source used to build those binaries.
- WixToolset.UI.wixext 6.0.1 embeds an Open Source Maintenance Fee Agreement for use of official
  binary releases. Its revenue applicability, payment or exemption needs release-owner review;
  the agreement separately preserves source/self-build and qualifying redistribution rights
  under the underlying open-source license. It is not a blanket ban on commercial distribution.

These release gates remain open. Qualified legal/release-owner approval is outside the codebase
and has not been obtained by this review. Exact package hashes, official sources, current security
findings and technical migration options are recorded in [DEPENDENCIES.md](DEPENDENCIES.md) and
[the license audit](docs/competitive/THIRD_PARTY_LICENSE_AUDIT.md).

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
