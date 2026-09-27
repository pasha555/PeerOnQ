<#
    Produces the two artifacts needed for a two-computer Phase 1 test:

      dist/PeerOnQ               self-contained desktop app (no .NET or WindowsAppSDK install)
      dist/PeerOnQ.Signaling     self-contained signaling server

    Self-contained on purpose: the second computer in an acceptance test should not need a
    developer toolchain, and "it works on the build machine" is not a test result.
#>

[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $RepoRoot 'dist' }

if (-not (Test-Path (Join-Path $RepoRoot 'PeerOnQ.slnx'))) {
    Write-Host "[ERROR] $RepoRoot is not the PeerOnQ workspace." -ForegroundColor Red
    exit 1
}

Write-Host "Publishing PeerOnQ Phase 1 ($Configuration / $Runtime)" -ForegroundColor Cyan
Write-Host "Output: $OutputRoot"
Write-Host ''

$appOut = Join-Path $OutputRoot 'PeerOnQ'
$serverOut = Join-Path $OutputRoot 'PeerOnQ.Signaling'

Write-Host 'Publishing the desktop app ...'
& dotnet publish (Join-Path $RepoRoot 'src\PeerOnQ.App\PeerOnQ.App.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -p:WindowsPackageType=None `
    -o $appOut

if ($LASTEXITCODE -ne 0) {
    Write-Host '[ERROR] Desktop app publish failed.' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host 'Publishing the signaling server ...'
& dotnet publish (Join-Path $RepoRoot 'src\PeerOnQ.Signaling.Server\PeerOnQ.Signaling.Server.csproj') `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $serverOut

if ($LASTEXITCODE -ne 0) {
    Write-Host '[ERROR] Signaling server publish failed.' -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  App:    $(Join-Path $appOut 'PeerOnQ.exe')"
Write-Host "  Server: $(Join-Path $serverOut 'PeerOnQ.Signaling.Server.exe')"
Write-Host ''
Write-Host 'Next: see the two-computer runbook in PHASE1.md.'
