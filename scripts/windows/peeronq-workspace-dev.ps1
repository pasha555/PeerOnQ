<# Starts and stops the complete PeerOnQ development workspace on Windows. #>

[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'restart', 'status')]
    [string]$Action = 'status'
)

$ErrorActionPreference = 'Stop'
$PowerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$WebController = Join-Path $PSScriptRoot 'peeronq-dev.ps1'
$Phase6Controller = Join-Path $PSScriptRoot 'peeronq-phase6-dev.ps1'
$Phase3Controller = Join-Path $PSScriptRoot 'peeronq-phase3-local.ps1'
$Phase3EnvironmentFile = Join-Path $RepoRoot '.peeronq-phase3\docker.env'
$Phase3ModeFile = Join-Path $RepoRoot '.peeronq-phase3\mode'
$Phase6EnvironmentFile = Join-Path $RepoRoot 'src\PeerOnQ.Infrastructure.Deployment\.env'

foreach ($controller in @($WebController, $Phase6Controller, $Phase3Controller)) {
    if (-not (Test-Path -LiteralPath $controller)) {
        throw "PeerOnQ development controller is missing: $controller"
    }
}

function Get-ConfiguredPhase3BindAddress {
    if (-not (Test-Path -LiteralPath $Phase3EnvironmentFile)) { return $null }

    foreach ($line in [IO.File]::ReadAllLines($Phase3EnvironmentFile)) {
        if ($line -notmatch '^\s*PEERONQ_LOCAL_BIND_ADDRESS\s*=\s*(\S+)\s*$') { continue }
        $parsed = $null
        if (-not [Net.IPAddress]::TryParse($Matches[1], [ref]$parsed) -or
            $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
            throw "Invalid Phase 3 bind address in $Phase3EnvironmentFile."
        }
        return $parsed.ToString()
    }

    throw "Phase 3 state is missing PEERONQ_LOCAL_BIND_ADDRESS: $Phase3EnvironmentFile"
}

