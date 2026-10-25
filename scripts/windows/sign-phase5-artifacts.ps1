<# Authenticode-signs release files with a CI-provided certificate and RFC3161 timestamp. #>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactPath,
    [Parameter(Mandatory)][string]$CertificatePath,
    [Parameter(Mandatory)][string]$TimestampUrl,
    [string]$PasswordEnvironmentVariable = 'PEERONQ_SIGNING_PFX_PASSWORD'
)

$ErrorActionPreference = 'Stop'
$artifact = (Resolve-Path -LiteralPath $ArtifactPath).Path
$certificate = (Resolve-Path -LiteralPath $CertificatePath).Path
$password = [Environment]::GetEnvironmentVariable($PasswordEnvironmentVariable)
if ([string]::IsNullOrEmpty($password)) { throw "$PasswordEnvironmentVariable is not set." }
if (-not $TimestampUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw 'TimestampUrl must use HTTPS.' }

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signtool = Get-ChildItem -LiteralPath $sdkRoot -Filter signtool.exe -Recurse -File |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signtool) { throw 'signtool.exe was not found in the Windows SDK.' }

$files = if ((Get-Item -LiteralPath $artifact).PSIsContainer) {
    Get-ChildItem -LiteralPath $artifact -Recurse -File |
        Where-Object {
            $_.Extension -eq '.msi' -or
            (($_.Extension -eq '.exe' -or $_.Extension -eq '.dll') -and $_.Name -like 'PeerOnQ*')
        }
} else {
    @(Get-Item -LiteralPath $artifact)
}
if ($files.Count -eq 0) { throw 'No Authenticode-signable artifacts were found.' }

foreach ($file in $files) {
    & $signtool.FullName sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $certificate /p $password $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($file.FullName)" }
    & $signtool.FullName verify /pa /all $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $($file.FullName)" }
}
Write-Host "Signed and verified $($files.Count) artifact(s)."
