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
    foreach ($relative in 'LICENSE.md', '_PeerOnQ/upstream.json', '_PeerOnQ/dtls-receive-wakeup.patch', '_PeerOnQ/provenance.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $noticeRoot $relative) -PathType Leaf)) {
            throw "The artifact is missing a maintained SIPSorcery notice: $relative"
        }
    }
    $manifestPath = Join-Path $noticeRoot '_PeerOnQ/upstream.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $provenance = Get-Content -LiteralPath (Join-Path $noticeRoot '_PeerOnQ/provenance.json') -Raw | ConvertFrom-Json
    $expectedIdentity = "$($manifest.package.id)/$($manifest.package.version)"
    if ($maintainedPackages[0] -ne $expectedIdentity -or
        $provenance.packageId -ne $manifest.package.id -or
        $provenance.packageVersion -ne $manifest.package.version -or
        $provenance.upstreamPackageId -ne $manifest.upstream.packageId -or
        $provenance.upstreamPackageVersion -ne $manifest.upstream.packageVersion -or
        $provenance.upstreamCommit -ne $manifest.upstream.commit -or
        $provenance.archiveSha256 -ne $manifest.upstream.archiveSha256 -or
        $provenance.patchSha256 -ne $manifest.patch.sha256 -or
        $provenance.patchedSourceSha256 -ne $manifest.patch.patchedFileSha256 -or
        $provenance.sdk -ne $manifest.requiredSdkVersion) {
        throw 'The artifact SIPSorcery dependency, retained manifest and provenance do not match.'
    }
    $hashChecks = @(
        @{ Path = (Join-Path $noticeRoot 'LICENSE.md'); Expected = $manifest.upstream.licenseSha256 },
        @{ Path = (Join-Path $noticeRoot '_PeerOnQ/dtls-receive-wakeup.patch'); Expected = $manifest.patch.sha256 },
        @{ Path = $manifestPath; Expected = $provenance.manifestSha256 }
    )
    $additionalPatches = @()
    $additionalPatchProperty = $manifest.PSObject.Properties['additionalPatches']
    if ($null -ne $additionalPatchProperty) { $additionalPatches = @($additionalPatchProperty.Value) }
    $additionalProvenance = @()
    $additionalProvenanceProperty = $provenance.PSObject.Properties['additionalPatches']
    if ($null -ne $additionalProvenanceProperty) { $additionalProvenance = @($additionalProvenanceProperty.Value) }
    if ($additionalPatches.Count -ne $additionalProvenance.Count) {
        throw 'The artifact additional SIPSorcery patches do not match the retained provenance.'
    }
    $sourceChain = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $sourceChain.Add($manifest.patch.targetPath, $manifest.patch.patchedFileSha256)
    foreach ($patch in $additionalPatches) {
        if ($patch.file -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.patch$') {
            throw 'The artifact contains an invalid maintained patch filename.'
        }
        $record = @($additionalProvenance | Where-Object { $_.file -ceq $patch.file })
        if ($record.Count -ne 1 -or $record[0].sha256 -ne $patch.sha256 -or
            $record[0].targetPath -cne $patch.targetPath -or
            $record[0].upstreamFileSha256 -ne $patch.upstreamFileSha256 -or
            $record[0].patchedFileSha256 -ne $patch.patchedFileSha256) {
            throw 'The artifact additional SIPSorcery patch identity/provenance does not match.'
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
    foreach ($check in $hashChecks) {
        if ($check.Expected -notmatch '^[a-fA-F0-9]{64}$' -or
            (Get-FileHash -Algorithm SHA256 -LiteralPath $check.Path).Hash -ne $check.Expected) {
            throw "The artifact contains an altered maintained SIPSorcery notice: $([IO.Path]::GetFileName($check.Path))"
        }
    }
}