function Get-ConfiguredPort([string]$name, [int]$defaultValue) {
    if (-not (Test-Path -LiteralPath $Phase6EnvironmentFile)) { return $defaultValue }
    $escapedName = [Regex]::Escape($name)
    foreach ($line in [IO.File]::ReadAllLines($Phase6EnvironmentFile)) {
        if ($line -notmatch "^\s*$escapedName\s*=\s*(\d+)\s*$") { continue }
        $parsed = 0
        if ([int]::TryParse($Matches[1], [ref]$parsed) -and $parsed -ge 1024 -and $parsed -le 65535) {
            return $parsed
        }
    }
    return $defaultValue
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

function Get-DockerDesktopExecutable {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\Docker Desktop.exe'),
        (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'),
        (Join-Path $env:LOCALAPPDATA 'Docker\Docker Desktop.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Docker\Docker\Docker Desktop.exe')
    )
    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Ensure-DockerReady([int]$seconds = 180) {
    if (Test-DockerReady) { return $true }

    $dockerDesktop = Get-DockerDesktopExecutable
    if (-not $dockerDesktop) {
        Write-Host '[ERROR] Docker Desktop is required for the complete PeerOnQ workspace and was not found.' -ForegroundColor Red
        return $false
    }

    $dockerProcesses = Get-Process -Name 'Docker Desktop', 'com.docker.backend' -ErrorAction SilentlyContinue
    if (-not $dockerProcesses) {
        Write-Host 'Starting Docker Desktop ...' -ForegroundColor Cyan
        Start-Process -FilePath $dockerDesktop -WindowStyle Hidden | Out-Null
    } else {
        Write-Host 'Docker Desktop is running; waiting for Docker Engine ...' -ForegroundColor Cyan
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    $attempt = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        $attempt++
        if (Test-DockerReady) {
            Write-Host 'Docker Engine is ready.' -ForegroundColor Green
            return $true
        }
        if ($attempt % 5 -eq 0) {
            Write-Host 'Still waiting for Docker Engine ...' -ForegroundColor Yellow
        }
        Start-Sleep -Seconds 2
    }

    Write-Host '[ERROR] Docker Engine did not become ready within three minutes.' -ForegroundColor Red
    return $false
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Controller(
    [string]$controller,
    [string]$action,
    [switch]$NoBrowser,
    [string[]]$AdditionalArguments = @()
) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $controller, '-Action', $action)
    if ($NoBrowser) { $arguments += '-NoBrowser' }
    $arguments += $AdditionalArguments
    & $PowerShell @arguments | Out-Host
    $code = $LASTEXITCODE
    return $code
}

function Invoke-ConfiguredPhase3([string]$action) {
    $bindAddress = Get-ConfiguredPhase3BindAddress
    if (-not $bindAddress) {
        if ($action -eq 'status') {
            Write-Host 'PeerOnQ Phase 3 LAN: not configured (skipped).'
        }
        return 0
    }

    Write-Host "PeerOnQ Phase 3 LAN ($bindAddress): $action"
    $arguments = @('-BindAddress', $bindAddress)
    if ($action -eq 'start' -and (Test-IsAdministrator)) {
        $arguments += '-ConfigureFirewall'
    }
    return (Invoke-Controller $Phase3Controller $action -AdditionalArguments $arguments)
}

function Open-WorkspacePages {
    $httpsPort = Get-ConfiguredPort 'PEERONQ_HTTPS_PORT' 8443
    $prometheusPort = Get-ConfiguredPort 'PEERONQ_PROMETHEUS_PORT' 9090
    $pages = @(
        [pscustomobject]@{ Name = 'Public website'; Url = 'http://localhost:5555/' },
        [pscustomobject]@{ Name = 'Admin console'; Url = "https://admin.dev.localhost:$httpsPort/" },
        [pscustomobject]@{ Name = 'Account portal'; Url = "https://portal.dev.localhost:$httpsPort/" },
        [pscustomobject]@{ Name = 'Grafana'; Url = "https://grafana.dev.localhost:$httpsPort/" },
        [pscustomobject]@{ Name = 'Prometheus'; Url = "http://localhost:$prometheusPort/" }
    )

    Write-Host ''
    Write-Host 'Opening all PeerOnQ web interfaces:' -ForegroundColor Cyan
    $failed = $false
    foreach ($page in $pages) {
        try {
            Start-Process -FilePath $page.Url -ErrorAction Stop | Out-Null
            Write-Host "  $($page.Name): $($page.Url)" -ForegroundColor Green
        }
        catch {
            Write-Host "[ERROR] Could not open $($page.Name): $($page.Url)" -ForegroundColor Red
            $failed = $true
        }
    }
    return $(if ($failed) { 1 } else { 0 })
}

function Invoke-Start {
    if (-not (Ensure-DockerReady)) { return 1 }
    $phase3Code = Invoke-ConfiguredPhase3 'start'
    if ($phase3Code -ne 0) { return $phase3Code }
    $phase6Code = Invoke-Controller $Phase6Controller 'start' -NoBrowser
    if ($phase6Code -ne 0) { return $phase6Code }
    $webCode = Invoke-Controller $WebController 'start' -NoBrowser
    if ($webCode -ne 0) { return $webCode }
    return (Open-WorkspacePages)
}

function Invoke-Stop {
    $phase3UsesDocker = (Test-Path -LiteralPath $Phase3ModeFile) -and
        (Get-Content -LiteralPath $Phase3ModeFile -Raw).Trim() -eq 'docker'
    $dockerRequired = (Test-Path -LiteralPath $Phase6EnvironmentFile) -or $phase3UsesDocker
    $dockerReady = -not $dockerRequired -or (Ensure-DockerReady)
    $webCode = Invoke-Controller $WebController 'stop'
    $phase6Code = if ($dockerReady) {
        Invoke-Controller $Phase6Controller 'stop'
    } else {
        Write-Host '[ERROR] Phase 6 containers could not be stopped because Docker Engine is unavailable.' -ForegroundColor Red
        1
    }
    $phase3Code = Invoke-ConfiguredPhase3 'stop'
    if ($webCode -ne 0) { return $webCode }
    if ($phase6Code -ne 0) { return $phase6Code }
    return $phase3Code
}

function Invoke-Status {
    $webCode = Invoke-Controller $WebController 'status'
    $phase6Code = Invoke-Controller $Phase6Controller 'status'
    $phase3Code = Invoke-ConfiguredPhase3 'status'
    if ($webCode -ne 0) { return $webCode }
    if ($phase6Code -ne 0) { return $phase6Code }
    return $phase3Code
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
