<# Fails when tracked or pending source files contain high-confidence secret material. #>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$textExtensions = @(
    '.cs', '.csproj', '.props', '.targets', '.json', '.yaml', '.yml', '.xml', '.md', '.txt',
    '.ps1', '.psm1', '.sh', '.conf', '.config', '.env', '.example', '.ts', '.tsx', '.js', '.jsx'
)
$patterns = [ordered]@{
    'private-key' = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    'aws-access-key' = '\b(?:AKIA|ASIA)[0-9A-Z]{16}\b'
    'github-token' = '\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{30,}\b'
    'github-fine-grained-token' = '\bgithub_pat_[A-Za-z0-9_]{40,}\b'
    'slack-token' = '\bxox[baprs]-[A-Za-z0-9-]{20,}\b'
    'stripe-live-key' = '\b(?:sk|rk)_live_[A-Za-z0-9]{20,}\b'
    'generic-assigned-secret' = '(?i)\b(?:api[_-]?key|client[_-]?secret|password|private[_-]?token)\b\s*[:=]\s*["''][^"'']{16,}["'']'
}
$safeValuePattern = '(?i)(example|placeholder|changeme|replace[_-]?me|not[_-]?a[_-]?secret|secret-scan:\s*allow-test-vector|\$\{|<[^>]+>)'

$listed = & git -C $repoRoot ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
$findings = New-Object 'System.Collections.Generic.List[string]'
foreach ($relative in $listed) {
    $fullPath = Join-Path $repoRoot $relative
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
    $item = Get-Item -LiteralPath $fullPath
    if ($item.Length -gt 2MB -or $textExtensions -notcontains $item.Extension.ToLowerInvariant()) { continue }

    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($fullPath)) {
        $lineNumber++
        if ($line -match $safeValuePattern) { continue }
        foreach ($entry in $patterns.GetEnumerator()) {
            if ($line -match $entry.Value) {
                $findings.Add("$relative`:$lineNumber [$($entry.Key)]")
            }
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | Sort-Object -Unique | ForEach-Object { Write-Error $_ }
    throw "Potential secrets found: $($findings.Count). Values are intentionally not printed."
}
Write-Host "Secret scan passed for $($listed.Count) repository file(s)."
