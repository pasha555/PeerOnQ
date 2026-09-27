<# Builds and signs a complete static website release for Admin Panel publication. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$PrivateKeyPemPath,

    [Parameter(Mandatory)]
    [string]$ExpectedPublicKeySpkiBase64,

    [string]$KeyId = 'peeronq-website-pilot-1',

    [string]$OutputDirectory,

    [string]$WindowsClientMsiPath,

    [version]$WindowsClientVersion,

    [switch]$IncludeUnsignedWindowsPilot,

    [uri]$TrackedDownloadBaseUrl,

    [switch]$SignedWindowsDownloadsAvailable
)

$ErrorActionPreference = 'Stop'
if ($WindowsClientMsiPath -or $null -ne $WindowsClientVersion -or $IncludeUnsignedWindowsPilot -or $PSBoundParameters.ContainsKey('TrackedDownloadBaseUrl') -or $SignedWindowsDownloadsAvailable) {
    throw 'Website patches cannot select or embed Windows clients. Publish client changes only through a signed full server release.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "dist\website\$Version" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$privateKey = (Resolve-Path -LiteralPath $PrivateKeyPemPath).Path
if ((Get-Item -LiteralPath $privateKey).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw 'PrivateKeyPemPath must not be a symbolic link or reparse point.'
}
if ($KeyId.Length -lt 3 -or $KeyId.Length -gt 128) { throw 'KeyId must contain 3-128 characters.' }
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("peeronq-website-{0}" -f [Guid]::NewGuid().ToString('N'))
$websiteRoot = Join-Path $temporaryRoot 'public'
$archiveName = "PeerOnQ-website-$Version.zip"
$archivePath = Join-Path $OutputDirectory $archiveName
$manifestPath = Join-Path $OutputDirectory 'manifest.json'
$checksumPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'

function Restore-EnvironmentValue([string]$Name, [string]$Value, [bool]$Existed) {
    if ($Existed) { [Environment]::SetEnvironmentVariable($Name, $Value, 'Process') }
    else { [Environment]::SetEnvironmentVariable($Name, $null, 'Process') }
}

$environmentNames = @(
    'BASE_PATH',
    'VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL',
    'VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE',
    'VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION',
    'VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE',
    'VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL',
    'VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION',
    'VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT',
    'VITE_PEERONQ_MACOS_DOWNLOAD_URL',
    'VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL',
    'VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL',
    'VITE_PEERONQ_ANDROID_DOWNLOAD_URL',
    'VITE_PEERONQ_IOS_DOWNLOAD_URL'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [pscustomobject]@{
        Existed = Test-Path "Env:$name"
        Value = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
}

try {
    New-Item -ItemType Directory -Path $websiteRoot -Force | Out-Null
    if (Test-Path -LiteralPath $OutputDirectory) {
        foreach ($file in @($archivePath, $manifestPath, $checksumPath)) {
            if (Test-Path -LiteralPath $file) { throw "Refusing to overwrite existing website release artifact: $file" }
        }
    }

    $env:BASE_PATH = '/'
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL', $null, 'Process')
    $env:VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE = 'false'
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION', $null, 'Process')
    $env:VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE = 'true'
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL', $null, 'Process')
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION', $null, 'Process')
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT', $null, 'Process')
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_MACOS_DOWNLOAD_URL', $null, 'Process')
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_LINUX_X64_DOWNLOAD_URL', $null, 'Process')
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_LINUX_ARM64_DOWNLOAD_URL', $null, 'Process')
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_ANDROID_DOWNLOAD_URL', $null, 'Process')
    [Environment]::SetEnvironmentVariable('VITE_PEERONQ_IOS_DOWNLOAD_URL', $null, 'Process')

    & pnpm --filter @workspace/peeronq exec vite build --config vite.config.ts --outDir $websiteRoot --emptyOutDir
    if ($LASTEXITCODE -ne 0) { throw 'The public website build failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $websiteRoot 'index.html') -PathType Leaf)) {
        throw 'The website build did not produce index.html.'
    }
    Copy-Item -LiteralPath (Join-Path $websiteRoot 'index.html') `
        -Destination (Join-Path $websiteRoot 'peeronq-downloads-ui-v1.html')

    # Vite copies the source public directory. A website signature never authorizes client binaries,
    # so remove every local download artifact before the static archive is signed.
    $downloads = Join-Path $websiteRoot 'downloads'
    if (Test-Path -LiteralPath $downloads) { [IO.Directory]::Delete($downloads, $true) }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory($websiteRoot, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $archiveInfo = Get-Item -LiteralPath $archivePath
    if ($archiveInfo.Length -gt 96MB) { throw 'The compressed website release exceeds 96 MiB.' }
    $archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

    & dotnet run --project (Join-Path $repoRoot 'src\PeerOnQ.Website.ReleaseTool\PeerOnQ.Website.ReleaseTool.csproj') `
        --configuration Release -- `
        --version $Version `
        --archive $archivePath `
        --private-key $privateKey `
        --expected-public-key $ExpectedPublicKeySpkiBase64 `
        --key-id $KeyId `
        --output $manifestPath
    if ($LASTEXITCODE -ne 0) { throw 'The website release signing tool rejected the release.' }

    $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(
        $checksumPath,
        "$archiveSha256  $archiveName`n$manifestSha256  manifest.json`n",
        [Text.UTF8Encoding]::new($false))
    Write-Host "Website archive:  $archivePath"
    Write-Host "Signed manifest:  $manifestPath"
    Write-Host "Archive SHA-256:  $archiveSha256"
}
finally {
    foreach ($name in $environmentNames) {
        Restore-EnvironmentValue $name $savedEnvironment[$name].Value $savedEnvironment[$name].Existed
    }
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
