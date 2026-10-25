# The maintained package ID does not inherit NuGet's upstream advisory identity.
# Query the original pinned package independently; never treat a custom ID as evidence of safety.
#Requires -Version 5.1
[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'eng/sipsorcery/upstream.json') -Raw | ConvertFrom-Json
$packageId = [string]$manifest.upstream.packageId
$packageVersion = [string]$manifest.upstream.packageVersion
$sdkVersion = [string]$manifest.requiredSdkVersion
if ($packageId -ne 'SIPSorcery' -or $packageVersion -notmatch '^\d+\.\d+\.\d+$' -or
    $sdkVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'The maintained dependency manifest has an unsupported upstream audit identity.'
}
# Advisories against the pinned upstream version that a reviewed PeerOnQ change fixes. Each entry
# names the patch or added file; the builder rejects entries that do not name reviewed changes.
$mitigated = @{}
$mitigatedProperty = $manifest.PSObject.Properties['mitigatedAdvisories']
if ($null -ne $mitigatedProperty -and $null -ne $mitigatedProperty.Value) {
    foreach ($entry in @($mitigatedProperty.Value)) {
        if ([string]$entry.id -notmatch '^GHSA(-[23456789cfghjmpqrvwx]{4}){3}$' -or @($entry.fixedBy).Count -eq 0) {
            throw 'The maintained dependency manifest has an invalid mitigated advisory entry.'
        }
        $mitigated[[string]$entry.id] = (@($entry.fixedBy) -join ', ')
    }
}
$mitigatedFound = New-Object 'System.Collections.Generic.List[string]'

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$workDirectory = Join-Path $temporaryRoot ('peeronq-upstream-advisory-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($workDirectory)
$locationChanged = $false
try {
    $project = Join-Path $workDirectory 'UpstreamAdvisoryAudit.csproj'
    [IO.File]::WriteAllText($project, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <NuGetAudit>true</NuGetAudit>
    <NuGetAuditMode>all</NuGetAuditMode>
    <NuGetAuditLevel>low</NuGetAuditLevel>
    <WarningsAsErrors>NU1900;NU1905</WarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$packageId" Version="$packageVersion" />
  </ItemGroup>
</Project>
"@)
    [IO.File]::WriteAllText((Join-Path $workDirectory 'global.json'),
        (@{ sdk = @{ version = $sdkVersion; rollForward = 'disable' } } | ConvertTo-Json))
    Push-Location -LiteralPath $workDirectory
    $locationChanged = $true
    & $DotNetPath restore $project --configfile (Join-Path $repoRoot 'NuGet.config') --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Upstream advisory audit restore failed.' }
    $json = & $DotNetPath list $project package --no-restore --vulnerable --include-transitive --format json
    if ($LASTEXITCODE -ne 0) { throw 'Upstream advisory inventory failed.' }
    $report = ($json -join [Environment]::NewLine) | ConvertFrom-Json
    if (@($report.projects).Count -ne 1) { throw 'Upstream advisory inventory did not contain the audited project.' }
    # NuGet omits frameworks entirely when --vulnerable finds no affected packages.
    $frameworksProperty = $report.projects[0].PSObject.Properties['frameworks']
    $frameworks = @()
    if ($null -ne $frameworksProperty) { $frameworks = @($frameworksProperty.Value) }
    $blocked = @(
        foreach ($framework in $frameworks) {
            foreach ($groupName in 'topLevelPackages', 'transitivePackages') {
                $group = $framework.PSObject.Properties[$groupName]
                if ($null -eq $group) { continue }
                foreach ($package in @($group.Value)) {
                    $advisories = $package.PSObject.Properties['vulnerabilities']
                    if ($null -eq $advisories) { continue }
                    foreach ($advisory in @($advisories.Value)) {
                        $advisoryId = ([string]$advisory.advisoryurl).TrimEnd('/').Split('/')[-1]
                        if ($package.id -eq $packageId -and $mitigated.ContainsKey($advisoryId)) {
                            $mitigatedFound.Add("$advisoryId ($($advisory.severity)) fixed by $($mitigated[$advisoryId])")
                            continue
                        }
                        if ($advisory.severity -in 'High', 'Critical') {
                            "$($package.id): $($advisory.severity) $($advisory.advisoryurl)"
                        }
                    }
                }
            }
        }
    )
    if ($blocked.Count -gt 0) { throw "Upstream $packageId $packageVersion has blocked advisories: $($blocked -join '; ')" }
    foreach ($line in $mitigatedFound) { Write-Host "Upstream $packageId ${packageVersion} advisory $line." }
    Write-Host "Upstream $packageId ${packageVersion}: no unmitigated high/critical NuGet advisories (including transitive packages)."
} finally {
    if ($locationChanged) { Pop-Location }
    $resolvedWorkDirectory = [IO.Path]::GetFullPath($workDirectory)
    $expectedPrefix = $temporaryRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedWorkDirectory.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedWorkDirectory) -notmatch '^peeronq-upstream-advisory-[a-f0-9]{32}$') {
        throw 'Refusing to remove an unexpected upstream advisory workspace.'
    }
    Remove-Item -LiteralPath $resolvedWorkDirectory -Recurse -Force
}
