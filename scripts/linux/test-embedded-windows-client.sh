#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
validator="$script_dir/verify-embedded-windows-client.sh"
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

make_fixture() {
  fixture=$1
  release_type=$2
  downloads="$fixture/artifacts/peeronq/public/downloads"
  mkdir -p "$downloads"
  printf '0.5.1\n' > "$downloads/embedded-windows-version.txt"
  printf '%s\n' "$release_type" > "$downloads/embedded-windows-release-type.txt"
  printf 'test-msi-payload\n' > "$downloads/PeerOnQ-Windows-x64.msi"
  hash=$(sha256sum "$downloads/PeerOnQ-Windows-x64.msi" | awk '{ print $1 }')
  printf '%s  PeerOnQ-Windows-x64.msi\n' "$hash" > "$downloads/SHA256SUMS.txt"
  if [ "$release_type" = "unsigned-pilot" ]; then
    printf '%s\n%s\n' \
      'This x64 MSI is unsigned and is authorized only for controlled PeerOnQ pilot testing.' \
      'Verify SHA256SUMS.txt before installation.' \
      > "$downloads/UNSIGNED-PILOT-NOTICE.txt"
  fi
}

expect_failure() {
  label=$1
  fixture=$2
  if sh "$validator" "$fixture" >/dev/null 2>&1; then
    printf 'Expected validation failure: %s\n' "$label" >&2
    exit 1
  fi
}

make_fixture "$temporary/signed" signed
sh "$validator" "$temporary/signed"

make_fixture "$temporary/unsigned" unsigned-pilot
sh "$validator" "$temporary/unsigned"

make_fixture "$temporary/missing-metadata" signed
rm "$temporary/missing-metadata/artifacts/peeronq/public/downloads/embedded-windows-version.txt"
expect_failure 'missing version metadata' "$temporary/missing-metadata"

make_fixture "$temporary/missing-release-type" signed
rm "$temporary/missing-release-type/artifacts/peeronq/public/downloads/embedded-windows-release-type.txt"
expect_failure 'missing release-type metadata' "$temporary/missing-release-type"

make_fixture "$temporary/missing-artifact" signed
rm "$temporary/missing-artifact/artifacts/peeronq/public/downloads/PeerOnQ-Windows-x64.msi"
expect_failure 'missing MSI artifact' "$temporary/missing-artifact"

make_fixture "$temporary/missing-hash" signed
rm "$temporary/missing-hash/artifacts/peeronq/public/downloads/SHA256SUMS.txt"
expect_failure 'missing SHA-256 metadata' "$temporary/missing-hash"

make_fixture "$temporary/hash-mismatch" signed
printf 'changed\n' >> "$temporary/hash-mismatch/artifacts/peeronq/public/downloads/PeerOnQ-Windows-x64.msi"
expect_failure 'SHA-256 mismatch' "$temporary/hash-mismatch"

make_fixture "$temporary/unsigned-without-opt-in-marker" unsigned-pilot
rm "$temporary/unsigned-without-opt-in-marker/artifacts/peeronq/public/downloads/UNSIGNED-PILOT-NOTICE.txt"
expect_failure 'unsigned pilot without notice' "$temporary/unsigned-without-opt-in-marker"

printf 'Embedded Windows client invariant tests passed.\n'
