<# Verifies that the website signing boundary cannot select or embed a Windows installer. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot 'build-peeronq-website-patch.ps1'
$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile($builder, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Website patch builder has PowerShell parse errors: $($parseErrors[0].Message)"
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("peeronq-website-invariant-{0}" -f [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null
    $powerShell = (Get-Process -Id $PID).Path
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $powerShell
    $startInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$builder`" -Version 0.0.0 -PrivateKeyPemPath `"missing.pem`" -ExpectedPublicKeySpkiBase64 invalid -OutputDirectory `"$temporary`" -WindowsClientMsiPath `"client.msi`""
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($startInfo)
    $output = $process.StandardOutput.ReadToEnd() + $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -eq 0) {
        throw 'Website patch builder unexpectedly accepted a Windows client.'
    }
    if ($output -notmatch [regex]::Escape('Website patches cannot select or embed Windows clients.')) {
        throw "Website patch builder did not report its client trust-boundary invariant: $output"
    }
    if (Get-ChildItem -LiteralPath $temporary -File -ErrorAction SilentlyContinue) {
        throw 'A failed website patch invariant check must not leave release artifacts.'
    }
    Write-Host 'Website patch client trust-boundary invariant test passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
