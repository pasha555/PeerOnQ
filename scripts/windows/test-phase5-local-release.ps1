<# Runs the complete GitHub-independent Phase 5 local release gate for unsigned x64 evidence. #>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][version]$Version,
    [uri]$SignalingUrl,
    [string]$DevelopmentRootCertificate,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Gate([string]$Name, [scriptblock]$Action) {
    $started = [DateTimeOffset]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        if ($LASTEXITCODE -notin @(0, $null)) { throw "$Name failed with exit code $LASTEXITCODE." }
        $script:results.Add([ordered]@{
            name = $Name
            status = 'PASS'
            exitCode = 0
            startedAtUtc = $started.ToString('O')
            durationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        })
    }
    catch {
        $script:results.Add([ordered]@{
            name = $Name
            status = 'FAIL'
            exitCode = if ($LASTEXITCODE -is [int]) { $LASTEXITCODE } else { 1 }
            startedAtUtc = $started.ToString('O')
            durationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            failure = $_.Exception.Message
        })
        throw
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $OutputRoot) {
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $OutputRoot = Join-Path $repoRoot "dist\phase5-local\$Version-$stamp"
}
$output = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must remain inside the PeerOnQ repository.'
}
if (Test-Path -LiteralPath $output) { throw "OutputRoot already exists: $output" }

$results = [Collections.Generic.List[object]]::new()
$gateFailed = $false
try {
    Invoke-Gate 'dotnet-tool-restore' { dotnet tool restore }
    Invoke-Gate 'dotnet-restore' { dotnet restore PeerOnQ.slnx }
    Invoke-Gate 'pnpm-frozen-install' { pnpm install --frozen-lockfile }
    Invoke-Gate 'native-ui-accessibility' { & (Join-Path $PSScriptRoot 'test-phase5-native-ui.ps1') }
    Invoke-Gate 'dotnet-format' { dotnet format PeerOnQ.slnx --verify-no-changes --no-restore }
    Invoke-Gate 'dotnet-release-tests' { dotnet test PeerOnQ.slnx -c Release --no-restore --nologo }
    Invoke-Gate 'frontend-typecheck' { pnpm run typecheck }
    Invoke-Gate 'frontend-lint' { pnpm run lint }
    Invoke-Gate 'frontend-tests' { pnpm run test }
    Invoke-Gate 'frontend-build' { pnpm run build }
    Invoke-Gate 'brand-purity' { & (Join-Path $repoRoot 'scripts\quality\test-brand-purity.ps1') }
    Invoke-Gate 'secret-scan' { & (Join-Path $repoRoot 'scripts\security\scan-repository-secrets.ps1') }

    $buildArguments = @{
        Version = $Version
        OutputRoot = $output
        Architectures = @('x64')
        SkipWebsitePublish = $true
    }
    if ($SignalingUrl) { $buildArguments.SignalingUrl = $SignalingUrl }
    if ($DevelopmentRootCertificate) {
        $buildArguments.DevelopmentRootCertificate = $DevelopmentRootCertificate
    }
    Invoke-Gate 'unsigned-development-x64-installer' {
        & (Join-Path $PSScriptRoot 'build-phase5-development.ps1') @buildArguments
    }
    Invoke-Gate 'sbom-rights-vulnerability-provenance' {
        & (Join-Path $PSScriptRoot 'new-phase5-local-evidence.ps1') `
            -ArtifactRoot $output -Version $Version -Architecture x64
    }
}
catch {
    $gateFailed = $true
    throw
}
finally {
    if (Test-Path -LiteralPath $output) {
        $reportPath = Join-Path $output 'phase5-local-verification.json'
        $summary = [ordered]@{
            schemaVersion = 1
            product = 'PeerOnQ'
            version = $Version.ToString()
            architecture = 'x64'
            signingState = 'Development/Unsigned'
            completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            status = if ($gateFailed) { 'FAIL' } else { 'PASS_WITH_EXTERNAL_CLEAN_MACHINE_CHECKLIST' }
            results = @($results)
        }
        [IO.File]::WriteAllText(
            $reportPath,
            ($summary | ConvertTo-Json -Depth 10),
            [Text.UTF8Encoding]::new($false))
    }
}

Write-Host "Phase 5 local release gate passed. External clean-machine items remain in: $output\evidence\CLEAN_MACHINE_INSTALL_CHECKLIST.md"
