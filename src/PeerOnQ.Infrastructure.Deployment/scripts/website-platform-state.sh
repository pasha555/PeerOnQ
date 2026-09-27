#!/bin/sh
set -eu

fail() {
  printf 'PeerOnQ website platform state: %s\n' "$1" >&2
  exit 1
}

website_root=${PEERONQ_WEBSITE_ROOT:-/website}
current="$website_root/current"
backup="$website_root/.platform-release-current"

case "$website_root" in /*) ;; *) fail "website root must be absolute" ;; esac
[ -d "$website_root" ] && [ ! -L "$website_root" ] || fail "website root is missing or unsafe"
[ -d "$website_root/releases" ] && [ ! -L "$website_root/releases" ] \
  || fail "website releases directory is missing or unsafe"

validate_release_link() {
  link_path=$1
  [ -L "$link_path" ] || fail "website state path is not a symbolic link"
  target=$(readlink "$link_path")
  case "$target" in releases/*) ;; *) fail "website release link escaped controlled storage" ;; esac
  version=${target#releases/}
  case "$version" in ''|.*|*.|*..*|*/*|*[!0-9.]*) fail "website release link version is invalid" ;; esac
  [ "${#version}" -le 64 ] || fail "website release link version is too long"
  [ -d "$website_root/$target" ] && [ ! -L "$website_root/$target" ] \
    || fail "website release link target is missing or unsafe"
}

path_exists() {
  [ -e "$1" ] || [ -L "$1" ]
}

case "${PEERONQ_WEBSITE_PLATFORM_ACTION:-}" in
  deactivate)
    if path_exists "$backup"; then
      validate_release_link "$backup"
      path_exists "$current" && fail "current and platform backup links both exist"
      exit 0
    fi
    if [ -L "$current" ]; then
      validate_release_link "$current"
      mv "$current" "$backup"
    elif [ -e "$current" ]; then
      fail "current website state is not a symbolic link"
    fi
    ;;
  restore)
    if [ -L "$backup" ]; then
      validate_release_link "$backup"
      path_exists "$current" && fail "current website state changed during platform rollback"
      mv "$backup" "$current"
    elif [ -e "$backup" ]; then
      fail "platform website backup is not a symbolic link"
    fi
    ;;
  commit)
    if [ -L "$backup" ]; then
      validate_release_link "$backup"
      path_exists "$current" && fail "current website state changed during platform activation"
      rm "$backup"
    elif [ -e "$backup" ]; then
      fail "platform website backup is not a symbolic link"
    fi
    ;;
  *)
    fail "action must be deactivate, restore, or commit"
    ;;
esac
