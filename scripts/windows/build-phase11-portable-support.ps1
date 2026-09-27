<# Builds the explicitly unsigned, no-install Portable Support client from the desktop codebase. #>
[CmdletBinding()]
param(
    [version]$Version,
    [ValidateSet('x64', 'arm64')]
    [string[]]$Architectures = @('x64'),
    [string]$OutputRoot,
    [uri]$SignalingUrl = 'wss://signal.peeronq.com/ws',
    [uri]$ApiBaseUrl = 'https://api.peeronq.com/',
    [uri]$PresenceUrl = 'https://presence.peeronq.com/',
    [uri]$UpdateBaseUrl = 'https://updates.peeronq.com/',
    [uri]$DownloadsBaseUrl = 'https://download.peeronq.com/',
    [uri]$DiagnosticsBaseUrl = 'https://api.peeronq.com/',
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,30}[a-z0-9]$')]
    [string]$Region = 'az-1',
    [switch]$IUnderstandThisIsNotProductionSigned
)

$ErrorActionPreference = 'Stop'
if (-not $IUnderstandThisIsNotProductionSigned) {
    throw 'Pass -IUnderstandThisIsNotProductionSigned to create development Portable Support packages.'
}

if (-not $SignalingUrl.IsAbsoluteUri -or $SignalingUrl.Scheme -ne 'wss' -or
    $SignalingUrl.AbsolutePath.TrimEnd('/') -ne '/ws' -or $SignalingUrl.UserInfo -or
    $SignalingUrl.Query -or $SignalingUrl.Fragment) {
    throw 'SignalingUrl must be an absolute wss:// URL ending in /ws without credentials, query, or fragment.'
}
foreach ($endpoint in @($ApiBaseUrl, $PresenceUrl, $UpdateBaseUrl, $DownloadsBaseUrl, $DiagnosticsBaseUrl)) {
    if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https' -or
        $endpoint.UserInfo -or $endpoint.Query -or $endpoint.Fragment) {
        throw 'Every cloud endpoint must be an absolute HTTPS URL without credentials, query, or fragment.'
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
$canonicalClientVersion = Get-PeerOnQClientVersion -RepositoryRoot $repoRoot
if ($null -eq $Version) {
    $Version = $canonicalClientVersion
} else {
    [void](Assert-PeerOnQClientVersion -Version $Version -RepositoryRoot $repoRoot -Context 'Portable Support build')
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot "dist\portable-support\$Version" }
$output = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must remain inside the repository.'
}
if (Test-Path -LiteralPath $output) { throw "Portable Support output already exists: $output" }
[IO.Directory]::CreateDirectory($output) | Out-Null

$architecturesToBuild = @($Architectures | Select-Object -Unique)
if ($architecturesToBuild.Count -eq 0) { throw 'Select at least one client architecture.' }
$archives = @()
foreach ($architecture in $architecturesToBuild) {
    $runtime = "win-$architecture"
    $publish = Join-Path $output "$architecture\PeerOnQ-Portable-Support"
    dotnet restore 'src\PeerOnQ.App\PeerOnQ.App.csproj' -r $runtime | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet publish 'src\PeerOnQ.App\PeerOnQ.App.csproj' --no-restore -c Release `
        -r $runtime --self-contained true -o $publish `
        "-p:Version=$Version" "-p:AssemblyVersion=$Version.0" "-p:FileVersion=$Version.0" `
        "-p:InformationalVersion=$Version-portable-support-unsigned-development" `
        "-p:PeerOnQSignalingUrl=$($SignalingUrl.AbsoluteUri)" `
        "-p:PeerOnQApiBaseUrl=$($ApiBaseUrl.AbsoluteUri)" `
        "-p:PeerOnQPresenceUrl=$($PresenceUrl.AbsoluteUri)" `
        "-p:PeerOnQUpdatesBaseUrl=$($UpdateBaseUrl.AbsoluteUri)" `
        "-p:PeerOnQDownloadsBaseUrl=$($DownloadsBaseUrl.AbsoluteUri)" `
        "-p:PeerOnQDiagnosticsBaseUrl=$($DiagnosticsBaseUrl.AbsoluteUri)" `
        '-p:PeerOnQDeploymentEnvironment=Production' "-p:PeerOnQRegion=$Region" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    $manifest = [ordered]@{
        product = 'PeerOnQ Portable Support'
        version = $Version.ToString()
        architecture = $architecture
        channel = 'development-unsigned'
        entrypoint = 'PeerOnQ.exe'
        portableSupport = $true
        persistence = 'ephemeral-user-temp-profile'
        unattendedAccess = $false
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $publish 'PeerOnQ.PortableSupport.json'),
        $manifest,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllLines(
        (Join-Path $publish 'PORTABLE-SUPPORT-README.txt'),
        @(
            'PeerOnQ Portable Support - DEVELOPMENT / UNSIGNED',
            'Run PeerOnQ.exe directly; no installation or Windows service is used.',
            'The app creates an ephemeral local profile and deletes it during normal shutdown.',
            'Unattended access is disabled. Every support session still requires a valid invitation and explicit remote Accept.',
            'Do not distribute this package as an official release until Authenticode signing and external release validation are complete.'
        ),
        [Text.UTF8Encoding]::new($false))

    $fileChecksums = Get-ChildItem -LiteralPath $publish -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($publish.Length).TrimStart('\', '/').Replace('\', '/')
            $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    [IO.File]::WriteAllLines(
        (Join-Path $publish 'SHA256SUMS.txt'),
        $fileChecksums,
        [Text.UTF8Encoding]::new($false))

    $archive = Join-Path $output "PeerOnQ-$Version-portable-support-unsigned-development-$architecture.zip"
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive -CompressionLevel Optimal
    $archives += $archive
}

$archiveChecksums = $archives | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($_))"
}
[IO.File]::WriteAllLines(
    (Join-Path $output 'SHA256SUMS.txt'),
    $archiveChecksums,
    [Text.UTF8Encoding]::new($false))
Write-Host "Unsigned Portable Support artifacts: $output"
