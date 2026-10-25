# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

> Status: the signing pipeline is in place; the SignPath Foundation application is pending. Until it
> is accepted, published Windows installers remain unsigned and are labeled that way on the
> download page. See [docs/signing/APPLY.md](docs/signing/APPLY.md).

## What is signed

- The Windows installers (`PeerOnQ-<version>-public-pilot-x64.msi` and `-arm64.msi`).
- Inside each installer, PeerOnQ's own executables and assemblies (`PeerOnQ.exe`, `PeerOnQ.dll`,
  `PeerOnQ.*.dll`, `PeerOnQ.SessionBroker.exe`).

Third-party files are not re-signed. The Windows App SDK runtime keeps Microsoft's signature, and
other third-party binaries keep whatever signature their vendors provide. The full component list
and licenses are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), which ships in every installer.

## How a signed release is produced

1. A maintainer pushes a tag `vX.Y.Z` that matches the version in `Directory.Build.props`.
2. GitHub Actions ([release-windows.yml](.github/workflows/release-windows.yml)) builds the x64 and
   ARM64 installers from that tagged commit, on GitHub-hosted runners, from this repository's source
   and its pinned package feeds only, and runs the unit tests.
3. The unsigned installers are uploaded as a GitHub Actions artifact and submitted to SignPath with
   the [artifact configuration](.signpath/artifact-configuration.xml) committed in this repository.
4. Release signing requires **manual approval** by an approver in SignPath. Manual and test runs use
   SignPath's test certificate instead.
5. The workflow verifies every signature (`signtool verify /pa`, publisher, SHA-256 thumbprint,
   RFC 3161 timestamp) and attaches the signed installers with `SHA256SUMS.txt` to a draft GitHub
   Release, which a maintainer publishes.

No signing key is stored in this repository or on a developer machine. The SignPath API token and
organization ID are GitHub secrets/variables.

## Team roles

| Role | Members | Responsibility |
| --- | --- | --- |
| Committers and reviewers | [pasha555](https://github.com/pasha555) | May merge into `main` and push release tags. |
| Approvers | [pasha555](https://github.com/pasha555) | Approve each release signing request in SignPath. |

Changes to this list are made by pull request to this file.

## Privacy

PeerOnQ transfers information over the network only for the purposes below, and collects no data
beyond them:

- **Signaling:** the client keeps an encrypted connection (WSS) to the configured PeerOnQ signaling
  server to register its device ID and public keys and to set up sessions (requests, permission
  answers, connection negotiation). Screen, input, clipboard, sound, voice and file content travel
  end-to-end encrypted between the two devices, directly or through the TURN relay when no direct
  path exists; the relay cannot read them.
- **Cloud service:** installation presence (online state, app version) and session lifecycle
  events (session ID, role, permission mode, connection path, timing, end reason, reconnect count).
  These never contain screen, input, clipboard, file or voice content, or credentials.
- **Updates:** when verified updates are enabled, the client downloads a signed update manifest and
  installers from the configured update server. They are disabled in current builds.
- **Diagnostics:** a diagnostic upload happens only when the user explicitly requests it and
  consents; the on-demand Network Doctor contacts the configured signaling, API, STUN and TURN
  endpoints to measure reachability.

See the [privacy overview](https://peeronq.com/privacy) (also in the website source,
`artifacts/peeronq/src/pages/PrivacyPage.tsx`).
