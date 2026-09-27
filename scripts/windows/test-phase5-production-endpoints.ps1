<# Verifies that official releases embed only public-style DNS service endpoints. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$validator = Join-Path $PSScriptRoot 'assert-phase5-production-endpoints.ps1'
$validEndpoints = @{
    UpdateBaseUrl      = [uri]'https://updates.peeronq.com/releases'
    SignalingUrl       = [uri]'wss://signal.peeronq.com/ws'
    ApiBaseUrl         = [uri]'https://api.peeronq.com'
    PresenceUrl        = [uri]'https://presence.peeronq.com'
    DownloadsBaseUrl   = [uri]'https://download.peeronq.com'
    DiagnosticsBaseUrl = [uri]'https://api.peeronq.com/diagnostics'
    TimestampUrl       = [uri]'https://timestamp.digicert.com'
}

& $validator @validEndpoints

function Assert-Rejected([string]$Name, [uri]$Value) {
    $candidate = $validEndpoints.Clone()
    $candidate[$Name] = $Value
    try {
        & $validator @candidate
    } catch {
        if ($_.Exception.Message -notmatch [regex]::Escape($Name)) {
            throw "The $Name rejection did not identify the invalid endpoint: $($_.Exception.Message)"
        }
        return
    }

    throw "$Name accepted a non-production host: $Value"
}

Assert-Rejected -Name 'UpdateBaseUrl' -Value ([uri]'https://10.0.0.10/releases')
Assert-Rejected -Name 'SignalingUrl' -Value ([uri]'wss://signal.10.0.0.10.sslip.io:5443/ws')
Assert-Rejected -Name 'ApiBaseUrl' -Value ([uri]'https://api.dev.localhost')
Assert-Rejected -Name 'PresenceUrl' -Value ([uri]'https://presence.internal')
Assert-Rejected -Name 'DownloadsBaseUrl' -Value ([uri]'https://download.10.0.0.10.nip.io')
Assert-Rejected -Name 'DiagnosticsBaseUrl' -Value ([uri]'https://diagnostics')
Assert-Rejected -Name 'TimestampUrl' -Value ([uri]'https://127.0.0.1')

Write-Host 'Phase 5 production endpoint policy validation passed.'
