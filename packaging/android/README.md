# PeerOnQ Android viewer preview

This is an attended **viewer/controller**, not an Android host. It can request view-only or
full-control sessions from a compatible PeerOnQ host. It does not advertise Android screen
capture, input injection, unattended access, clipboard or file transfer.

Requirements:

- .NET SDK/workload from `global.json`: `dotnet workload install android`
- Microsoft OpenJDK 21
- Android SDK platform 36 and build-tools
- Android 7.1 (API 25) or later
- arm64 on physical devices and x86_64 on 64-bit emulators
- a separate native x86 package for BlueStacks Nougat 32

If API 36 is missing, install the workload-owned dependencies:

```powershell
dotnet build src/PeerOnQ.App.Android/PeerOnQ.App.Android.csproj `
  -t:InstallAndroidDependencies -f net10.0-android `
  -p:AcceptAndroidSdkLicenses=true `
  -p:JavaSdkDirectory='C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot' `
  -p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk"
```

Create a server-bound development-signed APK:

```powershell
./scripts/android/build-peeronq-android-viewer.ps1 `
  -SignalingUrl 'wss://signal.example.test:5443/ws' `
  -DevelopmentRootCertificate './certificates/development-root.cer'
```

Create the API 25/x86 BlueStacks Nougat 32 package with the same endpoint and certificate options:

```powershell
./scripts/android/build-peeronq-android-viewer.ps1 `
  -SignalingUrl 'wss://signal.example.test:5443/ws' `
  -DevelopmentRootCertificate './certificates/development-root.cer' `
  -BlueStacksX86
```

The optional certificate must be a public CA certificate without a private key. Hostname
validation remains enabled. The output under `app-updates/android/` is signed with the local
Android development key so it can be installed for controlled testing; it is not a store or
production release.

The separate API 25/x86 target exists for BlueStacks Nougat 32 compatibility. Production use should
prefer a currently supported Android release on arm64 hardware.

Before promotion, validate on physical arm64 hardware: first-run Keystore provisioning,
attended view-only/full-control sessions over direct and TURN paths, VP8 MediaCodec output,
touch/hardware keyboard release, background/resume, reconnect, network switching and a session
longer than ten minutes.
