<# Verifies that a server bundle cannot be built without an explicit Windows MSI. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot 'build-peeronq-server-run.ps1'
$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile($builder, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Server bundle builder has PowerShell parse errors: $($parseErrors[0].Message)"
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("peeronq-server-invariant-{0}" -f [Guid]::NewGuid().ToString('N'))

function Invoke-BuilderProbe([string]$Version, [string]$OutputDirectory, [string]$AdditionalArguments = '') {
    $powerShell = (Get-Process -Id $PID).Path
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powerShell
    $startInfo.Arguments =
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$builder`" " +
        "-Version `"$Version`" -OutputDirectory `"$OutputDirectory`" $AdditionalArguments"
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
}

try {
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null

    foreach ($tamperedVersion in '.', '..', '01.2.3', '1.2.3-rc.1', '1.2.3.4', '1234567890.1.1') {
        $result = Invoke-BuilderProbe $tamperedVersion $temporary
        if ($result.ExitCode -eq 0) {
            throw "Server bundle builder unexpectedly accepted unsafe version '$tamperedVersion'."
        }
    }

    $result = Invoke-BuilderProbe '0.0.0' $temporary
    if ($result.ExitCode -eq 0) {
        throw 'Server bundle builder unexpectedly succeeded without -WindowsClientMsiPath.'
    }
    if ($result.Output -notmatch [regex]::Escape('-WindowsClientMsiPath is required.')) {
        throw "Server bundle builder did not report its mandatory Windows MSI invariant: $($result.Output)"
    }
    if (Get-ChildItem -LiteralPath $temporary -File -ErrorAction SilentlyContinue) {
        throw 'A failed version or mandatory-MSI invariant check must not leave a server bundle artifact.'
    }

    $result = Invoke-BuilderProbe '0.0.0' $temporary '-RequireSignature'
    if ($result.ExitCode -eq 0 -or $result.Output -notmatch 'GPG key ID is required') {
        throw "-RequireSignature did not fail closed before publication: $($result.Output)"
    }
    if (Get-ChildItem -LiteralPath $temporary -Force | Where-Object { $_.Name -like 'peeronq-server-*' -or $_.Name -like '.peeronq-server-publish-*' }) {
        throw 'A failed signed build must not leave a partial artifact trio or staging directory.'
    }

    $builderText = [IO.File]::ReadAllText($builder)
    foreach ($payloadEntry in @(
        'artifacts/peeronq-portal',
        'scripts/linux/peeronq-platform-upgrade-agent.sh',
        'scripts/linux/peeronq-platform-upgrade-agent.service',
        'scripts/linux/peeronq-platform-upgrade-agent.path')) {
        if ($builderText.IndexOf($payloadEntry, [StringComparison]::Ordinal) -lt 0) {
            throw "Server bundle omits constrained updater payload entry: $payloadEntry"
        }
    }
    $signaturePublish = $builderText.IndexOf('Move-Item -LiteralPath $workingSignaturePath -Destination $signaturePath', [StringComparison]::Ordinal)
    $checksumPublish = $builderText.IndexOf('Move-Item -LiteralPath $workingChecksumPath -Destination $checksumPath', [StringComparison]::Ordinal)
    $bundlePublish = $builderText.IndexOf('Move-Item -LiteralPath $workingOutputPath -Destination $outputPath', [StringComparison]::Ordinal)
    if ($signaturePublish -lt 0 -or $checksumPublish -le $signaturePublish -or $bundlePublish -le $checksumPublish) {
        throw 'Signed publication must expose signature and checksum before the .run commit marker.'
    }
    if ($builderText.IndexOf('$publishStageRoot = Join-Path $OutputDirectory', [StringComparison]::Ordinal) -lt 0) {
        throw 'Signed publication staging must stay on the output filesystem for atomic renames.'
    }

    Write-Host 'Server bundle publication, version, and Windows MSI invariant tests passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
