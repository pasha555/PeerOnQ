<# Validates MSI architecture, scope, features, persistence, contents, and optional signature. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [string]$ExpectedPublishDirectory,
    [switch]$RequireTrustedSignature,
    [switch]$SkipAdministrativeExtraction
)

$ErrorActionPreference = 'Stop'
$msi = (Resolve-Path -LiteralPath $MsiPath).Path
if ([IO.Path]::GetExtension($msi) -ne '.msi') { throw 'MsiPath must be an MSI package.' }
$expectedPublish = if ($ExpectedPublishDirectory) {
    (Resolve-Path -LiteralPath $ExpectedPublishDirectory).Path
} else {
    $null
}
$requiredSelfContainedRuntimeFiles = @(
    'Microsoft.WindowsAppRuntime.dll',
    'Microsoft.ui.xaml.dll',
    'Microsoft.UI.Xaml.Controls.dll',
    'DWriteCore.dll'
)
if ($expectedPublish) {
    foreach ($runtimeFile in $requiredSelfContainedRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $expectedPublish $runtimeFile) -PathType Leaf)) {
            throw "Publish output is framework-dependent; required Windows App Runtime file is missing: $runtimeFile"
        }
    }
}
if ($expectedPublish -and $SkipAdministrativeExtraction) {
    throw 'ExpectedPublishDirectory requires administrative extraction so the packaged payload can be compared.'
}

if ($RequireTrustedSignature) {
    $signature = Get-AuthenticodeSignature -LiteralPath $msi
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "MSI Authenticode verification failed: $($signature.StatusMessage)"
    }
}

