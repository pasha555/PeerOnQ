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

- A 64-bit Linux desktop supported by Avalonia.
- A desktop keyring with `secret-tool` available (libsecret tools). PeerOnQ will not store its
  device private material as plaintext if the keyring helper is unavailable.
- Network access to the configured PeerOnQ signaling and WebRTC endpoints.

## Run

1. Extract the archive.
2. Mark the executable as runnable if your archive tool did not preserve permissions:
   `chmod +x PeerOnQ`.
3. Start it with `./PeerOnQ`.

The default signaling URL is `wss://signal.127.0.0.1.sslip.io:5443/ws`. Override it before launch
with `PEERONQ_SIGNALING_URL`. A private local-development CA can be supplied through
`PEERONQ_DEVELOPMENT_ROOT_CERTIFICATE`; production TLS verification is never disabled.

Remote keyboard and pointer forwarding is off by default. It can be enabled only inside a
full-control session approved by the Windows owner. Press `Ctrl+Alt+Shift+Esc` to release it.
