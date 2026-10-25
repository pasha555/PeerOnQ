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

Source update, 2026-10-25: the WebRTC stack is the maintained derivative `PeerOnQ.SIPSorcery`
8.0.14.1, built in CI from SIPSorcery **8.0.14** (tag `v8.0.14`, commit
`08d3a0271dded6aaebc3183f963bc820b099c552`, released 2025-05-08) plus PeerOnQ's reviewed patches in
[`eng/sipsorcery`](eng/sipsorcery). 8.0.14 is the last SIPSorcery release whose `LICENSE.md` is
BSD-3-Clause without the "Additional Restriction" that upstream added on 2025-05-18 (commit
`004f72024151`); every later release, including the 10.0.x line PeerOnQ used before, carries that
non-OSI restriction. SIPSorceryMedia.Abstractions is **8.0.10** (commit
`8a27b7f8ad4fcc9ab0c64e7c22e77d05d811c09c`) and SIPSorceryMedia.Encoders is **8.0.7** (commit
`a12d06a05c4e4e9d588b7597e4ef1c1e82f6e445`); both license files are plain BSD-3-Clause. No shipped
SIPSorcery component carries the restriction.

The SIPSorcery 8.0.14 license file also contains an informational "DTLS & SRTP Implementation
Notice": parts of its DTLS/SRTP code derive from Jitsi SRTP (Apache-2.0), Minisip (LGPL) and
RestComm media-core (AGPL-3.0). All three are OSI-approved licenses. PeerOnQ publishes the complete
corresponding source of the shipped `SIPSorcery.dll` (the pinned upstream archive plus every patch),
and the license file ships unchanged with every application as `ThirdParty/SIPSorcery/LICENSE.md`
beside the patches and the build provenance. Fixes for upstream advisories that affect 8.0.14 were
written by PeerOnQ from the published problem descriptions; no code from the restricted releases is
used. The source-build/advisory contract is in [DEPENDENCIES.md](DEPENDENCIES.md).

