<#
    PeerOnQ local dev controller (Windows).

    Scope rules — this script must never affect anything outside this repository:
      * start records the PID of the process it launched in .peeronq-run/dev-server.pid
      * stop kills ONLY that recorded process tree, and only after verifying the
        process really is this repo's dev server (command line marker + PID liveness)
      * if port 5555 is held by a process this script did not start, it refuses to
        touch it and reports who owns the port
#>

[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'restart', 'status')]
    [string]$Action = 'status',
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'

$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# Hard guard: only ever operate on this project. The root is derived from this
# script's own location, and must look like the PeerOnQ workspace.
$requiredPaths = @(
    (Join-Path $RepoRoot 'pnpm-workspace.yaml'),
    (Join-Path $RepoRoot 'artifacts\peeronq\package.json'),
    (Join-Path $RepoRoot 'artifacts\peeronq\vite.config.ts')
)
foreach ($required in $requiredPaths) {
    if (-not (Test-Path $required)) {
        Write-Host "[ERROR] $RepoRoot is not the PeerOnQ workspace (missing $required)." -ForegroundColor Red
        Write-Host '        Keep these scripts in the project folder; they refuse to run anywhere else.' -ForegroundColor Red
        exit 1
    }
}
. (Join-Path $PSScriptRoot 'PeerOnQ.ClientVersion.ps1')
$CanonicalClientVersion = Get-PeerOnQClientVersion -RepositoryRoot $RepoRoot

$Port       = 5555
$Url        = "http://localhost:$Port"
$StateDir   = Join-Path $RepoRoot '.peeronq-run'
$PidFile    = Join-Path $StateDir 'dev-server.pid'
$DownloadReleaseStateFile = Join-Path $StateDir 'windows-download-release.json'
$DevCommand = 'pnpm --filter @workspace/peeronq run dev'
$Marker     = '@workspace/peeronq'
$Phase6EnvironmentFile = Join-Path $RepoRoot 'src\PeerOnQ.Infrastructure.Deployment\.env'

function Get-Phase6Port([string]$name, [int]$defaultValue) {
    if (-not (Test-Path -LiteralPath $Phase6EnvironmentFile)) { return $defaultValue }
    $escapedName = [Regex]::Escape($name)
    foreach ($line in [IO.File]::ReadLines($Phase6EnvironmentFile)) {
        if ($line -notmatch "^\s*$escapedName\s*=\s*(\d+)\s*$") { continue }
        $parsed = 0
        if ([int]::TryParse($Matches[1], [ref]$parsed) -and $parsed -ge 1024 -and $parsed -le 65535) {
            return $parsed
        }
    }
    return $defaultValue
}

function Get-WebsiteWindowsRelease([string]$downloadRoot, [version]$canonicalVersion) {
    if (-not (Test-Path -LiteralPath $downloadRoot -PathType Container)) { return $null }
    $checksumPath = Join-Path $downloadRoot 'SHA256SUMS.txt'
    $versionedPackages = @(Get-ChildItem -LiteralPath $downloadRoot -File -Filter 'PeerOnQ-*-unsigned-*.msi')
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        if ($versionedPackages.Count -gt 0) {
            throw 'Website Windows packages exist without SHA256SUMS.txt.'
        }
        return $null
    }

    $checksumEntries = @()
    foreach ($line in [IO.File]::ReadLines($checksumPath)) {
        if ($line -match '^(?<hash>[0-9A-Fa-f]{64})  (?<file>PeerOnQ-(?<version>[0-9]+(?:\.[0-9]+){2})-(?<qualifier>unsigned-(?:development|public-pilot))-(?<architecture>x64|arm64)\.msi)$') {
            $checksumEntries += [pscustomobject]@{
                Hash = $Matches['hash'].ToLowerInvariant()
                FileName = $Matches['file']
                Version = $Matches['version']
                ArtifactQualifier = $Matches['qualifier']
                Architecture = $Matches['architecture']
            }
        } elseif (-not [string]::IsNullOrWhiteSpace($line)) {
            throw "SHA256SUMS.txt contains an unsupported Windows package entry: $line"
        }
    }

    if ($checksumEntries.Count -eq 0 -and $versionedPackages.Count -eq 0) { return $null }
    if ($checksumEntries.Count -ne 2) {
        throw 'Website publication requires exactly one checksum-verified x64/ARM64 Windows package pair.'
    }

    $expectedVersion = $canonicalVersion.ToString(3)
    $versions = @($checksumEntries.Version | Sort-Object -Unique)
    $qualifiers = @($checksumEntries.ArtifactQualifier | Sort-Object -Unique)
    $architectures = @($checksumEntries.Architecture | Sort-Object -Unique)
    if ($versions.Count -ne 1 -or $versions[0] -ne $expectedVersion) {
        throw "Website Windows release $($versions -join ', ') does not match canonical client version $expectedVersion."
    }
    if ($qualifiers.Count -ne 1 -or $architectures.Count -ne 2 -or
        $architectures -notcontains 'x64' -or $architectures -notcontains 'arm64') {
        throw 'Website Windows release must use one classification and matching x64/ARM64 packages.'
    }

    foreach ($entry in $checksumEntries) {
        $filePath = Join-Path $downloadRoot $entry.FileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Website Windows package is missing: $($entry.FileName)"
        }
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash.ToLowerInvariant()
        if ($actualHash -ne $entry.Hash) {
            throw "Website Windows package checksum mismatch: $($entry.FileName)"
        }
    }

    return [pscustomobject]@{
        Version = $expectedVersion
        ArtifactQualifier = $qualifiers[0]
    }
}

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host '============================================' -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host '============================================' -ForegroundColor Cyan
}

