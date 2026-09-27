[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [uri]$SignalingUrl,
    [string]$DevelopmentRootCertificate,
    [string]$OutputRoot,
    [switch]$BlueStacksX86
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $repoRoot 'src\PeerOnQ.App.Android\PeerOnQ.App.Android.csproj'

if (-not $SignalingUrl.IsAbsoluteUri -or
    $SignalingUrl.Scheme -ne 'wss' -or
    $SignalingUrl.AbsolutePath.TrimEnd('/') -ne '/ws' -or
    $SignalingUrl.UserInfo -or $SignalingUrl.Query -or $SignalingUrl.Fragment) {
    throw 'SignalingUrl must be an absolute wss:// URL ending in /ws without credentials, query or fragment.'
}

$version = (& dotnet msbuild $project -nologo -getProperty:PeerOnQAndroidClientVersion | Select-Object -Last 1).Trim()
$versionCode = (& dotnet msbuild $project -nologo -getProperty:PeerOnQAndroidClientVersionCode | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$' -or $versionCode -notmatch '^[1-9]\d*$') {
    throw 'The canonical Android version properties could not be resolved.'
}

if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'app-updates\android' }
$output = [IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must remain inside the repository.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null

$artifactVariant = if ($BlueStacksX86) { 'android-bluestacks-x86' } else { 'android' }
$packageArchitectures = if ($BlueStacksX86) { @('x86') } else { @('arm64-v8a', 'x86_64') }
$artifactName = "peeronq-$version-$artifactVariant-development-signed-preview.apk"
$artifact = Join-Path $output $artifactName
$checksum = "$artifact.sha256"
$metadata = Join-Path $output "peeronq-$version-$artifactVariant-development-signed-preview.json"
foreach ($path in @($artifact, $checksum, $metadata)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite an existing Android artifact: $path" }
}

$certificatePath = $null
if ($DevelopmentRootCertificate) {
    $certificatePath = [IO.Path]::GetFullPath($DevelopmentRootCertificate)
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf) -or [IO.Path]::GetExtension($certificatePath) -ne '.cer') {
        throw 'DevelopmentRootCertificate must point to an existing public .cer file.'
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try {
        $constraints = $certificate.Extensions |
            Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] } |
            Select-Object -First 1
        if ($certificate.HasPrivateKey -or -not $constraints -or -not $constraints.CertificateAuthority) {
            throw 'DevelopmentRootCertificate must be a public certificate-authority certificate without a private key.'
        }
    } finally {
        $certificate.Dispose()
    }
}

$jdkCandidates = @()
if ($env:JAVA_HOME) { $jdkCandidates += $env:JAVA_HOME }
$microsoftJdks = Get-ChildItem -LiteralPath (Join-Path $env:ProgramFiles 'Microsoft') -Directory -Filter 'jdk-21*' -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object FullName
$jdkCandidates += $microsoftJdks
$javaSdk = $jdkCandidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'bin\javac.exe') } | Select-Object -First 1
if (-not $javaSdk) { throw 'Microsoft OpenJDK 21 is required to build the Android viewer.' }

$sdkCandidates = @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, (Join-Path $env:LOCALAPPDATA 'Android\Sdk')) |
    Where-Object { $_ }
$androidSdk = $sdkCandidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'platforms\android-36\android.jar') } | Select-Object -First 1
if (-not $androidSdk) {
    throw 'Android SDK platform 36 is required. Run the InstallAndroidDependencies target documented in packaging/android/README.md.'
}

$buildTools = Get-ChildItem -LiteralPath (Join-Path $androidSdk 'build-tools') -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $buildTools) { throw 'Android SDK build-tools are missing.' }
$aapt = Join-Path $buildTools.FullName 'aapt.exe'
$apksigner = Join-Path $buildTools.FullName 'lib\apksigner.jar'
$java = Join-Path $javaSdk 'bin\java.exe'
if (-not (Test-Path -LiteralPath $aapt) -or -not (Test-Path -LiteralPath $apksigner)) {
    throw 'Android aapt/apksigner tools are missing.'
}

