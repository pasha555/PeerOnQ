#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <peeronq-version-linux-(x64|arm64)-unsigned-preview.tar.gz>" >&2
  exit 2
fi

archive_path="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
archive_directory="$(dirname "$archive_path")"
archive_name="$(basename "$archive_path")"
if [[ ! "$archive_name" =~ ^peeronq-([0-9]+\.[0-9]+\.[0-9]+)-(linux-(x64|arm64))-unsigned-preview\.tar\.gz$ ]]; then
  echo "Linux viewer archive name is invalid: $archive_name" >&2
  exit 2
fi
version="${BASH_REMATCH[1]}"
runtime_id="${BASH_REMATCH[2]}"
package_name="${archive_name%.tar.gz}"
checksum_path="$archive_path.sha256"

[[ -f "$archive_path" ]] || { echo "Archive is missing: $archive_path" >&2; exit 2; }
[[ -f "$checksum_path" ]] || { echo "Archive checksum is missing: $checksum_path" >&2; exit 2; }

read -r expected_archive_hash checksum_name extra < "$checksum_path"
checksum_name="${checksum_name#\*}"
if [[ ! "$expected_archive_hash" =~ ^[0-9a-f]{64}$
      || "$checksum_name" != "$archive_name"
      || -n "${extra:-}" ]]; then
  echo "Archive checksum sidecar is malformed or names another file." >&2
  exit 1
fi
if command -v sha256sum >/dev/null 2>&1; then
  actual_archive_hash="$(sha256sum "$archive_path" | awk '{print $1}')"
else
  actual_archive_hash="$(shasum -a 256 "$archive_path" | awk '{print $1}')"
fi
[[ "$actual_archive_hash" == "$expected_archive_hash" ]] || {
  echo "Archive checksum mismatch: $archive_name" >&2
  exit 1
}

if tar -tzf "$archive_path" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then
  echo "Archive contains an absolute or traversal path." >&2
  exit 1
fi
if tar -tzf "$archive_path" | grep -Ev "^${package_name}(/|$)" | grep -q .; then
  echo "Archive contains an entry outside its package root." >&2
  exit 1
fi

verification_root="$(mktemp -d "$archive_directory/.peeronq-verify.XXXXXX")"
cleanup() {
  case "$verification_root" in
    "$archive_directory"/.peeronq-verify.*) rm -rf -- "$verification_root" ;;
    *) echo "Refusing to remove unexpected verification path: $verification_root" >&2 ;;
  esac
}
trap cleanup EXIT

tar -C "$verification_root" -xzf "$archive_path"
payload_root="$verification_root/$package_name"
[[ -d "$payload_root" ]] || { echo "Archive package root is missing." >&2; exit 1; }
if find "$payload_root" -type l -print -quit | grep -q .; then
  echo "Archive must not contain symbolic links." >&2
  exit 1
fi

manifest_path="$payload_root/PeerOnQ.LinuxViewer.json"
[[ -f "$manifest_path" ]] || { echo "Linux viewer manifest is missing." >&2; exit 1; }
grep -Fqx "  \"version\": \"$version\"," "$manifest_path"
grep -Fqx "  \"runtime\": \"$runtime_id\"," "$manifest_path"
grep -Fqx '  "channel": "unsigned-preview",' "$manifest_path"
grep -Fqx '  "viewerOnly": true,' "$manifest_path"
grep -Fqx '  "linuxHost": false,' "$manifest_path"
grep -Fqx '  "unattendedAccess": false' "$manifest_path"

[[ -f "$payload_root/README.md" ]] || { echo "Linux viewer README is missing." >&2; exit 1; }
[[ -s "$payload_root/PeerOnQ" ]] || { echo "Linux viewer entrypoint is missing or empty." >&2; exit 1; }
if [[ ! -x "$payload_root/PeerOnQ" ]]; then
  grep -Fq 'chmod +x PeerOnQ' "$payload_root/README.md" || {
    echo "A non-executable cross-published entrypoint must include the exact chmod instruction." >&2
    exit 1
  }
fi
[[ -f "$payload_root/SHA256SUMS.txt" ]] || { echo "Payload checksum manifest is missing." >&2; exit 1; }
(
  cd "$payload_root"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum -c SHA256SUMS.txt
  else
    shasum -a 256 -c SHA256SUMS.txt
  fi
) >/dev/null

echo "Linux viewer package validation passed: $archive_name ($actual_archive_hash)"
