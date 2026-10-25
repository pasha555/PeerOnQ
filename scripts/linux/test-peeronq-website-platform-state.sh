#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/../.." && pwd)
state_script="$repo_root/src/PeerOnQ.Infrastructure.Deployment/scripts/website-platform-state.sh"
installer_script="$repo_root/scripts/linux/peeronq-server-installer.sh"
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT HUP INT TERM
website="$temporary/website"

mkdir -p "$website/releases/1.2.3"
ln -s releases/1.2.3 "$website/current"

run_action() {
  PEERONQ_WEBSITE_ROOT="$website" PEERONQ_WEBSITE_PLATFORM_ACTION=$1 sh "$state_script"
}

run_action deactivate
[ ! -e "$website/current" ] && [ ! -L "$website/current" ]
[ "$(readlink "$website/.platform-release-current")" = releases/1.2.3 ]
[ -d "$website/releases/1.2.3" ]

run_action deactivate
run_action restore
[ "$(readlink "$website/current")" = releases/1.2.3 ]
[ ! -e "$website/.platform-release-current" ] && [ ! -L "$website/.platform-release-current" ]

run_action deactivate
run_action commit
[ ! -e "$website/current" ] && [ ! -L "$website/current" ]
[ ! -e "$website/.platform-release-current" ] && [ ! -L "$website/.platform-release-current" ]
[ -d "$website/releases/1.2.3" ]

ln -s ../outside "$website/current"
if run_action deactivate >"$temporary/unsafe.out" 2>&1; then
  printf 'Unsafe website release link unexpectedly passed.\n' >&2
  exit 1
fi
grep -Fq 'escaped controlled storage' "$temporary/unsafe.out"
[ "$(readlink "$website/current")" = ../outside ]

grep -Fq "expected_download_path='/downloads/PeerOnQ-Windows-x64.msi'" "$installer_script"
if grep -Fq 'expected_ui_label=' "$installer_script"; then
  printf 'Installer still depends on obsolete website release labels.\n' >&2
  exit 1
fi

printf 'Website platform state transaction tests passed.\n'
