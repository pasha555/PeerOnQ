#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
runtime_id="${1:-linux-x64}"
requested_version="${2:-}"
requested_output="${3:-}"
project_path="$repository_root/src/PeerOnQ.App.Linux/PeerOnQ.App.Linux.csproj"

if [[ $# -gt 3 ]]; then
  echo "Usage: $0 [linux-x64|linux-arm64] [canonical-version] [private-output-directory]" >&2
  exit 2
fi

case "$runtime_id" in
  linux-x64|linux-arm64) ;;
  *)
    echo "Supported runtime IDs: linux-x64, linux-arm64" >&2
    exit 2
    ;;
esac

version="$(
  dotnet msbuild "$project_path" -nologo -getProperty:PeerOnQLinuxClientVersion |
    tr -d '\r' |
    tail -n 1
)"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Directory.Build.props must define PeerOnQLinuxClientVersion in major.minor.patch format." >&2
  exit 2
fi
if [[ -n "$requested_version" && "$requested_version" != "$version" ]]; then
  echo "Linux viewer build version $requested_version does not match canonical version $version." >&2
  exit 2
fi

output_root="${requested_output:-$repository_root/app-updates/linux}"
mkdir -p "$output_root"
output_root="$(cd "$output_root" && pwd)"
package_name="peeronq-$version-$runtime_id-unsigned-preview"
archive_path="$output_root/$package_name.tar.gz"
checksum_path="$archive_path.sha256"

if [[ -e "$archive_path" || -e "$checksum_path" ]]; then
  echo "Refusing to overwrite an existing Linux viewer artifact: $archive_path" >&2
  exit 2
fi
staging_root="$(mktemp -d "$output_root/.peeronq-build.XXXXXX")"
publish_directory="$staging_root/$package_name"
staged_archive="$staging_root/$package_name.tar.gz"
staged_checksum="$staging_root/$package_name.tar.gz.sha256"

cleanup() {
  case "$staging_root" in
    "$output_root"/.peeronq-build.*) rm -rf -- "$staging_root" ;;
    *) echo "Refusing to remove unexpected staging path: $staging_root" >&2 ;;
  esac
}
trap cleanup EXIT

dotnet run --project "$repository_root/eng/PeerOnQ.DependencyBuilder" -c Release -- \
  --output "$repository_root/eng/sipsorcery/packages"

dotnet publish "$project_path" \
  --configuration Release \
  --runtime "$runtime_id" \
  --self-contained true \
  --artifacts-path "$staging_root/artifacts" \
  --output "$publish_directory" \
  -p:Version="$version" \
  -p:AssemblyVersion="$version.0" \
  -p:FileVersion="$version.0" \
  -p:InformationalVersion="$version-linux-viewer-unsigned-preview" \
  -p:DebugType=None \
  -p:DebugSymbols=false

cp "$repository_root/packaging/linux/README.md" "$publish_directory/README.md"
[[ -s "$publish_directory/LICENSE.txt" && -s "$publish_directory/THIRD_PARTY_NOTICES.md" ]] || {
  echo "Linux viewer license notices are missing." >&2
  exit 1
}
chmod 0755 "$publish_directory/PeerOnQ"
cat > "$publish_directory/PeerOnQ.LinuxViewer.json" <<EOF
{
  "product": "PeerOnQ Linux Viewer",
  "version": "$version",
  "runtime": "$runtime_id",
  "channel": "unsigned-preview",
  "entrypoint": "PeerOnQ",
  "viewerOnly": true,
  "linuxHost": false,
  "unattendedAccess": false
}
EOF

(
  cd "$publish_directory"
  while IFS= read -r -d '' file; do
    if command -v sha256sum >/dev/null 2>&1; then
      sha256sum "$file"
    else
      shasum -a 256 "$file"
    fi
  done < <(find . -type f ! -name SHA256SUMS.txt -print0 | sort -z)
) > "$publish_directory/SHA256SUMS.txt"

tar -C "$staging_root" -czf "$staged_archive" "$package_name"

if command -v sha256sum >/dev/null 2>&1; then
  (cd "$staging_root" && sha256sum "$package_name.tar.gz") > "$staged_checksum"
else
  (cd "$staging_root" && shasum -a 256 "$package_name.tar.gz") > "$staged_checksum"
fi
mv "$staged_archive" "$archive_path"
mv "$staged_checksum" "$checksum_path"

echo "Created $archive_path"
echo "Created $checksum_path"
