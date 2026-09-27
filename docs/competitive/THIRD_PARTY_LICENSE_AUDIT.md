# Third-party license audit: media/security scope

Audit date: 2026-08-18. This first pass uses pinned manifests plus the exact restored NuGet package
metadata/files. The release SBOM remains authoritative for a concrete artifact.

The production pnpm license inventory completed successfully. The repository vulnerability and
secret scans also passed. An exact-artifact SPDX SBOM/native-runtime inventory was deliberately not
reported as passed because its generator requires a concrete MSI plus matching application payload,
and the distribution findings below must be resolved before a release claim.

| Component | Version | Package-declared/embedded terms | Current decision |
| --- | --- | --- | --- |
| BouncyCastle.Cryptography | 2.6.2 | MIT | Compatible for current use; retain notice and security review |
| Vortice.Direct3D11 / Vortice.DXGI | 3.8.3 | MIT | Compatible; Microsoft platform/API terms remain separate |
| Serilog | 4.4.0 | Apache-2.0 | Compatible with notice obligations |
| SIPSorceryMedia.Encoders | 10.0.4 | NuSpec `BSD-3-Clause`; package embeds `vpxmd.dll` | libvpx binary/source and notice provenance must be included in exact-artifact review |
| SIPSorcery / SIPSorceryMedia.Abstractions | 10.0.15 | Package uses an embedded `LICENSE.md`: BSD-3 text plus an additional field-of-use/geographic restriction | **Distribution blocker:** not plain BSD-3/OSI terms; legal/maintainer resolution or a reviewed replacement is required |
| WiX UI extension binary | 6.0.1 | Embedded Open Source Maintenance Fee Agreement; source is described as MS-RL | Commercial/revenue distribution terms require release-owner review |
| Microsoft Windows App SDK / SDK tools | pinned centrally | Microsoft package/platform terms | Redistribution must follow Microsoft runtime terms |

## Findings

1. The restored SIPSorcery 10.0.15 package cannot be recorded simply as BSD-3 because its actual
   license file adds a use restriction. Downgrading is not an acceptable automatic fix: earlier
   versions were replaced for security reasons and a critical media dependency needs compatibility
   tests. No public release should proceed until this is resolved.
2. `SIPSorceryMedia.Encoders` embeds native `vpxmd.dll` but the package root does not include a
   separate libvpx notice. The exact upstream source, build options, patent/redistribution position
   and notice must be archived with the release evidence.
3. No GPL/FFmpeg binary was found in the inspected current media package. That is not a statement
   about every transitive/runtime artifact; the final SBOM/native inventory remains mandatory.
4. PeerOnQ's own license remains MIT. Third-party terms are not replaced by PeerOnQ's MIT grant.

## Required release gate

- regenerate SPDX/dependency/native-binary evidence for the exact final runtime tree;
- run vulnerability, secret and license scans;
- reject unknown/`NOASSERTION` items rather than guessing;
- retain all required notices and source/offer obligations;
- obtain qualified legal review for the two restricted/fee-bearing package findings;
- do not publish while the distribution blocker is unresolved.

The dependency entry point is `DEPENDENCIES.md`; the release-facing orientation notice is
`THIRD_PARTY_NOTICES.md`. PeerOnQ's `LICENSE` remains unchanged.
