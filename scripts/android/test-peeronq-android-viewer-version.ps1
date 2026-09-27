[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repoRoot 'src\PeerOnQ.App.Android\PeerOnQ.App.Android.csproj'
$props = Join-Path $repoRoot 'Directory.Build.props'

[xml]$buildProperties = Get-Content -LiteralPath $props -Raw
$version = [string]$buildProperties.Project.PropertyGroup.PeerOnQAndroidClientVersion
if ($version -ne '$(PeerOnQWindowsClientVersion)') {
    throw 'PeerOnQAndroidClientVersion must derive from PeerOnQWindowsClientVersion.'
}

$resolvedVersion = (& dotnet msbuild $project -nologo -getProperty:PeerOnQAndroidClientVersion | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or $resolvedVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Android viewer version is invalid: $resolvedVersion"
}

$versionCode = (& dotnet msbuild $project -nologo -getProperty:PeerOnQAndroidClientVersionCode | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or $versionCode -notmatch '^[1-9]\d*$' -or [int64]$versionCode -gt 2100000000) {
    throw "Android viewer version code is invalid: $versionCode"
}
$components = $resolvedVersion.Split('.') | ForEach-Object { [int64]$_ }
if ($components[1] -gt 999 -or $components[2] -gt 999) {
    throw 'Android canonical version components exceed the version-code encoding.'
}
$expectedVersionCode = $components[0] * 1000000 + $components[1] * 1000 + $components[2]
if ([int64]$versionCode -ne $expectedVersionCode) {
    throw "Android version code $versionCode does not match canonical version $resolvedVersion ($expectedVersionCode)."
}

$projectText = [IO.File]::ReadAllText($project)
foreach ($property in @('ApplicationDisplayVersion', 'Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion')) {
    if ($projectText.IndexOf("<$property>`$(PeerOnQAndroidClientVersion)", [StringComparison]::Ordinal) -lt 0) {
        throw "PeerOnQ.App.Android.csproj $property is not derived from PeerOnQAndroidClientVersion."
    }
}
if ($projectText.IndexOf('<ApplicationVersion>$(PeerOnQAndroidClientVersionCode)', [StringComparison]::Ordinal) -lt 0) {
    throw 'PeerOnQ.App.Android.csproj ApplicationVersion is not derived from PeerOnQAndroidClientVersionCode.'
}

$minimumSdk = (& dotnet msbuild $project -nologo -getProperty:SupportedOSPlatformVersion | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or $minimumSdk -notin @('25', '25.0')) {
    throw "Android viewer minimum SDK must remain BlueStacks Nougat 32 compatible (API 25): $minimumSdk"
}

$runtimeIdentifiers = ((& dotnet msbuild $project -nologo -getProperty:RuntimeIdentifiers | Select-Object -Last 1).Trim() -split ';')
if ($LASTEXITCODE -ne 0) { throw 'Android viewer runtime identifiers could not be resolved.' }
foreach ($requiredRuntime in @('android-arm64', 'android-x64')) {
    if ($runtimeIdentifiers -notcontains $requiredRuntime) {
        throw "Android viewer is missing required runtime identifier: $requiredRuntime"
    }
}

$blueStacksRuntimeIdentifiers = ((& dotnet msbuild $project -nologo -p:PeerOnQAndroidBlueStacksX86=true -getProperty:RuntimeIdentifiers | Select-Object -Last 1).Trim() -split ';')
if ($LASTEXITCODE -ne 0 -or $blueStacksRuntimeIdentifiers.Count -ne 1 -or $blueStacksRuntimeIdentifiers[0] -ne 'android-x86') {
    throw "Android viewer BlueStacks variant must resolve only android-x86: $($blueStacksRuntimeIdentifiers -join ', ')"
}

Write-Host "Canonical Android viewer version/compatibility invariant passed: $resolvedVersion ($versionCode), API $minimumSdk, default $($runtimeIdentifiers -join ', '), BlueStacks $($blueStacksRuntimeIdentifiers -join ', ')"
