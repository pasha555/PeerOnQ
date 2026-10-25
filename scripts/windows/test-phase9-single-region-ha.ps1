<#
  Runs the local, single-host Phase 9 application-tier HA gate. The script intentionally
  does not call this profile multi-region and never removes volumes. Evidence is
  written under the ignored .peeronq-phase9 directory.
#>

[CmdletBinding()]
param(
    [ValidateRange(10, 10000)][int]$RequestCount = 200,
    [ValidateRange(1, 64)][int]$Concurrency = 8,
    [ValidateRange(15, 300)][int]$RecoveryTimeoutSeconds = 90,
    [switch]$IncludeSingleNodeDataRestart
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$DeploymentDir = Join-Path $RepoRoot 'src\PeerOnQ.Infrastructure.Deployment'
$EnvironmentFile = Join-Path $DeploymentDir '.env'
$ComposeFiles = @(
    Join-Path $DeploymentDir 'docker-compose.development.yml'
    Join-Path $DeploymentDir 'docker-compose.ha.yml'
)
$EvidenceRoot = Join-Path $RepoRoot '.peeronq-phase9\evidence'
$RunId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$EvidenceFile = Join-Path $EvidenceRoot "single-region-ha-$RunId.json"

if (-not (Test-Path -LiteralPath $EnvironmentFile)) {
    throw "Phase 6 environment file does not exist: $EnvironmentFile"
}

function Initialize-SignalingRedisSecret {
    # Close the read handle before appending. Windows PowerShell 5.1 can retain the lazy
    # ReadLines enumerator long enough for AppendAllText to collide with this same process.
    foreach ($line in [IO.File]::ReadAllLines($EnvironmentFile)) {
        if ($line -match '^\s*PEERONQ_REDIS_SIGNALING_PASSWORD\s*=\s*(.+)$' -and
            -not [string]::IsNullOrWhiteSpace($Matches[1].Trim().Trim('"').Trim("'"))) {
            return
        }
    }

    $bytes = New-Object byte[] 36
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
        $value = [Convert]::ToBase64String($bytes)
        [IO.File]::AppendAllText(
            $EnvironmentFile,
            "`r`nPEERONQ_REDIS_SIGNALING_PASSWORD=$value`r`n",
            [Text.Encoding]::ASCII)
    }
    finally {
        $generator.Dispose()
    }
    Write-Host 'Generated the ignored local signaling Redis password.' -ForegroundColor Green
}

Initialize-SignalingRedisSecret

$localCertificates = Join-Path $RepoRoot '.peeronq-phase6\certs'
$localSecrets = Join-Path $RepoRoot '.peeronq-phase6\secrets'
if (Test-Path -LiteralPath $localCertificates) {
    $env:PEERONQ_TLS_CERT_DIR = (Resolve-Path $localCertificates).Path -replace '\\', '/'
    $privateKey = Join-Path $localCertificates 'privkey.pem'
    if (Test-Path -LiteralPath $privateKey) {
        $env:PEERONQ_TLS_PRIVATE_KEY_FILE = (Resolve-Path $privateKey).Path -replace '\\', '/'
    }
}

$composeArguments = @('--env-file', $EnvironmentFile)
foreach ($file in $ComposeFiles) { $composeArguments += @('-f', $file) }

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & docker compose @composeArguments @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-Percentile {
    param([double[]]$Values, [ValidateRange(0, 100)][double]$Percentile)

    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(0, [Math]::Ceiling(($Percentile / 100) * $sorted.Count) - 1)
    return [Math]::Round($sorted[$index], 3)
}

