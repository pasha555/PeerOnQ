<#
    PeerOnQ Phase 6 development controller for Windows.

    Starts the real Dockerized Cloud, Admin, Presence, Downloads, signaling,
    PostgreSQL, Redis and observability stack. Local state and certificates stay
    under .peeronq-phase6; database/Redis volumes are preserved on stop.
#>

[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'restart', 'status')]
    [string]$Action = 'status',
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$DeploymentDir = Join-Path $RepoRoot 'src\PeerOnQ.Infrastructure.Deployment'
$ComposeFile = Join-Path $DeploymentDir 'docker-compose.development.yml'
$LocalObservabilityComposeFile = Join-Path $DeploymentDir 'docker-compose.local-phase3-observability.yml'
$EnvironmentFile = Join-Path $DeploymentDir '.env'
$ComposeFiles = @('-f', $ComposeFile, '-f', $LocalObservabilityComposeFile)
$ComposeProjectName = 'peeronq-phase6-development'
$CertificateGenerator = Join-Path $PSScriptRoot 'PeerOnQ.LocalCertificate.cs'
$DockerNetworkHelper = Join-Path $PSScriptRoot 'PeerOnQ.LocalDockerNetwork.ps1'
$CertificateRootSubject = 'CN=PeerOnQ Phase 6 Local Development Root'
$StateDir = Join-Path $RepoRoot '.peeronq-phase6'
$CertificateDir = Join-Path $StateDir 'certs'
$CertificatePasswordFile = Join-Path $StateDir 'certificate-password'
$SecretsDir = Join-Path $StateDir 'secrets'
$BackupDir = Join-Path $StateDir 'backups'
$GrafanaPasswordFile = Join-Path $SecretsDir 'grafana-admin-password'
$AlertmanagerTokenFile = Join-Path $SecretsDir 'alertmanager-webhook-token'
$AttestationPrivateKeyFile = Join-Path $SecretsDir 'signaling-attestation-private.pem'
$AttestationPublicKeyFile = Join-Path $SecretsDir 'signaling-attestation-public.pem'
$PostgresBackupPgpassFile = Join-Path $SecretsDir 'postgres-backup.pgpass'
$PostgresRestorePgpassFile = Join-Path $SecretsDir 'postgres-restore.pgpass'

$ApiHost = 'api.dev.localhost'
$AdminHost = 'admin.dev.localhost'
$PortalHost = 'portal.dev.localhost'
$GrafanaHost = 'grafana.dev.localhost'
$DownloadHost = 'download.dev.localhost'
$PresenceHost = 'presence.dev.localhost'
$SignalHost = 'signal.dev.localhost'

$requiredPaths = @(
    (Join-Path $RepoRoot 'PeerOnQ.slnx'),
    $ComposeFile,
    $LocalObservabilityComposeFile,
    $CertificateGenerator,
    $DockerNetworkHelper,
    (Join-Path $RepoRoot 'artifacts\peeronq-admin\package.json')
    (Join-Path $RepoRoot 'artifacts\peeronq-portal\package.json')
)
foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "This script is not inside a complete PeerOnQ workspace: missing $requiredPath"
    }
}
. $DockerNetworkHelper

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host '============================================' -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host '============================================' -ForegroundColor Cyan
}