$staging = [IO.Path]::GetFullPath((Join-Path $output ('.peeronq-android-build-' + [IO.Path]::GetRandomFileName())))
$outputPrefix = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $staging.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Android staging directory escaped OutputRoot.'
}
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    $buildStartedAt = [DateTime]::UtcNow
    $arguments = @(
        'build', $project, '--configuration', 'Release', '--framework', 'net10.0-android', '--nologo', '-t:Rebuild',
        "-p:PeerOnQSignalingUrl=$($SignalingUrl.AbsoluteUri)",
        "-p:JavaSdkDirectory=$javaSdk",
        "-p:AndroidSdkDirectory=$androidSdk",
        "-p:Version=$version",
        "-p:AssemblyVersion=$version.0",
        "-p:FileVersion=$version.0",
        "-p:InformationalVersion=$version-android-viewer-development-signed-preview"
    )
    if ($certificatePath) { $arguments += "-p:PeerOnQDevelopmentRootCertificate=$certificatePath" }
    if ($BlueStacksX86) { $arguments += '-p:PeerOnQAndroidBlueStacksX86=true' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Android build failed.' }

    $builtApk = Join-Path $repoRoot 'src\PeerOnQ.App.Android\bin\Release\net10.0-android\io.peeronq.android-Signed.apk'
    if (-not (Test-Path -LiteralPath $builtApk -PathType Leaf)) { throw 'The installable Android APK was not produced.' }
    if ((Get-Item -LiteralPath $builtApk).LastWriteTimeUtc -lt $buildStartedAt.AddSeconds(-2)) {
        throw 'The Android publish did not produce a fresh APK payload.'
    }
    $assemblyInfo = Join-Path $repoRoot 'src\PeerOnQ.App.Android\obj\Release\net10.0-android\PeerOnQ.App.Android.AssemblyInfo.cs'
    $expectedEndpointMetadata = 'AssemblyMetadata("PeerOnQSignalingUrl", "{0}")' -f $SignalingUrl.AbsoluteUri
    if (-not (Test-Path -LiteralPath $assemblyInfo -PathType Leaf) -or
        [IO.File]::ReadAllText($assemblyInfo).IndexOf($expectedEndpointMetadata, [StringComparison]::Ordinal) -lt 0) {
        throw 'The Android assembly does not contain the requested signaling endpoint metadata.'
    }

    & $java -Xmx1024M -jar $apksigner verify --verbose --print-certs $builtApk
    if ($LASTEXITCODE -ne 0) { throw 'Android APK signature verification failed.' }
    $badgingOutput = & $aapt dump badging $builtApk
    $aaptExitCode = $LASTEXITCODE
    [string]$badging = $badgingOutput | Select-Object -First 1
    $expectedBadging = "package: name='io.peeronq.android' versionCode='$versionCode' versionName='$version'"
    if ($aaptExitCode -ne 0 -or
        -not $badging.StartsWith($expectedBadging, [StringComparison]::Ordinal)) {
        throw "Android APK package/version validation failed: $badging"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($builtApk)
    try {
        $entries = @($archive.Entries | ForEach-Object FullName)
        $requiredPayload = @('AndroidManifest.xml') + @($packageArchitectures | ForEach-Object { "lib/$_/libmonodroid.so" })
        foreach ($required in $requiredPayload) {
            if ($entries -notcontains $required) { throw "Android APK is missing required payload: $required" }
        }
        if ($entries | Where-Object { $_ -match '(^|/)vpxmd\.dll$' }) {
            throw 'Android APK contains the unsupported desktop vpxmd.dll payload.'
        }
    } finally {
        $archive.Dispose()
    }

    Copy-Item -LiteralPath $builtApk -Destination $artifact
    $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksum, "$hash  $artifactName`n")
    $manifest = [ordered]@{
        product = 'PeerOnQ Android Viewer'
        version = $version
        versionCode = [int64]$versionCode
        packageId = 'io.peeronq.android'
        channel = 'development-signed-preview'
        architectures = $packageArchitectures
        minimumAndroidApi = 25
        blueStacksNougat32 = [bool]$BlueStacksX86
        viewerOnly = $true
        androidHost = $false
        unattendedAccess = $false
        fileTransfer = $false
        signalingHost = $SignalingUrl.DnsSafeHost
        sha256 = $hash
    }
    [IO.File]::WriteAllText($metadata, ($manifest | ConvertTo-Json -Depth 4) + "`n")
} finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if ($resolvedStaging.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedStaging).StartsWith('.peeronq-android-build-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Warning "Refusing to remove unexpected Android staging path: $resolvedStaging"
    }
}

Write-Host "Created $artifact"
Write-Host "Created $checksum"
Write-Host "Created $metadata"
