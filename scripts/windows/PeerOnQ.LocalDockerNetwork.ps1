function Initialize-PeerOnQLocalInternalDockerNetwork {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$DockerExecutable,
        [string]$Name = 'peeronq-local-observability'
    )

    if ($Name -notmatch '^[a-z0-9][a-z0-9_.-]{1,62}$') {
        throw 'The local Docker network name is invalid.'
    }

    $description = @(
        & $DockerExecutable network inspect --format '{{.Driver}}|{{.Scope}}|{{.Internal}}' $Name 2>$null
    ) | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) {
        # A simultaneous Phase 3/Phase 6 start may win this create race. Re-inspection below
        # accepts that case only when the resulting network has the required isolation.
        & $DockerExecutable network create --driver bridge --internal `
            --label com.peeronq.scope=local-development $Name 2>$null | Out-Null
        $description = @(
            & $DockerExecutable network inspect --format '{{.Driver}}|{{.Scope}}|{{.Internal}}' $Name 2>$null
        ) | Select-Object -First 1
    }

    if ([string]::IsNullOrWhiteSpace($description) -or
        $description.Trim().ToLowerInvariant() -ne 'bridge|local|true') {
        throw "Docker network '$Name' must be a local, internal bridge network."
    }
}
