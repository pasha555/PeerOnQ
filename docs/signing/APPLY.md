# Applying to SignPath Foundation

Instructions for the maintainer. The application itself must be submitted by the maintainer at
<https://signpath.org/apply.html>, because the SignPath account and every release approval are
personal.

## Eligibility checklist (audited 2026-10-25, release 0.9.89)

- [x] **OSI-approved project license:** [MIT](../../LICENSE) covers every project in this repository
  (Windows client, Linux/Android/Apple viewers, server, website, scripts). No file claims another
  license.
- [x] **OSI-approved licenses for all components.** The WebRTC stack is the maintained
  `PeerOnQ.SIPSorcery` 8.0.14.1 derivative, built from SIPSorcery 8.0.14 (tag `v8.0.14`, commit
  `08d3a0271dded6aaebc3183f963bc820b099c552`), the last release published under plain
  BSD-3-Clause. SIPSorcery added a non-OSI "Additional Restriction" to its license on 2025-05-18
  (upstream commit `004f72024151`); 8.0.14 predates it. SIPSorceryMedia.Abstractions 8.0.10 and
  SIPSorceryMedia.Encoders 8.0.7 are plain BSD-3-Clause too. Evidence:
  - [eng/sipsorcery/upstream.json](../../eng/sipsorcery/upstream.json) pins the source archive and
    license hashes, the 23 PeerOnQ patches and one added file; the builder refuses any other input.
  - The shipped license text: `ThirdParty/SIPSorcery/LICENSE.md` in every MSI (also packed into the
    derivative NuGet package), with the patches beside it.
  - [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md) and
    [docs/THIRD_PARTY_LICENSE_AUDIT.md](../../docs/THIRD_PARTY_LICENSE_AUDIT.md) (2026-10-25
    remediation table).
  - SIPSorcery 8.0.14 includes an informational DTLS/SRTP notice naming code derived from Jitsi
    (Apache-2.0), Minisip and RestComm. It is reproduced in the notices; the released SIPSorcery
    8.0.14 package itself is licensed BSD-3-Clause.
- [x] **Advisories that affect 8.0.14 are fixed in the derivative**, by patches written for PeerOnQ
  (not ported from the restricted releases): GHSA-28gm-jrmw-xx93, GHSA-jwjp-4649-v8jp,
  GHSA-mc26-5x96-8x52, GHSA-mwf8-6m4x-pgmm and GHSA-qmvg-569h-hqrh, listed under
  `mitigatedAdvisories` in `upstream.json` and checked by
  [test-sipsorcery-upstream-advisories.ps1](../../scripts/security/test-sipsorcery-upstream-advisories.ps1).
- [x] **Disclosed:** SIPSorcery 8.0.14's DTLS code depends on Portable.BouncyCastle 1.9.0 (Bouncy
  Castle License, MIT). It is no longer maintained; see
  [docs/KNOWN_LIMITATIONS.md](../../docs/KNOWN_LIMITATIONS.md).
- [x] **No proprietary components** apart from platform runtimes: the Windows App SDK runtime
  (WinUI 3) is Microsoft-signed platform runtime under Microsoft's redistributable license terms;
  explain it as a system library, like the .NET runtime. Its unused AI, ML (ONNX Runtime, DirectML),
  Imaging/Vision and Widgets binaries are removed at build time (x64 app payload 235.8 MB to
  191.6 MB).
- [x] **Clear origin of bundled binaries.** `vpxmd.dll` (libvpx 1.17.0, BSD-3-Clause) is built from
  pinned source in CI for x64 and ARM64 by
  [native-libvpx.yml](../../.github/workflows/native-libvpx.yml) and
  [build-libvpx.ps1](../../scripts/windows/build-libvpx.ps1) (commit
  `6df3ec34557879fff673706f4a1d9fbd0f3a6f0e`, archive SHA-256 pinned in
  [eng/libvpx/libvpx.json](../../eng/libvpx/libvpx.json)); the build fails if the prebuilt DLL from
  SIPSorceryMedia.Encoders would ship. libvpx's LICENSE and PATENTS ship in `ThirdParty/libvpx`.