$dtf = Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE '.nuget\packages\wixtoolset.sdk') `
    -Recurse -Filter WixToolset.Dtf.WindowsInstaller.dll -File |
    Where-Object { $_.FullName -match '\\tools\\net472\\WixToolset\.Dtf\.WindowsInstaller\.dll$' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $dtf) { throw 'WiX DTF was not found. Restore installer/PeerOnQ.Installer.wixproj first.' }
Add-Type -Path $dtf.FullName

function Read-Rows($Database, [string]$Query) {
    $rows = New-Object System.Collections.Generic.List[object]
    $view = $Database.OpenView($Query)
    try {
        $view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $values = @(1..$record.FieldCount | ForEach-Object { $record.GetString($_) })
                $rows.Add($values)
            } finally { $record.Dispose() }
        }
    } finally { $view.Dispose() }
    return $rows
}

function Get-EmbeddedStreamHash(
    $Database,
    [ValidateSet('Binary', 'Icon')][string]$Table,
    [string]$Name
) {
    $view = $Database.OpenView("SELECT ``Data`` FROM ``$Table`` WHERE ``Name``='$Name'")
    try {
        $view.Execute()
        $record = $view.Fetch()
        if ($null -eq $record) { throw "Missing $Table stream: $Name" }
        try {
            $stream = $record.GetStream(1)
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
            } finally {
                $sha256.Dispose()
                $stream.Dispose()
            }
        } finally {
            $record.Dispose()
        }
    } finally {
        $view.Dispose()
    }
}

function Get-DirectoryHashManifest([string]$Root) {
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    $manifest = @{}
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -Recurse -File) {
        if ($file.Extension -in @('.pdb', '.xml')) { continue }
        if (-not $file.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Payload file escaped expected root: $($file.FullName)"
        }
        $relative = $file.FullName.Substring($prefix.Length)
        $manifest[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash
    }
    return $manifest
}

$database = New-Object WixToolset.Dtf.WindowsInstaller.Database(
    $msi,
    ([WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly))
try {
    $properties = @{}
    foreach ($row in Read-Rows $database 'SELECT `Property`,`Value` FROM `Property`') { $properties[$row[0]] = $row[1] }
    if ($properties['ALLUSERS'] -ne '1' -or $properties.ContainsKey('MSIINSTALLPERUSER')) {
        throw 'MSI must use an explicit per-machine installation context.'
    }
    if ($properties['ProductName'] -ne 'PeerOnQ' -or $properties['ARPPRODUCTICON'] -ne 'PeerOnQIcon') {
        throw 'MSI product identity or Add/Remove Programs icon is invalid.'
    }

    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $brandStreams = @(
        [pscustomobject]@{ Table = 'Binary'; Name = 'WixUI_Bmp_Banner'; Path = 'installer\Assets\WixUIBanner.png' },
        [pscustomobject]@{ Table = 'Binary'; Name = 'WixUI_Bmp_Dialog'; Path = 'installer\Assets\WixUIDialog.png' },
        [pscustomobject]@{ Table = 'Icon'; Name = 'PeerOnQIcon'; Path = 'src\PeerOnQ.App\Assets\PeerOnQ.ico' }
    )
    foreach ($brandStream in $brandStreams) {
        $assetPath = Join-Path $repoRoot $brandStream.Path
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "Brand source asset is missing: $assetPath"
        }
        $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $assetPath).Hash
        $embeddedHash = Get-EmbeddedStreamHash $database $brandStream.Table $brandStream.Name
        if ($embeddedHash -ne $sourceHash) {
            throw "MSI brand stream $($brandStream.Name) does not match $assetPath."
        }
    }
    $licensePath = Join-Path $repoRoot 'installer\license.rtf'
    $licenseText = Get-Content -Raw -LiteralPath $licensePath
    $embeddedLicense = @(Read-Rows $database `
        "SELECT ``Text`` FROM ``Control`` WHERE ``Dialog_``='LicenseAgreementDlg' AND ``Control``='LicenseText'")
    if ($embeddedLicense.Count -ne 1 -or $embeddedLicense[0][0] -ne $licenseText) {
        throw 'The MSI license dialog does not contain the repository installer license text.'
    }
    foreach ($requiredText in @(
        'PeerOnQ - MIT License',
        'Permission is hereby granted, free of charge',
        'No license key, product activation, paid subscription, or online entitlement check is required')) {
        if ($licenseText.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw "The installer license is missing required open-source disclosure: $requiredText"
        }
    }
    if ($properties.ContainsKey('ARPSYSTEMCOMPONENT') -and $properties['ARPSYSTEMCOMPONENT']) {
        throw 'MSI must remain visible in Windows Installed apps and Programs and Features.'
    }
    if ($properties.ContainsKey('ARPNOREMOVE') -and $properties['ARPNOREMOVE']) {
        throw 'MSI must expose its standard Windows Installer uninstall action.'
    }

    $executeActions = @(Read-Rows $database 'SELECT `Action` FROM `InstallExecuteSequence`' |
        ForEach-Object { $_[0] })
    if ($executeActions -notcontains 'RegisterProduct' -or $executeActions -notcontains 'PublishProduct') {
        throw 'MSI does not register and publish PeerOnQ as an installed Windows application.'
    }

    $summaryTemplate = $database.SummaryInfo.Template
    $expectedTemplate = if ($Architecture -eq 'arm64') { 'Arm64' } else { 'x64' }
    if (-not $summaryTemplate.StartsWith($expectedTemplate, [StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI template '$summaryTemplate' does not match $Architecture."
    }

    $installDirectory = @(Read-Rows $database `
        "SELECT ``Directory_Parent``,``DefaultDir`` FROM ``Directory`` WHERE ``Directory``='INSTALLFOLDER'")
    if ($installDirectory.Count -ne 1 -or $installDirectory[0][0] -ne 'ProgramFiles6432Folder' -or
        $installDirectory[0][1] -ne 'PeerOnQ') {
        throw 'PeerOnQ must default to ProgramFiles6432Folder\PeerOnQ.'
    }
    $architectureProgramFiles = @(Read-Rows $database `
        "SELECT ``Directory_Parent``,``DefaultDir`` FROM ``Directory`` WHERE ``Directory``='ProgramFiles6432Folder'")
    if ($architectureProgramFiles.Count -ne 1 -or $architectureProgramFiles[0][0] -ne 'ProgramFiles64Folder' -or
        $architectureProgramFiles[0][1] -ne '.') {
        throw 'x64/ARM64 ProgramFiles6432Folder must resolve to ProgramFiles64Folder.'
    }

    $features = @{}
    $featureDirectories = @{}
    foreach ($row in Read-Rows $database 'SELECT `Feature`,`Level`,`Directory_` FROM `Feature`') {
        $features[$row[0]] = [int]$row[1]
        $featureDirectories[$row[0]] = $row[2]
    }
    if ($features['MainFeature'] -ne 1 -or $features['DesktopShortcutFeature'] -ne 1 -or $features['StartupFeature'] -ne 2) {
        throw 'Core/optional feature levels do not default desktop on and startup off.'
    }
    if ($featureDirectories['MainFeature'] -ne 'INSTALLFOLDER') {
        throw 'MainFeature must keep INSTALLFOLDER configurable so the Browse button is enabled.'
    }

    $desktopShortcut = @(Read-Rows $database `
        "SELECT ``Directory_``,``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopPeerOnQ'")
    if ($desktopShortcut.Count -ne 1 -or $desktopShortcut[0][0] -ne 'DesktopFolder' -or
        $desktopShortcut[0][1] -ne 'PeerOnQIcon') {
        throw 'Default desktop shortcut must target DesktopFolder and use the PeerOnQ icon.'
    }

    $protocolRows = @(Read-Rows $database `
        "SELECT ``Root``,``Key``,``Name``,``Value`` FROM ``Registry`` WHERE ``Component_``='PeerOnQUrlProtocol'")
    if ($protocolRows.Count -lt 4 -or @($protocolRows | Where-Object {
            $_[0] -ne '2' -or -not $_[1].StartsWith('Software\Classes\peeronq', [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0) {
        throw 'The peeronq URL protocol must be registered machine-wide under Software\Classes\peeronq.'
    }
    $urlProtocol = @($protocolRows | Where-Object { $_[1] -eq 'Software\Classes\peeronq' -and $_[2] -eq 'URL Protocol' })
    $openCommand = @($protocolRows | Where-Object { $_[1] -eq 'Software\Classes\peeronq\shell\open\command' })
    if ($urlProtocol.Count -ne 1 -or $openCommand.Count -ne 1 -or
        $openCommand[0][3].IndexOf('[INSTALLFOLDER]PeerOnQ.exe', [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $openCommand[0][3].IndexOf('%1', [StringComparison]::Ordinal) -lt 0) {
        throw 'The peeronq URL protocol does not pass the invitation URI to the installed executable.'
    }

    $tables = @(Read-Rows $database 'SELECT `Name` FROM `_Tables`' | ForEach-Object { $_[0] })
    if ($tables -contains 'ServiceInstall' -or $tables -contains 'ServiceControl') {
        throw 'PeerOnQ MSI must not install or control a Windows service.'
    }
    if ($tables -contains 'CustomAction') { throw 'Unexpected installer custom actions are not allowed.' }

    $startupFeature = @(Read-Rows $database `
        "SELECT ``Feature_`` FROM ``FeatureComponents`` WHERE ``Component_``='StartupShortcut'")
    if ($startupFeature.Count -ne 1 -or $startupFeature[0] -ne 'StartupFeature') {
        throw 'Startup persistence is not isolated to its visible optional feature.'
    }

    $fileRows = @(Read-Rows $database 'SELECT `FileName`,`Language` FROM `File`')
    $invalidLanguageRows = @($fileRows | Where-Object {
        $_[1] -in @('1152', '1153', '1169') -or ($_[1] -and $_[1].Length -gt 255)
    })
    if ($invalidLanguageRows) {
        throw 'MSI contains Windows App SDK language metadata that is incompatible with Windows Installer ICE03.'
    }
    $files = @($fileRows | ForEach-Object { $_[0] })
    if (-not ($files | Where-Object { $_ -match 'PeerOnQ\.exe$' })) { throw 'PeerOnQ.exe is absent from MSI.' }
    foreach ($noticeFile in @('LICENSE.txt', 'THIRD_PARTY_NOTICES.md')) {
        if (-not ($files | Where-Object { $_ -match [regex]::Escape($noticeFile) + '$' })) {
            throw "MSI is missing required open-source notice file: $noticeFile"
        }
    }
} finally { $database.Dispose() }

if (-not $SkipAdministrativeExtraction) {
    $extractionRoot = Join-Path $env:TEMP ("peeronq-msi-extract-" + [Guid]::NewGuid().ToString('N'))
    $extractionRoot = [IO.Path]::GetFullPath($extractionRoot)
    $tempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $extractionRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Administrative extraction path escaped the Windows temporary directory.'
    }
    [IO.Directory]::CreateDirectory($extractionRoot) | Out-Null
    try {
        $logPath = Join-Path $extractionRoot 'administrative-install.log'
        $quotedMsi = '"' + $msi + '"'
        $targetDirectoryArgument = 'TARGETDIR="' + $extractionRoot + '"'
        $quotedLogPath = '"' + $logPath + '"'
        $process = Start-Process -FilePath (Join-Path ([Environment]::GetFolderPath('System')) 'msiexec.exe') `
            -ArgumentList @('/a', $quotedMsi, '/qn', $targetDirectoryArgument, '/l*v', $quotedLogPath) -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "MSI administrative extraction failed with $($process.ExitCode)." }
        $extractedExecutable = Get-ChildItem -LiteralPath $extractionRoot -Recurse -Filter PeerOnQ.exe -File |
            Select-Object -First 1
        if ($null -eq $extractedExecutable) {
            throw 'Extracted MSI contains no PeerOnQ.exe.'
        }
        foreach ($runtimeFile in $requiredSelfContainedRuntimeFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $extractedExecutable.DirectoryName $runtimeFile) -PathType Leaf)) {
                throw "Extracted MSI is missing self-contained Windows App Runtime file: $runtimeFile"
            }
        }
        if ($expectedPublish) {
            $expectedManifest = Get-DirectoryHashManifest $expectedPublish
            $packagedManifest = Get-DirectoryHashManifest $extractedExecutable.DirectoryName
            $pathDifference = @(Compare-Object @($expectedManifest.Keys) @($packagedManifest.Keys))
            if ($pathDifference) {
                $differenceSummary = $pathDifference | Select-Object -First 20 | Out-String
                throw "MSI application payload paths do not match the publish directory ($($pathDifference.Count) differences; first 20 shown): $differenceSummary"
            }
            foreach ($relativePath in $expectedManifest.Keys) {
                if ($packagedManifest[$relativePath] -ne $expectedManifest[$relativePath]) {
                    throw "MSI payload is stale or altered: $relativePath"
                }
            }
        }
    } finally {
        if (Test-Path -LiteralPath $extractionRoot) { Remove-Item -LiteralPath $extractionRoot -Recurse -Force }
    }
}

Write-Host "Installer validation passed: $Architecture $msi"