function Get-DotEnvValue([string]$name) {
    if (-not (Test-Path -LiteralPath $EnvironmentFile)) { return $null }
    $escapedName = [Regex]::Escape($name)
    # ReadAllLines closes the file before callers may append a generated secret. PowerShell 5.1
    # can otherwise retain ReadLines' lazy enumerator and make this script lock its own .env file.
    foreach ($line in [IO.File]::ReadAllLines($EnvironmentFile)) {
        if ($line -match "^\s*$escapedName\s*=\s*(.*)$") {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $null
}

function Get-ConfiguredPort([string]$name, [int]$defaultValue) {
    $raw = Get-DotEnvValue $name
    if ([string]::IsNullOrWhiteSpace($raw)) { return $defaultValue }
    $parsed = 0
    if (-not [int]::TryParse($raw, [ref]$parsed) -or $parsed -lt 1024 -or $parsed -gt 65535) {
        throw "$name must be a port from 1024 through 65535."
    }
    return $parsed
}

$HttpsPort = Get-ConfiguredPort 'PEERONQ_HTTPS_PORT' 8443
$PrometheusPort = Get-ConfiguredPort 'PEERONQ_PROMETHEUS_PORT' 9090
$AdminUrl = "https://${AdminHost}:$HttpsPort"
$PortalUrl = "https://${PortalHost}:$HttpsPort"
$CloudHealthUrl = "https://${ApiHost}:$HttpsPort/health/ready"
$GrafanaUrl = "https://${GrafanaHost}:$HttpsPort"
$PrometheusUrl = "http://localhost:$PrometheusPort"

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

function Initialize-LocalCustomerSecret {
    if (-not [string]::IsNullOrWhiteSpace((Get-DotEnvValue 'PEERONQ_CUSTOMER_TOKEN_SIGNING_KEY'))) { return }
    $value = New-RandomBase64 64
    [IO.File]::AppendAllText($EnvironmentFile, "`r`nPEERONQ_CUSTOMER_TOKEN_SIGNING_KEY=$value`r`n", [Text.Encoding]::ASCII)
    Write-Host 'Generated the ignored local customer-portal signing key.' -ForegroundColor Green
}

function Initialize-LocalSignalingRedisSecret {
    if (-not [string]::IsNullOrWhiteSpace((Get-DotEnvValue 'PEERONQ_REDIS_SIGNALING_PASSWORD'))) { return }
    $value = New-RandomBase64 36
    [IO.File]::AppendAllText($EnvironmentFile, "`r`nPEERONQ_REDIS_SIGNALING_PASSWORD=$value`r`n", [Text.Encoding]::ASCII)
    Write-Host 'Generated the ignored local signaling Redis password.' -ForegroundColor Green
}

function Write-PrivateText([string]$path, [string]$value) {
    [IO.File]::WriteAllText($path, $value, [Text.Encoding]::ASCII)
}

function Initialize-LocalOperationsArtifacts {
    foreach ($directory in @($StateDir, $SecretsDir, $BackupDir)) {
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
    }

    foreach ($secretFile in @($GrafanaPasswordFile, $AlertmanagerTokenFile)) {
        if (-not (Test-Path -LiteralPath $secretFile -PathType Leaf) -or
            (Get-Item -LiteralPath $secretFile).Length -lt 32) {
            Write-PrivateText $secretFile (New-RandomBase64 36)
        }
    }

    $attestationKeysReady =
        (Test-Path -LiteralPath $AttestationPrivateKeyFile -PathType Leaf) -and
        (Test-Path -LiteralPath $AttestationPublicKeyFile -PathType Leaf) -and
        (Get-Item -LiteralPath $AttestationPrivateKeyFile).Length -gt 100 -and
        (Get-Item -LiteralPath $AttestationPublicKeyFile).Length -gt 100
    if (-not $attestationKeysReady) {
        & dotnet run $CertificateGenerator -- --attestation-key-pair $SecretsDir
        if ($LASTEXITCODE -ne 0) { throw 'Phase 6 signaling attestation key generation failed.' }
        Write-Host 'Generated repository-scoped Phase 6 signaling attestation keys.' -ForegroundColor Green
    }

    $backupPassword = Get-DotEnvValue 'PEERONQ_POSTGRES_BACKUP_PASSWORD'
    $restorePassword = Get-DotEnvValue 'PEERONQ_POSTGRES_RESTORE_PASSWORD'
    if ([string]::IsNullOrWhiteSpace($backupPassword) -or [string]::IsNullOrWhiteSpace($restorePassword)) {
        throw 'PostgreSQL backup and restore passwords are required in the ignored Phase 6 .env file.'
    }

    Write-PrivateText $PostgresBackupPgpassFile "postgres:5432:peeronq_cloud:peeronq_backup:$backupPassword"
    Write-PrivateText $PostgresRestorePgpassFile "postgres:5432:*:peeronq_restore_operator:$restorePassword"
}

function Convert-ToDockerPath([string]$path) {
    return ([IO.Path]::GetFullPath($path) -replace '\\', '/')
}

function Assert-EnvironmentConfiguration {
    if (-not (Test-Path -LiteralPath $EnvironmentFile)) {
        Write-Host '[ERROR] Phase 6 local environment file is missing.' -ForegroundColor Red
        Write-Host "        Copy $EnvironmentFile.example to $EnvironmentFile and populate only development secrets." -ForegroundColor Yellow
        return $false
    }

    $requiredBootstrapValues = @(
        'PEERONQ_ADMIN_BOOTSTRAP_EMAIL',
        'PEERONQ_ADMIN_BOOTSTRAP_PASSWORD',
        'PEERONQ_ADMIN_BOOTSTRAP_TOTP_SECRET_BASE32',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_1',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_2',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_3',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_4',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_5',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_6',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_7',
        'PEERONQ_ADMIN_BOOTSTRAP_RECOVERY_CODE_8'
    )
    $missing = @($requiredBootstrapValues | Where-Object { [string]::IsNullOrWhiteSpace((Get-DotEnvValue $_)) })
    if ($missing.Count -gt 0) {
        Write-Host '[ERROR] Admin development bootstrap is incomplete.' -ForegroundColor Red
        Write-Host "        Missing values: $($missing -join ', ')" -ForegroundColor Yellow
        Write-Host '        Values are never printed by this controller.' -ForegroundColor Yellow
        return $false
    }
    return $true
}

function Test-LocalCertificateArtifacts {
    $requiredArtifacts = @(
        (Join-Path $CertificateDir 'root-ca.cer'),
        (Join-Path $CertificateDir 'fullchain.pem'),
        (Join-Path $CertificateDir 'privkey.pem'),
        (Join-Path $CertificateDir 'certificate.json')
    )
    if ($requiredArtifacts | Where-Object { -not (Test-Path -LiteralPath $_) }) { return $false }

    try {
        $metadata = Get-Content -LiteralPath (Join-Path $CertificateDir 'certificate.json') -Raw | ConvertFrom-Json
        $notAfter = [DateTimeOffset]::Parse($metadata.notAfter)
        return $metadata.signalHost -eq '*.dev.localhost' -and
            $metadata.turnHost -eq 'localhost' -and
            $metadata.bindAddress -eq '127.0.0.1' -and
            $metadata.rootSubject -eq $CertificateRootSubject -and
            $notAfter -gt [DateTimeOffset]::UtcNow.AddDays(7)
    } catch {
        return $false
    }
}

function Initialize-LocalCertificate {
    foreach ($directory in @($StateDir, $CertificateDir)) {
        if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory | Out-Null
        }
    }
    if (-not (Test-Path -LiteralPath $CertificatePasswordFile)) {
        Write-PrivateText $CertificatePasswordFile (New-RandomBase64 36)
    }

    $rootCertificatePath = Join-Path $CertificateDir 'root-ca.cer'
    $previousRoot = if (Test-Path -LiteralPath $rootCertificatePath) {
        [Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCertificatePath)
    } else {
        $null
    }

    if (-not (Test-LocalCertificateArtifacts)) {
        $password = (Get-Content -LiteralPath $CertificatePasswordFile -Raw).Trim()
        & dotnet run $CertificateGenerator -- $CertificateDir $password 'PeerOnQ Phase 6 Local Development Root' '*.dev.localhost' 'localhost' '127.0.0.1'
        if ($LASTEXITCODE -ne 0) { throw 'Phase 6 local certificate generation failed.' }
    }

    $rootCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCertificatePath)
    if ($previousRoot -and
        $previousRoot.Thumbprint -ne $rootCertificate.Thumbprint -and
        $previousRoot.Subject -in @('CN=PeerOnQ Local Development Root', $CertificateRootSubject)) {
        Get-ChildItem Cert:\CurrentUser\Root |
            Where-Object Thumbprint -EQ $previousRoot.Thumbprint |
            Remove-Item -Force
    }

    $trusted = Get-ChildItem Cert:\CurrentUser\Root |
        Where-Object Thumbprint -EQ $rootCertificate.Thumbprint |
        Select-Object -First 1
    if (-not $trusted) {
        Import-Certificate -FilePath $rootCertificatePath -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
        Write-Host 'Trusted the repository-scoped PeerOnQ development root in CurrentUser.' -ForegroundColor Green
    }
}

