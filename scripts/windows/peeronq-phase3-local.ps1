<#
    Local Phase 3 controller for Windows.

    It creates a repository-scoped development certificate and secret under .peeronq-phase3,
    trusts only the generated public root in CurrentUser, and binds every published port to the
    explicit local address. Loopback remains the safe default. Docker mode starts signaling,
    coturn, and Nginx. Without Docker, the script starts native HTTPS signaling and reports that
    relay validation is unavailable.
#>

[CmdletBinding()]
param(
    [ValidateSet('setup', 'start', 'stop', 'restart', 'status', 'test')]
    [string]$Action = 'status',
    [string]$BindAddress = '127.0.0.1',
    [ValidateRange(1024, 65535)]
    [int]$TurnPort = 3478,
    [ValidateRange(1024, 65535)]
    [int]$TurnTlsPort = 5349,
    [ValidateRange(1024, 65535)]
    [int]$TurnRelayMinPort = 49160,
    [ValidateRange(1024, 65535)]
    [int]$TurnRelayMaxPort = 49200,
    [string]$SignalHost,
    [string]$TurnHost,
    [switch]$ConfigureFirewall,
    [switch]$RotateCertificate
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
$CanonicalClientVersion = Get-PeerOnQClientVersion -RepositoryRoot $RepoRoot
$StateDir = Join-Path $RepoRoot '.peeronq-phase3'
$CertificateDir = Join-Path $StateDir 'certs'
$RuntimeDir = Join-Path $StateDir 'runtime'
$PidFile = Join-Path $StateDir 'signaling.pid'
$ModeFile = Join-Path $StateDir 'mode'
$PfxPasswordFile = Join-Path $StateDir 'certificate-password'
$TurnSecretFile = Join-Path $StateDir 'turn-shared-secret'
$DockerEnvironmentFile = Join-Path $StateDir 'docker.env'
$CertificateGenerator = Join-Path $PSScriptRoot 'PeerOnQ.LocalCertificate.cs'
$HttpsVerifier = Join-Path $PSScriptRoot 'PeerOnQ.LocalHttpsVerifier.cs'
$DockerNetworkHelper = Join-Path $PSScriptRoot 'PeerOnQ.LocalDockerNetwork.ps1'
$CertificateRootSubject = 'CN=PeerOnQ Phase 3 Local Development Root'
$ServerProject = Join-Path $RepoRoot 'src\PeerOnQ.Signaling.Server\PeerOnQ.Signaling.Server.csproj'
$ServerDll = Join-Path $RepoRoot 'src\PeerOnQ.Signaling.Server\bin\Debug\net10.0\PeerOnQ.Signaling.Server.dll'
$ComposeFile = Join-Path $RepoRoot 'src\PeerOnQ.Realtime.Deployment\docker-compose.local.yml'
$parsedBindAddress = $null
if (-not [Net.IPAddress]::TryParse($BindAddress, [ref]$parsedBindAddress) -or
    $parsedBindAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'BindAddress must be an IPv4 address assigned to this Windows computer.'
}
$BindAddress = $parsedBindAddress.ToString()
$IsLoopbackBinding = $parsedBindAddress.Equals([Net.IPAddress]::Loopback)
# sslip.io encodes the selected IPv4 address in DNS, so every LAN client resolves the same
# certificate name without a hosts-file edit or a public PeerOnQ DNS deployment.
$defaultSignalHost = "signal.$BindAddress.sslip.io"
$defaultTurnHost = "turn.$BindAddress.sslip.io"

function Resolve-DnsHost([string]$configuredHost, [string]$defaultHost, [string]$parameterName) {
    $candidate = if ([string]::IsNullOrWhiteSpace($configuredHost)) {
        $defaultHost
    } else {
        $configuredHost.Trim().TrimEnd('.')
    }
    if ($candidate.Length -gt 253 -or
        [Uri]::CheckHostName($candidate) -ne [UriHostNameType]::Dns) {
        throw "$parameterName must be a DNS hostname; IP literals are not accepted."
    }
    return $candidate.ToLowerInvariant()
}

$SignalHost = Resolve-DnsHost $SignalHost $defaultSignalHost 'SignalHost'
$TurnHost = Resolve-DnsHost $TurnHost $defaultTurnHost 'TurnHost'
$LanInterfaceAlias = $null
$LanInterfaceIndex = $null
$LanNetworkCategory = $null
if (-not $IsLoopbackBinding) {
    $localAddress = Get-NetIPAddress -AddressFamily IPv4 -IPAddress $BindAddress -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $localAddress) {
        throw "BindAddress $BindAddress is not assigned to this Windows computer."
    }
    $LanInterfaceAlias = $localAddress.InterfaceAlias
    $LanInterfaceIndex = $localAddress.InterfaceIndex
    $networkProfile = Get-NetConnectionProfile -InterfaceIndex $LanInterfaceIndex -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $LanNetworkCategory = if ($networkProfile) { $networkProfile.NetworkCategory.ToString() } else { 'Unknown' }
}