function Get-PortOwnerId {
    try {
        $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop |
                Select-Object -First 1
        if ($conn) { return [int]$conn.OwningProcess }
    } catch {
        # Fall back to netstat when Get-NetTCPConnection is unavailable.
        $line = & "$env:SystemRoot\System32\netstat.exe" -ano |
                Select-String -Pattern ":$Port\s.*LISTENING" |
                Select-Object -First 1
        if ($line) {
            $parts = ($line.ToString().Trim() -split '\s+')
            return [int]$parts[-1]
        }
    }
    return $null
}

function Get-ProcessTreeIds([int]$rootId) {
    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
    $ids = New-Object System.Collections.Generic.List[int]
    $ids.Add($rootId) | Out-Null

    $grew = $true
    while ($grew) {
        $grew = $false
        foreach ($p in $all) {
            if ($ids -contains [int]$p.ParentProcessId -and -not ($ids -contains [int]$p.ProcessId)) {
                $ids.Add([int]$p.ProcessId) | Out-Null
                $grew = $true
            }
        }
    }
    return $ids
}

# Returns the launcher process recorded by start, or $null. A PID that has been
# recycled by an unrelated program fails the command-line check and is ignored.
function Get-TrackedProcess {
    if (-not (Test-Path $PidFile)) { return $null }

    $raw = (Get-Content $PidFile -Raw).Trim()
    if ($raw -notmatch '^\d+$') { return $null }

    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$raw" -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    if ($proc.CommandLine -notlike "*$Marker*") { return $null }

    return $proc
}

function Remove-PidFile {
    if (Test-Path $PidFile) { Remove-Item $PidFile -Force -ErrorAction SilentlyContinue }
}

function Show-ForeignOwner([int]$ownerId) {
    $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$ownerId" -ErrorAction SilentlyContinue
    $name  = if ($owner) { $owner.Name } else { 'unknown' }
    Write-Host "Port $Port is held by PID $ownerId ($name), which these scripts did not start." -ForegroundColor Yellow
    Write-Host 'Leaving it alone. Stop that program yourself, or change $Port in this script.' -ForegroundColor Yellow
}