function Set-DevelopmentEnvironment {
    $env:PEERONQ_API_HOST = $ApiHost
    $env:PEERONQ_ADMIN_HOST = $AdminHost
    $env:PEERONQ_PORTAL_HOST = $PortalHost
    $env:PEERONQ_DOWNLOAD_HOST = $DownloadHost
    $env:PEERONQ_PRESENCE_HOST = $PresenceHost
    $env:PEERONQ_SIGNAL_HOST = $SignalHost
    $env:PEERONQ_TURN_PUBLIC_HOST = 'turn.dev.localhost'
    $env:PEERONQ_TURN_REALM = 'turn.dev.localhost'
    $env:PEERONQ_SIGNALING_ATTESTATION_ISSUER = "https://$ApiHost"
    $env:PEERONQ_TLS_CERT_DIR = Convert-ToDockerPath $CertificateDir
    $env:PEERONQ_TLS_PRIVATE_KEY_FILE = Convert-ToDockerPath (Join-Path $CertificateDir 'privkey.pem')
    $env:PEERONQ_TURN_CERT_DIR = Convert-ToDockerPath $CertificateDir
    $env:PEERONQ_ALERTMANAGER_WEBHOOK_TOKEN_FILE = Convert-ToDockerPath $AlertmanagerTokenFile
    $env:PEERONQ_GRAFANA_ADMIN_PASSWORD_FILE = Convert-ToDockerPath $GrafanaPasswordFile
    $env:PEERONQ_SIGNALING_ATTESTATION_PRIVATE_KEY_FILE = Convert-ToDockerPath $AttestationPrivateKeyFile
    $env:PEERONQ_SIGNALING_ATTESTATION_PUBLIC_KEY_FILE = Convert-ToDockerPath $AttestationPublicKeyFile
    $env:PEERONQ_POSTGRES_BACKUP_PGPASS_FILE = Convert-ToDockerPath $PostgresBackupPgpassFile
    $env:PEERONQ_POSTGRES_RESTORE_PGPASS_FILE = Convert-ToDockerPath $PostgresRestorePgpassFile
    $env:PEERONQ_BACKUP_DIR = Convert-ToDockerPath $BackupDir
}