$TlsPort = 5443
$HealthUrl = "https://${SignalHost}:$TlsPort/health/ready"
$Marker = 'PeerOnQ.Signaling.Server.dll'
if ($TurnRelayMinPort -gt $TurnRelayMaxPort) {
    throw 'TurnRelayMinPort must not be greater than TurnRelayMaxPort.'
}
if ($TurnPort -eq $TurnTlsPort -or
    ($TurnPort -ge $TurnRelayMinPort -and $TurnPort -le $TurnRelayMaxPort) -or
    ($TurnTlsPort -ge $TurnRelayMinPort -and $TurnTlsPort -le $TurnRelayMaxPort)) {
    throw 'TURN listener ports must be distinct from each other and from the relay allocation range.'
}

$requiredPaths = @(
    (Join-Path $RepoRoot 'PeerOnQ.slnx'),
    $ServerProject,
    $CertificateGenerator,
    $HttpsVerifier,
    $DockerNetworkHelper,
    $ComposeFile
)
foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "This script is not inside a complete PeerOnQ workspace: missing $requiredPath"
    }
}
. $DockerNetworkHelper

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function New-RandomBase64([int]$byteCount) {
    $bytes = New-Object byte[] $byteCount
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
        return [Convert]::ToBase64String($bytes)
    } finally {
        $generator.Dispose()
    }
}

function Write-PrivateText([string]$path, [string]$value) {
    [IO.File]::WriteAllText($path, $value, [Text.Encoding]::ASCII)
}

function Convert-ToDockerPath([string]$path) {
    return ([IO.Path]::GetFullPath($path) -replace '\\', '/')
}

function Get-DockerExecutable {
    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin\docker.exe'),
        (Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe')
    )
    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Test-DockerReady([int]$timeoutMilliseconds = 3000) {
    $docker = Get-DockerExecutable
    if (-not $docker) { return $false }

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $docker
    $startInfo.Arguments = 'info --format "{{.ServerVersion}}"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        if (-not $process -or -not $process.WaitForExit($timeoutMilliseconds)) {
            if ($process -and -not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit()
            }
            return $false
        }
        return $process.ExitCode -eq 0
    } catch {
        return $false
    } finally {
        if ($process) { $process.Dispose() }
    }
}

function Get-LocalCertificateArtifactPaths {
    return @(
        (Join-Path $CertificateDir 'peeronq-local.pfx'),
        (Join-Path $CertificateDir 'root-ca.cer'),
        (Join-Path $CertificateDir 'fullchain.pem'),
        (Join-Path $CertificateDir 'privkey.pem'),
        (Join-Path $CertificateDir 'certificate.json')
    )
}

function Test-LocalCertificateArtifacts {
    $requiredArtifacts = Get-LocalCertificateArtifactPaths
    if ($requiredArtifacts | Where-Object { -not (Test-Path -LiteralPath $_) }) {
        return $false
    }

    try {
        $fullchain = Get-Content -LiteralPath (Join-Path $CertificateDir 'fullchain.pem') -Raw
        $metadata = Get-Content -LiteralPath (Join-Path $CertificateDir 'certificate.json') -Raw | ConvertFrom-Json
        return $fullchain -match '-----END CERTIFICATE-----\r?\n-----BEGIN CERTIFICATE-----' -and
            $metadata.signalHost -eq $SignalHost -and
            $metadata.turnHost -eq $TurnHost -and
            $metadata.bindAddress -eq $BindAddress -and
            $metadata.rootSubject -eq $CertificateRootSubject
    } catch {
        return $false
    }
}

