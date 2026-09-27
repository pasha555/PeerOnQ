$ErrorActionPreference = "SilentlyContinue"

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Output "git not found"
    exit 0
}

Write-Output "=== Status ==="
git status --short

Write-Output ""
Write-Output "=== Diff stat ==="
git diff --stat
git diff --cached --stat

Write-Output ""
Write-Output "=== Whitespace/errors ==="
git diff --check

Write-Output ""
Write-Output "=== Changed paths ==="
git diff --name-only
git diff --cached --name-only
