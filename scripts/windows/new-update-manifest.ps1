<# Generates a detached ECDSA-signed update envelope after Authenticode verification. #>
#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][uri]$PackageUrl,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][version]$MinimumSupportedVersion,
    [Parameter(Mandatory)][ValidateSet('stable', 'beta')][string]$Channel,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory)][ValidateLength(1, 128)][string]$KeyId,
    [Parameter(Mandatory)][string]$PrivateKeyPemPath,
    [Parameter(Mandatory)][ValidateLength(1, 2048)][string]$ExpectedPublicKeySpkiBase64,
    [Parameter(Mandatory)][string]$ExpectedPublisherCertificateSha256,
    [ValidateRange(0, 100)][int]$RolloutPercentage = 100,
    [Parameter(Mandatory)][ValidateLength(1, 128)][string]$RolloutSeed,
    [ValidateRange(1, 720)][int]$ValidForHours = 48,
    [switch]$SecurityEmergency
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$privateKey = (Resolve-Path -LiteralPath $PrivateKeyPemPath).Path
if ($PackageUrl.Scheme -ne 'https') { throw 'PackageUrl must use HTTPS.' }
if ($Version -lt $MinimumSupportedVersion) { throw 'Version cannot be below MinimumSupportedVersion.' }
try {
    $expectedPublicKey = [Convert]::FromBase64String($ExpectedPublicKeySpkiBase64)
} catch {
    throw 'ExpectedPublicKeySpkiBase64 is invalid.'
}

$expectedPublisher = ($ExpectedPublisherCertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($expectedPublisher.Length -ne 64) { throw 'ExpectedPublisherCertificateSha256 must be a SHA-256 fingerprint.' }
$authenticode = Get-AuthenticodeSignature -LiteralPath $package
if ($authenticode.Status -ne 'Valid' -or $null -eq $authenticode.SignerCertificate) {
    throw "Package Authenticode validation failed: $($authenticode.StatusMessage)"
}
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $actualPublisher = [BitConverter]::ToString($sha256.ComputeHash($authenticode.SignerCertificate.RawData)).Replace('-', '')
} finally {
    $sha256.Dispose()
}
if ($actualPublisher -ne $expectedPublisher) { throw 'Package signer does not match the expected publisher certificate.' }

$packageInfo = Get-Item -LiteralPath $package
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$now = [DateTimeOffset]::UtcNow
$manifest = [ordered]@{
    schemaVersion = 1
    productId = 'com.peeronq.desktop'
    version = $Version.ToString(4)
    minimumSupportedVersion = $MinimumSupportedVersion.ToString(4)
    channel = if ($Channel -eq 'stable') { 0 } else { 1 }
    issuedAt = $now.ToString('O')
    expiresAt = $now.AddHours($ValidForHours).ToString('O')
    rolloutPercentage = $RolloutPercentage
    rolloutSeed = $RolloutSeed
    securityEmergency = [bool]$SecurityEmergency
    packages = @([ordered]@{
        architecture = $Architecture
        url = $PackageUrl.AbsoluteUri
        sha256 = $packageHash
        sizeBytes = $packageInfo.Length
        installerType = 'msi'
    })
}

$payloadJson = $manifest | ConvertTo-Json -Depth 8 -Compress
$payload = [Text.Encoding]::UTF8.GetBytes($payloadJson)
$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportFromPem([IO.File]::ReadAllText($privateKey))
    if ($ecdsa.KeySize -ne 256) { throw 'The update signing key must use ECDSA P-256.' }
    $actualPublicKey = $ecdsa.ExportSubjectPublicKeyInfo()
    if (-not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($actualPublicKey, $expectedPublicKey)) {
        throw 'The update private key does not match the public key compiled into the client.'
    }
    $signature = $ecdsa.SignData(
        $payload,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
} finally {
    $ecdsa.Dispose()
}

$verifier = [Security.Cryptography.ECDsa]::Create()
try {
    $bytesRead = 0
    $verifier.ImportSubjectPublicKeyInfo($expectedPublicKey, [ref]$bytesRead)
    if ($bytesRead -ne $expectedPublicKey.Length -or -not $verifier.VerifyData(
        $payload,
        $signature,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) {
        throw 'Generated update signature did not verify against the compiled public key.'
    }
} finally {
    $verifier.Dispose()
    [Array]::Clear($expectedPublicKey, 0, $expectedPublicKey.Length)
}

$envelope = [ordered]@{
    schemaVersion = 1
    keyId = $KeyId
    payload = [Convert]::ToBase64String($payload)
    signature = [Convert]::ToBase64String($signature)
}
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
if ($outputDirectory) { [IO.Directory]::CreateDirectory($outputDirectory) | Out-Null }
[IO.File]::WriteAllText($outputFullPath, ($envelope | ConvertTo-Json -Depth 4 -Compress), [Text.UTF8Encoding]::new($false))
Write-Host "Signed update manifest: $outputFullPath"
