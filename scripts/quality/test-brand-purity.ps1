#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$manifestPath = Join-Path $repoRoot 'docs\brand-compatibility-allowlist.txt'
$formerBrand = 'Link' + 'ora'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Brand compatibility manifest is missing: $manifestPath"
}

$allowRules = Get-Content -LiteralPath $manifestPath |
    ForEach-Object { ($_ -split '\|', 2)[0].Trim().Replace('\', '/') } |
    Where-Object { $_ -and -not $_.StartsWith('#', [StringComparison]::Ordinal) }

function Test-Allowlisted([string]$relativePath) {
    foreach ($rule in $allowRules) {
        if ($rule.EndsWith('/', [StringComparison]::Ordinal)) {
            if ($relativePath.StartsWith($rule, [StringComparison]::OrdinalIgnoreCase)) { return $true }
        } elseif ($relativePath.Equals($rule, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

$candidateFiles = & git -C $repoRoot ls-files -co --exclude-standard
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }

$violations = foreach ($candidate in $candidateFiles) {
    $relativePath = $candidate.Replace('\', '/')
    if (Test-Allowlisted $relativePath) { continue }
    if ($relativePath -match '(^|/)(generated|node_modules|dist|build|coverage|vendor)(/|$)') { continue }
    if ($relativePath.StartsWith('artifacts/peeronq/src/components/ui/', [StringComparison]::OrdinalIgnoreCase)) { continue }

    $absolutePath = Join-Path $repoRoot $candidate
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) { continue }

    if ($relativePath.IndexOf($formerBrand, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        "$relativePath (path)"
        continue
    }

    try {
        $content = [IO.File]::ReadAllText($absolutePath)
    } catch {
        continue
    }

    if ($content.IndexOf($formerBrand, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        "$relativePath (content)"
    }
}

if ($violations) {
    $violations | ForEach-Object { Write-Error "Forbidden former product brand: $_" }
    exit 1
}

Write-Host "Brand purity passed: no forbidden former product brand outside the compatibility manifest."
