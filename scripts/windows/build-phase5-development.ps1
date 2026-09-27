<# Creates clearly named unsigned development installers for x64 and ARM64 validation. #>
[CmdletBinding()]
param(
    [version]$Version,
    [string]$OutputRoot,
    [uri]$SignalingUrl,
    [uri]$ApiBaseUrl,
    [uri]$PresenceUrl,
    [uri]$UpdateBaseUrl,
    [ValidateSet('stable', 'beta')]
    [string]$UpdateChannel = 'beta',
    [ValidateLength(1, 2048)]
    [string]$UpdatePublicKeySpkiBase64,
    [ValidateLength(1, 128)]
    [string]$UpdateKeyId,
    [string]$PublisherCertificateSha256,
    [uri]$DownloadsBaseUrl,
    [uri]$DiagnosticsBaseUrl,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$')]
    [string]$Region = 'az-1',
    [ValidateSet('x64', 'arm64')]
    [string[]]$Architectures = @('x64', 'arm64'),
    [string]$DevelopmentRootCertificate,
    [switch]$PublishLanWebsiteDownloads,
    [switch]$PublishPublicPilotWebsiteDownloads,
    [switch]$SkipWebsitePublish,
    [switch]$AllowUnsignedPublicPilot
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
$canonicalClientVersion = Get-PeerOnQClientVersion -RepositoryRoot $repoRoot
if ($null -eq $Version) {
    $Version = $canonicalClientVersion
} else {
    [void](Assert-PeerOnQClientVersion -Version $Version -RepositoryRoot $repoRoot -Context 'Development client build')
}
if ($PublishLanWebsiteDownloads -and $PublishPublicPilotWebsiteDownloads) {
    throw 'LAN and public-pilot website publication modes are mutually exclusive.'
}
if ($PublishPublicPilotWebsiteDownloads -and -not $AllowUnsignedPublicPilot) {
    throw 'PublishPublicPilotWebsiteDownloads requires AllowUnsignedPublicPilot.'
}
if ($PublishPublicPilotWebsiteDownloads -and $SkipWebsitePublish) {
    throw 'PublishPublicPilotWebsiteDownloads cannot be combined with SkipWebsitePublish.'
}
if (-not $SkipWebsitePublish -and -not $SignalingUrl) {
    throw 'Publishing local website downloads requires a reachable LAN SignalingUrl and DevelopmentRootCertificate via -PublishLanWebsiteDownloads.'
}
if ($SignalingUrl) {
    if (-not $SignalingUrl.IsAbsoluteUri -or
        $SignalingUrl.Scheme -ne 'wss' -or
        $SignalingUrl.AbsolutePath.TrimEnd('/') -ne '/ws' -or
        $SignalingUrl.UserInfo -or
        $SignalingUrl.Query -or
        $SignalingUrl.Fragment) {
        throw 'SignalingUrl must be an absolute wss:// URL ending in /ws without credentials, query, or fragment.'
    }
    if (-not $SkipWebsitePublish -and -not $PublishLanWebsiteDownloads -and -not $PublishPublicPilotWebsiteDownloads) {
        throw 'A custom SignalingUrl requires an explicit LAN or public-pilot website publication mode, or -SkipWebsitePublish.'
    }
}

if ($PublishLanWebsiteDownloads) {
    if ($SkipWebsitePublish) {
        throw 'PublishLanWebsiteDownloads cannot be combined with SkipWebsitePublish.'
    }
    if (-not $SignalingUrl -or -not $DevelopmentRootCertificate) {
        throw 'Publishing a LAN development build to the website requires both SignalingUrl and DevelopmentRootCertificate.'
    }
}

$updateTrustInputs = @($UpdatePublicKeySpkiBase64, $UpdateKeyId, $PublisherCertificateSha256)
$configuredUpdateTrustInputs = @($updateTrustInputs | Where-Object {
    $null -ne $_ -and -not [string]::IsNullOrWhiteSpace($_.ToString())
})
if ($configuredUpdateTrustInputs.Count -notin @(0, $updateTrustInputs.Count)) {
    throw 'Client update trust requires UpdateBaseUrl, UpdatePublicKeySpkiBase64, UpdateKeyId, and PublisherCertificateSha256 together.'
}
$trustedClientUpdates = $configuredUpdateTrustInputs.Count -eq $updateTrustInputs.Count
if ($trustedClientUpdates) {
    if ($null -eq $UpdateBaseUrl -or -not $UpdateBaseUrl.IsAbsoluteUri -or $UpdateBaseUrl.Scheme -ne 'https' -or
        $UpdateBaseUrl.UserInfo -or $UpdateBaseUrl.Query -or $UpdateBaseUrl.Fragment) {
        throw 'UpdateBaseUrl must be an absolute HTTPS URL without credentials, query, or fragment.'
    }
    try {
        $updatePublicKey = [Convert]::FromBase64String($UpdatePublicKeySpkiBase64)
        if ($updatePublicKey.Length -eq 0) { throw 'empty' }
    } catch {
        throw 'UpdatePublicKeySpkiBase64 must be valid base64.'
    }
    $publisherFingerprints = @($PublisherCertificateSha256.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { ($_ -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() })
    if ($publisherFingerprints.Count -eq 0 -or @($publisherFingerprints | Where-Object { $_.Length -ne 64 }).Count -gt 0) {
        throw 'PublisherCertificateSha256 must contain one or more SHA-256 certificate fingerprints.'
    }
}

$cloudEndpoints = @($ApiBaseUrl, $PresenceUrl, $UpdateBaseUrl, $DownloadsBaseUrl, $DiagnosticsBaseUrl)
$nonUpdateCloudEndpoints = @($ApiBaseUrl, $PresenceUrl, $DownloadsBaseUrl, $DiagnosticsBaseUrl)
$configuredNonUpdateCloudEndpoints = @($nonUpdateCloudEndpoints | Where-Object { $null -ne $_ })
if ($AllowUnsignedPublicPilot) {
    if (-not $SkipWebsitePublish -and -not $PublishPublicPilotWebsiteDownloads) {
        throw 'Unsigned public-pilot packages require explicit PublishPublicPilotWebsiteDownloads before entering the local website.'
    }
    if ($DevelopmentRootCertificate) {
        throw 'Unsigned public-pilot packages cannot include a development root certificate.'
    }
    if (-not $SignalingUrl -or @($cloudEndpoints | Where-Object { $null -ne $_ }).Count -ne $cloudEndpoints.Count) {
        throw 'Unsigned public-pilot builds require Signaling, API, Presence, Update, Downloads, and Diagnostics endpoints.'
    }
    foreach ($endpoint in $cloudEndpoints) {
        if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https' -or
            $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment) {
            throw 'Every public-pilot cloud endpoint must be an absolute HTTPS URL without credentials, query, or fragment.'
        }
    }
} elseif ($configuredNonUpdateCloudEndpoints.Count -ne 0) {
    throw 'API, Presence, Downloads, and Diagnostics endpoints are accepted only with -AllowUnsignedPublicPilot.'
} elseif ($null -ne $UpdateBaseUrl -and -not $trustedClientUpdates) {
    throw 'UpdateBaseUrl outside a public-pilot build requires the complete trusted client-update bootstrap.'
}

$architecturesToBuild = @($Architectures | Select-Object -Unique)
if ($architecturesToBuild.Count -eq 0) { throw 'Select at least one client architecture.' }
if (-not $SkipWebsitePublish -and $architecturesToBuild.Count -ne 2) {
    throw 'The local website download set requires both x64 and arm64 packages. Use -SkipWebsitePublish for a single-architecture test build.'
}

$rootCertificatePath = $null
if ($DevelopmentRootCertificate) {
    $resolvedRootCertificate = Resolve-Path -LiteralPath $DevelopmentRootCertificate -ErrorAction SilentlyContinue
    $rootCertificatePath = if ($resolvedRootCertificate) { $resolvedRootCertificate.Path } else { $null }
    if (-not $rootCertificatePath -or
        -not (Test-Path -LiteralPath $rootCertificatePath -PathType Leaf) -or
        [IO.Path]::GetExtension($rootCertificatePath) -ne '.cer') {
        throw 'DevelopmentRootCertificate must point to an existing public .cer file.'
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCertificatePath)
    try {
        $basicConstraints = $certificate.Extensions |
            Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] } |
            Select-Object -First 1
        if ($certificate.HasPrivateKey -or -not $basicConstraints -or -not $basicConstraints.CertificateAuthority) {
            throw 'DevelopmentRootCertificate must be a public certificate-authority certificate without a private key.'
        }
    } finally {
        $certificate.Dispose()
    }
}

