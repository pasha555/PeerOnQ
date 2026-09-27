# PeerOnQ cross-platform capability and evidence matrix

Source/project/capability contracts reviewed on 2026-09-27. Build/test/package evidence retains
its original Phase 8 or 2026-08-26 scope below; it was not rerun for this documentation review.
This document is a truth ledger, not a roadmap-completion claim. A Linux server `.run` bundle is
deployment infrastructure and is **not** a Linux desktop client.

All four native app projects consume the canonical `Directory.Build.props` client version
(`0.9.66`); Android and Apple also consume bundle code `9066`. A matching source version is not a
published package or passed platform gate. `PeerOnQ.slnx` excludes the Android and Apple app
projects requiring their separate native workloads; shared platform tests do not compile those apps.

## Platform truth matrix

| Capability | Windows | Linux desktop | macOS | Android | iOS/iPadOS |
| --- | --- | --- | --- | --- | --- | --- |
| Host / sharer | `IMPLEMENTED_LOCAL_ONLY` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Viewer / controller | `IMPLEMENTED_LOCAL_ONLY` | `PARTIAL` | `PARTIAL` | `PARTIAL` | `PARTIAL` |
| Screen capture | `IMPLEMENTED_LOCAL_ONLY` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Input injection | `IMPLEMENTED_LOCAL_ONLY` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| File transfer | `IMPLEMENTED_LOCAL_ONLY` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Text clipboard | `IMPLEMENTED_LOCAL_ONLY` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Unattended access | `PARTIAL` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Background operation | `PARTIAL` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Hardware codec | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `PARTIAL` | `NOT_FOUND` |
| Installer / package | `IMPLEMENTED_LOCAL_ONLY` | `PARTIAL` | `NOT_FOUND` | `PARTIAL` | `NOT_FOUND` |
| Update | `IMPLEMENTED_LOCAL_ONLY` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Production code signing | `EXTERNALLY_BLOCKED` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` | `NOT_FOUND` |
| Native accessibility gate | `PARTIAL` | `PARTIAL` | `PARTIAL` | `PARTIAL` | `PARTIAL` |

Windows advertises its complete native-client manifest. Linux, Android and Apple compositions
advertise only viewer, screen-render, input-send, reconnect and mandatory hybrid-security
capabilities. Apple `PARTIAL` is source-only: the platform tests and build gates exist, but this
Windows environment has no Apple workload/Xcode, signed package or device evidence. No Apple
download is eligible for publication.

## Shared protocol foundation

`PeerOnQ.Transport.Protocol` is platform-neutral and contains signaling v3 version checks, bounded
capability declarations, optional-feature intersection, session capability requirements, safe
unknown-message handling and machine-readable incompatibility fields. `WebSocketSignalingClient`
sends an explicit native-client manifest and verifies the server echo, required server features and
optional-feature subset. `SignalingConnectionHandler` rejects a session before creating it when the
viewer or host did not advertise the exact required abilities.

The native compositions declare their abilities in
`PeerOnQ.Platform.Windows.WindowsClientCapabilityProfile.Create` and
`PeerOnQ.Platform.Linux.LinuxClientCapabilityProfile.Create`, and
`PeerOnQ.Platform.Android.AndroidClientCapabilityProfile.Create`, and
`PeerOnQ.Platform.Apple.AppleClientCapabilityProfile.Create`. Platform capture, input, secure
storage, permission, windowing and packaging remain outside the shared protocol layer.

Evidence:

- Source and entry point: `CapabilityNegotiator.TryNormalize`, `NegotiateServerFeatures` and
  `NegotiateSession`; `WebSocketSignalingClient.ConnectAsync`; `SignalingConnectionHandler.HandleHelloAsync`
  and `HandleSessionRequestAsync`.
- Automated tests: `CapabilityNegotiationTests`, signaling registration/validation flows and the
  shared `test-vectors/signaling-v3/hello-linux-viewer.json` byte vector.
- Proves: deterministic v3 JSON, older/newer version rejection, bounded declarations, dependency
  validation, implemented optional-feature intersection, safe unknown frame response and
  pre-session capability denial.
- Does not prove: third-party protocol interop, physical-device behavior,
  app-store acceptance or production signing.

## Windows evidence

- Host/capture/input source: `WindowsGraphicsCaptureSource`, `WindowsInputController`,
  `SessionCoordinator.StartSharingAsync`; native entry point is `PeerOnQ.App` through `AppServices`.
- Viewer/control source: `ViewerWindow`, `WebRtcMediaSession`, `RemoteInputSession`.
- File/clipboard source: `FileTransferService` and `ClipboardSyncService` on the approved ordered
  WebRTC data channel.
- Secure storage: `DpapiSecretStore` under the signed-in Windows user. PeerOnQ does not bypass UAC,
  UIPI or Secure Desktop.
- Package/update source: WiX MSI plus `UpdateService` signature/hash/publisher validation. Local
  unsigned development MSI evidence does not satisfy production Authenticode.
- Automated evidence: 84/84 signaling tests (plus one normal opt-in skip), 40/40 Windows end-to-end
  tests and the x64 WinUI Release build recorded in the Phase 8 report.
- Limitation: no Phase 8 two-physical-device, clean-VM, production certificate, manual screen-reader
  or hardware-codec evidence. Unattended operation stays inside the signed-in user process; there is
  no Windows service or secure-desktop bypass.

## Linux desktop implementation and remaining gate

`PeerOnQ.App.Linux` is an Avalonia viewer/controller that reuses the shared signaling, session,
hybrid-security and WebRTC layers without importing Windows platform code. It requests attended
view-only or full-control sessions hosted by Windows, renders decoded BGR24/I420 frames, maps Linux
keyboard events to the Windows host's bounded virtual-key protocol, and starts pointer/keyboard
forwarding only after explicit local enablement and remote focus acknowledgment.

- `PeerOnQ.Platform.Linux` declares the least-capability manifest and keeps capture fail-closed.
- `SecretToolDeviceSecretStore` stores private identity/audit material in the desktop Secret Service
  keyring, separates data-directory profiles, passes secret values only over stdin, and fails closed
  if the fixed-path `secret-tool` helper is unavailable.
- `scripts/linux/build-peeronq-linux-viewer.sh` produces self-contained `linux-x64` or `linux-arm64`
  portable archives plus SHA-256 sidecars. This is a portable package, not a distro-signed installer.
- Repository evidence: 11 Linux app render/key-map tests, 8 Linux platform/capability/keyring tests,
  a warning-free app build, and a successful `linux-x64` self-contained cross-publish.
- Remaining release evidence: launch and keyring behavior on physical GNOME/KDE Linux, assistive
  technology/keyboard review, real Windows-host video and control, reconnect/denial, and a ten-minute
  physical session. The public Downloads entry remains planned until this gate passes.

- Wayland host support must use the user-consented XDG RemoteDesktop/ScreenCast portal and PipeWire
  stream; available keyboard/pointer/touch devices and restore tokens are portal decisions. See the
  official [XDG RemoteDesktop portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html)
  and [PipeWire portal access model](https://flatpak.github.io/xdg-desktop-portal/docs/pipewire.html).
- X11 capture/input is a different native implementation and threat boundary; an X11 success must
  not be reported as Wayland support.
- Required future evidence: desktop-session/backend detection, denial/revocation, Secret Service or
  another reviewed OS-protected store, native accessibility, distro/package matrix and a ten-minute
  physical session.

### Historical Linux viewer packaging revalidation — 2026-08-26

The Linux viewer was already implemented; this pass removed the stale independent `0.9.21` metadata
and made it derive `PeerOnQLinuxClientVersion` from the canonical desktop source version. The project,
builder and CI invariant now fail closed when metadata or an explicitly requested package version
does not match canonical 0.9.66.

Recorded local evidence from that run:

- Linux platform/capability/keyring tests: 8 passed, 0 failed.
- Linux viewer render/key-map tests: 11 passed, 0 failed.
- Linux viewer Release build: 0 warnings, 0 errors.
- Self-contained unsigned-preview packages built for `linux-x64` and `linux-arm64`; both passed
  archive path-containment, manifest classification, entrypoint/README, per-file SHA-256 and archive
  SHA-256 validation.
- x64: `peeronq-0.9.66-linux-x64-unsigned-preview.tar.gz`, 54,592,653 bytes,
  SHA-256 `83cd090ab0c7da47a2334d3bf259e2bca92b6595f3f4e4fa74fc518cc43acfe8`.
- ARM64: `peeronq-0.9.66-linux-arm64-unsigned-preview.tar.gz`, 52,218,072 bytes,
  SHA-256 `5e3e65919751877e85886630ca07260284acc667e7ee644da58acb3f086ae8d9`.

These ignored local packages are not public downloads. A Windows cross-published archive may require
the documented `chmod +x PeerOnQ` after extraction. The Linux status remains `PARTIAL` until launch,
Secret Service, Windows-host video/control, denial/reconnect, accessibility and ten-minute session
evidence is captured on physical Linux hardware.

## macOS requirements

`PeerOnQ.App.Apple` now supplies a Mac Catalyst attended viewer/controller from the same UIKit source
as iPhone/iPad. It uses the shared authenticated signaling/session/WebRTC layers, advertises the
macOS least-capability viewer manifest, renders bounded decoded frames, and forwards pointer or
bounded ASCII input only after remote Full Control approval, fresh focus acknowledgment and an
explicit local switch. Host/capture/input-injection/file/clipboard/unattended/background capabilities
remain absent.

- Private identity material uses non-synchronizing data-protection Keychain SecItem records;
  non-secret identity and the last 100 masked session records stay in the app sandbox.
- VP8 decoding reuses the repository's existing libvpx binding. The Apple build requires a reviewed
  architecture-matching static `libvpx.a` and resolves it only from the signed main bundle.
- `scripts/apple/build-peeronq-apple-viewer.sh` requires macOS, WSS, the static decoder, an approved
  Developer ID identity, hardened runtime, sandbox/network-client entitlement and signature check.
- Remaining evidence: actual Mac/Xcode restore/build, Apple API compile review, arm64/x64 launch,
  Keychain, direct/TURN Windows-host video/control, focus/background handling, VoiceOver/keyboard,
  ten-minute session, Developer ID package and notarization. No Mac artifact has been built here.

A future macOS host remains a separate scope. It would require ScreenCaptureKit consent and the
supported Accessibility/Input Monitoring path; this viewer does not request either permission.

## Android requirements

`PeerOnQ.App.Android` now implements the first valid scope: an attended Android 7.1+ viewer/controller.
It reuses signaling/session/hybrid security, keeps Android host capability fail-closed, renders
authenticated VP8 through MediaCodec, and enables touch/hardware/explicit short-text input only
after accepted control scope plus the existing focus acknowledgment. Public identity and bounded
masked audit state use private preferences; private identity material is wrapped by Android
Keystore AES-GCM. The APK requests only Internet access, disables backup/cleartext traffic and has
no MediaProjection, AccessibilityService or foreground-service declaration.

- `PeerOnQ.Platform.Android` has unit-tested least-capability, fail-closed capture, VP8-header and
  Android-to-protocol key mapping boundaries.
- `scripts/android/build-peeronq-android-viewer.ps1` compiles an exact WSS endpoint and optional
  public development CA, verifies the development signature/package version/arm64+x86_64 payload,
  and can build a separately validated API 25/x86 package for BlueStacks Nougat 32,
  rejects desktop `vpxmd.dll`, and emits SHA-256 metadata. It is not a Play/store signing flow.
- Local source evidence on 2026-08-26: Android project Release build produced an installable APK
  with zero warnings/errors; the physical-device and Windows-host interoperability gates remain open.
- Required release evidence: physical arm64 first-run Keystore provisioning, publicly trusted and
  scoped-development-CA WSS, direct/TURN Windows-host viewing/control, lifecycle/network changes,
  denial/revocation/reconnect, TalkBack and large-text review, ten-minute session, production signing
  and store policy review.

A future host cannot imply silent or unrestricted capture/control.

- MediaProjection requires per-session user consent and, for current target SDKs, the declared
  `mediaProjection` foreground-service permission/type. See
  [Android media projection](https://developer.android.com/media/grow/media-projection).
- Accessibility services are intended to assist users with disabilities; gesture dispatch requires
  an explicitly declared service capability and policy review. See
  [Android AccessibilityService](https://developer.android.com/reference/android/accessibilityservice/AccessibilityService).
- Required evidence includes Android Keystore-backed identity, rotation/network-change handling,
  touch-to-pointer/keyboard mapping, lifecycle denial, TalkBack, package signing and store-policy review.

## iOS and iPadOS requirements

The `net10.0-ios` target of `PeerOnQ.App.Apple` supplies the same attended viewer/controller on iPhone
and iPad, with distinct protocol platform identifiers. It has safe-area/dynamic-type UIKit layout,
aspect-fit touch mapping, bounded short-text forwarding and explicit visible loading/error/retry
states. Input is default-off; resigning activation/backgrounding disables it, releases held state and
ends the session. No background mode is declared.

- Keychain items are `WhenUnlockedThisDeviceOnly`, non-synchronizing data-protection records.
- Both iPhone build modes require an arm64 static libvpx archive. The `ios-development` mode accepts
  only an Apple Development identity and verifies the embedded development profile, signing team,
  effective bundle identifier and requested registered-device UDID. A Personal Team can supply a
  development-only bundle-ID override without altering the canonical distribution ID; this package
  is personal-device evidence, not a public download. The `ios` distribution mode continues to
  require an Apple Distribution identity and explicit iOS provisioning profile. Both modes verify
  bundle version and signature.
- Remaining evidence: Mac/Xcode compile, signed IPA, physical iPhone and iPad launch, Keychain and
  locked-device behavior, direct/TURN Windows-host video/control, lifecycle/network transitions,
  touch/keyboard denial/revocation, VoiceOver/Dynamic Type/rotation, ten-minute session, production
  signing and App Store policy review. No IPA has been built here.

PeerOnQ still does not claim iPhone/iPad host, capture, general remote input injection or unattended
control. A future sharing feature would require a separate Apple-supported capture/consent design.

## Mobile security baseline

The applicable baseline is OWASP MASVS **2.1.0**, verified from the official
[OWASP MASVS release record](https://github.com/OWASP/masvs/releases/tag/v2.1.0). Android now has
source-level storage, authenticated networking, platform-interaction and privacy controls, but no
physical-device MASVS assessment or certification is claimed. Apple now has source-level Keychain,
authenticated networking, lifecycle and least-capability controls, but no physical-device MASVS
assessment or certification is claimed.

## Local and CI verification strategy

The authoritative shared/Windows checks are repository-local and require no GitHub service:

```powershell
dotnet test tests/PeerOnQ.Signaling.Tests/PeerOnQ.Signaling.Tests.csproj -c Release --no-restore
dotnet test tests/PeerOnQ.EndToEnd.Tests/PeerOnQ.EndToEnd.Tests.csproj -c Release --no-restore
dotnet build src/PeerOnQ.App/PeerOnQ.App.csproj -c Release -r win-x64 --no-restore
dotnet test tests/PeerOnQ.Platform.Linux.Tests/PeerOnQ.Platform.Linux.Tests.csproj -c Release
dotnet test tests/PeerOnQ.App.Linux.Tests/PeerOnQ.App.Linux.Tests.csproj -c Release
dotnet publish src/PeerOnQ.App.Linux/PeerOnQ.App.Linux.csproj -c Release -r linux-x64 --self-contained true
dotnet test tests/PeerOnQ.Platform.Android.Tests/PeerOnQ.Platform.Android.Tests.csproj -c Release
dotnet build src/PeerOnQ.App.Android/PeerOnQ.App.Android.csproj -c Release
./scripts/android/test-peeronq-android-viewer-version.ps1
dotnet test tests/PeerOnQ.Platform.Apple.Tests/PeerOnQ.Platform.Apple.Tests.csproj -c Release
./scripts/apple/test-peeronq-apple-viewer-version.ps1
```

Linux, Android and platform-neutral Apple checks are repository-local, while physical hardware is
still mandatory for any release claim. A signed Apple application build additionally requires a Mac,
Xcode, the .NET Apple workloads, a reviewed architecture-matching static libvpx archive and approved
signing/provisioning inputs; `scripts/apple/build-peeronq-apple-viewer.sh` enforces those inputs.
Keep local scripts authoritative even if CI is later mirrored to another provider.

## Claim gate for any new platform

A platform remains `NOT_FOUND`, `PARTIAL` or `EXTERNALLY_BLOCKED` until evidence records: native
build, package/install, launch, identity/authentication, connection, video, allowed input, reconnect,
claimed file/clipboard behavior, permission denial, native accessibility, ten-minute physical
session, Windows interop and self-hosted-server interop. Source compilation or a protocol vector
alone is never sufficient.
