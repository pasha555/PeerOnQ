<# Verifies SignPath-signed PeerOnQ MSIs and the PeerOnQ binaries inside them, writes SHA256SUMS.txt,
   and reports each signer's subject and SHA-256 thumbprint (needed for the update publisher pin).

   Policy release: every MSI and every PeerOnQ binary inside it must pass `signtool verify /pa`, carry an
   RFC 3161 timestamp, and be signed by the expected publisher (and, when given, one of the allowed
   SHA-256 certificate fingerprints). Policy test: SignPath's test certificate is not trusted by Windows,
   so a present, consistent signature is required and trust failures are reported, not fatal.
   Vendor-signed third-party files (Windows App SDK) must keep their vendor signature. #>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [ValidateSet('release', 'test')][string]$Policy = 'release',
    [string]$ExpectedPublisher = 'SignPath Foundation',
    [string]$AllowedPublisherSha256,
    [string]$SummaryPath
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Directory).Path
$strict = $Policy -eq 'release'

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$signtool = Get-ChildItem -LiteralPath $sdkRoot -Filter signtool.exe -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $signtool) { throw 'signtool.exe was not found in the Windows SDK.' }

$allowed = @()
if (-not [string]::IsNullOrWhiteSpace($AllowedPublisherSha256)) {
    $allowed = @($AllowedPublisherSha256.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { ($_ -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() })
}

function Get-SignatureFacts([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $certificate = $signature.SignerCertificate
    & $signtool.FullName verify /pa /q $Path *> $null
    $signtoolPassed = $LASTEXITCODE -eq 0
    $signed = $null -ne $certificate
    [pscustomobject]@{
        Path = $Path
        Status = $signature.Status.ToString()
        SigntoolPassed = $signtoolPassed
        Subject = if ($signed) { $certificate.Subject } else { $null }
        Publisher = if ($signed) { $certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) } else { $null }
        Sha256 = if ($signed) { [Convert]::ToHexString($certificate.GetCertHash([Security.Cryptography.HashAlgorithmName]::SHA256)) } else { $null }
        Timestamped = $null -ne $signature.TimeStamperCertificate
    }
}

$failures = [Collections.Generic.List[string]]::new()
function Assert-Signed($Facts, [string]$Label, [string]$ExpectedSha256) {
    if (-not $Facts.Sha256) { $failures.Add("$Label is not signed."); return }
    if ($ExpectedSha256 -and $Facts.Sha256 -ne $ExpectedSha256) {
        $failures.Add("$Label is signed by $($Facts.Subject), not by the MSI's signer.")
    }
    if (-not $strict) { return }
    if (-not $Facts.SigntoolPassed -or $Facts.Status -ne 'Valid') { $failures.Add("$Label failed signtool verify /pa ($($Facts.Status)).") }
    if (-not $Facts.Timestamped) { $failures.Add("$Label has no RFC 3161 timestamp.") }
    if ($Facts.Publisher -ne $ExpectedPublisher) { $failures.Add("$Label publisher is '$($Facts.Publisher)', expected '$ExpectedPublisher'.") }
    if ($allowed.Count -gt 0 -and $Facts.Sha256 -notin $allowed) { $failures.Add("$Label certificate $($Facts.Sha256) is not an allowed publisher fingerprint.") }
}

$msis = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter 'PeerOnQ-*.msi' | Sort-Object Name)
if ($msis.Count -eq 0) { throw "No PeerOnQ MSI was found under $root." }
if ($strict) {
    foreach ($architecture in 'x64', 'arm64') {
        if (-not ($msis | Where-Object Name -like "*-$architecture.msi")) { $failures.Add("The $architecture MSI is missing.") }
    }
}

$rows = [Collections.Generic.List[string]]::new()
foreach ($msi in $msis) {
    $msiFacts = Get-SignatureFacts $msi.FullName
    Assert-Signed $msiFacts $msi.Name $null
    $rows.Add("| $($msi.Name) | $($msiFacts.Status) | $($msiFacts.Subject) | ``$($msiFacts.Sha256)`` | $($msiFacts.Timestamped) |")

    # An administrative install extracts the payload without installing anything.
    $extracted = Join-Path ([IO.Path]::GetTempPath()) ("peeronq-verify-" + [Guid]::NewGuid().ToString('N'))
    $process = Start-Process msiexec.exe -ArgumentList @('/a', "`"$($msi.FullName)`"", '/qn', "TARGETDIR=`"$extracted`"") -Wait -PassThru
    if ($process.ExitCode -ne 0) { $failures.Add("$($msi.Name) could not be extracted (msiexec $($process.ExitCode))."); continue }
    try {
        $own = @(Get-ChildItem -LiteralPath $extracted -Recurse -File |
            Where-Object { $_.Name -eq 'PeerOnQ.exe' -or ($_.Name -like 'PeerOnQ*.dll') -or ($_.Name -like 'PeerOnQ.*.exe') })
        if (-not ($own | Where-Object Name -eq 'PeerOnQ.exe')) { $failures.Add("$($msi.Name) contains no PeerOnQ.exe.") }
        foreach ($file in $own) {
            $facts = Get-SignatureFacts $file.FullName
            Assert-Signed $facts "$($msi.Name)!$($file.Name)" $msiFacts.Sha256
            if ($file.Name -eq 'PeerOnQ.exe') {
                $rows.Add("| $($msi.Name) -> PeerOnQ.exe | $($facts.Status) | $($facts.Subject) | ``$($facts.Sha256)`` | $($facts.Timestamped) |")
            }
        }
        foreach ($vendorFile in @(Get-ChildItem -LiteralPath $extracted -Recurse -File -Include 'RestartAgent.exe', 'Microsoft.UI.Xaml.dll')) {
            $vendor = Get-SignatureFacts $vendorFile.FullName
            if ($vendor.Sha256 -and $vendor.Sha256 -eq $msiFacts.Sha256) {
                $failures.Add("$($msi.Name)!$($vendorFile.Name) is a vendor file but was re-signed by the PeerOnQ signer.")
            }
        }
    } finally {
        Remove-Item -LiteralPath $extracted -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$checksums = $msis | ForEach-Object {
    "$((Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
}
[IO.File]::WriteAllLines((Join-Path $root 'SHA256SUMS.txt'), [string[]]$checksums, [Text.UTF8Encoding]::new($false))

$summary = @(
    "## PeerOnQ signature verification ($Policy policy)",
    '',
    '| File | Status | Signer subject | Signer SHA-256 thumbprint | Timestamped |',
    '| --- | --- | --- | --- | --- |'
) + $rows + @('', 'Add the release signer thumbprint to the repository variable `PEERONQ_PUBLISHER_CERTIFICATE_SHA256` (`;`-separated) before verified updates are enabled.')
if ($failures.Count -gt 0) { $summary += @('', '### Failures') + ($failures | ForEach-Object { "- $_" }) }
$summary | Write-Host
if ($SummaryPath) { Add-Content -LiteralPath $SummaryPath -Value ($summary -join [Environment]::NewLine) -Encoding utf8 }

if ($failures.Count -gt 0) { throw "Signature verification failed ($($failures.Count) problem(s))." }
Write-Host "Signature verification passed for $($msis.Count) MSI(s)."
