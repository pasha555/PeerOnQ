#!/usr/bin/env bash
set -euo pipefail

usage() {
  printf '%s\n' \
    "Usage: $0 <ios-development|ios|macos-arm64|macos-x64> <wss://host/ws> <absolute-libvpx.a> [output-directory]" \
    "" \
    "ios-development requires:" \
    "  PEERONQ_APPLE_CODESIGN_KEY=Apple Development: ..." \
    "  PEERONQ_APPLE_DEVICE_UDID=<registered iPhone UDID>" \
    "  PEERONQ_APPLE_PROVISIONING_PROFILE=<optional installed profile name or UUID>" \
    "  PEERONQ_APPLE_DEVELOPMENT_BUNDLE_ID=<optional Personal Team bundle ID>" \
    "" \
    "ios requires an approved Apple Distribution PEERONQ_APPLE_CODESIGN_KEY and" \
    "PEERONQ_APPLE_PROVISIONING_PROFILE. Private signing keys stay in Apple Keychain." >&2
}

if [[ $# -lt 3 || $# -gt 4 ]]; then
  usage
  exit 64
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Apple viewer packages must be built on macOS with the matching Xcode and .NET Apple workload." >&2
  exit 1
fi

target="$1"
signaling_url="$2"
libvpx_path="$3"
output_directory="${4:-$(pwd)/app-updates/apple}"
signing_kind=""
provisioning_profile="${PEERONQ_APPLE_PROVISIONING_PROFILE:-}"
device_udid="${PEERONQ_APPLE_DEVICE_UDID:-}"
development_bundle_id="${PEERONQ_APPLE_DEVELOPMENT_BUNDLE_ID:-}"

case "$target" in
  ios-development)
    target_framework="net10.0-ios"
    runtime_identifier="ios-arm64"
    required_architecture="arm64"
    required_apple_platform="2"
    signing_kind="development"
    : "${PEERONQ_APPLE_CODESIGN_KEY:?Set PEERONQ_APPLE_CODESIGN_KEY to the exact Apple Development identity in Keychain.}"
    : "${PEERONQ_APPLE_DEVICE_UDID:?Set PEERONQ_APPLE_DEVICE_UDID to the registered physical iPhone UDID.}"
    if [[ "$PEERONQ_APPLE_CODESIGN_KEY" != "Apple Development:"* ]]; then
      echo "ios-development requires an Apple Development signing identity." >&2
      exit 1
    fi
    if [[ ! "$device_udid" =~ ^([[:xdigit:]]{40}|[[:xdigit:]]{8}-[[:xdigit:]]{16})$ ]]; then
      echo "PEERONQ_APPLE_DEVICE_UDID is not a supported physical iPhone UDID format." >&2
      exit 1
    fi
    if [[ -n "$development_bundle_id" && ( ${#development_bundle_id} -gt 155 || ! "$development_bundle_id" =~ ^([A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?\.)+[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$ ) ]]; then
      echo "PEERONQ_APPLE_DEVELOPMENT_BUNDLE_ID must be a bounded reverse-DNS bundle identifier." >&2
      exit 1
    fi
    ;;
  ios)
    target_framework="net10.0-ios"
    runtime_identifier="ios-arm64"
    required_architecture="arm64"
    required_apple_platform="2"
    signing_kind="distribution"
    : "${PEERONQ_APPLE_CODESIGN_KEY:?Set PEERONQ_APPLE_CODESIGN_KEY to the approved Apple Distribution identity.}"
    : "${PEERONQ_APPLE_PROVISIONING_PROFILE:?Set PEERONQ_APPLE_PROVISIONING_PROFILE to the approved iOS provisioning profile name.}"
    ;;
  macos-arm64)
    target_framework="net10.0-maccatalyst"
    runtime_identifier="maccatalyst-arm64"
    required_architecture="arm64"
    required_apple_platform="6"
    signing_kind="developer-id"
    : "${PEERONQ_APPLE_CODESIGN_KEY:?Set PEERONQ_APPLE_CODESIGN_KEY to the approved Developer ID Application identity.}"
    ;;
  macos-x64)
    target_framework="net10.0-maccatalyst"
    runtime_identifier="maccatalyst-x64"
    required_architecture="x86_64"
    required_apple_platform="6"
    signing_kind="developer-id"
    : "${PEERONQ_APPLE_CODESIGN_KEY:?Set PEERONQ_APPLE_CODESIGN_KEY to the approved Developer ID Application identity.}"
    ;;
  *)
    usage
    exit 64
    ;;
esac

if [[ "$signaling_url" != wss://*/ws || "$signaling_url" == *'?'* || "$signaling_url" == *'#'* || "$signaling_url" == *'@'* ]]; then
  echo "The Apple viewer requires a credential-free wss:// signaling URL ending exactly in /ws." >&2
  exit 1
fi

if [[ "$libvpx_path" != /* || ! -f "$libvpx_path" ]]; then
  echo "The static libvpx input must be an existing absolute .a path." >&2
  exit 1
fi
if [[ "${libvpx_path##*.}" != "a" ]]; then
  echo "The Apple VP8 decoder input must be a static .a archive." >&2
  exit 1
fi

archive_architectures="$(/usr/bin/xcrun lipo -archs "$libvpx_path")"
if [[ " $archive_architectures " != *" $required_architecture "* ]]; then
  echo "The libvpx archive does not contain the required $required_architecture architecture." >&2
  exit 1
fi

archive_platforms="$(/usr/bin/xcrun otool -l "$libvpx_path" \
  | /usr/bin/awk '$1 == "platform" { print $2 }' \
  | /usr/bin/sort -u \
  | /usr/bin/tr '\n' ' ' \
  | /usr/bin/sed 's/[[:space:]]*$//')"
if [[ "$archive_platforms" != "$required_apple_platform" ]]; then
  echo "The libvpx archive platform '$archive_platforms' does not match target platform '$required_apple_platform'." >&2
  exit 1
fi

for required_symbol in _vpx_codec_vp8_dx _vpx_codec_decode _vpx_codec_get_frame; do
  if ! /usr/bin/xcrun nm -gU "$libvpx_path" | /usr/bin/grep " $required_symbol$" >/dev/null; then
    echo "The reviewed libvpx archive is missing $required_symbol." >&2
    exit 1
  fi
done

repository_root="$(cd "$(dirname "$0")/../.." && pwd -P)"
project="$repository_root/src/PeerOnQ.App.Apple/PeerOnQ.App.Apple.csproj"
mkdir -p "$output_directory"
output_directory="$(cd "$output_directory" && pwd -P)"

build_arguments=(
  publish "$project"
  -c Release
  -f "$target_framework"
  -r "$runtime_identifier"
  --nologo
  -p:PeerOnQSignalingUrl="$signaling_url"
  -p:PeerOnQAppleLibVpxPath="$libvpx_path"
  -p:CodesignKey="$PEERONQ_APPLE_CODESIGN_KEY"
  -p:ArchiveOnBuild=true
  -p:CreatePackage=true
  -p:PublishDir="$output_directory/$target/"
)
if [[ "$target_framework" == "net10.0-ios" ]]; then
  build_arguments+=(
    -p:BuildIpa=true
  )
  if [[ -n "$provisioning_profile" ]]; then
    build_arguments+=(
      -p:CodesignProvision="$provisioning_profile"
    )
  fi
  if [[ "$signing_kind" == "development" && -n "$development_bundle_id" ]]; then
    build_arguments+=(
      -p:ApplicationId="$development_bundle_id"
    )
  fi
fi

dotnet "${build_arguments[@]}"

mapfile -t bundles < <(/usr/bin/find "$output_directory/$target" -type d -name '*.app' -prune)
if [[ ${#bundles[@]} -ne 1 ]]; then
  echo "Expected exactly one signed Apple application bundle, found ${#bundles[@]}." >&2
  exit 1
fi
bundle="${bundles[0]}"
/usr/bin/codesign --verify --deep --strict --verbose=2 "$bundle"
signature_details="$(/usr/bin/codesign -dv --verbose=4 "$bundle" 2>&1)"

bundle_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$bundle/Info.plist")"
canonical_version="$(/usr/bin/sed -n 's:.*<PeerOnQWindowsClientVersion>\([^<]*\)</PeerOnQWindowsClientVersion>.*:\1:p' "$repository_root/Directory.Build.props")"
if [[ -z "$canonical_version" || "$bundle_version" != "$canonical_version" ]]; then
  echo "Apple bundle version '$bundle_version' does not match canonical '$canonical_version'." >&2
  exit 1
fi

if [[ "$signing_kind" == "development" ]]; then
  if ! /usr/bin/grep -q '^Authority=Apple Development:' <<<"$signature_details"; then
    echo "The iPhone bundle is not signed by an Apple Development identity." >&2
    exit 1
  fi

  embedded_profile="$bundle/embedded.mobileprovision"
  if [[ ! -f "$embedded_profile" ]]; then
    echo "The development-signed iPhone bundle is missing its embedded provisioning profile." >&2
    exit 1
  fi

  profile_plist="$(/usr/bin/mktemp -t peeronq-ios-development-profile)"
  cleanup_profile_plist() {
    /bin/rm -f "$profile_plist"
  }
  trap cleanup_profile_plist EXIT
  /usr/bin/security cms -D -i "$embedded_profile" >"$profile_plist"

  profile_name="$(/usr/libexec/PlistBuddy -c 'Print :Name' "$profile_plist")"
  profile_uuid="$(/usr/libexec/PlistBuddy -c 'Print :UUID' "$profile_plist")"
  profile_team_identifier="$(/usr/libexec/PlistBuddy -c 'Print :TeamIdentifier:0' "$profile_plist")"
  profile_application_identifier="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:application-identifier' "$profile_plist")"
  profile_get_task_allow="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:get-task-allow' "$profile_plist")"
  profile_devices="$(/usr/libexec/PlistBuddy -c 'Print :ProvisionedDevices' "$profile_plist")"
  bundle_identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$bundle/Info.plist")"
  bundle_team_identifier="$(printf '%s\n' "$signature_details" | /usr/bin/sed -n 's/^TeamIdentifier=//p')"
  profile_bundle_pattern="${profile_application_identifier#*.}"
  normalized_profile_devices="$(printf '%s\n' "$profile_devices" | /usr/bin/sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"

  if [[ -n "$provisioning_profile" && "$profile_name" != "$provisioning_profile" && "$profile_uuid" != "$provisioning_profile" ]]; then
    echo "The embedded development profile does not match PEERONQ_APPLE_PROVISIONING_PROFILE." >&2
    exit 1
  fi
  if [[ "$profile_get_task_allow" != "true" ]]; then
    echo "The embedded iPhone profile is not a development provisioning profile." >&2
    exit 1
  fi
  if [[ -z "$bundle_team_identifier" || "$bundle_team_identifier" != "$profile_team_identifier" ]]; then
    echo "The development certificate team does not match the embedded provisioning profile." >&2
    exit 1
  fi
  if [[ "$profile_bundle_pattern" != "$bundle_identifier" && "$profile_bundle_pattern" != "*" ]]; then
    echo "The embedded provisioning profile does not authorize the PeerOnQ bundle identifier." >&2
    exit 1
  fi
  if ! /usr/bin/grep -Fx -- "$device_udid" <<<"$normalized_profile_devices" >/dev/null; then
    echo "The embedded development profile does not authorize PEERONQ_APPLE_DEVICE_UDID." >&2
    exit 1
  fi
fi

if [[ "$target" == macos-* ]]; then
  if ! /usr/bin/codesign -d --entitlements :- "$bundle" 2>&1 \
      | /usr/bin/grep 'com.apple.security.app-sandbox' >/dev/null; then
    echo "The Mac Catalyst application is missing its sandbox entitlement." >&2
    exit 1
  fi
  echo "Signed Mac Catalyst bundle built at $bundle. Notarization is still required before distribution."
else
  mapfile -t packages < <(/usr/bin/find "$output_directory/$target" -type f -name '*.ipa')
  if [[ ${#packages[@]} -ne 1 ]]; then
    echo "Expected exactly one signed iPhone IPA, found ${#packages[@]}." >&2
    exit 1
  fi
  if [[ "$signing_kind" == "development" ]]; then
    echo "Development-signed iPhone IPA built at ${packages[0]}. It is restricted to registered devices and is not eligible for website or App Store publication."
  else
    echo "Signed iPhone IPA built at ${packages[0]}. Physical-device and App Store gates are still required."
  fi
fi