if (-not $OutputRoot) {
    if ($AllowUnsignedPublicPilot) {
        $OutputRoot = Join-Path $repoRoot "dist\public-pilot\$Version"
    } elseif ($SignalingUrl) {
        $safeHost = $SignalingUrl.DnsSafeHost -replace '[^A-Za-z0-9.-]', '_'
        $OutputRoot = Join-Path $repoRoot "dist\lan-development\$safeHost\$Version"
    } else {
        $OutputRoot = Join-Path $repoRoot "dist\development\$Version"
    }
}
$output = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputRoot must remain inside the repository.' }
if (Test-Path -LiteralPath $output) { throw "Development output already exists: $output" }
[IO.Directory]::CreateDirectory($output) | Out-Null
$artifactQualifier = if ($AllowUnsignedPublicPilot) { 'unsigned-public-pilot' } else { 'unsigned-development' }

foreach ($architecture in $architecturesToBuild) {
    $runtime = "win-$architecture"
    $publish = Join-Path $output "$architecture\app"
    dotnet restore 'src\PeerOnQ.App\PeerOnQ.App.csproj' -r $runtime
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    $publishArguments = @(
        'publish', 'src\PeerOnQ.App\PeerOnQ.App.csproj', '--no-restore', '-c', 'Release',
        '-r', $runtime, '--self-contained', 'true', '-o', $publish,
        "-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version-$artifactQualifier"
    )
    if ($SignalingUrl) { $publishArguments += "-p:PeerOnQSignalingUrl=$($SignalingUrl.AbsoluteUri)" }
    if ($SignalingUrl -and -not $AllowUnsignedPublicPilot) {
        $publishArguments += '-p:PeerOnQLanDevelopment=true'
    }
    if ($trustedClientUpdates) {
        $manifestUrl = "$($UpdateBaseUrl.AbsoluteUri.TrimEnd('/'))/$UpdateChannel/$architecture/manifest.json"
        $publishArguments += @(
            "-p:PeerOnQReleaseChannel=$UpdateChannel",
            "-p:PeerOnQUpdateManifestUrl=$manifestUrl",
            "-p:PeerOnQUpdatePublicKeySpki=$UpdatePublicKeySpkiBase64",
            "-p:PeerOnQUpdateKeyId=$UpdateKeyId",
            "-p:PeerOnQPublisherCertificateSha256=$PublisherCertificateSha256"
        )
    }
    if ($AllowUnsignedPublicPilot) {
        $publishArguments += @(
            "-p:PeerOnQApiBaseUrl=$($ApiBaseUrl.AbsoluteUri)",
            "-p:PeerOnQPresenceUrl=$($PresenceUrl.AbsoluteUri)",
            "-p:PeerOnQUpdatesBaseUrl=$($UpdateBaseUrl.AbsoluteUri)",
            "-p:PeerOnQDownloadsBaseUrl=$($DownloadsBaseUrl.AbsoluteUri)",
            "-p:PeerOnQDiagnosticsBaseUrl=$($DiagnosticsBaseUrl.AbsoluteUri)",
            '-p:PeerOnQDeploymentEnvironment=Production',
            "-p:PeerOnQRegion=$Region"
        )
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if ($rootCertificatePath) {
        Copy-Item -LiteralPath $rootCertificatePath `
            -Destination (Join-Path $publish 'PeerOnQ-Local-Development-Root.cer')
    }

    $platform = if ($architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
    # WiX does not track every harvested bind-path file as an incremental input. Rebuild is
    # mandatory so a same-version local package can never retain an older application payload.
    dotnet restore 'installer\PeerOnQ.Installer.wixproj' "-p:Platform=$platform"
    if ($LASTEXITCODE -ne 0) { throw 'Installer restore failed.' }
    dotnet build 'installer\PeerOnQ.Installer.wixproj' --no-restore -c Release -t:Rebuild `
        "-p:PublishDir=$publish" "-p:ProductVersion=$Version" "-p:Platform=$platform"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    $builtMsi = "installer\bin\$platform\Release\PeerOnQ-$Version-$platform.msi"
    & (Join-Path $PSScriptRoot 'test-phase5-installer.ps1') `
        -MsiPath $builtMsi `
        -Architecture $architecture `
        -ExpectedPublishDirectory $publish
    if ($LASTEXITCODE -ne 0) { throw 'Installer payload validation failed.' }
    Copy-Item -LiteralPath $builtMsi `
        -Destination (Join-Path $output "PeerOnQ-$Version-$artifactQualifier-$architecture.msi")
}

[string[]]$notice = if ($AllowUnsignedPublicPilot) {
    @(
        'These artifacts are unsigned public-pilot builds for controlled testing only.',
        'They are not production releases; their updater accepts only signed official releases matching the compiled manifest key and publisher.'
    )
} else {
    @('These artifacts are unsigned development builds. They are not authorized for distribution.')
}
if ($SignalingUrl) { $notice += "Compiled signaling endpoint: $($SignalingUrl.AbsoluteUri)" }
if ($trustedClientUpdates) {
    $notice += "Compiled client update channel: $UpdateChannel"
    $notice += "Compiled client update base: $($UpdateBaseUrl.AbsoluteUri)"
}
if ($AllowUnsignedPublicPilot) {
    $notice += "Compiled API endpoint: $($ApiBaseUrl.AbsoluteUri)"
    $notice += "Compiled Presence endpoint: $($PresenceUrl.AbsoluteUri)"
    $notice += "Compiled Updates endpoint: $($UpdateBaseUrl.AbsoluteUri)"
    $notice += "Compiled Downloads endpoint: $($DownloadsBaseUrl.AbsoluteUri)"
    $notice += "Compiled Diagnostics endpoint: $($DiagnosticsBaseUrl.AbsoluteUri)"
    $notice += "Compiled region: $Region"
}
if ($rootCertificatePath) {
    Copy-Item -LiteralPath $rootCertificatePath -Destination (Join-Path $output 'PeerOnQ-Local-Development-Root.cer')
    $notice += 'The MSI pins PeerOnQ-Local-Development-Root.cer only for its configured WSS connection; Windows trust stores are unchanged.'
}
[IO.File]::WriteAllLines(
    (Join-Path $output 'UNSIGNED-DEVELOPMENT.txt'),
    $notice,
    [Text.UTF8Encoding]::new($false))

$publishedFiles = $architecturesToBuild | ForEach-Object { "PeerOnQ-$Version-$artifactQualifier-$_.msi" }
$checksums = $publishedFiles | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $output $_)).Hash.ToLowerInvariant()
    "$hash  $_"
}
[IO.File]::WriteAllLines(
    (Join-Path $output 'SHA256SUMS.txt'),
    $checksums,
    [Text.UTF8Encoding]::new($false))