- [x] **Actively maintained and released:** the
  [0.9.89 release](https://github.com/pasha555/PeerOnQ/releases/tag/v0.9.89) was built and
  published by GitHub Actions from the tagged commit, with SHA-256 checksums
  (`SHA256SUMS.txt`). It is a pre-release marked "Unsigned. Code signing via SignPath Foundation is
  pending." Earlier versions are in the [changelog](../../CHANGELOG.md).
- [x] **Fully automated build from the repository:**
  [release-windows.yml](../../.github/workflows/release-windows.yml) builds both MSIs on
  GitHub-hosted runners from the tagged commit. The SIPSorcery derivative and libvpx are built in the
  same run from hash-pinned upstream source. No locally copied binaries are used.
- [x] **Continuous integration is green** on `main`: Quality (build, unit and integration tests,
  web workspace, secret scan), CodeQL (C# and JavaScript) and the native libvpx builds. Runners and
  actions are pinned (`windows-2025`, `ubuntu-24.04`, actions by commit SHA on Node 24).
- [x] **Published code signing policy:** [CODE_SIGNING_POLICY.md](../../CODE_SIGNING_POLICY.md) and
  the website page <https://peeronq.com/code-signing>, linked from the README, the release notes and
  the download section.
- [x] **Privacy policy:** <https://peeronq.com/privacy>, linked from the code signing policy and the
  release notes.
- [x] **Download page matches the release:** the website serves the same MSI files as the GitHub
  release (identical SHA-256; the server bundle embeds the release assets) and, like the release
  notes and the README, says: "Windows installers are code-signed through SignPath.io with a
  certificate by SignPath Foundation (free code signing for open source). Until the application is
  accepted, installers are published unsigned.", with a link to the code signing policy.
- [x] **Third-party notices:** [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md) ships in every
  MSI.

Package metadata notes: Portable.BouncyCastle's nuspec has a license URL only; its license text is
recorded by hand in the notices. (Common.Logging and Makaretu.Dns were dependencies of the 10.x
stack and are no longer shipped.)

## Links for the application form

- Repository: <https://github.com/pasha555/PeerOnQ>
- Website: <https://peeronq.com>
- Code signing policy: <https://github.com/pasha555/PeerOnQ/blob/main/CODE_SIGNING_POLICY.md> and
  <https://peeronq.com/code-signing>
- Privacy policy: <https://peeronq.com/privacy>
- Latest release: <https://github.com/pasha555/PeerOnQ/releases/tag/v0.9.89>
- Build workflow: `.github/workflows/release-windows.yml`
- Artifact configuration: `.signpath/artifact-configuration.xml`

## After acceptance

1. In SignPath, create the project with slug `PeerOnQ` (or set the repository variable
   `SIGNPATH_PROJECT_SLUG` to the slug you choose) and link the GitHub repository
   `pasha555/PeerOnQ` as its trusted build system (GitHub-hosted runners only).
2. Add the artifact configuration with slug `windows-msi-pair` and paste
   `.signpath/artifact-configuration.xml` (or set `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`).
3. Create two signing policies:
   - `test-signing`: SignPath test certificate, no approval (manual workflow runs);
   - `release-signing`: SignPath Foundation certificate, manual approval by the approvers listed in
     the code signing policy; restrict it to tag builds (`refs/tags/v*`).
4. Create an API token for a CI submitter user and add it to GitHub as the secret
   `SIGNPATH_API_TOKEN`. Add the organization ID as the repository variable
   `SIGNPATH_ORGANIZATION_ID`. Never commit either.
5. Optional: override the compiled endpoints with the repository variables `PEERONQ_SIGNALING_URL`,
   `PEERONQ_API_BASE_URL`, `PEERONQ_PRESENCE_URL`, `PEERONQ_UPDATE_BASE_URL`,
   `PEERONQ_DOWNLOADS_BASE_URL`, `PEERONQ_DIAGNOSTICS_BASE_URL` and `PEERONQ_RELEASE_REGION`; by
   default the workflow uses the public peeronq.com endpoints of the public-pilot builder.
6. Run the workflow manually once (test-signing) and check the job summary.
7. Push a tag `vX.Y.Z` that matches `Directory.Build.props`, approve the request in SignPath, and
   publish the draft GitHub release the workflow creates. Until signing is configured, a tag build
   publishes an unsigned pre-release instead.
8. Copy the release signer's SHA-256 thumbprint from the job summary into the repository variable
   `PEERONQ_PUBLISHER_CERTIFICATE_SHA256` (`;`-separated, so a renewed certificate can be added before
   the old one expires). The next release embeds it; verified updates stay disabled until the update
   manifest URL, key ID and public key are configured too (a separate task).
9. Switch the download page from "Not signed" to the signed installers once the signed release is
   published.
