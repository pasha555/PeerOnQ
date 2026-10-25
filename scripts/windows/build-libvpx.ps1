<#
.SYNOPSIS
Builds vpxmd.dll (libvpx, BSD-3-Clause) for win-x64 or win-arm64 from the source pinned in
eng/libvpx/libvpx.json.

.DESCRIPTION
Every download (libvpx release archive, GNU make, NASM) is verified against its pinned SHA-256
before use; the libvpx archive must also be the git archive of the pinned commit (its pax header)
and carry the pinned license files. libvpx's own configure script generates the Visual Studio
project (MSVC, dynamic CRT /MD) for VP8/VP9 encode and decode only (no examples, tools, docs or
tests); x64 assembles its SIMD code with NASM, arm64 uses Neon intrinsics. MSBuild compiles the
static library vpxmd.lib with /Brepro, then link.exe produces vpxmd.dll with /Brepro, exporting
exactly libvpx's public API from the .def file that libvpx generates from vpx/exports_* and
vp8|vp9/exports_*. Outputs land in artifacts/native/libvpx/<arch>/ (ignored): the DLL and PDB,
the .def, libvpx license files, exports.txt, build.log, provenance.json and SHA256SUMS.txt.

Requirements: Visual Studio 2022 or later (Build Tools suffice) with MSBuild and the C++ toolset
for the target (component Microsoft.VisualStudio.Component.VC.Tools.x86.x64 or ...VC.Tools.ARM64),
Git for Windows (its bash, perl and coreutils run libvpx's configure), and Windows tar.exe.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [string]$OutputRoot,
    [string]$WorkRoot,
    [string]$DownloadCache,
    [string]$BashPath,
    [ValidateRange(1, 64)][int]$MaxCpuCount = [Math]::Min([Environment]::ProcessorCount, 16)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pinPath = Join-Path $repoRoot 'eng/libvpx/libvpx.json'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts/native/libvpx' }
if (-not $WorkRoot) { $WorkRoot = Join-Path $repoRoot 'artifacts/native/libvpx/.work' }
if (-not $DownloadCache) { $DownloadCache = Join-Path $repoRoot 'artifacts/native/libvpx/.downloads' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$DownloadCache = [IO.Path]::GetFullPath($DownloadCache)
$outputDirectory = Join-Path $OutputRoot $Architecture
$work = Join-Path $WorkRoot $Architecture
$utf8 = New-Object Text.UTF8Encoding($false)
$script:LogWriter = $null

function Write-Log([string]$Message) {
    if ($null -ne $script:LogWriter) { $script:LogWriter.WriteLine($Message) }
    Write-Host $Message
}

function Write-Step([string]$Message) {
    Write-Log ''
    Write-Log "==> $Message"
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Sha256([string]$Path, [string]$Expected, [string]$What) {
    $actual = Get-Sha256 $Path
    if ($actual -cne $Expected.ToLowerInvariant()) {
        throw "SHA-256 mismatch for ${What}: expected $Expected, got $actual."
    }
    Write-Log "Verified SHA-256 $actual  $What"
}

function Assert-Pattern([string]$Value, [string]$Pattern, [string]$What) {
    if ($Value -cnotmatch $Pattern) { throw "Pinned $What '$Value' is malformed." }
}

function Reset-Directory([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    [void][IO.Directory]::CreateDirectory($Path)
}

# Native stderr arrives as error records (an empty line renders as its exception type name).
function ConvertTo-OutputLine($Item) {
    if ($Item -is [Management.Automation.ErrorRecord]) { return [string]$Item.Exception.Message }
    return [string]$Item
}

# Runs a native tool, streams stdout and stderr into the console and build.log, and throws on a
# non-zero exit code. Environment entries apply only for the duration of the call.
function Invoke-Logged {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [hashtable]$Environment
    )
    Write-Log ("> " + $FilePath + ' ' + ($ArgumentList -join ' '))
    $saved = @{}
    if ($null -ne $Environment) {
        foreach ($name in $Environment.Keys) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, [string]$Environment[$name], 'Process')
        }
    }
    $previousPreference = $ErrorActionPreference
    $pushed = $false
    try {
        if ($WorkingDirectory) { Push-Location -LiteralPath $WorkingDirectory; $pushed = $true }
        # Windows PowerShell turns native stderr lines into error records; never stop on them.
        $ErrorActionPreference = 'Continue'
        & $FilePath @ArgumentList 2>&1 | ForEach-Object { Write-Log (ConvertTo-OutputLine $_) }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
        if ($pushed) { Pop-Location }
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
    if ($exitCode -ne 0) {
        throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $exitCode; see $(Join-Path $outputDirectory 'build.log')."
    }
}

# Returns the merged output lines of a tool whose exit code is informational only (version banners).
function Get-ToolOutput([string]$FilePath, [string[]]$ArgumentList = @(), [hashtable]$Environment) {
    $saved = @{}
    if ($null -ne $Environment) {
        foreach ($name in $Environment.Keys) {
            $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, [string]$Environment[$name], 'Process')
        }
    }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        return @(& $FilePath @ArgumentList 2>&1 | ForEach-Object { (ConvertTo-OutputLine $_).TrimEnd() } | Where-Object { $_ })
    } finally {
        $ErrorActionPreference = $previousPreference
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}

function Get-VerifiedDownload([string]$Url, [string]$Sha256, [string]$FileName) {
    Assert-Pattern $Url '^https://[^\s]+$' 'download URL'
    Assert-Pattern $Sha256 '^[0-9a-f]{64}$' "SHA-256 of $FileName"
    [void][IO.Directory]::CreateDirectory($DownloadCache)
    $target = Join-Path $DownloadCache $FileName
    if (Test-Path -LiteralPath $target) {
        if ((Get-Sha256 $target) -ceq $Sha256) {
            Write-Log "Verified SHA-256 $Sha256  $FileName (cached)"
            return $target
        }
        Write-Log "Discarding cached $FileName with an unexpected SHA-256."
        Remove-Item -LiteralPath $target -Force
    }
    $partial = "$target.partial"
    for ($attempt = 1; ; $attempt++) {
        try {
            Write-Log "Downloading $Url"
            Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing -MaximumRedirection 10
            break
        } catch {
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
            if ($attempt -ge 3) { throw "Download failed after $attempt attempts: $Url ($($_.Exception.Message))" }
            Write-Log "Download attempt $attempt failed: $($_.Exception.Message)"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
    try {
        Assert-Sha256 $partial $Sha256 $FileName
    } catch {
        Remove-Item -LiteralPath $partial -Force
        throw
    }
    Move-Item -LiteralPath $partial -Destination $target -Force
    return $target
}

function Read-Exactly([IO.Stream]$Stream, [int]$Count) {
    $buffer = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { throw 'Unexpected end of the libvpx archive.' }
        $offset += $read
    }
    return , $buffer
}

# `git archive` (which GitHub uses for tag archives) records the commit in a leading pax global
# header ("52 comment=<sha>"). A mismatch means the archive is not the pinned commit's tree.
function Get-ArchiveCommit([string]$Path) {
    $file = [IO.File]::OpenRead($Path)
    try {
        $gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionMode]::Decompress)
        try {
            $header = Read-Exactly $gzip 512
            if ([char]$header[156] -cne 'g') { throw 'The libvpx archive has no pax global header; it is not a git archive.' }
            $sizeText = [Text.Encoding]::ASCII.GetString($header, 124, 12).Trim([char]0, ' ')
            $size = [Convert]::ToInt32($sizeText, 8)
            if ($size -le 0 -or $size -gt 4096) { throw 'The libvpx archive pax header has an invalid size.' }
            $data = Read-Exactly $gzip ([int]([Math]::Ceiling($size / 512.0) * 512))
            foreach ($record in ([Text.Encoding]::UTF8.GetString($data, 0, $size) -split "`n")) {
                if ($record -cmatch '^\d+ comment=([0-9a-f]{40})$') { return $Matches[1] }
            }
            throw 'The libvpx archive pax header records no commit.'
        } finally {
            $gzip.Dispose()
        }
    } finally {
        $file.Dispose()
    }
}