function Initialize-LocalState {
    $certificateRegenerated = $false
    foreach ($directory in @($StateDir, $CertificateDir, $RuntimeDir)) {
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
    }

    if (-not (Test-Path -LiteralPath $PfxPasswordFile)) {
        Write-PrivateText $PfxPasswordFile (New-RandomBase64 36)
    }
    if (-not (Test-Path -LiteralPath $TurnSecretFile)) {
        Write-PrivateText $TurnSecretFile (New-RandomBase64 48)
    }

    $rootCertificatePath = Join-Path $CertificateDir 'root-ca.cer'
    if (-not (Test-LocalCertificateArtifacts)) {
        $existingCertificateArtifacts = @(Get-LocalCertificateArtifactPaths | Where-Object { Test-Path -LiteralPath $_ })
        if ($existingCertificateArtifacts.Count -gt 0 -and -not $RotateCertificate) {
            throw 'Existing Phase 3 certificate artifacts do not match the requested LAN identity. Re-run with the original bind/host values, or use -RotateCertificate explicitly and rebuild every pinned LAN client.'
        }
        if ($existingCertificateArtifacts.Count -gt 0) {
            $backupRoot = Join-Path $StateDir 'certificate-backups'
            $backupDirectory = Join-Path $backupRoot ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
            foreach ($artifact in $existingCertificateArtifacts) {
                Move-Item -LiteralPath $artifact -Destination $backupDirectory
            }
            Write-Host "Previous certificate artifacts were preserved at $backupDirectory" -ForegroundColor Yellow
        }
        $password = (Get-Content -LiteralPath $PfxPasswordFile -Raw).Trim()
        & dotnet run $CertificateGenerator -- $CertificateDir $password 'PeerOnQ Phase 3 Local Development Root' $SignalHost $TurnHost $BindAddress | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Local certificate generation failed.' }
        $certificateRegenerated = $true
    }

    $dockerLines = @(
        "PEERONQ_LOCAL_SIGNAL_HOST=$SignalHost",
        "PEERONQ_LOCAL_TURN_HOST=$TurnHost",
        "PEERONQ_LOCAL_BIND_ADDRESS=$BindAddress",
        "PEERONQ_LOCAL_TLS_PORT=$TlsPort",
        "PEERONQ_LOCAL_TURN_PORT=$TurnPort",
        "PEERONQ_LOCAL_TURN_TLS_PORT=$TurnTlsPort",
        "PEERONQ_LOCAL_TURN_RELAY_MIN_PORT=$TurnRelayMinPort",
        "PEERONQ_LOCAL_TURN_RELAY_MAX_PORT=$TurnRelayMaxPort",
        "PEERONQ_TURN_EXTERNAL_IP=$BindAddress",
        "PEERONQ_LOCAL_CERT_DIR=$(Convert-ToDockerPath $CertificateDir)",
        "PEERONQ_TURN_SHARED_SECRET_FILE=$(Convert-ToDockerPath $TurnSecretFile)",
        'PEERONQ_TURN_RELAY_ONLY=false'
    )
    [IO.File]::WriteAllLines($DockerEnvironmentFile, $dockerLines, [Text.Encoding]::ASCII)
    return $certificateRegenerated
}

