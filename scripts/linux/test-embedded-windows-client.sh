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

make_pair_fixture() {
  make_fixture "$1" signed
  pair_downloads="$1/artifacts/peeronq/public/downloads"
  printf 'test-arm64-msi-payload\n' > "$pair_downloads/PeerOnQ-Windows-arm64.msi"
  pair_hash=$(sha256sum "$pair_downloads/PeerOnQ-Windows-arm64.msi" | awk '{ print $1 }')
  printf '%s  PeerOnQ-Windows-arm64.msi\n' "$pair_hash" >> "$pair_downloads/SHA256SUMS.txt"
}

make_pair_fixture "$temporary/pair"
sh "$validator" "$temporary/pair"
make_pair_fixture "$temporary/pair-missing-arm64"
rm "$pair_downloads/PeerOnQ-Windows-arm64.msi"
expect_failure 'paired ARM64 MSI missing' "$temporary/pair-missing-arm64"
make_pair_fixture "$temporary/pair-arm64-tampered"
printf 'changed\n' >> "$pair_downloads/PeerOnQ-Windows-arm64.msi"
expect_failure 'paired ARM64 hash mismatch' "$temporary/pair-arm64-tampered"
make_pair_fixture "$temporary/pair-arm64-hash-missing"
sed -i '/PeerOnQ-Windows-arm64.msi/d' "$pair_downloads/SHA256SUMS.txt"
expect_failure 'paired ARM64 checksum missing' "$temporary/pair-arm64-hash-missing"
make_pair_fixture "$temporary/pair-arm64-duplicate-hash"
duplicate_hash=$(tail -n 1 "$pair_downloads/SHA256SUMS.txt")
printf '%s\n' "$duplicate_hash" >> "$pair_downloads/SHA256SUMS.txt"
expect_failure 'paired ARM64 duplicate checksum' "$temporary/pair-arm64-duplicate-hash"
make_pair_fixture "$temporary/pair-arm64-symlink"
mv "$pair_downloads/PeerOnQ-Windows-arm64.msi" "$pair_downloads/real-arm64.msi"
ln -s real-arm64.msi "$pair_downloads/PeerOnQ-Windows-arm64.msi"
expect_failure 'paired ARM64 symlink' "$temporary/pair-arm64-symlink"
printf 'Paired Windows client invariant tests passed.\n'

# Execute the real Dockerfile's embedded-release RUN gate with only its final
# pnpm build replaced by a stub. Failed hash/file tests must stop before build.
dockerfile="$script_dir/../../artifacts/peeronq/Dockerfile"
mkdir -p "$temporary/bin"
printf '#!/bin/sh\n[ "$VITE_PEERONQ_EMBEDDED_WINDOWS_ARM64_URL" = /downloads/PeerOnQ-Windows-arm64.msi ]\n' > "$temporary/bin/pnpm"
chmod +x "$temporary/bin/pnpm"
awk '
  /^RUN set -eu; if / { copy = 1; sub(/^RUN /, "") }
  copy { sub(/ \\$/, ""); print }
  copy && /pnpm --filter @workspace\/peeronq run build/ { exit }
' "$dockerfile" > "$temporary/docker-gate-template.sh"
[ -s "$temporary/docker-gate-template.sh" ] || { printf 'Docker embedded validation gate is missing.\n' >&2; exit 1; }
run_docker_gate() {
  sed "s|/workspace|$1|g" "$temporary/docker-gate-template.sh" > "$temporary/docker-gate.sh"
  PATH="$temporary/bin:$PATH" sh "$temporary/docker-gate.sh"
}
make_pair_fixture "$temporary/docker-valid"
run_docker_gate "$temporary/docker-valid"
for failure in missing-arm64 changed-arm64 changed-x64; do
  fixture="$temporary/docker-$failure"
  make_pair_fixture "$fixture"
  case "$failure" in
    missing-arm64) rm "$pair_downloads/PeerOnQ-Windows-arm64.msi" ;;
    changed-arm64) printf 'changed\n' >> "$pair_downloads/PeerOnQ-Windows-arm64.msi" ;;
    changed-x64) printf 'changed\n' >> "$pair_downloads/PeerOnQ-Windows-x64.msi" ;;
  esac
  if run_docker_gate "$fixture" >/dev/null 2>&1; then
    printf 'Docker embedded validation accepted %s.\n' "$failure" >&2
    exit 1
  fi
done
printf 'Docker paired file/hash validation gates passed.\n'
