#Requires -Version 5.1
[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$targets = Join-Path $repoRoot 'Directory.Build.targets'
$targetsHash = (Get-FileHash -LiteralPath $targets -Algorithm SHA256).Hash
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$work = Join-Path $temporaryRoot ('peeronq-maintained-copy-' + [Guid]::NewGuid().ToString('N'))
$epoch = [DateTime]::SpecifyKind([DateTime]'1980-01-01', [DateTimeKind]::Utc)
$locationChanged = $false

function Invoke-CopyProbe([string]$Case, [string]$Target, [bool]$Repair) {
    $log = Join-Path $work ($Case + '.log')
    & $DotNetPath msbuild (Join-Path $work 'CopyProbe.proj') -nologo -verbosity:quiet -nodeReuse:false `
        "-target:$Target" "-property:UsePeerOnQHashRepair=$Repair" '-property:NoBuild=true' *> $log
    if ($LASTEXITCODE -ne 0) {
        throw "Copy/hash regression failed during ${Case}: $((Get-Content -LiteralPath $log -Raw).Trim())"
    }
}

function Set-OldDestination([string]$Destination) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination))
    [IO.File]::Copy((Join-Path $work 'old.bin'), $Destination, $true)
    [IO.File]::SetLastWriteTimeUtc($Destination, $epoch)
}

function Assert-ProbeHash([string]$Path, [string]$Expected, [string]$Case) {
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "Copy/hash regression failed: $Case."
    }
}

function Assert-UnchangedLockedDestination([string]$Path, [string]$Target, [string]$Case) {
    [IO.File]::SetLastWriteTimeUtc($Path, $epoch)
    # A redundant overwrite fails under this read lock. Hash inspection remains allowed.
    $readLock = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Invoke-CopyProbe $Case $Target $true } finally { $readLock.Dispose() }
    Assert-ProbeHash $Path $currentHash $Case
    if ([IO.File]::GetLastWriteTimeUtc($Path) -ne $epoch) {
        throw "Copy/hash regression unexpectedly touched unchanged content: $Case."
    }
}

try {
    [void][IO.Directory]::CreateDirectory((Join-Path $work 'current'))
    # Tiny non-executable byte-copy test vectors, with equal lengths and NuGet's fixed timestamp.
    [IO.File]::WriteAllBytes((Join-Path $work 'old.bin'), [byte[]](0..31))
    $source = Join-Path $work 'current/SIPSorcery.dll'
    [IO.File]::WriteAllBytes($source, [byte[]](32..63))
    [IO.File]::SetLastWriteTimeUtc((Join-Path $work 'old.bin'), $epoch)
    [IO.File]::SetLastWriteTimeUtc($source, $epoch)
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $work 'old.bin') -Algorithm SHA256).Hash
    $currentHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $project = @'
<Project>
  <PropertyGroup>
    <WorkRoot>__WORK__</WorkRoot>
    <OutDir>$(WorkRoot)/build/</OutDir>
    <PublishDir>$(WorkRoot)/publish/</PublishDir>
    <BuildForPublish Condition="'$(NoBuild)' != 'true'">Build;</BuildForPublish>
  </PropertyGroup>
  <ItemGroup>
    <ReferenceCopyLocalPaths Include="$(WorkRoot)/current/SIPSorcery.dll">
      <NuGetPackageId>PeerOnQ.SIPSorcery</NuGetPackageId>
      <DestinationSubDirectory>references/</DestinationSubDirectory>
    </ReferenceCopyLocalPaths>
    <ResolvedFileToPublish Include="$(WorkRoot)/current/SIPSorcery.dll">
      <NuGetPackageId>PeerOnQ.SIPSorcery</NuGetPackageId>
      <RelativePath>payload/SIPSorcery.dll</RelativePath>
    </ResolvedFileToPublish>
  </ItemGroup>
  <Target Name="_CopyFilesMarkedCopyLocal">
    <Copy SourceFiles="@(ReferenceCopyLocalPaths)"
          DestinationFiles="@(ReferenceCopyLocalPaths->'$(OutDir)%(DestinationSubDirectory)%(Filename)%(Extension)')"
          SkipUnchangedFiles="true" />
  </Target>
  <Target Name="Build" DependsOnTargets="_CopyFilesMarkedCopyLocal">
    <WriteLinesToFile File="$(WorkRoot)/build-invocations.txt" Lines="build" Overwrite="false" />
  </Target>
  <Target Name="CopyFilesToPublishDirectory">
    <Copy SourceFiles="@(ResolvedFileToPublish)"
          DestinationFiles="@(ResolvedFileToPublish->'$(PublishDir)%(RelativePath)')"
          SkipUnchangedFiles="true" />
  </Target>
  <Target Name="Publish" DependsOnTargets="$(BuildForPublish)CopyFilesToPublishDirectory" />
  <Import Project="__TARGETS__" Condition="'$(UsePeerOnQHashRepair)' == 'true'" />
</Project>
'@
    $project = $project.Replace('__WORK__', [Security.SecurityElement]::Escape($work)).
        Replace('__TARGETS__', [Security.SecurityElement]::Escape($targets))
    [IO.File]::WriteAllText((Join-Path $work 'CopyProbe.proj'), $project)
    Push-Location -LiteralPath $repoRoot
    $locationChanged = $true

    $builtDll = Join-Path $work 'build/references/SIPSorcery.dll'
    Set-OldDestination $builtDll
    Invoke-CopyProbe 'baseline-build' 'Build' $false
    Assert-ProbeHash $builtDll $oldHash 'MSBuild baseline must retain the same-size/time old build bytes'
    Invoke-CopyProbe 'repaired-build' 'Build' $true
    Assert-ProbeHash $builtDll $currentHash 'Build must repair old bytes using DestinationSubDirectory'
    Assert-UnchangedLockedDestination $builtDll 'Build' 'unchanged-build'
    $buildCount = @(Get-Content -LiteralPath (Join-Path $work 'build-invocations.txt')).Count

    $publishedDll = Join-Path $work 'publish/payload/SIPSorcery.dll'
    Set-OldDestination $publishedDll
    Invoke-CopyProbe 'baseline-publish' 'Publish' $false
    Assert-ProbeHash $publishedDll $oldHash 'MSBuild baseline must retain the same-size/time old publish bytes'
    Invoke-CopyProbe 'repaired-publish' 'Publish' $true
    Assert-ProbeHash $publishedDll $currentHash 'No-build publish must repair old bytes using RelativePath'
    Assert-UnchangedLockedDestination $publishedDll 'Publish' 'unchanged-publish'
    if (@(Get-Content -LiteralPath (Join-Path $work 'build-invocations.txt')).Count -ne $buildCount) {
        throw 'The no-build publish regression unexpectedly invoked Build.'
    }
    if ((Get-FileHash -LiteralPath $targets -Algorithm SHA256).Hash -ne $targetsHash) {
        throw 'The imported copy/hash targets changed during validation.'
    }
    Write-Host 'Maintained dependency copy/hash regression: 6 cases passed (build/publish stale baseline, repair and unchanged locked-file no-op).'
} finally {
    if ($locationChanged) { Pop-Location }
    if (Test-Path -LiteralPath $work) {
        $resolvedWork = [IO.Path]::GetFullPath($work)
        if ([IO.Path]::GetDirectoryName($resolvedWork) -ne $temporaryRoot -or
            [IO.Path]::GetFileName($resolvedWork) -notmatch '^peeronq-maintained-copy-[a-f0-9]{32}$' -or
            ((Get-Item -LiteralPath $resolvedWork -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            @(Get-ChildItem -LiteralPath $resolvedWork -Force -Recurse | Where-Object {
                ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            }).Count -ne 0) {
            throw 'Refusing to clean an unexpected copy/hash regression workspace.'
        }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