function Test-WebReady {
    try {
        & curl.exe --noproxy '*' --fail --silent --show-error --output NUL --max-time 3 $Url *> $null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Invoke-Status {
    Write-Head "PeerOnQ - status (port $Port)"

    $tracked = Get-TrackedProcess
    $ownerId = Get-PortOwnerId

    if ($tracked -and $ownerId) {
        Write-Host "Tracked dev server: PID $($tracked.ProcessId)" -ForegroundColor Green
    } elseif ($tracked) {
        Write-Host "Tracked launcher PID $($tracked.ProcessId) has no listening dev server." -ForegroundColor Yellow
    } else {
        Write-Host 'Tracked dev server: none' -ForegroundColor Gray
    }

    if ($ownerId) {
        $ours = $tracked -and ((Get-ProcessTreeIds ([int]$tracked.ProcessId)) -contains $ownerId)
        if ($ours) {
            if (Test-WebReady) {
                Write-Host "Port $Port : serving PeerOnQ ($Url)" -ForegroundColor Green
                if (Test-Path -LiteralPath $DownloadReleaseStateFile -PathType Leaf) {
                    $releaseState = Get-Content -LiteralPath $DownloadReleaseStateFile -Raw | ConvertFrom-Json
                    Write-Host "Windows downloads: $($releaseState.version) $($releaseState.artifactQualifier)" -ForegroundColor Green
                }
            } else {
                Write-Host "Port $Port : tracked process is listening but HTTP is unresponsive" -ForegroundColor Yellow
            }
        } else {
            Show-ForeignOwner $ownerId
        }
    } else {
        Write-Host "Port $Port : free" -ForegroundColor Gray
    }
}

function Invoke-Start {
    Write-Head "PeerOnQ - starting on $Url"

    $tracked = Get-TrackedProcess
    $ownerId = Get-PortOwnerId

    if ($tracked -and $ownerId -and ((Get-ProcessTreeIds ([int]$tracked.ProcessId)) -contains $ownerId)) {
        if (Test-WebReady) {
            Write-Host "Already running and ready (PID $($tracked.ProcessId))." -ForegroundColor Green
            if (-not $NoBrowser) { Start-Process -FilePath $Url | Out-Null }
            return 0
        }

        Write-Host "Tracked dev server PID $($tracked.ProcessId) is unresponsive; restarting only this repository process tree." -ForegroundColor Yellow
        $stopCode = Invoke-Stop
        if ($stopCode -ne 0) { return $stopCode }
        $tracked = $null
        $ownerId = $null
    }

    if ($tracked -and -not $ownerId) {
        Write-Host "Tracked launcher PID $($tracked.ProcessId) has no listening dev server; cleaning up that repository process tree." -ForegroundColor Yellow
        $stopCode = Invoke-Stop
        if ($stopCode -ne 0) { return $stopCode }
        $tracked = $null
    }

    if ($ownerId) {
        Show-ForeignOwner $ownerId
        return 1
    }

    if (-not $tracked) { Remove-PidFile }

    if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
        Write-Host '[ERROR] pnpm was not found in PATH. Install it with: npm install -g pnpm' -ForegroundColor Red
        return 1
    }

    if (-not (Test-Path (Join-Path $RepoRoot 'node_modules'))) {
        Write-Host 'Dependencies are missing. Running pnpm install ...'
        Push-Location $RepoRoot
        try { & pnpm install } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) {
            Write-Host '[ERROR] pnpm install failed.' -ForegroundColor Red
            return 1
        }
    }

    if (-not (Test-Path $StateDir)) { New-Item -ItemType Directory -Path $StateDir | Out-Null }
    if (Test-Path -LiteralPath $DownloadReleaseStateFile) {
        Remove-Item -LiteralPath $DownloadReleaseStateFile -Force
    }

    # vite.config.ts requires both and uses strictPort, so the app always lands on $Port.
    $env:PORT = "$Port"
    $env:BASE_PATH = '/'
    $phase6HttpsPort = Get-Phase6Port 'PEERONQ_HTTPS_PORT' 8443
    $prometheusPort = Get-Phase6Port 'PEERONQ_PROMETHEUS_PORT' 9090
    $env:VITE_PEERONQ_ADMIN_PANEL_URL = "https://admin.dev.localhost:$phase6HttpsPort"
    $env:VITE_PEERONQ_CLOUD_HEALTH_URL = "https://api.dev.localhost:$phase6HttpsPort/health/ready"
    $env:VITE_PEERONQ_GRAFANA_URL = "https://grafana.dev.localhost:$phase6HttpsPort"
    $env:VITE_PEERONQ_PROMETHEUS_URL = "http://localhost:$prometheusPort"
    $env:PEERONQ_DOWNLOAD_TELEMETRY_BASE_URL = "https://download.dev.localhost:$phase6HttpsPort"
    $phase6CertificateChain = Join-Path $RepoRoot '.peeronq-phase6\certs\fullchain.pem'
    $env:PEERONQ_DOWNLOAD_TELEMETRY_CA_FILE = if (Test-Path -LiteralPath $phase6CertificateChain) { $phase6CertificateChain } else { $null }
    $downloadRoot = Join-Path $RepoRoot 'artifacts\peeronq\public\downloads'
    $websiteWindowsRelease = Get-WebsiteWindowsRelease $downloadRoot $CanonicalClientVersion
    $env:VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE = if ($websiteWindowsRelease) { 'true' } else { 'false' }
    $env:VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION = $websiteWindowsRelease.Version
    $env:VITE_PEERONQ_WINDOWS_DOWNLOAD_ARTIFACT_QUALIFIER = $websiteWindowsRelease.ArtifactQualifier
    $windowsDownloadStatus = if ($websiteWindowsRelease) {
        "version $($websiteWindowsRelease.Version) $($websiteWindowsRelease.ArtifactQualifier)"
    } else { 'none' }
    Write-Host "Windows client downloads available: $env:VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE ($windowsDownloadStatus)"
    Write-Host "Phase 6 Admin console: $env:VITE_PEERONQ_ADMIN_PANEL_URL"

    Write-Host 'Launching the dev server ...'
    $proc = Start-Process -FilePath "$env:SystemRoot\System32\cmd.exe" `
                          -ArgumentList '/k', $DevCommand `
                          -WorkingDirectory $RepoRoot `
                          -WindowStyle Minimized `
                          -PassThru

    Set-Content -Path $PidFile -Value $proc.Id -Encoding ascii
    Write-Host "Launcher PID $($proc.Id) recorded in .peeronq-run/dev-server.pid"

    Write-Host "Waiting for $Url ..."
    $treeIds = $null
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Seconds 1

        if ($proc.HasExited) {
            Write-Host '[ERROR] The dev server window exited. Check the minimized cmd window.' -ForegroundColor Red
            Remove-PidFile
            return 1
        }

        $ownerId = Get-PortOwnerId
        if ($ownerId) {
            $treeIds = Get-ProcessTreeIds ([int]$proc.Id)
            if ($treeIds -contains $ownerId) {
                if (-not (Test-WebReady)) { continue }
                Write-Host ''
                Write-Host "PeerOnQ is running on $Url" -ForegroundColor Green
                if ($websiteWindowsRelease) {
                    $releaseState = [ordered]@{
                        version = $websiteWindowsRelease.Version
                        artifactQualifier = $websiteWindowsRelease.ArtifactQualifier
                    } | ConvertTo-Json -Compress
                    [IO.File]::WriteAllText(
                        $DownloadReleaseStateFile,
                        $releaseState,
                        [Text.UTF8Encoding]::new($false))
                }
                if (-not $NoBrowser) { Start-Process -FilePath $Url | Out-Null }
                Write-Host 'Stop it with peeronq-stop.bat, restart with peeronq-restart.bat.'
                return 0
            }
            Show-ForeignOwner $ownerId
            return 1
        }
    }

    Write-Host '[ERROR] The server did not start within 90 seconds.' -ForegroundColor Red
    Write-Host '        Check the minimized "cmd" window for the pnpm output.' -ForegroundColor Red
    return 1
}