function ConvertTo-BashLiteral([string]$Value) {
    if ($Value.Contains("'")) { throw "Paths and arguments passed to bash must not contain single quotes: $Value" }
    return "'$Value'"
}

function Resolve-GitBash {
    $candidates = New-Object Collections.Generic.List[string]
    if ($BashPath) {
        $candidates.Add($BashPath)
    } else {
        $git = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($git) {
            $directory = Split-Path -Parent $git.Source
            for ($level = 0; $level -lt 3 -and $directory; $level++) {
                $candidates.Add((Join-Path $directory 'usr\bin\bash.exe'))
                $directory = Split-Path -Parent $directory
            }
        }
        if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'Git\usr\bin\bash.exe')) }
    }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $system = Get-ToolOutput $candidate @('--noprofile', '--norc', '-c', 'PATH=/usr/bin:$PATH; uname -o')
        if (@($system) -contains 'Msys' -or @($system) -contains 'Cygwin') { return [IO.Path]::GetFullPath($candidate) }
    }
    throw 'Git for Windows bash (usr\bin\bash.exe, an MSYS2 runtime) was not found. Install Git for Windows or pass -BashPath.'
}

function Resolve-VisualStudio {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw "vswhere.exe was not found at $vswhere; install Visual Studio 2022 or later." }
    $component = if ($Architecture -eq 'x64') {
        'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
    } else {
        'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
    }
    $json = (& $vswhere -latest -products '*' -requires $component 'Microsoft.Component.MSBuild' -format json -utf8) | Out-String
    if ($LASTEXITCODE -ne 0) { throw "vswhere failed with exit code $LASTEXITCODE." }
    $instances = @($json | ConvertFrom-Json)
    if ($instances.Count -eq 0 -or $null -eq $instances[0]) {
        throw "No Visual Studio instance with MSBuild and the $Architecture C++ toolset ($component) was found."
    }
    $instance = $instances[0]
    $version = [version]$instance.installationVersion
    $vsTarget = switch ($version.Major) {
        17 { 'vs17' }
        18 { 'vs18' }
        default { throw "Visual Studio $($instance.installationVersion) is not a libvpx 1.17 target (vs17 or vs18)." }
    }
    $hostArchitecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $msbuildBin = Join-Path $instance.installationPath 'MSBuild\Current\Bin'
    $msbuild = Join-Path $msbuildBin $(if ($hostArchitecture -eq 'arm64') { 'arm64\MSBuild.exe' } else { 'amd64\MSBuild.exe' })
    if (-not (Test-Path -LiteralPath $msbuild)) { $msbuild = Join-Path $msbuildBin 'MSBuild.exe' }
    $vcvarsall = Join-Path $instance.installationPath 'VC\Auxiliary\Build\vcvarsall.bat'
    foreach ($required in $msbuild, $vcvarsall) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Visual Studio file not found: $required" }
    }
    $hostTarget = switch ("$hostArchitecture>$Architecture") {
        'x64>x64' { 'amd64' }
        'x64>arm64' { 'amd64_arm64' }
        'arm64>arm64' { 'arm64' }
        'arm64>x64' { 'arm64_amd64' }
    }
    [pscustomobject]@{
        Vswhere = $vswhere
        InstallationPath = $instance.installationPath
        InstallationVersion = [string]$instance.installationVersion
        DisplayName = [string]$instance.displayName
        VsTarget = $vsTarget
        MSBuild = $msbuild
        VcVarsAll = $vcvarsall
        HostTarget = $hostTarget
        HostArchitecture = $hostArchitecture
    }
}

