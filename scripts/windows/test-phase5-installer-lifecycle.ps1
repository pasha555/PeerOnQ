<# Runs destructive MSI lifecycle checks only on an explicitly approved disposable clean x64 VM. #>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineMsi,
    [Parameter(Mandatory)][string]$CandidateMsi,
    [Parameter(Mandatory)][switch]$ConfirmDisposableCleanMachine,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $ConfirmDisposableCleanMachine) {
    throw 'Use -ConfirmDisposableCleanMachine only on a disposable clean Windows x64 VM.'
}

function Get-MsiProperty([string]$Path, [string]$Name) {
    $windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    $record = $null
    try {
        $database = $windowsInstaller.OpenDatabase($Path, 0)
        $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Name'")
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw "MSI property is missing: $Name" }
        return [string]$record.StringData(1)
    }
    finally {
        if ($null -ne $record) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null }
        if ($null -ne $view) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
        if ($null -ne $database) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller) | Out-Null
    }
}

function Invoke-Msi([string]$Operation, [string]$Msi, [string]$LogPath, [int[]]$AllowedExitCodes = @(0)) {
    $arguments = @($Operation, ('"' + $Msi + '"'), '/qn', '/norestart', '/l*v', ('"' + $LogPath + '"'))
    $process = Start-Process -FilePath (Join-Path ([Environment]::GetFolderPath('System')) 'msiexec.exe') `
        -ArgumentList $arguments -Wait -PassThru
    if ($AllowedExitCodes -notcontains $process.ExitCode) {
        throw "Windows Installer $Operation failed with $($process.ExitCode). See $LogPath"
    }
    return $process.ExitCode
}

function Assert-NoPersistenceSideEffects {
    if (Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'PeerOnQ' -or $_.DisplayName -match 'PeerOnQ' }) {
        throw 'The MSI created an unexpected PeerOnQ Windows service.'
    }
    if (Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -match 'PeerOnQ' -or $_.TaskPath -match 'PeerOnQ' }) {
        throw 'The MSI created an unexpected PeerOnQ scheduled task.'
    }
    if (Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'PeerOnQ' }) {
        throw 'The MSI created an unexpected PeerOnQ firewall rule.'
    }
}

$baseline = (Resolve-Path -LiteralPath $BaselineMsi).Path
$candidate = (Resolve-Path -LiteralPath $CandidateMsi).Path
if ([Environment]::Is64BitOperatingSystem -ne $true -or $env:PROCESSOR_ARCHITECTURE -notmatch 'AMD64') {
    throw 'This lifecycle gate requires a clean native x64 Windows environment.'
}
$baselineVersion = [version](Get-MsiProperty $baseline 'ProductVersion')
$candidateVersion = [version](Get-MsiProperty $candidate 'ProductVersion')
if ($candidateVersion -le $baselineVersion) { throw 'CandidateMsi must be newer than BaselineMsi.' }

$installed = Get-ItemProperty `
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' `
    -ErrorAction SilentlyContinue | Where-Object DisplayName -eq 'PeerOnQ'
if ($installed) { throw 'PeerOnQ is already installed. Use a disposable clean VM; this script will not replace an existing installation.' }
Assert-NoPersistenceSideEffects

if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $env:TEMP "peeronq-phase5-msi-$($candidateVersion)-$([Guid]::NewGuid().ToString('N'))"
}
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $evidence) { throw "EvidenceDirectory already exists: $evidence" }
[IO.Directory]::CreateDirectory($evidence) | Out-Null

$installFolder = Join-Path $env:ProgramFiles 'PeerOnQ'
$executable = Join-Path $installFolder 'PeerOnQ.exe'
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PeerOnQ'
$marker = Join-Path $dataRoot 'phase5-installer-preservation.marker'
$candidateInstalled = $false
try {
    Invoke-Msi '/i' $baseline (Join-Path $evidence '01-clean-install.log') | Out-Null
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Clean install did not create PeerOnQ.exe.' }
    if (-not (Test-Path -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PeerOnQ\PeerOnQ.lnk'))) {
        throw 'Clean install did not create the all-users Start menu shortcut.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'PeerOnQ.lnk'))) {
        throw 'The default desktop-shortcut feature was not installed.'
    }
    if (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('CommonStartup')) 'PeerOnQ.lnk')) {
        throw 'Startup must remain off by default.'
    }
    $registration = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' `
        -ErrorAction SilentlyContinue | Where-Object DisplayName -eq 'PeerOnQ'
    if (@($registration).Count -ne 1) { throw 'PeerOnQ is not registered exactly once in Installed Apps.' }
    Assert-NoPersistenceSideEffects

    $app = Start-Process -FilePath $executable -PassThru
    try {
        if (-not $app.WaitForInputIdle(15000) -or $app.HasExited) { throw 'Installed PeerOnQ did not reach an interactive launch state.' }
    }
    finally {
        if (-not $app.HasExited) {
            $app.CloseMainWindow() | Out-Null
            if (-not $app.WaitForExit(15000)) { Stop-Process -Id $app.Id -Force }
        }
        $app.Dispose()
    }

    [IO.Directory]::CreateDirectory($dataRoot) | Out-Null
    [IO.File]::WriteAllText($marker, 'preserve-across-upgrade-repair-uninstall', [Text.UTF8Encoding]::new($false))

    Invoke-Msi '/i' $candidate (Join-Path $evidence '02-upgrade.log') | Out-Null
    $candidateInstalled = $true
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Upgrade removed per-user PeerOnQ data.' }

    Invoke-Msi '/fa' $candidate (Join-Path $evidence '03-repair.log') | Out-Null
    if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath $marker)) {
        throw 'Repair did not restore the app while preserving per-user data.'
    }

    $downgradeExit = Invoke-Msi '/i' $baseline (Join-Path $evidence '04-downgrade-rejection.log') @(1603, 1638)
    if ($downgradeExit -eq 0) { throw 'Downgrade unexpectedly succeeded.' }

    Invoke-Msi '/x' $candidate (Join-Path $evidence '05-uninstall.log') | Out-Null
    $candidateInstalled = $false
    if (Test-Path -LiteralPath $executable) { throw 'Uninstall left the installer-owned executable behind.' }
    if (-not (Test-Path -LiteralPath $marker)) { throw 'Uninstall silently removed per-user data.' }
    Assert-NoPersistenceSideEffects
}
finally {
    if ($candidateInstalled) {
        try { Invoke-Msi '/x' $candidate (Join-Path $evidence '99-failure-cleanup.log') @(0, 1605) | Out-Null } catch { }
    }
}

Write-Host "Phase 5 clean-machine MSI lifecycle passed. User-data marker was intentionally preserved at: $marker"
Write-Host "Evidence: $evidence"
