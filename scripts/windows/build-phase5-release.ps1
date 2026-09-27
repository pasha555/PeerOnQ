<# Builds, signs, validates, inventories, and manifests a versioned PeerOnQ release. #>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][version]$MinimumSupportedVersion,
    [Parameter(Mandatory)][ValidateSet('stable', 'beta')][string]$Channel,
    [Parameter(Mandatory)][uri]$UpdateBaseUrl,
    [Parameter(Mandatory)][uri]$SignalingUrl,
    [Parameter(Mandatory)][uri]$ApiBaseUrl,
    [Parameter(Mandatory)][uri]$PresenceUrl,
    [Parameter(Mandatory)][uri]$DownloadsBaseUrl,
    [Parameter(Mandatory)][uri]$DiagnosticsBaseUrl,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,62}$')][string]$Region,
    [Parameter(Mandatory)][string]$UpdatePublicKeySpkiBase64,
    [Parameter(Mandatory)][string]$UpdateKeyId,
    [Parameter(Mandatory)][string]$UpdatePrivateKeyPemPath,
    [Parameter(Mandatory)][string]$PublisherCertificateSha256,
    [Parameter(Mandatory)][string]$SigningCertificatePath,
    [Parameter(Mandatory)][uri]$TimestampUrl,
    [Parameter(Mandatory)][ValidateLength(1, 128)][string]$RolloutSeed,
    [ValidateRange(0, 100)][int]$RolloutPercentage = 100,
    [string]$OutputRoot,
    [switch]$SecurityEmergency
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE." }
}