# Captures the environment vcvarsall.bat establishes for the target, for link.exe, rc.exe and dumpbin.
function Get-VcEnvironment($VisualStudio) {
    $commandFile = Join-Path $work 'vcvars-environment.cmd'
    $lines = @(
        '@echo off',
        "set `"PATH=$(Split-Path -Parent $VisualStudio.Vswhere);%PATH%`"",
        "call `"$($VisualStudio.VcVarsAll)`" $($VisualStudio.HostTarget) >nul",
        'if errorlevel 1 exit /b 1',
        'set'
    )
    [IO.File]::WriteAllLines($commandFile, $lines, (New-Object Text.ASCIIEncoding))
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& cmd.exe /d /c $commandFile 2>$null)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) { throw "vcvarsall.bat $($VisualStudio.HostTarget) failed with exit code $exitCode." }
    $environment = @{}
    foreach ($line in $output) {
        if ($line -match '^([^=]+)=(.*)$') { $environment[$Matches[1]] = $Matches[2] }
    }
    foreach ($name in 'PATH', 'LIB', 'INCLUDE', 'VCToolsVersion', 'VCToolsInstallDir') {
        if (-not $environment.ContainsKey($name)) { throw "vcvarsall.bat did not define $name." }
    }
    return $environment
}

function Find-InPath([hashtable]$Environment, [string]$FileName) {
    foreach ($directory in ($Environment['PATH'] -split ';')) {
        if (-not $directory) { continue }
        $candidate = Join-Path $directory $FileName
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "$FileName was not found in the Visual Studio $Architecture environment."
}

try {
    if (-not (Test-Path -LiteralPath $pinPath)) { throw "Pin file not found: $pinPath" }
    $pin = Get-Content -LiteralPath $pinPath -Raw | ConvertFrom-Json
    if ($pin.schemaVersion -ne 1) { throw "Unsupported libvpx pin schema $($pin.schemaVersion)." }
    $source = $pin.source
    Assert-Pattern $source.tag '^v\d+\.\d+\.\d+$' 'libvpx tag'
    Assert-Pattern $source.version '^\d+\.\d+\.\d+$' 'libvpx version'
    Assert-Pattern $source.commit '^[0-9a-f]{40}$' 'libvpx commit'
    Assert-Pattern $source.archiveRoot '^[A-Za-z0-9._-]+$' 'libvpx archive root'
    if ($source.tag -cne "v$($source.version)") { throw 'The pinned libvpx tag and version disagree.' }

    Reset-Directory $outputDirectory
    $logPath = Join-Path $outputDirectory 'build.log'
    $script:LogWriter = New-Object IO.StreamWriter($logPath, $false, $utf8)
    $script:LogWriter.AutoFlush = $true
    Write-Log "PeerOnQ libvpx build: $($source.tag) ($($source.commit)) for win-$Architecture"
    Write-Log "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition)); host $env:PROCESSOR_ARCHITECTURE"

    Write-Step 'Locating tools'
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar)) { throw "Windows tar.exe was not found at $tar." }
    $bash = Resolve-GitBash
    $vs = Resolve-VisualStudio
    Write-Log "bash: $bash"
    Write-Log "Visual Studio: $($vs.DisplayName) $($vs.InstallationVersion) at $($vs.InstallationPath) (libvpx target $($vs.VsTarget))"
    Write-Log "MSBuild: $($vs.MSBuild)"

    Write-Step 'Downloading and verifying pinned inputs'
    $archive = Get-VerifiedDownload $source.archiveUrl $source.archiveSha256 "libvpx-$($source.tag).tar.gz"
    $archiveCommit = Get-ArchiveCommit $archive
    if ($archiveCommit -cne $source.commit) {
        throw "The libvpx archive is commit $archiveCommit, not the pinned $($source.commit)."
    }
    Write-Log "Verified archive commit $archiveCommit"
    $makeTool = $pin.tools.make
    $makePackage = Get-VerifiedDownload $makeTool.url $makeTool.sha256 ([IO.Path]::GetFileName(([uri]$makeTool.url).AbsolutePath))
    $useNasm = @($pin.tools.nasm.architectures) -contains $Architecture
    if ($useNasm) {
        $nasmTool = $pin.tools.nasm
        $nasmArchive = Get-VerifiedDownload $nasmTool.url $nasmTool.sha256 ([IO.Path]::GetFileName(([uri]$nasmTool.url).AbsolutePath))
    }

    Write-Step 'Extracting into a clean work directory'
    Reset-Directory $work
    $sourceParent = Join-Path $work 'src'
    $buildDirectory = Join-Path $work 'build'
    $linkDirectory = Join-Path $work 'link'
    $makeRoot = Join-Path $work 'tools\make'
    foreach ($directory in $sourceParent, $buildDirectory, $linkDirectory, $makeRoot) { [void][IO.Directory]::CreateDirectory($directory) }
    Invoke-Logged $tar @('-xzf', $archive, '-C', $sourceParent)
    $sourceDirectory = Join-Path $sourceParent $source.archiveRoot
    if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'configure') -PathType Leaf)) {
        throw "The libvpx archive does not contain $($source.archiveRoot)/configure."
    }
    foreach ($license in $source.licenseFiles) {
        Assert-Sha256 (Join-Path $sourceDirectory $license.path) $license.sha256 "libvpx $($license.path)"
    }
    Invoke-Logged $tar @('-xf', $makePackage, '-C', $makeRoot)
    $makeExecutable = Join-Path $makeRoot $makeTool.executable
    if (-not (Test-Path -LiteralPath $makeExecutable -PathType Leaf)) { throw "The make package does not contain $($makeTool.executable)." }
    $toolPaths = @((Split-Path -Parent $makeExecutable))
    $nasmExecutable = $null
    if ($useNasm) {
        $nasmRoot = Join-Path $work 'tools\nasm'
        Expand-Archive -LiteralPath $nasmArchive -DestinationPath $nasmRoot
        $nasmExecutable = Join-Path $nasmRoot $nasmTool.executable
        if (-not (Test-Path -LiteralPath $nasmExecutable -PathType Leaf)) { throw "The NASM archive does not contain $($nasmTool.executable)." }
        $toolPaths += (Split-Path -Parent $nasmExecutable)
    }

    Write-Step 'Configuring libvpx and generating the Visual Studio project'
    $target = $pin.build.targets.$Architecture.Replace('{vs}', $vs.VsTarget.Substring(2))
    $configureArguments = @("--target=$target") + @($pin.build.configureArguments.common) + @($pin.build.configureArguments.$Architecture)
    foreach ($argument in $configureArguments) { Assert-Pattern $argument '^--[a-z0-9-]+(=[A-Za-z0-9_.-]+)?$' 'configure argument' }
    $bashToolPath = ($toolPaths | ForEach-Object { '$(cygpath -u ' + (ConvertTo-BashLiteral $_) + ')' }) -join ':'
    $configureScript = @(
        'set -euo pipefail',
        'export PATH="/usr/bin:$PATH"',
        "export PATH=`"${bashToolPath}:`$PATH`"",
        'export LC_ALL=C',
        'unset MAKEFLAGS MFLAGS CFLAGS CXXFLAGS LDFLAGS',
        ('cd "$(cygpath -u ' + (ConvertTo-BashLiteral $buildDirectory) + ')"'),
        'bash --version | head -n 1',
        'make --version | head -n 1',
        'perl -e ''print "perl $^V\n"''',
        ("../src/$($source.archiveRoot)/configure " + ($configureArguments -join ' ')),
        'make vpx.vcxproj'
    )
    $configureScriptPath = Join-Path $work 'configure-libvpx.sh'
    [IO.File]::WriteAllText($configureScriptPath, (($configureScript -join "`n") + "`n"), $utf8)
    Invoke-Logged $bash @('--noprofile', '--norc', $configureScriptPath.Replace('\', '/')) -WorkingDirectory $work
    foreach ($generated in 'vpx.vcxproj', 'vpx.def', 'vpx_config.h', 'vpx_config.c', 'vpx_version.h') {
        if (-not (Test-Path -LiteralPath (Join-Path $buildDirectory $generated))) { throw "libvpx configure did not produce $generated." }
    }
    $vpxConfigC = [IO.File]::ReadAllText((Join-Path $buildDirectory 'vpx_config.c'))
    $buildConfig = if ($vpxConfigC -match 'cfg = "([^"]*)"') { $Matches[1] } else { throw 'vpx_config.c has no configuration string.' }
    $enabledFeatures = @(Get-Content -LiteralPath (Join-Path $buildDirectory 'vpx_config.h') |
        Where-Object { $_ -match '^#define (HAVE|CONFIG)_[A-Z0-9_]+ 1$' } |
        ForEach-Object { ($_ -split ' ')[1] } | Sort-Object)

    Write-Step 'Compiling vpxmd.lib with MSBuild'
    $platform = $pin.build.msbuildPlatforms.$Architecture
    $compilerOptions = '/Brepro'
    $msbuildArguments = @(
        'vpx.vcxproj', '-nologo', "-m:$MaxCpuCount", '-nodeReuse:false', '-t:Build', '-v:minimal', '-clp:Summary',
        '-p:Configuration=Release', "-p:Platform=$platform",
        "-p:SolutionDir=$($buildDirectory.Replace('\', '/'))/",
        "-p:PreferredToolArchitecture=$($vs.HostArchitecture)",
        # The work tree lives inside this repository; keep the .NET Directory.* files out of libvpx.
        '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ImportDirectoryPackagesProps=false'
    )
    $msbuildEnvironment = @{
        CL = $compilerOptions
        PATH = (($toolPaths + @([Environment]::GetEnvironmentVariable('PATH', 'Process'))) -join ';')
    }
    Invoke-Logged $vs.MSBuild $msbuildArguments -WorkingDirectory $buildDirectory -Environment $msbuildEnvironment
    $staticLibrary = Join-Path $buildDirectory "$platform\Release\vpxmd.lib"
    if (-not (Test-Path -LiteralPath $staticLibrary -PathType Leaf)) { throw "MSBuild did not produce $staticLibrary." }

    Write-Step 'Linking vpxmd.dll'
    $vc = Get-VcEnvironment $vs
    $link = Find-InPath $vc 'link.exe'
    $rc = Find-InPath $vc 'rc.exe'
    $dumpbin = Find-InPath $vc 'dumpbin.exe'
    $cl = Find-InPath $vc 'cl.exe'
    $definition = @(Get-Content -LiteralPath (Join-Path $buildDirectory 'vpx.def'))
    if ($definition.Count -lt 3 -or $definition[0] -cne 'LIBRARY vpx' -or $definition[1] -cne 'EXPORTS') {
        throw 'libvpx generated an unexpected vpx.def.'
    }
    # Same exports as libvpx's own vpx.def; only the module name matches the shipped file name.
    $definition[0] = 'LIBRARY vpxmd'
    $expectedExports = @($definition | Select-Object -Skip 2 | ForEach-Object { ($_.Trim() -split '\s+')[0] } | Where-Object { $_ } | Sort-Object)
    [IO.File]::WriteAllLines((Join-Path $linkDirectory 'vpxmd.def'), $definition, (New-Object Text.ASCIIEncoding))
    $versionParts = $source.version.Split('.')
    $resource = @(
        '#include <winver.h>',
        'VS_VERSION_INFO VERSIONINFO',
        " FILEVERSION $($versionParts[0]),$($versionParts[1]),$($versionParts[2]),0",
        " PRODUCTVERSION $($versionParts[0]),$($versionParts[1]),$($versionParts[2]),0",
        ' FILEFLAGSMASK VS_FFI_FILEFLAGSMASK',
        ' FILEFLAGS 0x0L',
        ' FILEOS VOS_NT_WINDOWS32',
        ' FILETYPE VFT_DLL',
        ' FILESUBTYPE VFT2_UNKNOWN',
        'BEGIN',
        '  BLOCK "StringFileInfo"',
        '  BEGIN',
        '    BLOCK "040904b0"',
        '    BEGIN',
        "      VALUE `"Comments`", `"Built by PeerOnQ scripts/windows/build-libvpx.ps1 from libvpx $($source.tag) (commit $($source.commit)).`"",
        '      VALUE "FileDescription", "libvpx VP8/VP9 codec library"',
        "      VALUE `"FileVersion`", `"$($source.version)`"",
        '      VALUE "InternalName", "vpxmd"',
        '      VALUE "LegalCopyright", "Copyright (c) 2010, The WebM Project authors. BSD-3-Clause."',
        '      VALUE "OriginalFilename", "vpxmd.dll"',
        '      VALUE "ProductName", "libvpx"',
        "      VALUE `"ProductVersion`", `"$($source.version)`"",
        '    END',
        '  END',
        '  BLOCK "VarFileInfo"',
        '  BEGIN',
        '    VALUE "Translation", 0x409, 1200',
        '  END',
        'END'
    )
    [IO.File]::WriteAllLines((Join-Path $linkDirectory 'vpxmd.rc'), $resource, (New-Object Text.ASCIIEncoding))
    Invoke-Logged $rc @('/nologo', '/fo', 'vpxmd.res', 'vpxmd.rc') -WorkingDirectory $linkDirectory -Environment $vc
    $machine = if ($Architecture -eq 'x64') { 'X64' } else { 'ARM64' }
    $linkArguments = @(
        '/NOLOGO', '/DLL', "/MACHINE:$machine", '/OUT:vpxmd.dll', '/DEF:vpxmd.def',
        '/LTCG', '/OPT:REF', '/OPT:ICF', '/INCREMENTAL:NO', '/Brepro',
        '/DYNAMICBASE', '/NXCOMPAT', '/HIGHENTROPYVA',
        '/DEBUG:FULL', '/PDB:vpxmd.pdb', '/PDBALTPATH:%_PDB%'
    )
    $linkInputs = @($staticLibrary, 'vpxmd.res', 'kernel32.lib')
    Invoke-Logged $link ($linkArguments + $linkInputs) -WorkingDirectory $linkDirectory -Environment $vc
    $dll = Join-Path $linkDirectory 'vpxmd.dll'
    $pdb = Join-Path $linkDirectory 'vpxmd.pdb'

    Write-Step 'Verifying the DLL'
    $exportLines = Get-ToolOutput $dumpbin @('/nologo', '/exports', $dll) $vc
    $actualExports = @($exportLines | Where-Object { $_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]{8}\s+(\S+)' } |
        ForEach-Object { if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]{8}\s+(\S+)') { $Matches[1] } } | Sort-Object)
    $exportDifference = @(Compare-Object -ReferenceObject $expectedExports -DifferenceObject $actualExports -CaseSensitive)
    if ($exportDifference.Count -ne 0 -or $actualExports.Count -eq 0) {
        throw "vpxmd.dll exports differ from vpx.def: $(($exportDifference | ForEach-Object { "$($_.SideIndicator)$($_.InputObject)" }) -join ', ')"
    }
    Write-Log "Exports: $($actualExports.Count) symbols, identical to libvpx's vpx.def"
    $headerLines = Get-ToolOutput $dumpbin @('/nologo', '/headers', $dll) $vc
    $expectedMachine = if ($Architecture -eq 'x64') { '8664 machine (x64)' } else { 'AA64 machine (ARM64)' }
    if (-not ($headerLines | Where-Object { $_.Trim() -ceq $expectedMachine })) { throw "vpxmd.dll is not a $expectedMachine image." }
    $dependents = @(Get-ToolOutput $dumpbin @('/nologo', '/dependents', $dll) $vc |
        Where-Object { $_ -match '^\s+\S+\.dll$' } | ForEach-Object { $_.Trim() })
    Write-Log "Machine: $expectedMachine; imports: $($dependents -join ', ')"

    Write-Step 'Writing outputs and provenance'
    Copy-Item -LiteralPath $dll, $pdb, (Join-Path $linkDirectory 'vpxmd.def') -Destination $outputDirectory
    foreach ($license in $source.licenseFiles) {
        Copy-Item -LiteralPath (Join-Path $sourceDirectory $license.path) -Destination (Join-Path $outputDirectory "libvpx-$($license.path).txt")
    }
    [IO.File]::WriteAllLines((Join-Path $outputDirectory 'exports.txt'), [string[]]$actualExports, $utf8)
    $clBanner = @(Get-ToolOutput $cl @() $vc | Where-Object { $_ -match 'Compiler Version' }) | Select-Object -First 1
    $linkBanner = @(Get-ToolOutput $link @('/?') $vc | Where-Object { $_ -match 'Linker Version' }) | Select-Object -First 1
    $nasmVersion = if ($useNasm) { @(Get-ToolOutput $nasmExecutable @('-v')) | Select-Object -First 1 } else { $null }
    $msbuildVersion = @(Get-ToolOutput $vs.MSBuild @('-nologo', '-version')) | Select-Object -Last 1
    $repositoryCommit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } elseif (Get-Command git.exe -ErrorAction SilentlyContinue) {
        @(Get-ToolOutput 'git.exe' @('-C', $repoRoot, 'rev-parse', 'HEAD')) | Select-Object -First 1
    } else { $null }
    $provenance = [ordered]@{
        schemaVersion = 1
        artifact = [ordered]@{
            fileName = 'vpxmd.dll'
            runtimeIdentifier = "win-$Architecture"
            sha256 = Get-Sha256 $dll
            size = (Get-Item -LiteralPath $dll).Length
            pdbSha256 = Get-Sha256 $pdb
            machine = $expectedMachine
            exports = $actualExports
            imports = $dependents
        }
        source = [ordered]@{
            name = 'libvpx'
            license = $pin.library.license
            tag = $source.tag
            commit = $source.commit
            archiveCommitFromPaxHeader = $archiveCommit
            archiveUrl = $source.archiveUrl
            archiveSha256 = $source.archiveSha256
            canonicalRepositoryUrl = $source.canonicalRepositoryUrl
            licenseFiles = @($source.licenseFiles | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256 } })
        }
        build = [ordered]@{
            target = $target
            configureArguments = $configureArguments
            libvpxBuildConfig = $buildConfig
            enabledFeatures = $enabledFeatures
            msbuildArguments = $msbuildArguments
            compilerEnvironment = [ordered]@{ CL = $compilerOptions }
            projectCompilerSettings = 'libvpx gen_msvs_vcxproj.sh Release: /O2 /Ot /GL, /MD, C11, NDEBUG, warning level 3'
            linkArguments = $linkArguments + $linkInputs
            versionResource = "libvpx $($source.version)"
        }
        toolchain = [ordered]@{
            visualStudio = "$($vs.DisplayName) $($vs.InstallationVersion)"
            vcvarsall = $vs.HostTarget
            msvcToolsVersion = $vc['VCToolsVersion']
            compiler = $clBanner
            linker = $linkBanner
            windowsSdkVersion = if ($vc.ContainsKey('WindowsSDKVersion')) { $vc['WindowsSDKVersion'].TrimEnd('\') } else { $null }
            msbuild = $msbuildVersion
            nasm = if ($useNasm) { [ordered]@{ version = $nasmVersion; url = $nasmTool.url; sha256 = $nasmTool.sha256 } } else { $null }
            make = [ordered]@{ version = $makeTool.version; url = $makeTool.url; sha256 = $makeTool.sha256 }
            bash = $bash
        }
        invocation = [ordered]@{
            repositoryCommit = $repositoryCommit
            githubRun = if ($env:GITHUB_RUN_ID) { "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID" } else { $null }
            builtAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [Globalization.CultureInfo]::InvariantCulture)
        }
    }
    [IO.File]::WriteAllText((Join-Path $outputDirectory 'provenance.json'), ($provenance | ConvertTo-Json -Depth 8), $utf8)
    Write-Log "vpxmd.dll SHA-256 $($provenance.artifact.sha256)"
    Write-Log "Output: $outputDirectory"
    $script:LogWriter.Dispose()
    $script:LogWriter = $null
    $sums = Get-ChildItem -LiteralPath $outputDirectory -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object Name | ForEach-Object { "$(Get-Sha256 $_.FullName)  $($_.Name)" }
    [IO.File]::WriteAllLines((Join-Path $outputDirectory 'SHA256SUMS.txt'), [string[]]$sums, $utf8)
} finally {
    if ($null -ne $script:LogWriter) { $script:LogWriter.Dispose(); $script:LogWriter = $null }
}
