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

function Test-WebsiteEnvironmentIsolation([bool]$Inherited) {
    $names = @(
        'VITE_PEERONQ_SERVER_WINDOWS_X64_AVAILABLE',
        'VITE_PEERONQ_SERVER_WINDOWS_ARM64_AVAILABLE',
        'VITE_PEERONQ_EMBEDDED_WINDOWS_X64_URL',
        'VITE_PEERONQ_EMBEDDED_WINDOWS_ARM64_URL',
        'VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE',
        'VITE_PEERONQ_WINDOWS_DOWNLOAD_VERSION',
        'VITE_PEERONQ_EMBEDDED_WINDOWS_VERSION',
        'VITE_PEERONQ_EMBEDDED_WINDOWS_UNSIGNED_PILOT',
        'VITE_PEERONQ_TRACKED_DOWNLOAD_BASE_URL'
    )
    $saved = @{}
    foreach ($name in $names) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        $value = if ($Inherited) { 'inherited-test-value' } else { $null }
        [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
    $probe = [pscustomobject]@{ Captured = $null }
    $previousExitCode = $global:LASTEXITCODE
    # Fail at the actual build boundary: no Vite process, signing tool, or key read runs.
    function pnpm {
        $probe.Captured = @{}
        foreach ($name in $names) {
            $probe.Captured[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
        $global:LASTEXITCODE = 61
    }
    try {
        $failedAtBuild = $false
        try {
            & $builder -Version 0.0.0 -PrivateKeyPemPath $placeholderKeyPath `
                -ExpectedPublicKeySpkiBase64 invalid -OutputDirectory (Join-Path $temporary 'unused-output')
        }
        catch {
            if ($_.Exception.Message -ne 'The public website build failed.') { throw }
            $failedAtBuild = $true
        }
        if (!$failedAtBuild -or $null -eq $probe.Captured) {
            throw 'The environment regression must stop at the intercepted build boundary.'
        }
        foreach ($name in $names) {
            $expected = if ($name -like '*SERVER_WINDOWS_*_AVAILABLE') { 'true' } `
                elseif ($name -eq 'VITE_PEERONQ_WINDOWS_DOWNLOADS_AVAILABLE') { 'false' } else { $null }
            $matches = if ($null -eq $expected) { [string]::IsNullOrEmpty($probe.Captured[$name]) } `
                else { $probe.Captured[$name] -ceq $expected }
            if (!$matches) {
                throw "Website build environment was not neutral for $name (inherited=$Inherited)."
            }
            $expectedRestored = if ($Inherited) { 'inherited-test-value' } else { $null }
            $restored = [Environment]::GetEnvironmentVariable($name, 'Process')
            $matches = if ($null -eq $expectedRestored) { [string]::IsNullOrEmpty($restored) } `
                else { $restored -ceq $expectedRestored }
            if (!$matches) {
                throw "Website builder did not restore $name after build failure (inherited=$Inherited)."
            }
        }
        if (Test-Path -LiteralPath (Join-Path $temporary 'unused-output')) {
            throw 'An intercepted failed build must not leave website release artifacts.'
        }
    }
    finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
        $global:LASTEXITCODE = $previousExitCode
        $probe.Captured = $null
    }
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
    $placeholderKeyPath = Join-Path $temporary 'not-a-key.fixture'
    [IO.File]::WriteAllText($placeholderKeyPath, 'Synthetic path fixture, not a signing key.')
    Test-WebsiteEnvironmentIsolation $true
    Test-WebsiteEnvironmentIsolation $false
    Write-Host 'Website patch client trust-boundary invariant test passed.'
    Write-Host 'Website patch x64/ARM64 environment isolation and failure restoration passed (inherited and absent).'
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        $absoluteTemporary = [IO.Path]::GetFullPath($temporary)
        if (!$absoluteTemporary.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) `
            -or [IO.Path]::GetFileName($absoluteTemporary) -notmatch '^peeronq-website-invariant-[a-f0-9]{32}$') {
            throw 'Refusing to remove an unexpected invariant-test directory.'
        }
        Remove-Item -LiteralPath $absoluteTemporary -Recurse -Force
    }
}
