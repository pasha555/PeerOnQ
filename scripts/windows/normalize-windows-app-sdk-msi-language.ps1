<# Normalizes Windows App SDK language metadata that the stock MSI ICE03 locale table cannot represent. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath
)

$ErrorActionPreference = 'Stop'
$msi = (Resolve-Path -LiteralPath $MsiPath).Path
if ([IO.Path]::GetExtension($msi) -ne '.msi') { throw 'MsiPath must be an MSI package.' }

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

function Get-LongName([string]$MsiName) {
    if (-not $MsiName) { return $MsiName }
    return $MsiName.Split('|')[-1]
}

$expectedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    'Microsoft.ui.xaml.dll',
    'Microsoft.UI.Xaml.Phone.dll',
    'gd-gb\Microsoft.ui.xaml.dll.mui',
    'gd-gb\Microsoft.UI.Xaml.Phone.dll.mui',
    'mi-NZ\Microsoft.ui.xaml.dll.mui',
    'mi-NZ\Microsoft.UI.Xaml.Phone.dll.mui',
    'ug-CN\Microsoft.ui.xaml.dll.mui',
    'ug-CN\Microsoft.UI.Xaml.Phone.dll.mui'
) | ForEach-Object { [void]$expectedPaths.Add($_) }

$database = New-Object WixToolset.Dtf.WindowsInstaller.Database(
    $msi,
    ([WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly))
try {
    $directories = @{}
    foreach ($row in Read-Rows $database 'SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`') {
        $directories[$row[0]] = [pscustomobject]@{ Parent = $row[1]; Name = (Get-LongName $row[2]) }
    }
    $componentDirectories = @{}
    foreach ($row in Read-Rows $database 'SELECT `Component`,`Directory_` FROM `Component`') {
        $componentDirectories[$row[0]] = $row[1]
    }

    $matchingRows = @{}
    foreach ($row in Read-Rows $database 'SELECT `File`,`FileName`,`Language`,`Component_` FROM `File`') {
        $parts = New-Object System.Collections.Generic.List[string]
        $directoryId = $componentDirectories[$row[3]]
        while ($directoryId -and $directoryId -ne 'INSTALLFOLDER') {
            $directory = $directories[$directoryId]
            if ($null -eq $directory) { break }
            if ($directory.Name -and $directory.Name -ne '.') { $parts.Insert(0, $directory.Name) }
            $directoryId = $directory.Parent
        }
        $parts.Add((Get-LongName $row[1]))
        $relativePath = $parts -join '\'
        if ($expectedPaths.Contains($relativePath)) {
            $matchingRows[$relativePath] = [pscustomobject]@{ Id = $row[0]; Language = $row[2] }
        }
    }
} finally { $database.Dispose() }

$missing = @($expectedPaths | Where-Object { -not $matchingRows.ContainsKey($_) })
if ($missing) { throw "Expected Windows App SDK MSI files are missing: $($missing -join ', ')" }

$database = New-Object WixToolset.Dtf.WindowsInstaller.Database(
    $msi,
    ([WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::Transact))
try {
    foreach ($row in $matchingRows.Values) {
        if (-not $row.Language) { continue }
        $view = $database.OpenView("UPDATE ``File`` SET ``Language``=NULL WHERE ``File``='$($row.Id)'")
        try {
            $view.Execute()
        } finally { $view.Dispose() }
    }
    $database.Commit()
} finally { $database.Dispose() }

$database = New-Object WixToolset.Dtf.WindowsInstaller.Database(
    $msi,
    ([WixToolset.Dtf.WindowsInstaller.DatabaseOpenMode]::ReadOnly))
try {
    foreach ($row in Read-Rows $database 'SELECT `File`,`Language` FROM `File`') {
        if (($matchingRows.Values.Id -contains $row[0]) -and $row[1]) {
            throw "Windows App SDK MSI language metadata was not normalized: $($row[0])"
        }
    }
} finally { $database.Dispose() }

$wix = Get-ChildItem -LiteralPath (Join-Path $env:USERPROFILE '.nuget\packages\wixtoolset.sdk') `
    -Recurse -Filter wix.exe -File |
    Where-Object { $_.FullName -match '\\tools\\net472\\x64\\wix\.exe$' } |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($null -eq $wix) { throw 'WiX executable was not found for final ICE03 validation.' }

& $wix.FullName msi validate -ice ICE03 $msi
if ($LASTEXITCODE -ne 0) { throw "Final MSI ICE03 validation failed with exit code $LASTEXITCODE." }

Write-Host "Windows App SDK MSI language metadata normalized and ICE03 validation passed: $msi"
