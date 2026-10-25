function Assert-PeerOnQMaintainedSipsorceryNotices {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PublishDirectory)

    $depsPath = Join-Path $PublishDirectory 'PeerOnQ.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { return }
    $dependencies = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $maintainedPackages = @($dependencies.libraries.PSObject.Properties.Name |
        Where-Object { $_ -match '^PeerOnQ\.SIPSorcery/' })
    # Published packages from before adoption retain their original validation contract.
    if ($maintainedPackages.Count -eq 0) { return }
    if ($maintainedPackages.Count -ne 1) { throw 'The artifact contains multiple maintained SIPSorcery versions.' }

    $noticeRoot = Join-Path $PublishDirectory 'ThirdParty/SIPSorcery'
    foreach ($relative in 'LICENSE.md', '_PeerOnQ/upstream.json', '_PeerOnQ/provenance.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $noticeRoot $relative) -PathType Leaf)) {
            throw "The artifact is missing a maintained SIPSorcery notice: $relative"
        }
    }
    $manifestPath = Join-Path $noticeRoot '_PeerOnQ/upstream.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $provenance = Get-Content -LiteralPath (Join-Path $noticeRoot '_PeerOnQ/provenance.json') -Raw | ConvertFrom-Json
    $expectedIdentity = "$($manifest.package.id)/$($manifest.package.version)"
    if ($manifest.schemaVersion -ne 2 -or
        $maintainedPackages[0] -ne $expectedIdentity -or
        $provenance.packageId -ne $manifest.package.id -or
        $provenance.packageVersion -ne $manifest.package.version -or
        $provenance.upstreamPackageId -ne $manifest.upstream.packageId -or
        $provenance.upstreamPackageVersion -ne $manifest.upstream.packageVersion -or
        $provenance.upstreamCommit -ne $manifest.upstream.commit -or
        $provenance.archiveSha256 -ne $manifest.upstream.archiveSha256 -or
        $provenance.licenseSha256 -ne $manifest.upstream.licenseSha256 -or
        $provenance.sdk -ne $manifest.requiredSdkVersion) {
        throw 'The artifact SIPSorcery dependency, retained manifest and provenance do not match.'
    }
    $hashChecks = @(
        @{ Path = (Join-Path $noticeRoot 'LICENSE.md'); Expected = $manifest.upstream.licenseSha256 },
        @{ Path = $manifestPath; Expected = $provenance.manifestSha256 }
    )

    $patches = @($manifest.patches)
    $patchProvenance = @($provenance.patches)
    if ($patches.Count -eq 0 -or $patches.Count -ne $patchProvenance.Count) {
        throw 'The artifact SIPSorcery patches do not match the retained provenance.'
    }
    $sourceChain = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $patches.Count; $index++) {
        $patch = $patches[$index]
        $record = $patchProvenance[$index]
        if ($patch.file -cnotmatch '^[a-z0-9]+(-[a-z0-9]+)*\.patch$') {
            throw 'The artifact contains an invalid maintained patch filename.'
        }
        if ($record.file -cne $patch.file -or $record.sha256 -ne $patch.sha256 -or
            $record.targetPath -cne $patch.targetPath -or
            $record.upstreamFileSha256 -ne $patch.upstreamFileSha256 -or
            $record.patchedFileSha256 -ne $patch.patchedFileSha256) {
            throw 'The artifact SIPSorcery patch identity/provenance does not match.'
        }
        if ($sourceChain.ContainsKey($patch.targetPath) -and
            $sourceChain[$patch.targetPath] -ne $patch.upstreamFileSha256) {
            throw 'The artifact SIPSorcery patch source chain is discontinuous.'
        }
        $sourceChain[$patch.targetPath] = $patch.patchedFileSha256
        $patchPath = Join-Path $noticeRoot ('_PeerOnQ/' + $patch.file)
        if (-not (Test-Path -LiteralPath $patchPath -PathType Leaf)) {
            throw "The artifact is missing a maintained SIPSorcery notice: $($patch.file)"
        }
        $hashChecks += @{ Path = $patchPath; Expected = $patch.sha256 }
    }

    $addedProperty = $manifest.PSObject.Properties['addedFiles']
    $added = if ($null -ne $addedProperty -and $null -ne $addedProperty.Value) { @($addedProperty.Value) } else { @() }
    $addedProvenance = @($provenance.addedFiles)
    if ($added.Count -ne $addedProvenance.Count) {
        throw 'The artifact SIPSorcery added files do not match the retained provenance.'
    }
    for ($index = 0; $index -lt $added.Count; $index++) {
        $file = $added[$index]
        $record = $addedProvenance[$index]
        if ($file.file -cnotmatch '^[A-Za-z0-9]+\.cs$' -or $record.file -cne $file.file -or
            $record.sha256 -ne $file.sha256 -or $record.targetPath -cne $file.targetPath) {
            throw 'The artifact SIPSorcery added file identity/provenance does not match.'
        }
        $addedPath = Join-Path $noticeRoot ('_PeerOnQ/added/' + $file.file)
        if (-not (Test-Path -LiteralPath $addedPath -PathType Leaf)) {
            throw "The artifact is missing a maintained SIPSorcery notice: added/$($file.file)"
        }
        $hashChecks += @{ Path = $addedPath; Expected = $file.sha256 }
    }

    foreach ($check in $hashChecks) {
        if ($check.Expected -notmatch '^[a-fA-F0-9]{64}$' -or
            (Get-FileHash -Algorithm SHA256 -LiteralPath $check.Path).Hash -ne $check.Expected) {
            throw "The artifact contains an altered maintained SIPSorcery notice: $([IO.Path]::GetFileName($check.Path))"
        }
    }
}
