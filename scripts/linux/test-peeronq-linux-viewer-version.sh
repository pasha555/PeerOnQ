#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project_path="$repository_root/src/PeerOnQ.App.Linux/PeerOnQ.App.Linux.csproj"
builder_path="$repository_root/scripts/linux/build-peeronq-linux-viewer.sh"

read_property() {
  dotnet msbuild "$project_path" -nologo "-getProperty:$1" |
    tr -d '\r' |
    tail -n 1
}

canonical_version="$(read_property PeerOnQLinuxClientVersion)"
if [[ ! "$canonical_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "PeerOnQLinuxClientVersion is missing or invalid: $canonical_version" >&2
  exit 1
fi

[[ "$(read_property Version)" == "$canonical_version" ]]
[[ "$(read_property AssemblyVersion)" == "$canonical_version.0" ]]
[[ "$(read_property FileVersion)" == "$canonical_version.0" ]]
[[ "$(read_property InformationalVersion)" == "$canonical_version-linux-viewer-unsigned-preview" ]]

if bash "$builder_path" linux-x64 999.999.999 >/dev/null 2>&1; then
  echo "Linux viewer builder accepted a non-canonical version." >&2
  exit 1
fi

echo "Canonical Linux viewer version invariant passed: $canonical_version"
