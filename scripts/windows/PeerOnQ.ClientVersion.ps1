function Get-PeerOnQClientVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $propsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath -PathType Leaf)) {
        throw "Canonical client version file is missing: $propsPath"
    }

    try {
        [xml]$props = [IO.File]::ReadAllText($propsPath)
    } catch {
        throw "Canonical client version file is invalid XML: $propsPath"
    }

    $values = @($props.Project.PropertyGroup.PeerOnQWindowsClientVersion |
        ForEach-Object { $_.ToString().Trim() } |
        Where-Object { $_ })
    if ($values.Count -ne 1 -or $values[0] -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'Directory.Build.props must contain exactly one three-part PeerOnQWindowsClientVersion.'
    }

    return [version]$values[0]
}

function Assert-PeerOnQClientVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [version]$Version,
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [string]$Context = 'Windows client build'
    )

    $canonical = Get-PeerOnQClientVersion -RepositoryRoot $RepositoryRoot
    if ($Version -ne $canonical) {
        throw "$Context version $Version does not match canonical Windows client version $canonical. Bump PeerOnQWindowsClientVersion once before building or publishing."
    }

    return $canonical
}
