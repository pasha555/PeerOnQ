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

Source update, 2026-10-18: the maintainers adopted a maintained derivative; the source selects `PeerOnQ.SIPSorcery` 10.0.17.6,
derived from official SIPSorcery 10.0.17 with seven pinned patches for DTLS receive wakeup, bounded
SCTP acknowledgements, sender/receiver flow control, single-owner UDP receive admission and first-packet RTCP transit initialization. Its package
retains the exact upstream license and source provenance; PeerOnQ's MIT license does not override
those terms. Maintenance approval is not redistribution/legal approval. Abstractions is 10.0.17;
the source-build/advisory contract is in [DEPENDENCIES.md](DEPENDENCIES.md).

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
update the Linux library through the distribution package manager. This does not resolve the
bundled Windows codec's provenance or the maintained transport's redistribution gates.

The 2026-08-18 dependency review findings were rechecked against the restored packages on 2026-09-27:

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
[the license audit](docs/THIRD_PARTY_LICENSE_AUDIT.md).

## Windows client component inventory (0.9.88)

This file ships in every Windows MSI and Linux archive (`THIRD_PARTY_NOTICES.md`, beside
`LICENSE.txt`); the installers and the Linux package gate validate its presence. The inventory below was
taken on 2026-10-25 from `Directory.Packages.props`, the restored package metadata and the x64
Release publish tree. Each component's own license file in its package is authoritative.

| Component | Version | License | Shipped as | Notes |
| --- | --- | --- | --- | --- |
| PeerOnQ (all `PeerOnQ.*` assemblies, `RestartAgent` excluded) | 0.9.88 | MIT | `PeerOnQ.exe`, `PeerOnQ*.dll`, `PeerOnQ.SessionBroker.exe` | This repository |
| PeerOnQ.SIPSorcery (from SIPSorcery 10.0.17 + 7 patches) | 10.0.17.6 | BSD-3-Clause text **plus a geographic use restriction** | `SIPSorcery.dll` | Not OSI-approved as shipped; see the blocker above and `docs/signing/APPLY.md` |
| SIPSorceryMedia.Abstractions | 10.0.17 | Same as SIPSorcery | managed DLL | Same restriction |
| SIPSorceryMedia.Encoders | 10.0.4 | BSD-3-Clause | managed DLL, `vpxmd.dll` (libvpx) | libvpx source revision of the bundled binary unverified |
| Common.Logging / Common.Logging.Core | 3.4.1 | Apache-2.0 (upstream; nuspec has no license field) | managed DLLs | Via SIPSorcery |
| Makaretu.Dns / Makaretu.Dns.Multicast | 2.0.1 / 0.27.0 | MIT (upstream; nuspec has no license field) | managed DLLs | Via SIPSorcery |
| SimpleBase | 1.3.1 | Apache-2.0 | managed DLL | Via SIPSorcery |
| DnsClient | 1.8.0 | Apache-2.0 | managed DLL | Via SIPSorcery |
| SIPSorcery.WebSocketSharp | 0.0.1 | MIT | managed DLL | Via SIPSorcery |
| BouncyCastle.Cryptography | 2.7.0 | MIT | managed DLL | |
| Concentus | 2.2.2 | BSD-3-Clause (Opus) | managed DLL, `ThirdParty/Concentus/LICENSE` | |
| Vortice.Direct3D11 / DXGI / MediaFoundation, SharpGen.Runtime | 3.8.3 / 2.4.2-beta | MIT | managed DLLs | |
| NAudio.Core / NAudio.Wasapi | 3.2.0 | MIT | managed DLLs | |
| Microsoft.Data.Sqlite, SQLitePCLRaw | 10.0.13 / 3.0.4 | MIT / Apache-2.0 | managed DLLs, `e_sqlite3.dll` | SQLite itself is public domain |
| Serilog family | 4.4.0 and companions | Apache-2.0 | managed DLLs | |
| Microsoft .NET runtime and Microsoft.Extensions.* | 10.0.13 | MIT | self-contained runtime | |
| Microsoft.WindowsAppSDK (WinUI 3 runtime, WebView2 loader) | 1.8.251106002 | Microsoft Software License Terms (runtime); WebView2 loader BSD-style | self-contained runtime DLLs, `WebView2Loader.dll` | Vendor-signed platform runtime; includes unused ONNX Runtime/DirectML/Widgets binaries that a release could trim |
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