SIPSorcery 8.0.14 depends on Portable.BouncyCastle 1.9.0 for DTLS. Its license is the Bouncy Castle
License (MIT text; copyright (c) 2000-2021 The Legion of the Bouncy Castle Inc.; see
[crypto/License.html at release-1.9.0](https://github.com/bcgit/bc-csharp/blob/release-1.9.0/crypto/License.html)).
Its package metadata has only a license URL, so the license is recorded here from the upstream file.
The package is no longer maintained and has moderate-severity advisories; see
[Known limitations](docs/KNOWN_LIMITATIONS.md).

Source update, 2026-10-23: the maintainers adopted Concentus 2.2.2 for two-way Opus voice. It is a
managed port of the Opus reference codec distributed under the Opus BSD-3-Clause license
(copyright Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation, Jean-Marc Valin,
Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry, Logan Stromberg and other holders); its
package `LICENSE` ships beside every application as `ThirdParty/Concentus/LICENSE`, and the build
fails if it is missing. PeerOnQ disables its optional native-library probe, so
no unreviewed native Opus binary is loaded. Opus patent terms are reference material for release
review.

The Linux viewer preview uses the desktop distribution's separately installed `libvpx.so.9`
from fixed system directories. Its portable payload excludes Windows `vpxmd.dll`; install and
update the Linux library through the distribution package manager.

Source update, 2026-10-25: the Windows `vpxmd.dll` is built in CI from libvpx v1.17.0 (BSD-3-Clause;
commit `6df3ec34557879fff673706f4a1d9fbd0f3a6f0e`, archive SHA-256
`1020f184046187baa2985dbde38e0691f49c44088bca7a1842b0236c6081dc0a`) for x64 and ARM64 with the
flags in [`eng/libvpx/libvpx.json`](eng/libvpx/libvpx.json). libvpx's `LICENSE` and `PATENTS` ship
beside every Windows application as `ThirdParty/libvpx/`, and each build records its source hash,
toolchain and output hash in `provenance.json`. The prebuilt DLL inside SIPSorceryMedia.Encoders is
never shipped.

The 2026-08-18 dependency review findings were rechecked against the restored packages on 2026-09-27:

- (Historical, resolved 2026-10-25.) SIPSorcery and SIPSorceryMedia.Abstractions 10.0.x ship
  `LICENSE.md` with BSD-3-Clause text plus additional geographic/use restrictions and are no longer
  used; see the source update above.
- (Historical, resolved 2026-10-25.) The prebuilt `vpxmd.dll` in SIPSorceryMedia.Encoders had no
  verifiable libvpx source revision and no bundled libvpx notices; PeerOnQ now builds the DLL from
  pinned libvpx source and ships libvpx's own LICENSE and PATENTS files.
- WixToolset.UI.wixext 6.0.1 embeds an Open Source Maintenance Fee Agreement for use of official
  binary releases. The fee applies to revenue-generating use; PeerOnQ is a non-revenue project and
  the release owner recorded that decision in [the license audit](docs/THIRD_PARTY_LICENSE_AUDIT.md).
  The agreement separately preserves source/self-build and redistribution rights under the
  underlying open-source license.

Qualified legal approval is outside the codebase and has not been obtained by this review. Exact package hashes, official sources, current security
findings and technical migration options are recorded in [DEPENDENCIES.md](DEPENDENCIES.md) and
[the license audit](docs/THIRD_PARTY_LICENSE_AUDIT.md).

## Windows client component inventory (0.9.89)

This file ships in every Windows MSI and Linux archive (`THIRD_PARTY_NOTICES.md`, beside
`LICENSE.txt`); the installers and the Linux package gate validate its presence. The inventory below was
updated on 2026-10-25 from `Directory.Packages.props`, the restored package metadata and the x64
Release publish tree. Each component's own license file in its package is authoritative.

| Component | Version | License | Shipped as | Notes |
| --- | --- | --- | --- | --- |
| PeerOnQ (all `PeerOnQ.*` assemblies, `RestartAgent` excluded) | 0.9.89 | MIT | `PeerOnQ.exe`, `PeerOnQ*.dll`, `PeerOnQ.SessionBroker.exe` | This repository |
| PeerOnQ.SIPSorcery (SIPSorcery 8.0.14 + PeerOnQ patches) | 8.0.14.1 | BSD-3-Clause (with the informational DTLS/SRTP notice above) | `SIPSorcery.dll`, `ThirdParty/SIPSorcery/` | Built from pinned source in CI; no use restriction |
| SIPSorceryMedia.Abstractions | 8.0.10 | BSD-3-Clause | managed DLL | Last release before the restriction |
| SIPSorceryMedia.Encoders | 8.0.7 | BSD-3-Clause | managed DLL | Managed VP8 bindings only; the package's prebuilt native DLL is not shipped |
| libvpx | 1.17.0 | BSD-3-Clause, with the WebM patent grant | `vpxmd.dll`, `ThirdParty/libvpx/LICENSE`, `ThirdParty/libvpx/PATENTS` | Built in CI from pinned source for x64 and ARM64 |
| Portable.BouncyCastle | 1.9.0 | Bouncy Castle License (MIT) | managed DLL | Via SIPSorcery (DTLS); nuspec has a license URL only, text recorded above |
| DnsClient | 1.8.0 | Apache-2.0 | managed DLL | Via SIPSorcery |
| SIPSorcery.WebSocketSharp | 0.0.1 | MIT | managed DLL | Via SIPSorcery |
| BouncyCastle.Cryptography | 2.7.0 | MIT | managed DLL | PeerOnQ post-quantum layer |
| Concentus | 2.2.2 | BSD-3-Clause (Opus) | managed DLL, `ThirdParty/Concentus/LICENSE` | |
| Vortice.Direct3D11 / DXGI / MediaFoundation, SharpGen.Runtime | 3.8.3 / 2.4.2-beta | MIT | managed DLLs | |
| NAudio.Core / NAudio.Wasapi | 3.2.0 | MIT | managed DLLs | |
| Microsoft.Data.Sqlite, SQLitePCLRaw | 10.0.13 / 3.0.4 | MIT / Apache-2.0 | managed DLLs, `e_sqlite3.dll` | SQLite itself is public domain |
| Serilog family | 4.4.0 and companions | Apache-2.0 | managed DLLs | |
| Microsoft .NET runtime and Microsoft.Extensions.* | 10.0.13 | MIT | self-contained runtime | |
| Windows App SDK components (WinUI 3 runtime, WebView2 loader) | 1.8.251106002 | Microsoft Software License Terms (redistributable platform runtime); WebView2 loader BSD-style | self-contained runtime DLLs, `WebView2Loader.dll` | Vendor-signed platform runtime; the unused AI, ML (ONNX Runtime, DirectML), Imaging/Vision and Widgets binaries are removed at build time |
| WiX Toolset UI extension | 6.0.1 | MS-RL source; binary release under the Open Source Maintenance Fee terms | dialog resources inside the MSI | Build-time tool; owner review recorded above |

Server-only components (ASP.NET Core, EF Core, Npgsql (PostgreSQL License), StackExchange.Redis
(MIT), OpenTelemetry (Apache-2.0)) and the web applications' npm packages are covered by the
release SBOM and are not part of the Windows installer.

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
