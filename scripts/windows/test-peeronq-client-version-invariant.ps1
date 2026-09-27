[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
$canonical = Get-PeerOnQClientVersion -RepositoryRoot $repoRoot

$versionOwners = @(
    'scripts\windows\build-phase5-development.ps1',
    'scripts\windows\build-phase5-release.ps1',
    'scripts\windows\build-peeronq-public-pilot.ps1',
    'scripts\windows\build-phase11-portable-support.ps1',
    'scripts\windows\build-peeronq-server-run.ps1',
    'scripts\windows\peeronq-dev.ps1',
    'scripts\windows\peeronq-phase3-local.ps1'
)

foreach ($relativePath in $versionOwners) {
    $path = Join-Path $repoRoot $relativePath
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "$relativePath has PowerShell parse errors: $($parseErrors[0].Message)"
    }
    $text = [IO.File]::ReadAllText($path)
    if ($text.IndexOf('PeerOnQ.ClientVersion.ps1', [StringComparison]::Ordinal) -lt 0) {
        throw "$relativePath does not consume the canonical Windows client version helper."
    }
}

$appProject = Join-Path $repoRoot 'src\PeerOnQ.App\PeerOnQ.App.csproj'
$appProjectText = [IO.File]::ReadAllText($appProject)
foreach ($property in @('Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion')) {
    $expectedProperty = '<{0}>$(PeerOnQWindowsClientVersion)' -f $property
    if ($appProjectText.IndexOf($expectedProperty, [StringComparison]::Ordinal) -lt 0) {
        throw "PeerOnQ.App.csproj $property is not derived from PeerOnQWindowsClientVersion."
    }
}

$mismatchRejected = $false
try {
    [void](Assert-PeerOnQClientVersion -Version ([version]'0.0.0') -RepositoryRoot $repoRoot -Context 'Invariant probe')
} catch {
    $mismatchRejected = $_.Exception.Message -match 'does not match canonical Windows client version'
}
if (-not $mismatchRejected) {
    throw 'Canonical client-version mismatch did not fail closed.'
}

$resolvedVersion = & dotnet msbuild $appProject -nologo -getProperty:Version
if ($LASTEXITCODE -ne 0) {
    throw 'MSBuild could not resolve the Windows client version.'
}
$resolvedVersion = ($resolvedVersion | Select-Object -Last 1).Trim()
if ($resolvedVersion -ne $canonical.ToString(3)) {
    throw "PeerOnQ.App resolves version $resolvedVersion instead of canonical version $canonical."
}

Write-Host "Canonical Windows client version invariant passed: $canonical"
