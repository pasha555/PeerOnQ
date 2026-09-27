<# Builds explicit unsigned Windows clients pinned to the public PeerOnQ pilot endpoints. #>
[CmdletBinding()]
param(
    [version]$Version,
    [ValidateSet('x64', 'arm64')]
    [string[]]$Architectures = @('x64'),
    [string]$OutputRoot,
    [ValidateSet('stable', 'beta')]
    [string]$UpdateChannel = 'beta',
    [ValidateLength(1, 2048)]
    [string]$UpdatePublicKeySpkiBase64,
    [ValidateLength(1, 128)]
    [string]$UpdateKeyId,
    [string]$PublisherCertificateSha256,
    [switch]$PublishLocalWebsiteDownloads,
    [switch]$IUnderstandThisIsNotProductionSigned
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
$canonicalClientVersion = Get-PeerOnQClientVersion -RepositoryRoot $repoRoot
if ($null -eq $Version) {
    $Version = $canonicalClientVersion
} else {
    [void](Assert-PeerOnQClientVersion -Version $Version -RepositoryRoot $repoRoot -Context 'Public-pilot client build')
}
if (-not $IUnderstandThisIsNotProductionSigned) {
    throw 'Pass -IUnderstandThisIsNotProductionSigned to create controlled-test clients.'
}

$arguments = @{
    Version = $Version
    SignalingUrl = [uri]'wss://signal.peeronq.com/ws'
    ApiBaseUrl = [uri]'https://api.peeronq.com/'
    PresenceUrl = [uri]'https://presence.peeronq.com/'
    UpdateBaseUrl = [uri]'https://updates.peeronq.com/'
    UpdateChannel = $UpdateChannel
    DownloadsBaseUrl = [uri]'https://download.peeronq.com/'
    DiagnosticsBaseUrl = [uri]'https://api.peeronq.com/'
    Region = 'az-1'
    Architectures = $Architectures
    SkipWebsitePublish = -not $PublishLocalWebsiteDownloads
    AllowUnsignedPublicPilot = $true
}
if ($PublishLocalWebsiteDownloads) { $arguments.PublishPublicPilotWebsiteDownloads = $true }
if ($UpdatePublicKeySpkiBase64) { $arguments.UpdatePublicKeySpkiBase64 = $UpdatePublicKeySpkiBase64 }
if ($UpdateKeyId) { $arguments.UpdateKeyId = $UpdateKeyId }
if ($PublisherCertificateSha256) { $arguments.PublisherCertificateSha256 = $PublisherCertificateSha256 }
if ($OutputRoot) { $arguments.OutputRoot = $OutputRoot }

& (Join-Path $PSScriptRoot 'build-phase5-development.ps1') @arguments
if ($LASTEXITCODE -ne 0) { throw 'Public-pilot client build failed.' }
