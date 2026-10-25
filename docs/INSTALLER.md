# Windows installer guide

## Package design

PeerOnQ uses WiX Toolset 6 MSI packages for x64 and ARM64. The desktop includes both the .NET runtime
and Windows App SDK 1.8 runtime, is English-only, and does not require either runtime to be installed
separately. The MSI uses
an explicit per-machine scope and therefore requests administrator approval through the standard
Windows UAC flow.
No service, driver, scheduled task, firewall exception, protocol handler, antivirus exclusion, or
hidden local listener is installed.

The license page contains the repository's MIT grant and states explicitly that the installed client
does not require a license key, product activation, paid subscription, or online entitlement check.
`LICENSE.txt` and `THIRD_PARTY_NOTICES.md` are shipped beside `PeerOnQ.exe` and are covered by the MSI
payload-integrity test.

The Start menu shortcut is core. The visible desktop-shortcut feature is selected by default but may
be deselected in Custom Setup. The visible all-users startup feature remains off by default and is
never hidden persistence. The application, shortcuts, MSI icon, banner, and welcome graphic use the
established PeerOnQ mark instead of stock WiX branding.

Windows Installer registers PeerOnQ as a normal installed application. It appears as **PeerOnQ**
with its icon, publisher, and version in both Windows **Installed apps** and Control Panel
**Programs and Features** for the installation scope. The package does not set the MSI properties
that hide the entry or disable its standard Uninstall action.

The standard WiX feature-tree UI offers an enabled destination chooser on the main PeerOnQ feature
and optional feature selection. Both x64 and ARM64 packages are 64-bit and default to
`%ProgramFiles%\PeerOnQ` (`C:\Program Files\PeerOnQ` on a standard Windows installation). The user
may choose another administrator-approved local directory with Browse. `Program Files (x86)` is
reserved for a future x86 package and is not used by these installers.

## User operations

- Clean install: open the signed MSI, verify publisher/version, review the license/features, and
  approve installation.
- Upgrade: open a higher signed version. WiX performs a transactional major upgrade and preserves
  `%LOCALAPPDATA%\PeerOnQ`.
- Repair/change: use Windows **Installed apps → PeerOnQ → Modify/Repair**. Optional shortcuts remain
  MSI-owned.
- Uninstall: use Windows **Installed apps → PeerOnQ → Uninstall**. MSI-owned files/shortcuts are
  removed; user identity, configuration, audit, and diagnostics remain by policy.

The Settings page shows and opens the exact per-user data folder. This is the safe removal choice for
data the per-machine MSI cannot own across multiple Windows accounts: uninstall first, close every
PeerOnQ process, then the data owner may delete that disclosed folder. The MSI deliberately has no
elevated custom action that enumerates or silently removes other users' profiles.

The first PeerOnQ launch after a 0.5.0 upgrade performs a fail-closed, idempotent data migration.
It copies the previous per-user data tree into `%LOCALAPPDATA%\PeerOnQ`, renames database/log files,
and retains the source for one rollback window. Existing DPAPI secrets are decrypted with the legacy
entropy only as a compatibility path and immediately re-encrypted with PeerOnQ entropy. If both data
trees already contain state, startup stops instead of choosing an identity silently.

Downgrades are blocked. Same-version package identity is deterministic in the official release
script, while every higher version receives an architecture-specific product code. Release artifacts
must be Authenticode signed and timestamped; `unsigned-development` packages are for isolated testing
only and must not be distributed.

## Automated enterprise command line

Silent deployment is supported only when an administrator has already approved the package and
policy. Use the exact signed MSI and record its SHA-256. PeerOnQ declares `ALLUSERS=1`; deployment
must run with administrator approval. Do not override the scope or force the optional startup
feature. Capture verbose MSI logs without embedding secrets.

## Validation gates

Before release, run `scripts/windows/test-phase5-installer-lifecycle.ps1` on an explicitly approved
disposable clean x64 VM to test clean install, launch, repair, previous-version upgrade, downgrade
rejection, uninstall, preserved user data, optional features, standard-user scope, and absence of
services/tasks. ARM64 remains an external gate until toolchain plus physical/emulated ARM64 runtime
evidence exists. Verify every production signature with
`signtool verify /pa /all` and archive the logs in the release evidence bundle.

The publish and installer gates reject a framework-dependent build unless the native Windows App
Runtime, WinUI XAML, controls, and DWrite payloads are present. WiX's stock ICE03 locale table cannot
represent metadata embedded in eight Microsoft WinUI files; the build neutralizes only those MSI
`File.Language` cells, preserves every payload file, and reruns ICE03 against the final MSI.

Local evidence on 2026-08-10: the unsigned x64 test package requested UAC, registered per-machine,
and installed into `C:\Program Files\PeerOnQ`. The default public-desktop and common Start menu
shortcuts targeted the installed executable; startup remained absent. The installed executable icon
rendered as the PeerOnQ mark, the MSI embedded the source-matching branded banner/dialog/icon, and
uninstall removed every installer-owned path and registration with exit code 0. This workstation run
does not replace the clean-VM, ARM64 launch, or trusted-signature release gates. The replacement
self-contained x64 publish also launched successfully and loaded `Microsoft.WindowsAppRuntime.dll`
and `Microsoft.UI.Xaml.dll` from its own application directory.
