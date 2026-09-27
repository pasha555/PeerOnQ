param(
    [int]$MaxFiles = 120
)

$ErrorActionPreference = "SilentlyContinue"

Write-Output "=== Git ==="
if (Get-Command git -ErrorAction SilentlyContinue) {
    Write-Output ("Branch: " + (git branch --show-current))
    git status --short
} else {
    Write-Output "git not found"
}

Write-Output ""
Write-Output "=== Top-level ==="
Get-ChildItem -Force | Where-Object {
    $_.Name -notin @(".git","node_modules","dist","build",".next",".venv","venv","target","coverage")
} | Select-Object Mode, Length, Name | Format-Table -AutoSize

Write-Output ""
Write-Output "=== Key manifests/docs ==="
$patterns = @(
    "AGENTS.md","AGENTS.override.md","README*","PROJECT_MAP.md","AI_CHANGELOG.md",
    "package.json","pnpm-lock.yaml","yarn.lock","package-lock.json",
    "pyproject.toml","requirements*.txt","poetry.lock",
    "Cargo.toml","go.mod","*.sln","*.csproj",
    "docker-compose*.yml","docker-compose*.yaml","Dockerfile*"
)
foreach ($pattern in $patterns) {
    Get-ChildItem -Path . -Filter $pattern -File -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }
}

Write-Output ""
Write-Output "=== Tracked files sample ==="
if (Get-Command git -ErrorAction SilentlyContinue) {
    git ls-files | Select-Object -First $MaxFiles
} else {
    Get-ChildItem -Recurse -File |
        Where-Object {
            $_.FullName -notmatch "\\(node_modules|dist|build|\.next|\.venv|venv|target|coverage)\\"
        } |
        Select-Object -First $MaxFiles -ExpandProperty FullName
}
