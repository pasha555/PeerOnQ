# PeerOnQ Linux Viewer

This is an unsigned preview of the native Linux viewer/controller. It connects to an attended
PeerOnQ session hosted by a Windows device. This build does not share the Linux screen and does not
accept incoming sessions. It has not passed the physical-Linux release gate and must not be treated
as a production-signed download.

Each archive includes `PeerOnQ.LinuxViewer.json` with the exact canonical version/runtime/capability
classification and `SHA256SUMS.txt` for every payload file. The archive-level `.sha256` sidecar uses
the archive file name only, so it can be verified after moving both files into the same directory.
Repository maintainers validate a built archive with
`scripts/linux/test-peeronq-linux-viewer-package.sh <archive.tar.gz>` before retaining evidence.

## Requirements

- Ubuntu 24.04 x86-64 is the runtime-validation baseline. Other 64-bit distributions need the
  same maintained `libvpx.so.9` and Avalonia desktop dependencies; they have not been validated.
- A desktop keyring with `secret-tool` available (libsecret tools). PeerOnQ will not store its
  device private material as plaintext if the keyring helper is unavailable.
- An active graphical session (Avalonia uses X11/XWayland by default) and its unlocked Secret
  Service keyring. Start as the signed-in desktop user, not with `sudo`.
- Network access to the configured Cloud API, presence, signaling and WebRTC endpoints.

On Ubuntu 24.04, install missing desktop prerequisites with:

```sh
sudo apt install libx11-6 libice6 libsm6 libfontconfig1 libxrandr2 libxi6 libxcursor1 \
  libxkbcommon0 libxkbcommon-x11-0 libegl1 libgl1 libsecret-tools gnome-keyring libvpx9
```

The portable archive includes the .NET runtime; installing a .NET SDK is unnecessary for launch.
The desktop session must provide its normal D-Bus/Secret Service environment. A headless shell
without an unlocked keyring cannot provision a PeerOnQ identity.
VP8 uses the distribution-maintained `libvpx.so.9` from fixed system library directories. A
Windows `vpxmd.dll` is not shipped in this Linux package; keep libvpx updated through your distro.

## Run

1. Extract the archive.
   From its extracted directory, verify every payload file with `sha256sum -c SHA256SUMS.txt`.
2. Mark the executable as runnable if your archive tool did not preserve permissions:
   `chmod +x PeerOnQ`.
3. Configure the deployment endpoints as described below, then start it with `./PeerOnQ`.

The default signaling URL is `wss://signal.127.0.0.1.sslip.io:5443/ws`. Override it before launch
with `PEERONQ_SIGNALING_URL`. A private local-development CA can be supplied through
`PEERONQ_DEVELOPMENT_ROOT_CERTIFICATE`; production TLS verification is never disabled.
The loopback default is for an explicitly configured development server. Production signaling
requires cloud enrollment and a current proof-bound attestation; setting only a signaling URL
does not supply that authorization. This preview reuses the existing device-signing/keyring and
cloud-enrollment protocol. Its short-lived access token and signaling attestation stay in memory.
Both cloud enrollment and signaling use the canonical numeric product version; the unsigned
preview classification remains in the package and informational version.

A package built with a complete public endpoint manifest uses those compiled endpoints. For an
unconfigured unsigned preview, set all of the following to your deployment's actual public URLs:

```sh
export PEERONQ_API_BASE_URL='https://your-api-host/'
export PEERONQ_PRESENCE_URL='https://your-presence-host/'
export PEERONQ_SIGNALING_URL='wss://your-signaling-host/ws'
export PEERONQ_UPDATES_BASE_URL='https://your-updates-host/'
export PEERONQ_DOWNLOADS_BASE_URL='https://your-downloads-host/'
export PEERONQ_DIAGNOSTICS_BASE_URL='https://your-diagnostics-host/'
export PEERONQ_DEPLOYMENT_ENVIRONMENT='Production'
export PEERONQ_REGION='your-region'
./PeerOnQ
```

These are placeholders, not verified service addresses. The shared endpoint parser rejects an
incomplete manifest and non-TLS remote endpoints. Loopback HTTP/WS is permitted only with
`PEERONQ_DEPLOYMENT_ENVIRONMENT=Development`. The updates/downloads/diagnostics fields complete
the shared manifest; this viewer does not add automatic updates, uploads or file transfer.

Keep the entire extracted directory together. It includes the runtime, native libraries, MIT
license, third-party notices and maintained transport source/patch provenance. Per-file and
archive checksums detect corruption; they are not publisher signatures.

Remote keyboard and pointer forwarding is off by default. It can be enabled only inside a
full-control session approved by the Windows owner. Press `Ctrl+Alt+Shift+Esc` to release it.
Keyboard forwarding uses Windows virtual keys and the host's layout; local IME/Unicode text
composition is not implemented in this preview.
