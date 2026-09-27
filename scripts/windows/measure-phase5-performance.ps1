<# Repeatable, evidence-only Windows process benchmark. Unsupported metrics remain null. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [ValidateRange(5, 600)][int]$DurationSeconds = 15,
    [string]$OutputPath,
    [string]$MediaDiagnosticsPath,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (-not $OutputPath) {
    $OutputPath = Join-Path (Split-Path -Parent $executable) 'phase5-performance.json'
}
$output = [IO.Path]::GetFullPath($OutputPath)

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Ceiling(($Percentile / 100.0) * $sorted.Count) - 1
    return [Math]::Round($sorted[[Math]::Max(0, $index)], 3)
}

$process = $null
$launched = $false
try {
    $startupTimer = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $executable -PassThru
    $launched = $true
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    } while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and [DateTimeOffset]::UtcNow -lt $deadline)
    $startupTimer.Stop()
    if ($process.HasExited) { throw "PeerOnQ exited during startup with code $($process.ExitCode)." }
    $startupMs = if ($process.MainWindowHandle -ne 0) { [Math]::Round($startupTimer.Elapsed.TotalMilliseconds, 3) } else { $null }
    $startupFailure = if ($null -eq $startupMs) { 'No top-level window was observed within 30 seconds.' } else { $null }

    $cpu = New-Object 'System.Collections.Generic.List[double]'
    $workingSet = New-Object 'System.Collections.Generic.List[double]'
    $privateMemory = New-Object 'System.Collections.Generic.List[double]'
    $gpuDedicated = New-Object 'System.Collections.Generic.List[double]'
    $gpuFailure = $null
    $previousCpu = $process.TotalProcessorTime
    $previousTime = [DateTimeOffset]::UtcNow
    $sampleUntil = [DateTimeOffset]::UtcNow.AddSeconds($DurationSeconds)
    while ([DateTimeOffset]::UtcNow -lt $sampleUntil) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw 'PeerOnQ exited during the idle benchmark.' }
        $now = [DateTimeOffset]::UtcNow
        $elapsed = ($now - $previousTime).TotalMilliseconds
        $cpuDelta = ($process.TotalProcessorTime - $previousCpu).TotalMilliseconds
        $cpu.Add([Math]::Max(0, ($cpuDelta / $elapsed / [Environment]::ProcessorCount) * 100))
        $workingSet.Add($process.WorkingSet64 / 1MB)
        $privateMemory.Add($process.PrivateMemorySize64 / 1MB)
        $previousCpu = $process.TotalProcessorTime
        $previousTime = $now

        if ($null -eq $gpuFailure) {
            try {
                $counter = Get-Counter '\GPU Process Memory(*)\Dedicated Usage' -ErrorAction Stop
                $matching = $counter.CounterSamples | Where-Object { $_.InstanceName -match "^pid_$($process.Id)_" }
                $gpuDedicated.Add((($matching | Measure-Object -Property CookedValue -Sum).Sum) / 1MB)
            } catch {
                $gpuFailure = 'GPU Process Memory performance counters are unavailable on this machine.'
            }
        }
    }

    $media = $null
    if ($MediaDiagnosticsPath) {
        $mediaPath = (Resolve-Path -LiteralPath $MediaDiagnosticsPath).Path
        $media = Get-Content -LiteralPath $mediaPath -Raw | ConvertFrom-Json -Depth 32
    }

    $result = [ordered]@{
        schemaVersion = 1
        measuredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        conditions = [ordered]@{
            machineNameHash = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME))) -Algorithm SHA256).Hash.Substring(0, 12)
            os = [Environment]::OSVersion.VersionString
            architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
            logicalProcessors = [Environment]::ProcessorCount
            durationSeconds = $DurationSeconds
            mode = 'idle-main-window'
            executableVersion = $process.MainModule.FileVersionInfo.FileVersion
        }
        startup = [ordered]@{ milliseconds = $startupMs; unavailableReason = $startupFailure }
        idle = [ordered]@{
            cpuPercentMedian = Get-Percentile $cpu.ToArray() 50
            cpuPercentP95 = Get-Percentile $cpu.ToArray() 95
            workingSetMiBMedian = Get-Percentile $workingSet.ToArray() 50
            workingSetMiBP95 = Get-Percentile $workingSet.ToArray() 95
            privateMemoryMiBP95 = Get-Percentile $privateMemory.ToArray() 95
            gpuDedicatedMiBP95 = Get-Percentile $gpuDedicated.ToArray() 95
            gpuUnavailableReason = $gpuFailure
            samples = $cpu.Count
        }
        mediaDiagnostics = $media
        notMeasured = if ($null -eq $media) {
            @('active capture CPU/GPU', 'encode CPU/GPU', 'decode CPU/GPU', 'capture/encode/render FPS',
              'input latency', 'end-to-end frame latency', 'network bitrate/loss', 'reconnect duration',
              'file-transfer throughput')
        } else { @('input latency', 'true end-to-end frame latency', 'file-transfer throughput') }
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
    [IO.File]::WriteAllText($output, ($result | ConvertTo-Json -Depth 32), [Text.UTF8Encoding]::new($false))
    Write-Host "Performance evidence: $output"
} finally {
    if ($launched -and -not $KeepRunning -and $null -ne $process -and -not $process.HasExited) {
        if (-not $process.CloseMainWindow()) { $process.Kill() }
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    if ($null -ne $process) { $process.Dispose() }
}
