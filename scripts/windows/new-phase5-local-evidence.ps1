<# Generates local Phase 5 SBOM, rights, vulnerability, provenance, and checksum evidence. #>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][version]$Version,
    [ValidateSet('x64')][string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Captured([string]$File, [string[]]$Arguments) {
    $output = & $File @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE.`n$($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine)
}

function Get-PortableRelativePath([string]$BasePath, [string]$Path) {
    $baseUri = [Uri]([IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\')
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ArtifactRoot).Path)
if (-not $root.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ArtifactRoot must remain inside the PeerOnQ repository.'
}
$applicationRoot = Join-Path $root "$Architecture\app"
$applicationProjectRoot = Join-Path $repoRoot 'src\PeerOnQ.App'
if (-not (Test-Path -LiteralPath (Join-Path $applicationRoot 'PeerOnQ.exe') -PathType Leaf)) {
    throw "ArtifactRoot does not contain the expected $Architecture PeerOnQ application payload."
}
$installers = @(Get-ChildItem -LiteralPath $root -Filter "PeerOnQ-$Version-*-x64.msi" -File)
if ($installers.Count -ne 1) { throw 'ArtifactRoot must contain exactly one x64 Phase 5 MSI.' }
$installer = $installers[0]

& dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Local SBOM tool restore failed.' }

# Generate into the artifact tree before creating the evidence folder so the SBOM cannot inventory itself.
# The published payload supplies the file inventory; the app project supplies its NuGet dependency graph.
& dotnet tool run sbom-tool -- generate -b $root -bc $applicationProjectRoot -pn PeerOnQ -pv $Version.ToString() `
    -ps PeerOnQ -pm true -nsb "https://peeronq.example/sbom/$Version/$Architecture"
if ($LASTEXITCODE -ne 0) { throw 'SPDX SBOM generation failed.' }

$sbomManifest = Get-ChildItem -LiteralPath (Join-Path $root '_manifest') -Recurse `
    -Filter 'manifest.spdx.json' -File | Select-Object -First 1
if ($null -eq $sbomManifest) { throw 'SPDX SBOM manifest was not created.' }
$sbomDocument = Get-Content -LiteralPath $sbomManifest.FullName -Raw | ConvertFrom-Json
$rootPackage = @($sbomDocument.packages | Where-Object { $_.SPDXID -eq 'SPDXRef-RootPackage' })
if ($rootPackage.Count -ne 1) { throw 'SPDX SBOM root package is missing or ambiguous.' }
$rootPackage[0].licenseDeclared = 'MIT'
$rootPackage[0].licenseConcluded = 'MIT'
$rootPackage[0].copyrightText = 'Copyright (c) PeerOnQ contributors'
[IO.File]::WriteAllText(
    $sbomManifest.FullName,
    ($sbomDocument | ConvertTo-Json -Depth 100),
    [Text.UTF8Encoding]::new($false))

$evidence = Join-Path $root 'evidence'
if (Test-Path -LiteralPath $evidence) {
    throw "Evidence output already exists and will not be overwritten: $evidence"
}
[IO.Directory]::CreateDirectory($evidence) | Out-Null

$sbomReport = Join-Path $evidence 'sbom-validation.json'
& dotnet tool run sbom-tool -- validate -b $root -mi SPDX:2.2 -n true -o $sbomReport -V Warning
if ($LASTEXITCODE -ne 0) { throw 'SPDX SBOM validation failed.' }
$sbomValidation = Get-Content -LiteralPath $sbomReport -Raw | ConvertFrom-Json
if ($sbomValidation.Result -ne 'Success') {
    throw "SPDX SBOM validation reported $($sbomValidation.Result)."
}

$dotnetInventory = Invoke-Captured dotnet @(
    'list', 'PeerOnQ.slnx', 'package', '--include-transitive', '--format', 'json')
[IO.File]::WriteAllText(
    (Join-Path $evidence 'dotnet-dependencies.json'),
    $dotnetInventory,
    [Text.UTF8Encoding]::new($false))

$dotnetVulnerabilities = Invoke-Captured dotnet @(
    'list', 'PeerOnQ.slnx', 'package', '--vulnerable', '--include-transitive', '--format', 'json')
[IO.File]::WriteAllText(
    (Join-Path $evidence 'dotnet-vulnerabilities.json'),
    $dotnetVulnerabilities,
    [Text.UTF8Encoding]::new($false))

$pnpmLicenses = Invoke-Captured pnpm @('licenses', 'list', '--prod', '--json')
[IO.File]::WriteAllText(
    (Join-Path $evidence 'pnpm-production-licenses.json'),
    $pnpmLicenses,
    [Text.UTF8Encoding]::new($false))

$pnpmAudit = Invoke-Captured pnpm @('audit', '--prod', '--json')
[IO.File]::WriteAllText(
    (Join-Path $evidence 'pnpm-production-audit.json'),
    $pnpmAudit,
    [Text.UTF8Encoding]::new($false))

Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $evidence 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $evidence

$commit = (Invoke-Captured git @('rev-parse', 'HEAD')).Trim()
$workingTreeStatus = Invoke-Captured git @('status', '--porcelain')
$sourceTreeDirty = -not [string]::IsNullOrWhiteSpace($workingTreeStatus)
$materials = foreach ($relativePath in @(
    'Directory.Packages.props',
    'pnpm-lock.yaml',
    '.config/dotnet-tools.json',
    'src/PeerOnQ.App/PeerOnQ.App.csproj',
    'installer/Package.wxs',
    'installer/license.rtf',
    'scripts/windows/build-phase5-development.ps1',
    'scripts/windows/new-phase5-local-evidence.ps1')) {
    $path = Join-Path $repoRoot $relativePath
    [ordered]@{
        uri = "file:///$($relativePath.Replace('\', '/'))"
        digest = [ordered]@{ sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}
$subjects = @($installer.FullName) + @(
    Get-ChildItem -LiteralPath (Join-Path $root '_manifest') -Recurse -File |
        Select-Object -ExpandProperty FullName)
$subjectRecords = foreach ($path in $subjects | Sort-Object) {
    [ordered]@{
        name = Get-PortableRelativePath $root $path
        digest = [ordered]@{ sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}
$provenance = [ordered]@{
    _type = 'https://in-toto.io/Statement/v1'
    subject = @($subjectRecords)
    predicateType = 'https://slsa.dev/provenance/v1'
    predicate = [ordered]@{
        buildDefinition = [ordered]@{
            buildType = 'https://peeronq.example/build-types/local-phase5-development/v1'
            externalParameters = [ordered]@{
                version = $Version.ToString()
                architecture = $Architecture
                configuration = 'Release'
                signingState = 'Development/Unsigned'
                sourceRevision = $commit
                sourceTreeDirty = $sourceTreeDirty
            }
            internalParameters = [ordered]@{}
            resolvedDependencies = @(
                [ordered]@{ uri = "git+local://PeerOnQ@$commit"; digest = [ordered]@{ gitCommit = $commit } }
            ) + @($materials)
        }
        runDetails = [ordered]@{
            builder = [ordered]@{ id = 'local://PeerOnQ/scripts/windows/build-phase5-development.ps1' }
            metadata = [ordered]@{
                invocationId = [Guid]::NewGuid().ToString('D')
                finishedOn = [DateTimeOffset]::UtcNow.ToString('O')
                reproducible = $false
                reproducibilityNote = 'Local timestamps, WiX package metadata, generated product codes, and unsigned build environment are intentionally recorded as nondeterministic inputs.'
            }
            byproducts = @(
                [ordered]@{ name = 'evidence/dotnet-dependencies.json' },
                [ordered]@{ name = 'evidence/pnpm-production-licenses.json' },
                [ordered]@{ name = 'evidence/dotnet-vulnerabilities.json' },
                [ordered]@{ name = 'evidence/pnpm-production-audit.json' },
                [ordered]@{ name = 'evidence/sbom-validation.json' })
        }
    }
}
[IO.File]::WriteAllText(
    (Join-Path $evidence 'provenance.intoto.json'),
    ($provenance | ConvertTo-Json -Depth 20),
    [Text.UTF8Encoding]::new($false))

$checklist = @'
# PeerOnQ clean-machine x64 checklist

This checklist is external runtime evidence. Do not mark an item complete without the named clean
Windows x64 environment and retained installer logs.

- [ ] Verify the MSI SHA-256 and Authenticode publisher before launch.
- [ ] Clean install with standard UAC approval; confirm Program Files destination and Installed Apps entry.
- [ ] Launch from Start menu; confirm PeerOnQ ID appears and no license key or activation is requested.
- [ ] Confirm desktop shortcut can be deselected and startup remains off unless explicitly selected.
- [ ] Repair from Installed Apps and launch again with the same per-user identity.
- [ ] Upgrade from the supported previous signed release and confirm local identity/audit preservation.
- [ ] Attempt a lower version and confirm Windows Installer blocks the downgrade.
- [ ] Uninstall and confirm installer-owned files/shortcuts/registration are removed.
- [ ] Confirm per-user data is preserved; then, as the data owner, delete the Settings-disclosed folder and verify removal.
- [ ] Confirm no PeerOnQ service, driver, scheduled task, firewall rule, antivirus exclusion, or hidden startup entry was added.
'@
[IO.File]::WriteAllText(
    (Join-Path $evidence 'CLEAN_MACHINE_INSTALL_CHECKLIST.md'),
    $checklist,
    [Text.UTF8Encoding]::new($false))

$checksumTargets = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object FullName
$checksums = foreach ($file in $checksumTargets) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Get-PortableRelativePath $root $file.FullName)"
}
[IO.File]::WriteAllLines(
    (Join-Path $root 'SHA256SUMS.txt'),
    $checksums,
    [Text.UTF8Encoding]::new($false))

Write-Host "Phase 5 local release evidence created at: $evidence"
