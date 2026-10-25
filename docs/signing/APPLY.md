# Applying to SignPath Foundation

Instructions for the maintainer. The application itself must be submitted by the maintainer at
<https://signpath.org/apply.html>, because the SignPath account and every release approval are
personal.

## Eligibility checklist (audited 2026-10-25)

- [x] **OSI-approved project license:** [MIT](../../LICENSE) covers every project in this repository
  (Windows client, Linux/Android/Apple viewers, server, website, scripts). No file claims another
  license.
- [ ] **OSI-approved licenses for all components — blocked.** SIPSorcery 10.0.17 and
  SIPSorceryMedia.Abstractions 10.0.17 (and the maintained `PeerOnQ.SIPSorcery` 10.0.17.6 derivative
  built from them) ship BSD-3-Clause text plus "§2 Additional Use Restriction", a geographic
  restriction that conflicts with OSD criteria 5 and 6. The repository already records this as a
  distribution blocker (`docs/THIRD_PARTY_LICENSE_AUDIT.md`). Options before applying:
  1. rebase the maintained derivative on the last SIPSorcery release without the restriction and port
     the seven patches in `eng/sipsorcery/`;
  2. ask the SIPSorcery maintainer to relicense;
  3. replace the WebRTC stack.
- [x] **No proprietary components** apart from platform runtimes: the Windows App SDK runtime is
  Microsoft-signed platform runtime under Microsoft's license terms; explain it as a system library
  in the application. Recommended before applying: trim its unused ONNX Runtime, DirectML and Widgets
  binaries from the installer.
- [ ] **Clear origin of bundled binaries — open item.** `vpxmd.dll` (libvpx, BSD-3-Clause) comes
  prebuilt from SIPSorceryMedia.Encoders 10.0.4; its source revision is unverified. Building libvpx
  from pinned source in CI would close this.
- [x] **Actively maintained and released:** releases through 0.9.88 (see the release notes and the
  GitHub releases page).
- [x] **Fully automated build from the repository:**
  [release-windows.yml](../../.github/workflows/release-windows.yml) builds both MSIs on
  GitHub-hosted runners from the tagged commit; the maintained SIPSorcery derivative is built in the
  same job from hash-pinned upstream source and the patches in `eng/sipsorcery/`. No locally copied
  binaries are used.
- [x] **Published code signing policy:** [CODE_SIGNING_POLICY.md](../../CODE_SIGNING_POLICY.md) and
  the website page `/code-signing` (linked from the footer and the download section's installer
  details).
- [x] **Third-party notices:** [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md) ships in every
  MSI.

Package metadata gaps to mention: Common.Logging (Apache-2.0 upstream) and Makaretu.Dns (MIT upstream)
have no license field in their nuspec; the license texts were recorded by hand.

## Links for the application form

- Repository: <https://github.com/pasha555/PeerOnQ>
- Website: <https://peeronq.com>
- Code signing policy: <https://github.com/pasha555/PeerOnQ/blob/main/CODE_SIGNING_POLICY.md> and
  <https://peeronq.com/code-signing> (after the website patch is deployed)
- Latest release: <https://github.com/pasha555/PeerOnQ/releases>
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
   publish the draft GitHub release the workflow creates.
8. Copy the release signer's SHA-256 thumbprint from the job summary into the repository variable
   `PEERONQ_PUBLISHER_CERTIFICATE_SHA256` (`;`-separated, so a renewed certificate can be added before
   the old one expires). The next release embeds it; verified updates stay disabled until the update
   manifest URL, key ID and public key are configured too (a separate task).
9. Switch the download page from "Not signed" to the signed installers once the signed release is
   published.
