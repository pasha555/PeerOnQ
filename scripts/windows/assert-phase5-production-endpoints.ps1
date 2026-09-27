<# Validates the complete domain-based endpoint set embedded in an official PeerOnQ release. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$UpdateBaseUrl,
    [Parameter(Mandatory)][uri]$SignalingUrl,
    [Parameter(Mandatory)][uri]$ApiBaseUrl,
    [Parameter(Mandatory)][uri]$PresenceUrl,
    [Parameter(Mandatory)][uri]$DownloadsBaseUrl,
    [Parameter(Mandatory)][uri]$DiagnosticsBaseUrl,
    [Parameter(Mandatory)][uri]$TimestampUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-ProductionDomain([uri]$Endpoint, [string]$Name) {
    $hostName = $Endpoint.DnsSafeHost.TrimEnd('.').ToLowerInvariant()
    $developmentSuffixes = @('.localhost', '.local', '.lan', '.internal', '.sslip.io', '.nip.io')
    $isDevelopmentName = $hostName -eq 'localhost' -or
        @($developmentSuffixes | Where-Object { $hostName.EndsWith($_, [StringComparison]::Ordinal) }).Count -gt 0

    if ([Uri]::CheckHostName($hostName) -ne [UriHostNameType]::Dns -or
            -not $hostName.Contains('.') -or $isDevelopmentName) {
        throw "$Name must use a production DNS domain name. IP addresses, single-label hosts, localhost, private development suffixes, sslip.io, and nip.io are not allowed in official releases."
    }
}

$httpsEndpoints = [ordered]@{
    'UpdateBaseUrl'      = $UpdateBaseUrl
    'ApiBaseUrl'         = $ApiBaseUrl
    'PresenceUrl'        = $PresenceUrl
    'DownloadsBaseUrl'   = $DownloadsBaseUrl
    'DiagnosticsBaseUrl' = $DiagnosticsBaseUrl
    'TimestampUrl'       = $TimestampUrl
}

foreach ($entry in $httpsEndpoints.GetEnumerator()) {
    $endpoint = $entry.Value
    if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https' -or
            -not [string]::IsNullOrEmpty($endpoint.UserInfo) -or
            -not [string]::IsNullOrEmpty($endpoint.Query) -or
            -not [string]::IsNullOrEmpty($endpoint.Fragment)) {
        throw "$($entry.Key) must be an absolute HTTPS URL without credentials, a query string, or a fragment."
    }

    Assert-ProductionDomain -Endpoint $endpoint -Name $entry.Key
}

if (-not $SignalingUrl.IsAbsoluteUri -or $SignalingUrl.Scheme -ne 'wss' -or
        -not [string]::IsNullOrEmpty($SignalingUrl.UserInfo) -or
        -not [string]::IsNullOrEmpty($SignalingUrl.Query) -or
        -not [string]::IsNullOrEmpty($SignalingUrl.Fragment) -or
        $SignalingUrl.AbsolutePath.TrimEnd('/') -ne '/ws') {
    throw 'SignalingUrl must be an absolute wss:// URL ending in /ws without credentials, a query string, or a fragment.'
}

Assert-ProductionDomain -Endpoint $SignalingUrl -Name 'SignalingUrl'
