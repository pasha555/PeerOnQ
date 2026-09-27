#!/bin/sh
set -eu

[ "$#" -eq 1 ] || {
  printf 'Usage: %s RELEASE_PATH\n' "$0" >&2
  exit 64
}

downloads_path="$1/artifacts/peeronq/public/downloads"
msi_path="$downloads_path/PeerOnQ-Windows-x64.msi"
hash_path="$downloads_path/SHA256SUMS.txt"
version_path="$downloads_path/embedded-windows-version.txt"
release_type_path="$downloads_path/embedded-windows-release-type.txt"

for required_file in "$msi_path" "$hash_path" "$version_path" "$release_type_path"; do
  [ -f "$required_file" ] && [ ! -L "$required_file" ] || {
    printf 'Embedded Windows client file is missing or unsafe: %s\n' "$required_file" >&2
    exit 1
  }
done

awk '
  NR == 1 && $0 ~ /^[0-9]+\.[0-9]+\.[0-9]+$/ { valid = 1; next }
  { valid = 0 }
  END { exit !(NR == 1 && valid) }
' "$version_path" || {
  printf 'Embedded Windows client version metadata is invalid.\n' >&2
  exit 1
}

release_type=$(awk 'NR == 1 { value = $0 } END { if (NR != 1) exit 1; print value }' "$release_type_path") || {
  printf 'Embedded Windows client release-type metadata is invalid.\n' >&2
  exit 1
}
case "$release_type" in
  signed)
    [ ! -e "$downloads_path/UNSIGNED-PILOT-NOTICE.txt" ] || {
      printf 'A signed embedded Windows client must not contain an unsigned-pilot notice.\n' >&2
      exit 1
    }
    ;;
  unsigned-pilot)
    notice_path="$downloads_path/UNSIGNED-PILOT-NOTICE.txt"
    [ -f "$notice_path" ] && [ ! -L "$notice_path" ] \
      && grep -Fqx 'This x64 MSI is unsigned and is authorized only for controlled PeerOnQ pilot testing.' "$notice_path" || {
        printf 'Unsigned embedded Windows client notice is missing or invalid.\n' >&2
        exit 1
      }
    ;;
  *)
    printf 'Embedded Windows client release type must be signed or unsigned-pilot.\n' >&2
    exit 1
    ;;
esac

msi_size=$(stat -c '%s' "$msi_path")
case "$msi_size" in
  *[!0-9]*|'')
    printf 'Embedded Windows client size is invalid.\n' >&2
    exit 1
    ;;
esac
[ "$msi_size" -gt 0 ] && [ "$msi_size" -le 100663296 ] || {
  printf 'Embedded Windows client must be between 1 byte and 96 MiB.\n' >&2
  exit 1
}

expected_hash=$(awk '
  $2 == "PeerOnQ-Windows-x64.msi" {
    count++
    if (NF != 2 || length($1) != 64 || $1 ~ /[^0-9a-f]/) invalid = 1
    value = $1
  }
  END {
    if (count != 1 || invalid) exit 1
    print value
  }
' "$hash_path") || {
  printf 'Embedded Windows client SHA-256 metadata is missing or invalid.\n' >&2
  exit 1
}
actual_hash=$(sha256sum "$msi_path" | awk '{ print $1 }')
[ "$actual_hash" = "$expected_hash" ] || {
  printf 'Embedded Windows client SHA-256 does not match its metadata.\n' >&2
  exit 1
}
