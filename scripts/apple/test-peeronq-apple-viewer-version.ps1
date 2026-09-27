[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$propsPath = Join-Path $repositoryRoot 'Directory.Build.props'
$projectPath = Join-Path $repositoryRoot 'src\PeerOnQ.App.Apple\PeerOnQ.App.Apple.csproj'
$buildScriptPath = Join-Path $repositoryRoot 'scripts\apple\build-peeronq-apple-viewer.sh'

[xml]$props = Get-Content -Raw -LiteralPath $propsPath
$propertyGroup = $props.Project.PropertyGroup | Where-Object { $_.PeerOnQWindowsClientVersion } | Select-Object -First 1
if (-not $propertyGroup) { throw 'Canonical client version property group was not found.' }
$canonical = [string]$propertyGroup.PeerOnQWindowsClientVersion
$appleVersion = [string]$propertyGroup.PeerOnQAppleClientVersion
$appleCode = [string]$propertyGroup.PeerOnQAppleClientVersionCode
if ($appleVersion -ne '$(PeerOnQWindowsClientVersion)') {
    throw 'PeerOnQAppleClientVersion must derive from PeerOnQWindowsClientVersion.'
}
if ($appleCode -notmatch '^\d+$' -or [int64]$appleCode -le 0) {
    throw 'PeerOnQAppleClientVersionCode must be a positive monotonic integer.'
}

[xml]$project = Get-Content -Raw -LiteralPath $projectPath
$main = $project.Project.PropertyGroup | Select-Object -First 1
if ([string]$main.ApplicationDisplayVersion -ne '$(PeerOnQAppleClientVersion)' -or
    [string]$main.ApplicationVersion -ne '$(PeerOnQAppleClientVersionCode)' -or
    [string]$main.Version -ne '$(PeerOnQAppleClientVersion)') {
    throw 'The Apple project does not consume canonical display, bundle and assembly versions.'
}
if ([string]$main.TargetFrameworks -ne 'net10.0-ios;net10.0-maccatalyst') {
    throw 'The Apple project must target both iPhone/iPad and Mac Catalyst.'
}
$projectText = Get-Content -Raw -LiteralPath $projectPath
if ($projectText -notmatch 'RemoveDesktopVp8NativeLibrary' -or
    $projectText -notmatch "vpxmd\.dll") {
    throw 'The Apple project must remove the package-provided Windows VP8 DLL.'
}

$buildScript = Get-Content -Raw -LiteralPath $buildScriptPath
if ($buildScript -notmatch 'PeerOnQAppleLibVpxPath' -or
    $buildScript -notmatch 'codesign --verify' -or
    $buildScript -notmatch 'Notarization is still required') {
    throw 'The Apple build script is missing a decoder, signature or notarization fail-closed gate.'
}
if ($buildScript -notmatch 'ios-development' -or
    $buildScript -notmatch 'Apple Development:' -or
    $buildScript -notmatch 'PEERONQ_APPLE_DEVICE_UDID' -or
    $buildScript -notmatch 'PEERONQ_APPLE_DEVELOPMENT_BUNDLE_ID' -or
    $buildScript -notmatch 'embedded\.mobileprovision' -or
    $buildScript -notmatch 'get-task-allow' -or
    $buildScript -notmatch 'ProvisionedDevices' -or
    $buildScript -notmatch 'not eligible for website or App Store publication') {
    throw 'The Apple build script is missing a registered-device development-signing gate.'
}
if ($buildScript -match '(?m)^\s*(?:version|client_version)=') {
    throw 'The Apple build script must not carry an independent client version.'
}

Write-Host "Apple client version invariant passed: $canonical (bundle code $appleCode)."