function Invoke-EndpointLoad {
    param([string]$Name, [string]$Url, [int]$Count = $RequestCount)

    $curlArguments = @(
        '--parallel', '--parallel-max', $Concurrency,
        '--silent', '--show-error', '--ssl-no-revoke',
        '--connect-timeout', '5', '--max-time', '15',
        '--write-out', '%{http_code} %{time_total}\n'
    )
    1..$Count | ForEach-Object {
        $curlArguments += @('--output', 'NUL', '--url', $Url)
    }

    $started = [DateTimeOffset]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $previousErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& curl.exe @curlArguments 2>&1)
        $curlExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    $watch.Stop()

    $samples = [Collections.Generic.List[object]]::new()
    foreach ($line in $lines) {
        if ([string]$line -match '^(\d{3})\s+([0-9.]+)$') {
            $samples.Add([pscustomobject]@{
                Status = [int]$Matches[1]
                Milliseconds = [double]$Matches[2] * 1000
            })
        }
    }

    $successful = @($samples | Where-Object { $_.Status -ge 200 -and $_.Status -lt 400 })
    $latencies = @($successful | ForEach-Object { $_.Milliseconds })
    $errors = $Count - $successful.Count
    $result = [ordered]@{
        name = $Name
        url = $Url
        startedUtc = $started.ToString('O')
        requests = $Count
        concurrency = $Concurrency
        successes = $successful.Count
        errors = $errors
        errorRatePercent = [Math]::Round(($errors * 100.0) / $Count, 3)
        durationMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
        throughputRequestsPerSecond = if ($watch.Elapsed.TotalSeconds -gt 0) {
            [Math]::Round($Count / $watch.Elapsed.TotalSeconds, 3)
        } else { 0 }
        latencyMilliseconds = [ordered]@{
            p50 = Get-Percentile $latencies 50
            p95 = Get-Percentile $latencies 95
            p99 = Get-Percentile $latencies 99
            max = if ($latencies.Count) { [Math]::Round(($latencies | Measure-Object -Maximum).Maximum, 3) } else { $null }
        }
        curlExitCode = $curlExit
    }

    return [pscustomobject]$result
}