function Sync-GrafanaAdminPassword {
    $docker = Get-DockerExecutable
    $containerId = (& $docker compose --env-file $EnvironmentFile @ComposeFiles ps -q grafana | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($containerId)) { throw 'Grafana container was not created.' }

    # cmd input redirection preserves the exact ASCII bytes. PowerShell's native pipeline can
    # transcode or append CRLF, which would silently set a different Grafana password.
    $commandLine = "`"$docker`" exec -i $containerId grafana cli admin reset-admin-password --password-from-stdin < `"$GrafanaPasswordFile`""
    $output = & $env:ComSpec /d /c $commandLine 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Grafana administrator password synchronization failed: $output"
    }
    if (-not ($output -match 'successfully')) {
        throw 'Grafana did not confirm administrator password synchronization.'
    }
    Write-Host 'Grafana administrator credential synchronized.' -ForegroundColor Green
}

function Invoke-Compose([string[]]$ComposeArguments) {
    $docker = Get-DockerExecutable
    $arguments = @('compose', '--env-file', $EnvironmentFile) + $ComposeFiles + $ComposeArguments
    & $docker @arguments | Out-Host
    $code = $LASTEXITCODE
    return $code
}

function Repair-StaleAdminInfrastructureLeaseAcl {
    $docker = Get-DockerExecutable
    $containerId = @(& $docker compose --env-file $EnvironmentFile @ComposeFiles ps --all --quiet redis 2>$null) |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($containerId)) { return $true }

    & $docker exec $containerId sh -c `
        "grep -Fq 'operation-lease:admin-infrastructure-snapshots' /data/users.acl" *> $null
    if ($LASTEXITCODE -eq 0) { return $true }

    Write-Host 'Restarting Redis once to apply the scoped Admin infrastructure lease ACL ...' -ForegroundColor Yellow
    return (Invoke-Compose @('restart', 'redis')) -eq 0
}

function Repair-StaleSecretMount(
    [string]$service,
    [string]$secretDestination,
    [string]$expectedPath
) {
    $docker = Get-DockerExecutable
    $containerId = @(& $docker compose --env-file $EnvironmentFile @ComposeFiles ps --all --quiet $service 2>$null) |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($containerId)) { return $true }

    $container = @(& $docker inspect $containerId 2>$null | ConvertFrom-Json) | Select-Object -First 1
    $mountedSource = @($container.Mounts |
        Where-Object Destination -EQ $secretDestination |
        Select-Object -ExpandProperty Source -First 1) | Select-Object -First 1
    if ($null -eq $mountedSource) { $mountedSource = '' }
    $expectedSource = (Convert-ToDockerPath $expectedPath).Replace('\', '/').TrimEnd('/')
    $comparableMountedSource = $mountedSource.Replace('\', '/').TrimEnd('/')
    if ($comparableMountedSource.Equals($expectedSource, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    Write-Host "Recreating $service to repair a stale development secret mount (data volumes preserved) ..." -ForegroundColor Yellow
    return (Invoke-Compose @('rm', '--stop', '--force', $service)) -eq 0
}

function Repair-StaleDevelopmentSecretMounts {
    $mounts = @(
        [pscustomobject]@{ Service = 'grafana'; Destination = '/run/secrets/peeronq_grafana_admin_password'; Path = $GrafanaPasswordFile },
        [pscustomobject]@{ Service = 'cloud-api'; Destination = '/run/secrets/peeronq_signaling_attestation_private_key'; Path = $AttestationPrivateKeyFile },
        [pscustomobject]@{ Service = 'signaling'; Destination = '/run/secrets/peeronq_signaling_attestation_public_key'; Path = $AttestationPublicKeyFile }
    )
    foreach ($mount in $mounts) {
        if (-not (Repair-StaleSecretMount $mount.Service $mount.Destination $mount.Path)) { return $false }
    }
    return $true
}

function Stop-RemainingProjectContainers {
    $docker = Get-DockerExecutable
    $containerIds = @(& $docker ps --quiet --filter "label=com.docker.compose.project=$ComposeProjectName")
    if ($containerIds.Count -eq 0) { return 0 }

    Write-Host 'Stopping remaining PeerOnQ Phase 6 project containers ...' -ForegroundColor Yellow
    & $docker stop @containerIds | Out-Host
    return $LASTEXITCODE
}

function Test-Url([string]$url) {
    try {
        & curl.exe --noproxy '*' --ssl-no-revoke --fail --silent --show-error --output NUL --max-time 8 $url *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Wait-ForUrl([string]$label, [string]$url, [int]$attempts = 90) {
    Write-Host "Waiting for $label ..."
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        if (Test-Url $url) {
            Write-Host "$label is ready." -ForegroundColor Green
            return $true
        }
        Start-Sleep -Seconds 2
    }
    Write-Host "[ERROR] $label did not become ready: $url" -ForegroundColor Red
    return $false
}

function Invoke-Start {
    Write-Head 'PeerOnQ Phase 6 - starting full development stack'
    if (-not (Assert-EnvironmentConfiguration)) { return 1 }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host '[ERROR] dotnet was not found in PATH.' -ForegroundColor Red
        return 1
    }
    if (-not (Test-DockerReady)) {
        Write-Host '[ERROR] Docker Desktop is not running or Docker Engine is unavailable.' -ForegroundColor Red
        return 1
    }
    Initialize-PeerOnQLocalInternalDockerNetwork -DockerExecutable (Get-DockerExecutable)

    Initialize-LocalCertificate
    Initialize-LocalCustomerSecret
    Initialize-LocalSignalingRedisSecret
    Initialize-LocalOperationsArtifacts
    Set-DevelopmentEnvironment

    if ((Invoke-Compose @('config', '--quiet')) -ne 0) { return 1 }
    if (-not (Repair-StaleDevelopmentSecretMounts)) { return 1 }
    if ((Invoke-Compose @('up', '-d', 'postgres', 'redis')) -ne 0) { return 1 }
    if (-not (Repair-StaleAdminInfrastructureLeaseAcl)) { return 1 }
    if ((Invoke-Compose @('run', '--rm', '--build', 'migrations')) -ne 0) { return 1 }
    if ((Invoke-Compose @('run', '--rm', 'database-permissions')) -ne 0) { return 1 }
    if ((Invoke-Compose @('up', '-d', '--build')) -ne 0) { return 1 }
    Sync-GrafanaAdminPassword

    if (-not (Wait-ForUrl 'Admin console' $AdminUrl)) {
        Write-Host 'Run peeronq-status.bat for container and endpoint status.' -ForegroundColor Yellow
        return 1
    }
    if (-not (Wait-ForUrl 'Cloud API' $CloudHealthUrl 30)) { return 1 }
    if (-not (Wait-ForUrl 'Account portal' $PortalUrl 30)) { return 1 }
    if (-not (Wait-ForUrl 'Grafana' "$GrafanaUrl/api/health" 30)) { return 1 }
    if (-not (Wait-ForUrl 'Prometheus' "$PrometheusUrl/-/ready" 30)) { return 1 }

    Write-Host ''
    Write-Host "Admin console : $AdminUrl" -ForegroundColor Green
    Write-Host "Account portal: $PortalUrl" -ForegroundColor Green
    Write-Host "Grafana       : $GrafanaUrl"
    Write-Host "Prometheus    : $PrometheusUrl"
    Write-Host 'Use the local Admin bootstrap credentials and TOTP from the ignored Phase 6 .env file.'
    if (-not $NoBrowser) {
        Start-Process -FilePath $AdminUrl | Out-Null
        Start-Process -FilePath $PortalUrl | Out-Null
        Start-Process -FilePath $GrafanaUrl | Out-Null
        Start-Process -FilePath $PrometheusUrl | Out-Null
    }
    return 0
}

function Invoke-Stop {
    Write-Head 'PeerOnQ Phase 6 - stopping containers (data preserved)'
    if (-not (Test-Path -LiteralPath $EnvironmentFile)) {
        Write-Host 'Phase 6 local environment is absent; no scoped Compose action was taken.' -ForegroundColor Yellow
        return 0
    }
    if (-not (Test-DockerReady)) {
        Write-Host '[ERROR] Docker Engine is unavailable; Phase 6 containers could not be stopped.' -ForegroundColor Red
        return 1
    }
    Initialize-LocalCustomerSecret
    Initialize-LocalSignalingRedisSecret
    Set-DevelopmentEnvironment
    $code = Invoke-Compose @('stop')
    $remainingCode = Stop-RemainingProjectContainers
    if ($code -eq 0 -and $remainingCode -eq 0) {
        Write-Host 'Phase 6 containers stopped. PostgreSQL and Redis volumes were preserved.' -ForegroundColor Green
        return 0
    }
    if ($code -ne 0) { return $code }
    return $remainingCode
}

function Invoke-Status {
    Write-Head 'PeerOnQ Phase 6 - status'
    Write-Host "Admin console : $AdminUrl ($(if (Test-Url $AdminUrl) { 'ready' } else { 'unavailable' }))"
    Write-Host "Cloud API     : $CloudHealthUrl ($(if (Test-Url $CloudHealthUrl) { 'ready' } else { 'unavailable' }))"
    Write-Host "Account portal: $PortalUrl ($(if (Test-Url $PortalUrl) { 'ready' } else { 'unavailable' }))"
    Write-Host "Grafana       : $GrafanaUrl ($(if (Test-Url "$GrafanaUrl/api/health") { 'ready' } else { 'unavailable' }))"
    Write-Host "Prometheus    : $PrometheusUrl ($(if (Test-Url "$PrometheusUrl/-/ready") { 'ready' } else { 'unavailable' }))"

    if (-not (Test-Path -LiteralPath $EnvironmentFile) -or -not (Test-DockerReady)) { return 0 }
    Initialize-LocalCustomerSecret
    Initialize-LocalSignalingRedisSecret
    Set-DevelopmentEnvironment
    $code = Invoke-Compose @('ps')
    return $code
}

switch ($Action) {
    'start' { exit (Invoke-Start) }
    'stop' { exit (Invoke-Stop) }
    'status' { exit (Invoke-Status) }
    'restart' {
        $code = Invoke-Stop
        if ($code -ne 0) { exit $code }
        exit (Invoke-Start)
    }
}