if (-not $SkipWebsitePublish) {
    $websiteDownloads = Join-Path $repoRoot 'artifacts\peeronq\public\downloads'
    $websiteController = Join-Path $PSScriptRoot 'peeronq-dev.ps1'
    [IO.Directory]::CreateDirectory($websiteDownloads) | Out-Null
    foreach ($fileName in $publishedFiles) {
        Copy-Item -LiteralPath (Join-Path $output $fileName) `
            -Destination (Join-Path $websiteDownloads $fileName) -Force
    }
    Copy-Item -LiteralPath (Join-Path $output 'SHA256SUMS.txt') `
        -Destination (Join-Path $websiteDownloads 'SHA256SUMS.txt') -Force
    & $websiteController -Action restart -NoBrowser
    if ($LASTEXITCODE -ne 0) {
        throw 'Website downloads were copied, but the local website preview did not restart successfully.'
    }
    foreach ($fileName in $publishedFiles) {
        try {
            $response = Invoke-WebRequest `
                -Uri "http://localhost:5555/downloads/$fileName" `
                -Method Head `
                -UseBasicParsing `
                -TimeoutSec 10
        } catch {
            throw "The restarted website preview does not serve $fileName."
        }
        if ($response.StatusCode -ne 200) {
            throw "The restarted website preview returned HTTP $($response.StatusCode) for $fileName."
        }
    }
    $releaseStatePath = Join-Path $repoRoot '.peeronq-run\windows-download-release.json'
    if (-not (Test-Path -LiteralPath $releaseStatePath -PathType Leaf)) {
        throw 'The restarted website preview did not record its selected Windows release.'
    }
    $selectedRelease = Get-Content -LiteralPath $releaseStatePath -Raw | ConvertFrom-Json
    if ($selectedRelease.version -ne $Version.ToString() -or
        $selectedRelease.artifactQualifier -ne $artifactQualifier) {
        throw "Website selected $($selectedRelease.version) $($selectedRelease.artifactQualifier), expected $Version $artifactQualifier."
    }
    Write-Host "Local website downloads published and verified: $websiteDownloads"
}
Write-Host "Unsigned development artifacts: $output"