function Wait-ContainerHealthy {
    param([string]$Service)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($RecoveryTimeoutSeconds)
    do {
        $containerId = @(& docker compose @composeArguments ps -q $Service) | Select-Object -First 1
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect Compose service $Service." }
        if (-not [string]::IsNullOrWhiteSpace($containerId)) {
            $health = (& docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $containerId).Trim()
            if ($health -eq 'healthy') { return }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "$Service did not become healthy within $RecoveryTimeoutSeconds seconds."
}

function Wait-EndpointReady {
    param([string]$Url)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($RecoveryTimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) {
                $watch.Stop()
                return [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
            }
        }
        catch {
            # Expected while an interrupted dependency reconnects.
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "$Url did not recover within $RecoveryTimeoutSeconds seconds."
}

function Invoke-ReplicaLossDrill {
    param([string]$Service, [string]$Url)

    Invoke-Compose stop --timeout 30 $Service | Out-Null
    try {
        $recovery = Wait-EndpointReady $Url
        $load = Invoke-EndpointLoad "$Service-primary-stopped" $Url
        return [pscustomobject]@{
            service = $Service
            recoveryAfterStopMilliseconds = $recovery
            load = $load
            measuredDataLoss = 'not_applicable_health_read_only'
        }
    }
    finally {
        Invoke-Compose start $Service | Out-Null
        Wait-ContainerHealthy $Service
    }
}

function Get-ContainerStats {
    $names = @(
        'peeronq-phase6-development-cloud-api-1',
        'peeronq-phase6-development-cloud-api-ha-2-1',
        'peeronq-phase6-development-presence-1',
        'peeronq-phase6-development-presence-ha-2-1',
        'peeronq-phase6-development-downloads-1',
        'peeronq-phase6-development-downloads-ha-2-1',
        'peeronq-phase6-development-signaling-1',
        'peeronq-phase6-development-signaling-ha-2-1',
        'peeronq-phase6-development-postgres-1',
        'peeronq-phase6-development-redis-1',
        'peeronq-phase6-development-proxy-1'
    )
    $rows = @(& docker stats --no-stream --format '{{json .}}' @names)
    if ($LASTEXITCODE -ne 0) { throw 'Could not collect Docker resource statistics.' }
    return @($rows | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}

New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null

$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$operatingSystem = Get-CimInstance Win32_OperatingSystem
$result = [ordered]@{
    schemaVersion = 1
    runId = $RunId
    topology = 'single_host_single_region_stateless_ha_validation'
    multiRegion = $false
    profile = [ordered]@{
        requestsPerEndpoint = $RequestCount
        concurrency = $Concurrency
        transport = 'local HTTPS through Nginx; development CA validated; revocation lookup disabled only for curl because the local CA has no CRL endpoint'
    }
    host = [ordered]@{
        os = $operatingSystem.Caption
        osBuild = $operatingSystem.BuildNumber
        cpu = $processor.Name
        logicalProcessors = $processor.NumberOfLogicalProcessors
        memoryBytes = [uint64]$operatingSystem.TotalVisibleMemorySize * 1024
        docker = (& docker version --format '{{.Server.Version}}').Trim()
        powershell = $PSVersionTable.PSVersion.ToString()
    }
    startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    baseline = @()
    failover = @()
    dependencyRestart = @()
    resourceStatsBefore = @()
    resourceStatsAfter = @()
    finalStatus = 'running'
}

$endpoints = @(
    @{ Service = 'cloud-api'; Name = 'cloud-api'; Url = 'https://api.dev.localhost:8443/health/ready' },
    @{ Service = 'presence'; Name = 'presence'; Url = 'https://presence.dev.localhost:8443/health/ready' },
    @{ Service = 'downloads'; Name = 'downloads'; Url = 'https://download.dev.localhost:8443/health/ready' }
    @{ Service = 'signaling'; Name = 'signaling'; Url = 'https://signal.dev.localhost:8443/health/ready' }
)

try {
    Invoke-Compose config --quiet | Out-Null
    foreach ($service in @('cloud-api', 'cloud-api-ha-2', 'presence', 'presence-ha-2', 'downloads', 'downloads-ha-2', 'signaling', 'signaling-ha-2')) {
        Wait-ContainerHealthy $service
    }

    $result.resourceStatsBefore = @(Get-ContainerStats)
    foreach ($endpoint in $endpoints) {
        $measurement = Invoke-EndpointLoad "$($endpoint.Name)-baseline" $endpoint.Url
        $result.baseline += $measurement
        if ($measurement.errors -gt 0 -or $measurement.curlExitCode -ne 0) {
            throw "$($measurement.name) produced $($measurement.errors) errors out of $($measurement.requests) requests."
        }
    }
    foreach ($endpoint in $endpoints) {
        $measurement = Invoke-ReplicaLossDrill $endpoint.Service $endpoint.Url
        $result.failover += $measurement
        if ($measurement.load.errors -gt 0 -or $measurement.load.curlExitCode -ne 0) {
            throw "$($measurement.load.name) produced $($measurement.load.errors) errors out of $($measurement.load.requests) requests."
        }
    }

    if ($IncludeSingleNodeDataRestart) {
        foreach ($dependency in @('redis', 'postgres')) {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            Invoke-Compose restart $dependency | Out-Null
            Wait-ContainerHealthy $dependency
            $endpointRecovery = Wait-EndpointReady 'https://api.dev.localhost:8443/health/ready'
            $watch.Stop()
            $result.dependencyRestart += [pscustomobject]@{
                service = $dependency
                kind = 'single_node_interruption_not_failover'
                totalRecoveryMilliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 3)
                endpointRecoveryAfterContainerHealthyMilliseconds = $endpointRecovery
                measuredDataLoss = 'not_measured_no_test_owned_write'
            }
        }
    }

    $result.resourceStatsAfter = @(Get-ContainerStats)
    $result.finalStatus = 'passed'
}
catch {
    $result.finalStatus = 'failed'
    $result.failure = $_.Exception.Message
    throw
}
finally {
    foreach ($service in @('cloud-api', 'presence', 'downloads', 'signaling', 'redis', 'postgres')) {
        try {
            Invoke-Compose start $service | Out-Null
            Wait-ContainerHealthy $service
        }
        catch {
            Write-Warning "Could not restore $service automatically: $($_.Exception.Message)"
        }
    }
    $result.finishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $EvidenceFile -Encoding UTF8
    Write-Host "Phase 9 evidence: $EvidenceFile"
}

$result | ConvertTo-Json -Depth 8