function Initialize-LanFirewall {
    if ($IsLoopbackBinding) { return }

    if ($LanNetworkCategory -ne 'Private') {
        $profileMessage = "LAN interface '$LanInterfaceAlias' is '$LanNetworkCategory'. PeerOnQ LAN firewall rules are intentionally restricted to Private networks."
        if ($ConfigureFirewall) {
            throw "$profileMessage If this is a trusted LAN, run: Set-NetConnectionProfile -InterfaceIndex $LanInterfaceIndex -NetworkCategory Private"
        }
        Write-Host "$profileMessage Change it only on a trusted LAN before using -ConfigureFirewall." -ForegroundColor Yellow
    }

    if (-not $ConfigureFirewall) {
        Write-Host 'Continuing without changing Windows Firewall. For LAN clients, run once from Administrator PowerShell with -ConfigureFirewall to add Private/LocalSubnet inbound rules.' -ForegroundColor Yellow
        return
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '-ConfigureFirewall requires an Administrator PowerShell window.'
    }

    $rules = @(
        [PSCustomObject]@{ Name = 'PeerOnQ Local Signaling TLS'; Protocol = 'TCP'; Ports = @("$TlsPort") },
        [PSCustomObject]@{ Name = 'PeerOnQ Local TURN TCP'; Protocol = 'TCP'; Ports = @("$TurnPort", "$TurnTlsPort") },
        [PSCustomObject]@{ Name = 'PeerOnQ Local TURN UDP'; Protocol = 'UDP'; Ports = @("$TurnPort", "$TurnTlsPort", "$TurnRelayMinPort-$TurnRelayMaxPort") }
    )
    foreach ($rule in $rules) {
        Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue |
            Where-Object Group -EQ 'PeerOnQ Local Development' |
            Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $rule.Name -Group 'PeerOnQ Local Development' `
            -Direction Inbound -Action Allow -Enabled True -Profile Private `
            -Protocol $rule.Protocol -LocalPort $rule.Ports -LocalAddress $BindAddress `
            -RemoteAddress LocalSubnet | Out-Null
        Write-Host "Added Windows Firewall rule: $($rule.Name)" -ForegroundColor Green
    }
}

function Get-TrackedNativeProcess {
    if (-not (Test-Path -LiteralPath $PidFile)) { return $null }
    $raw = (Get-Content -LiteralPath $PidFile -Raw).Trim()
    if ($raw -notmatch '^\d+$') { return $null }

    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$raw" -ErrorAction SilentlyContinue
    if (-not $process -or $process.CommandLine -notlike "*$Marker*") { return $null }
    return $process
}

function Get-ProcessTreeIds([int]$rootId) {
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
    $ids = New-Object System.Collections.Generic.List[int]
    $ids.Add($rootId) | Out-Null
    $grew = $true
    while ($grew) {
        $grew = $false
        foreach ($process in $all) {
            if ($ids -contains [int]$process.ParentProcessId -and -not ($ids -contains [int]$process.ProcessId)) {
                $ids.Add([int]$process.ProcessId) | Out-Null
                $grew = $true
            }
        }
    }
    return $ids
}

function Get-LocalHttpsStatusCode([string]$uri, [int]$timeoutSeconds) {
    $result = @(
        & dotnet run $HttpsVerifier -- (Join-Path $CertificateDir 'root-ca.cer') $uri $timeoutSeconds)
    if ($LASTEXITCODE -ne 0) {
        throw "Pinned TLS request failed for $uri."
    }
    $status = $result | Select-Object -Last 1
    if ($status -notmatch '^\d{3}$') { throw "Pinned TLS verifier returned an invalid status for $uri." }
    return [int]$status
}

function Test-LocalHealth {
    try {
        return (Get-LocalHttpsStatusCode $HealthUrl 5) -eq 200
    } catch {
        return $false
    }
}

function Wait-ForHealth([int]$seconds) {
    for ($attempt = 0; $attempt -lt $seconds; $attempt++) {
        if (Test-LocalHealth) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Invoke-DockerCompose([string[]]$arguments) {
    $docker = Get-DockerExecutable
    if (-not $docker) { throw 'Docker CLI is unavailable.' }
    $composeArguments = @(
        'compose',
        '--env-file',
        "`"$DockerEnvironmentFile`"",
        '-f',
        "`"$ComposeFile`""
    ) + $arguments
    $process = Start-Process -FilePath $docker `
        -ArgumentList $composeArguments `
        -NoNewWindow `
        -Wait `
        -PassThru
    return [int]$process.ExitCode
}

function New-TurnRestCredential([DateTimeOffset]$expiresAt, [string]$subject) {
    $username = "$($expiresAt.ToUnixTimeSeconds()):$subject"
    $secret = (Get-Content -LiteralPath $TurnSecretFile -Raw).Trim()
    $hmac = [Security.Cryptography.HMACSHA1]::new([Text.Encoding]::UTF8.GetBytes($secret))
    try {
        $credential = [Convert]::ToBase64String(
            $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($username)))
    } finally {
        $hmac.Dispose()
    }
    return [PSCustomObject]@{ Username = $username; Credential = $credential }
}

function Start-NativeSignaling {
    & dotnet build $ServerProject --no-restore
    if ($LASTEXITCODE -ne 0) { return 1 }

    $environmentValues = @{
        'ASPNETCORE_ENVIRONMENT' = 'Development'
        'ASPNETCORE_URLS' = "https://${BindAddress}:$TlsPort"
        'ASPNETCORE_Kestrel__Certificates__Default__Path' = (Join-Path $CertificateDir 'peeronq-local.pfx')
        'ASPNETCORE_Kestrel__Certificates__Default__Password' = (Get-Content -LiteralPath $PfxPasswordFile -Raw).Trim()
        'AllowedHosts' = $SignalHost
        'Signaling__ServerId' = 'signal-local-native-1'
        'Signaling__RequireTlsOutsideLoopback' = 'true'
        'Signaling__PinStorePath' = (Join-Path $StateDir 'device-pins.json')
        'Signaling__Attestation__Required' = 'false'
        'Signaling__Attestation__AllowDevelopmentTofuFallback' = 'true'
        'Signaling__Turn__Realm' = $TurnHost
        'Signaling__Turn__ServerId' = 'turn-not-running'
        'Signaling__Turn__Region' = 'local-signaling-only'
    }
    $savedEnvironment = @{}
    foreach ($entry in $environmentValues.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    try {
        $process = Start-Process -FilePath 'dotnet' `
            -ArgumentList "`"$ServerDll`" --environment Development" `
            -WorkingDirectory $RuntimeDir `
            -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $RuntimeDir 'signaling.stdout.log') `
            -RedirectStandardError (Join-Path $RuntimeDir 'signaling.stderr.log') `
            -PassThru
    } finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }

    Write-PrivateText $PidFile "$($process.Id)"
    Write-PrivateText $ModeFile 'native'
    if (-not (Wait-ForHealth 45)) {
        Write-Host "Native signaling did not become healthy. See $RuntimeDir." -ForegroundColor Red
        return 1
    }

    Write-Host "Native HTTPS signaling is ready at $HealthUrl" -ForegroundColor Green
    Write-Host 'TURN relay is not running because Docker Engine is unavailable.' -ForegroundColor Yellow
    return 0
}

function Invoke-Start {
    $certificateRegenerated = Initialize-LocalState
    Initialize-LanFirewall

    if (Test-DockerReady) {
        Initialize-PeerOnQLocalInternalDockerNetwork -DockerExecutable (Get-DockerExecutable)
        # A healthy container can still contain an older signaling protocol. Always ask Compose to
        # rebuild from the current source; Docker keeps unchanged layers and recreates only services
        # whose image or configuration actually changed.
        $composeArguments = @('up', '-d', '--build')
        if ($certificateRegenerated) { $composeArguments += '--force-recreate' }
        $exitCode = Invoke-DockerCompose $composeArguments
        if ($exitCode -ne 0) { return $exitCode }
        Write-PrivateText $ModeFile 'docker'
        if (-not (Wait-ForHealth 90)) {
            Write-Host 'Docker stack did not become healthy.' -ForegroundColor Red
            Invoke-DockerCompose @('ps') | Out-Null
            return 1
        }
        Write-Host "Full local signaling/coturn stack is ready at $HealthUrl" -ForegroundColor Green
        return 0
    }

    if (Test-LocalHealth) {
        Write-Host "PeerOnQ Phase 3 is already healthy at $HealthUrl" -ForegroundColor Green
        return 0
    }

    return Start-NativeSignaling
}

function Invoke-Stop {
    $mode = if (Test-Path -LiteralPath $ModeFile) { (Get-Content -LiteralPath $ModeFile -Raw).Trim() } else { '' }
    if ($mode -eq 'docker' -and (Test-DockerReady)) {
        return Invoke-DockerCompose @('down')
    }

    $process = Get-TrackedNativeProcess
    if ($process) {
        $processIds = @(Get-ProcessTreeIds ([int]$process.ProcessId))
        [array]::Reverse($processIds)
        foreach ($processId in $processIds) {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
    }
    if (Test-Path -LiteralPath $PidFile) { Remove-Item -LiteralPath $PidFile -Force }
    Write-Host 'Local PeerOnQ Phase 3 processes stopped.' -ForegroundColor Green
    return 0
}

function Invoke-Status {
    $mode = if (Test-Path -LiteralPath $ModeFile) { (Get-Content -LiteralPath $ModeFile -Raw).Trim() } else { 'not-started' }
    Write-Host "Mode: $mode"
    Write-Host "Bind: $BindAddress ($(if ($IsLoopbackBinding) { 'this computer only' } else { 'LAN' }))"
    if (-not $IsLoopbackBinding) {
        Write-Host "LAN interface: $LanInterfaceAlias (index $LanInterfaceIndex, profile $LanNetworkCategory)"
    }
    Write-Host "Client URL: wss://${SignalHost}:$TlsPort/ws"
    Write-Host "Health URL: $HealthUrl"
    Write-Host "TURN: ${TurnHost}:$TurnPort (TLS $TurnTlsPort, relay $TurnRelayMinPort-$TurnRelayMaxPort)"
    Write-Host "Client root certificate: $(Join-Path $CertificateDir 'root-ca.cer')"
    if (-not $IsLoopbackBinding) {
        Write-Host 'Development client build:'
        Write-Host "  .\scripts\windows\build-phase5-development.ps1 -Version $CanonicalClientVersion -SignalingUrl 'wss://${SignalHost}:$TlsPort/ws' -Architectures x64 -DevelopmentRootCertificate '.peeronq-phase3\certs\root-ca.cer' -SkipWebsitePublish"
    }
    Write-Host "Health: $(if (Test-LocalHealth) { 'ready' } else { 'unavailable' })"
    Write-Host "Docker Engine: $(if (Test-DockerReady) { 'ready' } else { 'unavailable' })"
    if ($mode -eq 'docker' -and (Test-DockerReady)) {
        return Invoke-DockerCompose @('ps')
    }
    return 0
}

function Invoke-Test {
    $exitCode = Invoke-Start
    if ($exitCode -ne 0) { return $exitCode }

    foreach ($path in @('/health/live', '/health/ready')) {
        $statusCode = Get-LocalHttpsStatusCode "https://${SignalHost}:$TlsPort$path" 10
        if ($statusCode -ne 200) { throw "$path returned HTTP $statusCode." }
    }

    $mode = (Get-Content -LiteralPath $ModeFile -Raw).Trim()
    if ($mode -eq 'docker') {
        foreach ($path in @('/health', '/metrics')) {
            $exitCode = Invoke-DockerCompose @(
                'exec', '-T', 'signaling', 'curl', '--fail', '--silent', '--show-error',
                '--header', "Host:$SignalHost", "http://127.0.0.1:8080$path")
            if ($exitCode -ne 0) { return $exitCode }
        }
    } else {
        foreach ($path in @('/health', '/metrics')) {
            $statusCode = Get-LocalHttpsStatusCode "https://${SignalHost}:$TlsPort$path" 10
            if ($statusCode -ne 200) { throw "$path returned HTTP $statusCode." }
        }
    }

    if ($mode -eq 'docker') {
        # A killed acceptance host cannot release its TURN allocations. Reset only the local relay
        # before the suite so repeated/crash-recovery runs do not inherit stale allocation quota.
        $exitCode = Invoke-DockerCompose @('restart', 'turn')
        if ($exitCode -ne 0) { return $exitCode }
    }

    & dotnet test (Join-Path $RepoRoot 'tests\PeerOnQ.Signaling.Tests\PeerOnQ.Signaling.Tests.csproj') `
        --no-restore -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { return $LASTEXITCODE }
    & dotnet test (Join-Path $RepoRoot 'tests\PeerOnQ.EndToEnd.Tests\PeerOnQ.EndToEnd.Tests.csproj') `
        --no-restore -c Release | Out-Host
    if ($LASTEXITCODE -ne 0) { return $LASTEXITCODE }

    if ($mode -eq 'docker') {
        $exitCode = Invoke-DockerCompose @('exec', '-T', 'turn', 'turnutils_stunclient', '-p', '3478', '127.0.0.1')
        if ($exitCode -ne 0) { return $exitCode }

        $credential = New-TurnRestCredential ([DateTimeOffset]::UtcNow.AddMinutes(5)) 'local-acceptance'
        $relayChecks = @(
            ,@('-v', '-Y', 'alloc', '-m', '1', '-n', '1', '-u', $credential.Username, '-w', $credential.Credential, '-p', '3478', '127.0.0.1')
            ,@('-t', '-v', '-Y', 'alloc', '-m', '1', '-n', '1', '-u', $credential.Username, '-w', $credential.Credential, '-p', '3478', '127.0.0.1')
            ,@('-S', '-v', '-Y', 'alloc', '-m', '1', '-n', '1', '-E', '/certs/fullchain.pem', '-u', $credential.Username, '-w', $credential.Credential, '-p', '5349', '127.0.0.1')
            ,@('-t', '-S', '-v', '-Y', 'alloc', '-m', '1', '-n', '1', '-E', '/certs/fullchain.pem', '-u', $credential.Username, '-w', $credential.Credential, '-p', '5349', '127.0.0.1')
        )
        foreach ($relayArguments in $relayChecks) {
            $exitCode = Invoke-DockerCompose (@('exec', '-T', 'turn', 'turnutils_uclient') + $relayArguments)
            if ($exitCode -ne 0) { return $exitCode }
        }

        $invalidExit = Invoke-DockerCompose @(
            'exec', '-T', 'turn', 'turnutils_uclient', '-v', '-Y', 'alloc', '-m', '1', '-n', '1',
            '-u', $credential.Username, '-w', 'invalid-local-credential', '-p', '3478', '127.0.0.1')
        if ($invalidExit -eq 0) { throw 'coturn accepted an invalid REST credential.' }

        $expired = New-TurnRestCredential ([DateTimeOffset]::UtcNow.AddMinutes(-5)) 'expired-local-acceptance'
        $expiredExit = Invoke-DockerCompose @(
            'exec', '-T', 'turn', 'turnutils_uclient', '-v', '-Y', 'alloc', '-m', '1', '-n', '1',
            '-u', $expired.Username, '-w', $expired.Credential, '-p', '3478', '127.0.0.1')
        if ($expiredExit -eq 0) { throw 'coturn accepted an expired REST credential.' }

        # Development signaling advertises these two client relay routes. The TLS/DTLS listener
        # is validated by the authenticated coturn allocation checks above, but is not advertised
        # to the LAN client as a WebRTC media route.
        $relayMediaChecks = @(
            [PSCustomObject]@{ Name = "TURN UDP $TurnPort"; Url = "turn:${TurnHost}:$TurnPort?transport=udp"; Interrupt = $true },
            [PSCustomObject]@{ Name = "TURN TCP $TurnPort"; Url = "turn:${TurnHost}:$TurnPort?transport=tcp"; Interrupt = $false }
        )
        foreach ($relayMediaCheck in $relayMediaChecks) {
            Write-Host "Running real relay-only media acceptance over $($relayMediaCheck.Name)..."
            $env:PEERONQ_LIVE_TURN_TEST = '1'
            $env:PEERONQ_LIVE_TURN_URL = $relayMediaCheck.Url
            $env:PEERONQ_LIVE_TURN_USERNAME = $credential.Username
            $env:PEERONQ_LIVE_TURN_CREDENTIAL = $credential.Credential
            $env:PEERONQ_LIVE_DOCKER_EXE = Get-DockerExecutable
            $env:PEERONQ_LIVE_COMPOSE_ENV_FILE = $DockerEnvironmentFile
            $env:PEERONQ_LIVE_COMPOSE_FILE = $ComposeFile
            if ($relayMediaCheck.Interrupt) {
                $env:PEERONQ_LIVE_TURN_INTERRUPT_TEST = '1'
            }
            try {
                & dotnet test (Join-Path $RepoRoot 'tests\PeerOnQ.Media.Tests\PeerOnQ.Media.Tests.csproj') `
                    --no-restore -c Release `
                    --filter 'FullyQualifiedName~Relay_only_video_flows_through_the_live_turn_server' | Out-Host
                if ($LASTEXITCODE -ne 0) { return $LASTEXITCODE }
            } finally {
                Remove-Item Env:PEERONQ_LIVE_TURN_TEST -ErrorAction SilentlyContinue
                Remove-Item Env:PEERONQ_LIVE_TURN_URL -ErrorAction SilentlyContinue
                Remove-Item Env:PEERONQ_LIVE_TURN_USERNAME -ErrorAction SilentlyContinue
                Remove-Item Env:PEERONQ_LIVE_TURN_CREDENTIAL -ErrorAction SilentlyContinue
                Remove-Item Env:PEERONQ_LIVE_TURN_INTERRUPT_TEST -ErrorAction SilentlyContinue
                Remove-Item Env:PEERONQ_LIVE_DOCKER_EXE -ErrorAction SilentlyContinue
                Remove-Item Env:PEERONQ_LIVE_COMPOSE_ENV_FILE -ErrorAction SilentlyContinue
                Remove-Item Env:PEERONQ_LIVE_COMPOSE_FILE -ErrorAction SilentlyContinue
            }
        }

        Write-Host "Running real relay-only file-transfer acceptance over TURN UDP $TurnPort..."
        $fileCredential = New-TurnRestCredential ([DateTimeOffset]::UtcNow.AddMinutes(5)) 'local-file-acceptance'
        $env:PEERONQ_LIVE_FILE_TRANSFER_TEST = '1'
        $env:PEERONQ_LIVE_TURN_URL = "turn:${TurnHost}:$TurnPort?transport=udp"
        $env:PEERONQ_LIVE_TURN_USERNAME = $fileCredential.Username
        $env:PEERONQ_LIVE_TURN_CREDENTIAL = $fileCredential.Credential
        try {
            & dotnet test (Join-Path $RepoRoot 'tests\PeerOnQ.Media.Tests\PeerOnQ.Media.Tests.csproj') `
                --no-restore -c Release `
                --filter 'FullyQualifiedName~File_transfer_flows_over_the_authorized_data_only_media_session' | Out-Host
            if ($LASTEXITCODE -ne 0) { return $LASTEXITCODE }
        } finally {
            Remove-Item Env:PEERONQ_LIVE_FILE_TRANSFER_TEST -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_TURN_URL -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_TURN_USERNAME -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_TURN_CREDENTIAL -ErrorAction SilentlyContinue
        }

        Write-Host 'Running live signaling restart/re-authentication acceptance...'
        $env:PEERONQ_LIVE_DOCKER_TEST = '1'
        $env:PEERONQ_LIVE_SIGNALING_URL = "wss://${SignalHost}:$TlsPort/ws"
        $env:PEERONQ_LIVE_DEVELOPMENT_ROOT_CERTIFICATE = Join-Path $CertificateDir 'root-ca.cer'
        $env:PEERONQ_LIVE_DOCKER_EXE = Get-DockerExecutable
        $env:PEERONQ_LIVE_COMPOSE_ENV_FILE = $DockerEnvironmentFile
        $env:PEERONQ_LIVE_COMPOSE_FILE = $ComposeFile
        try {
            & dotnet test (Join-Path $RepoRoot 'tests\PeerOnQ.Signaling.Tests\PeerOnQ.Signaling.Tests.csproj') `
                --no-restore -c Release `
                --filter 'FullyQualifiedName~Signaling_restart_reauthenticates_but_does_not_resume_lost_server_state' | Out-Host
            if ($LASTEXITCODE -ne 0) { return $LASTEXITCODE }
        } finally {
            Remove-Item Env:PEERONQ_LIVE_DOCKER_TEST -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_SIGNALING_URL -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_DEVELOPMENT_ROOT_CERTIFICATE -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_DOCKER_EXE -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_COMPOSE_ENV_FILE -ErrorAction SilentlyContinue
            Remove-Item Env:PEERONQ_LIVE_COMPOSE_FILE -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host 'SKIPPED: live coturn STUN/TURN checks require Docker Engine.' -ForegroundColor Yellow
    }

    Write-Host 'Local Phase 3 checks passed.' -ForegroundColor Green
    return 0
}

switch ($Action) {
    'setup' { Initialize-LocalState; exit 0 }
    'start' { exit (Invoke-Start) }
    'stop' { exit (Invoke-Stop) }
    'restart' {
        $stopCode = Invoke-Stop
        if ($stopCode -ne 0) { exit $stopCode }
        exit (Invoke-Start)
    }
    'status' { exit (Invoke-Status) }
    'test' { exit (Invoke-Test) }
}