function Get-DeterministicProductCode([string]$Identity) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Identity))
    try {
        $guidBytes = [byte[]]::new(16)
        [Array]::Copy($hash, $guidBytes, 16)
        $guidBytes[7] = ($guidBytes[7] -band 0x0F) -bor 0x50
        $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
        return [Guid]::new($guidBytes).ToString('D').ToUpperInvariant()
    } finally {
        [Array]::Clear($hash, 0, $hash.Length)
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
[void](Assert-PeerOnQClientVersion -Version $Version -RepositoryRoot $repoRoot -Context 'Official client release')
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot "dist\release\$Version" }
$releaseRoot = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $releaseRoot.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must remain inside the PeerOnQ repository.'
}
if (Test-Path -LiteralPath $releaseRoot) {
    throw "Release output already exists and will not be overwritten: $releaseRoot"
}
if ($Version -lt $MinimumSupportedVersion) { throw 'Version cannot be below MinimumSupportedVersion.' }
& (Join-Path $PSScriptRoot 'assert-phase5-production-endpoints.ps1') `
    -UpdateBaseUrl $UpdateBaseUrl -SignalingUrl $SignalingUrl -ApiBaseUrl $ApiBaseUrl `
    -PresenceUrl $PresenceUrl -DownloadsBaseUrl $DownloadsBaseUrl `
    -DiagnosticsBaseUrl $DiagnosticsBaseUrl -TimestampUrl $TimestampUrl
try { [Convert]::FromBase64String($UpdatePublicKeySpkiBase64) | Out-Null } catch { throw 'UpdatePublicKeySpkiBase64 is invalid.' }

$privateKey = (Resolve-Path -LiteralPath $UpdatePrivateKeyPemPath).Path
$certificate = (Resolve-Path -LiteralPath $SigningCertificatePath).Path
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null

Invoke-Native dotnet @('tool', 'restore')
Invoke-Native dotnet @('test', 'PeerOnQ.slnx', '-c', 'Release', '--nologo')

foreach ($architecture in @('x64', 'arm64')) {
    $runtime = "win-$architecture"
    $architectureRoot = Join-Path $releaseRoot $architecture
    $publishRoot = Join-Path $architectureRoot 'app'
    [IO.Directory]::CreateDirectory($publishRoot) | Out-Null
    $manifestUrl = "$($UpdateBaseUrl.AbsoluteUri.TrimEnd('/'))/$Channel/$architecture/manifest.json"

    Invoke-Native dotnet @('restore', 'src\PeerOnQ.App\PeerOnQ.App.csproj', '-r', $runtime)
    Invoke-Native dotnet @(
        'publish', 'src\PeerOnQ.App\PeerOnQ.App.csproj', '--no-restore', '-c', 'Release', '-r', $runtime,
        '--self-contained', 'true', '-o', $publishRoot,
        "-p:Version=$Version", "-p:FileVersion=$Version", "-p:AssemblyVersion=$Version",
        "-p:InformationalVersion=$Version-$Channel", "-p:PeerOnQReleaseChannel=$Channel",
        '-p:PeerOnQOfficialRelease=true', "-p:PeerOnQSignalingUrl=$($SignalingUrl.AbsoluteUri)",
        "-p:PeerOnQApiBaseUrl=$($ApiBaseUrl.AbsoluteUri)",
        "-p:PeerOnQPresenceUrl=$($PresenceUrl.AbsoluteUri)",
        "-p:PeerOnQUpdatesBaseUrl=$($UpdateBaseUrl.AbsoluteUri)",
        "-p:PeerOnQDownloadsBaseUrl=$($DownloadsBaseUrl.AbsoluteUri)",
        "-p:PeerOnQDiagnosticsBaseUrl=$($DiagnosticsBaseUrl.AbsoluteUri)",
        '-p:PeerOnQDeploymentEnvironment=Production', "-p:PeerOnQRegion=$Region",
        "-p:PeerOnQUpdateManifestUrl=$manifestUrl",
        "-p:PeerOnQUpdatePublicKeySpki=$UpdatePublicKeySpkiBase64", "-p:PeerOnQUpdateKeyId=$UpdateKeyId",
        "-p:PeerOnQPublisherCertificateSha256=$PublisherCertificateSha256")

    & (Join-Path $PSScriptRoot 'sign-phase5-artifacts.ps1') `
        -ArtifactPath $publishRoot -CertificatePath $certificate -TimestampUrl $TimestampUrl.AbsoluteUri

    $productCode = Get-DeterministicProductCode "com.peeronq.desktop|$Version|$architecture"
    $wixPlatform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    Invoke-Native dotnet @('restore', 'installer\PeerOnQ.Installer.wixproj', "-p:Platform=$wixPlatform")
    Invoke-Native dotnet @(
        'build', 'installer\PeerOnQ.Installer.wixproj', '--no-restore', '-c', 'Release', '-t:Rebuild',
        "-p:PublishDir=$publishRoot", "-p:ProductVersion=$Version", "-p:ProductCode=$productCode",
        "-p:Platform=$wixPlatform")

    $builtMsi = Join-Path $repoRoot "installer\bin\$wixPlatform\Release\PeerOnQ-$Version-$wixPlatform.msi"
    $packageName = "PeerOnQ-$Version-$architecture.msi"
    $packagePath = Join-Path $architectureRoot $packageName
    Copy-Item -LiteralPath $builtMsi -Destination $packagePath
    & (Join-Path $PSScriptRoot 'sign-phase5-artifacts.ps1') `
        -ArtifactPath $packagePath -CertificatePath $certificate -TimestampUrl $TimestampUrl.AbsoluteUri
    & (Join-Path $PSScriptRoot 'test-phase5-installer.ps1') `
        -MsiPath $packagePath `
        -Architecture $architecture `
        -ExpectedPublishDirectory $publishRoot `
        -ExpectedVersion $Version `
        -RequireTrustedSignature

    $packageUrl = [uri]"$($UpdateBaseUrl.AbsoluteUri.TrimEnd('/'))/$Channel/$architecture/$packageName"
    $manifestPath = Join-Path $architectureRoot 'manifest.json'
    & (Join-Path $PSScriptRoot 'new-update-manifest.ps1') `
        -PackagePath $packagePath -PackageUrl $packageUrl -OutputPath $manifestPath `
        -Version $Version -MinimumSupportedVersion $MinimumSupportedVersion -Channel $Channel `
        -Architecture $architecture -KeyId $UpdateKeyId -PrivateKeyPemPath $privateKey `
        -ExpectedPublicKeySpkiBase64 $UpdatePublicKeySpkiBase64 `
        -ExpectedPublisherCertificateSha256 $PublisherCertificateSha256 `
        -RolloutPercentage $RolloutPercentage -RolloutSeed $RolloutSeed `
        -SecurityEmergency:$SecurityEmergency

    Invoke-Native dotnet @(
        'tool', 'run', 'sbom-tool', '--', 'generate', '-b', $architectureRoot, '-bc', $publishRoot,
        '-pn', 'PeerOnQ', '-pv', $Version.ToString(), '-ps', 'PeerOnQ',
        '-pm', 'true', '-nsb', "https://peeronq.example/sbom/$Version/$architecture")
    $sbomValidationReport = [IO.Path]::GetTempFileName()
    try {
        Invoke-Native dotnet @(
            'tool', 'run', 'sbom-tool', '--', 'validate', '-b', $architectureRoot,
            '-mi', 'SPDX:2.2', '-n', 'true', '-o', $sbomValidationReport, '-V', 'Warning')
    }
    finally {
        Remove-Item -LiteralPath $sbomValidationReport -Force -ErrorAction SilentlyContinue
    }

    $checksumFiles = @($packagePath, $manifestPath) +
        @(Get-ChildItem -LiteralPath (Join-Path $architectureRoot '_manifest') -Recurse -File | Select-Object -ExpandProperty FullName)
    $checksums = foreach ($file in $checksumFiles | Sort-Object) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([IO.Path]::GetRelativePath($architectureRoot, $file).Replace('\', '/'))"
    }
    [IO.File]::WriteAllLines((Join-Path $architectureRoot 'SHA256SUMS'), $checksums, [Text.UTF8Encoding]::new($false))
}

Write-Host "Validated production release created at: $releaseRoot"
