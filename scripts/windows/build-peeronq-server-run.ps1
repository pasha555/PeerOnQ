[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$')]
    [string]$Version,

    [string]$OutputDirectory,

    [string]$GpgKeyId,

    [switch]$RequireSignature,

    [string]$WindowsClientMsiPath,

    [version]$WindowsClientVersion,

    [switch]$IncludeUnsignedWindowsPilot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
[void](Assert-PeerOnQClientVersion -Version ([version]$Version) -RepositoryRoot $repoRoot -Context 'Server bundle')
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'dist\server'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("peeronq-server-{0}" -f [Guid]::NewGuid().ToString('N'))
$stageRoot = Join-Path $temporaryRoot 'payload'
$payloadPath = Join-Path $temporaryRoot 'payload.tar.gz'
$headerPath = Join-Path $repoRoot 'scripts\linux\peeronq-server-installer.sh'
$outputFileName = "peeronq-server-{0}.run" -f $Version
$outputPath = Join-Path $OutputDirectory $outputFileName
$checksumPath = "$outputPath.sha256"
$signaturePath = "$outputPath.asc"
$publishStageRoot = Join-Path $OutputDirectory (".peeronq-server-publish-{0}" -f [Guid]::NewGuid().ToString('N'))
$workingOutputPath = Join-Path $publishStageRoot $outputFileName
$workingChecksumPath = "$workingOutputPath.sha256"
$workingSignaturePath = "$workingOutputPath.asc"
$publishedPaths = [Collections.Generic.List[string]]::new()

if ($RequireSignature -and [string]::IsNullOrWhiteSpace($GpgKeyId)) {
    throw 'A GPG key ID is required when -RequireSignature is used.'
}
foreach ($artifactPath in @($outputPath, $checksumPath, $signaturePath)) {
    if (Test-Path -LiteralPath $artifactPath) {
        throw "Refusing to replace an existing immutable server release artifact: $artifactPath"
    }
}

$dockerIgnorePath = Join-Path $repoRoot '.dockerignore'
$dockerIgnoreRules = @(Get-Content -LiteralPath $dockerIgnorePath)
$requiredEmbeddedDownloadRules = @(
    '!artifacts/peeronq/public/downloads/PeerOnQ-Windows-x64.msi',
    '!artifacts/peeronq/public/downloads/SHA256SUMS.txt',
    '!artifacts/peeronq/public/downloads/embedded-windows-version.txt',
    '!artifacts/peeronq/public/downloads/embedded-windows-release-type.txt',
    '!artifacts/peeronq/public/downloads/UNSIGNED-PILOT-NOTICE.txt'
)
foreach ($rule in $requiredEmbeddedDownloadRules) {
    if ($dockerIgnoreRules -notcontains $rule) {
        throw "The Docker build context does not allow the embedded Windows pilot file: $rule"
    }
}

if ([string]::IsNullOrWhiteSpace($WindowsClientMsiPath)) {
    throw '-WindowsClientMsiPath is required. Server bundles must explicitly embed a verified Windows x64 MSI.'
}

$payloadEntries = @(
    '.dockerignore',
    'artifacts/peeronq',
    'artifacts/peeronq-admin',
    'artifacts/peeronq-portal',
    'lib',
    'src',
    'scripts/check-package-manager.mjs',
    'scripts/linux/bootstrap-peeronq-production.sh',
    'scripts/linux/peeronq-platform-upgrade-agent.sh',
    'scripts/linux/peeronq-platform-upgrade-agent.service',
    'scripts/linux/peeronq-platform-upgrade-agent.path',
    'scripts/linux/renew-peeronq-tls.sh',
    'scripts/linux/peeronq-spaceship-dns-hook.py',
    'scripts/linux/verify-embedded-windows-client.sh',
    'package.json',
    'pnpm-lock.yaml',
    'pnpm-workspace.yaml',
    'tsconfig.base.json',
    'Directory.Build.props',
    'Directory.Build.targets',
    'Directory.Packages.props',
    'global.json',
    'NuGet.config'
)

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    $trackedFiles = @(& git -C $repoRoot ls-files --cached --others --exclude-standard -- $payloadEntries)
    if ($LASTEXITCODE -ne 0 -or $trackedFiles.Count -eq 0) {
        throw 'Failed to enumerate the repository payload.'
    }
    foreach ($entry in $trackedFiles) {
        $source = Join-Path $repoRoot $entry
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $destination = Join-Path $stageRoot $entry
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    if ($WindowsClientMsiPath) {
        $windowsMsi = (Resolve-Path -LiteralPath $WindowsClientMsiPath).Path
        if ([IO.Path]::GetExtension($windowsMsi) -ne '.msi') {
            throw 'WindowsClientMsiPath must be an MSI package.'
        }
        if ((Get-Item -LiteralPath $windowsMsi).Length -gt 96MB) {
            throw 'The Windows client MSI exceeds the website publication limit.'
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $windowsMsi
        $isUnsignedWindowsPilot = $signature.Status -eq 'NotSigned'
        if ($isUnsignedWindowsPilot) {
            if (-not $IncludeUnsignedWindowsPilot) {
                throw 'The Windows client MSI is unsigned. Pass -IncludeUnsignedWindowsPilot only for an authorized controlled pilot.'
            }
        }
        elseif ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
            throw "The Windows client MSI has an invalid Authenticode status: $($signature.Status)."
        }
        elseif ($IncludeUnsignedWindowsPilot) {
            throw '-IncludeUnsignedWindowsPilot is valid only for an unsigned Windows client.'
        }

        & (Join-Path $PSScriptRoot 'test-phase5-installer.ps1') `
            -MsiPath $windowsMsi -Architecture x64 -ExpectedVersion ([version]$Version) -SkipAdministrativeExtraction

        if (-not $WindowsClientVersion) {
            $versionMatch = [regex]::Match([IO.Path]::GetFileName($windowsMsi), '(?<![0-9])(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?![0-9])')
            if (-not $versionMatch.Success) {
                throw 'WindowsClientVersion is required when the MSI filename does not contain a three-part version.'
            }
            $WindowsClientVersion = [version]$versionMatch.Groups['version'].Value
        }
        [void](Assert-PeerOnQClientVersion `
            -Version $WindowsClientVersion `
            -RepositoryRoot $repoRoot `
            -Context 'Server-embedded Windows client')

        $downloadsDirectory = Join-Path $stageRoot 'artifacts\peeronq\public\downloads'
        New-Item -ItemType Directory -Path $downloadsDirectory -Force | Out-Null
        $canonicalMsi = Join-Path $downloadsDirectory 'PeerOnQ-Windows-x64.msi'
        Copy-Item -LiteralPath $windowsMsi -Destination $canonicalMsi -Force
        $msiSha256 = (Get-FileHash -LiteralPath $canonicalMsi -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText(
            (Join-Path $downloadsDirectory 'SHA256SUMS.txt'),
            "$msiSha256  PeerOnQ-Windows-x64.msi`n",
            [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText(
            (Join-Path $downloadsDirectory 'embedded-windows-version.txt'),
            "$($WindowsClientVersion.ToString(3))`n",
            [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText(
            (Join-Path $downloadsDirectory 'embedded-windows-release-type.txt'),
            "$(if ($isUnsignedWindowsPilot) { 'unsigned-pilot' } else { 'signed' })`n",
            [Text.UTF8Encoding]::new($false))
        if ($isUnsignedWindowsPilot) {
            [IO.File]::WriteAllText(
                (Join-Path $downloadsDirectory 'UNSIGNED-PILOT-NOTICE.txt'),
                "This x64 MSI is unsigned and is authorized only for controlled PeerOnQ pilot testing.`nVerify SHA256SUMS.txt before installation.`n",
                [Text.UTF8Encoding]::new($false))
        }
    }

    Get-ChildItem -LiteralPath $stageRoot -Directory -Recurse -Force |
        Where-Object { $_.Name -in @('bin', 'obj', 'dist', 'node_modules', 'runtime', 'logs', 'backups', '.local') } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $stageRoot -File -Recurse -Force |
        Where-Object { $_.Name -eq '.env' -or $_.Extension -in @('.pfx', '.key') } |
        Remove-Item -Force

    & tar -C $stageRoot -czf $payloadPath .
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create the server payload archive.' }
    $payloadSha256 = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $header = [IO.File]::ReadAllText($headerPath)
    $header = $header.Replace('__VERSION__', $Version).Replace('__PAYLOAD_SHA256__', $payloadSha256)
    $header = $header.Replace("`r`n", "`n")
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $publishStageRoot | Out-Null
    $stream = [IO.File]::Open($workingOutputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $headerBytes = [Text.UTF8Encoding]::new($false).GetBytes($header)
        $stream.Write($headerBytes, 0, $headerBytes.Length)
        $payloadBytes = [IO.File]::ReadAllBytes($payloadPath)
        $stream.Write($payloadBytes, 0, $payloadBytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
    if ((Get-Item -LiteralPath $workingOutputPath).Length -gt 256MB) {
        throw 'The complete server bundle exceeds the 256 MiB Admin platform-upgrade limit.'
    }

    $runSha256 = (Get-FileHash -LiteralPath $workingOutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($workingChecksumPath, "$runSha256  $outputFileName`n", [Text.UTF8Encoding]::new($false))

    if ($GpgKeyId) {
        & gpg --batch --yes --armor --local-user $GpgKeyId --detach-sign --output $workingSignaturePath $workingOutputPath
        if ($LASTEXITCODE -ne 0) { throw 'Detached GPG signature creation failed.' }
        & gpg --batch --verify $workingSignaturePath $workingOutputPath
        if ($LASTEXITCODE -ne 0) { throw 'Detached GPG signature verification failed.' }
    }

    # Publish the detached signature and checksum first. The .run file is the commit marker:
    # once it becomes visible, the complete immutable artifact set is already present.
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if ($GpgKeyId) {
        Move-Item -LiteralPath $workingSignaturePath -Destination $signaturePath
        $publishedPaths.Add($signaturePath)
    }
    Move-Item -LiteralPath $workingChecksumPath -Destination $checksumPath
    $publishedPaths.Add($checksumPath)
    Move-Item -LiteralPath $workingOutputPath -Destination $outputPath
    $publishedPaths.Add($outputPath)

    Write-Host "Server bundle: $outputPath"
    Write-Host "SHA-256:      $runSha256"
    if (-not $GpgKeyId) {
        Write-Warning 'No detached authenticity signature was created. Do not use this bundle in production until it is signed and verified.'
    }
}
catch {
    foreach ($publishedPath in $publishedPaths) {
        Remove-Item -LiteralPath $publishedPath -Force -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $publishStageRoot) {
        Remove-Item -LiteralPath $publishStageRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