function Invoke-Stop {
    Write-Head "PeerOnQ - stopping (port $Port)"

    $tracked = Get-TrackedProcess

    if (-not $tracked) {
        Remove-PidFile
        if (Test-Path -LiteralPath $DownloadReleaseStateFile) {
            Remove-Item -LiteralPath $DownloadReleaseStateFile -Force
        }
        $ownerId = Get-PortOwnerId
        if ($ownerId) {
            Show-ForeignOwner $ownerId
        } else {
            Write-Host 'PeerOnQ is not running (nothing was started by these scripts).'
        }
        return 0
    }

    Write-Host "Stopping PID $($tracked.ProcessId) and its child processes ..."
    & "$env:SystemRoot\System32\taskkill.exe" /F /T /PID $tracked.ProcessId | Out-Null

    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        if (-not (Get-PortOwnerId)) { break }
    }

    Remove-PidFile
    if (Test-Path -LiteralPath $DownloadReleaseStateFile) {
        Remove-Item -LiteralPath $DownloadReleaseStateFile -Force
    }

    $ownerId = Get-PortOwnerId
    if ($ownerId) {
        Write-Host ''
        Show-ForeignOwner $ownerId
        return 1
    }

    Write-Host ''
    Write-Host 'PeerOnQ stopped.' -ForegroundColor Green
    return 0
}

switch ($Action) {
    'start'   { exit (Invoke-Start) }
    'stop'    { exit (Invoke-Stop) }
    'status'  { Invoke-Status; exit 0 }
    'restart' {
        $code = Invoke-Stop
        if ($code -ne 0) { exit $code }
        Start-Sleep -Seconds 2
        exit (Invoke-Start)
    }
}
