#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
installer="$script_dir/peeronq-server-installer.sh"
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT HUP INT TERM

stubs="$temporary/stubs"
mkdir -p "$stubs"

real_id=$(command -v id)
real_stat=$(command -v stat)
cat > "$stubs/id" <<EOF
#!/bin/sh
if [ "\${1-}" = "-u" ]; then
  printf '0\n'
  exit 0
fi
exec "$real_id" "\$@"
EOF
cat > "$stubs/stat" <<EOF
#!/bin/sh
if [ "\${1-}" = "-c" ] && [ "\${2-}" = "%u" ]; then
  printf '0\n'
  exit 0
fi
exec "$real_stat" "\$@"
EOF
for command_name in chown flock openssl; do
  cat > "$stubs/$command_name" <<'EOF'
#!/bin/sh
exit 0
EOF
done
cat > "$stubs/uname" <<'EOF'
#!/bin/sh
printf 'x86_64\n'
EOF
cat > "$stubs/docker" <<'EOF'
#!/bin/sh
[ "${1-}" = "compose" ] && [ "${2-}" = "version" ]
EOF
chmod 755 "$stubs"/*

render_installer() {
  version=$1
  sandbox=$2
  output=$3
  mkdir -p "$sandbox/opt" "$sandbox/etc" "$sandbox/run" "$sandbox/var/log"
  awk \
    -v version="$version" \
    -v install_root="$sandbox/opt/peeronq" \
    -v env_file="$sandbox/etc/peeronq/peeronq.env" \
    -v lock_file="$sandbox/run/peeronq-server-install.lock" \
    -v log_root="$sandbox/var/log/peeronq/installer" '
      /^INSTALLER_VERSION=/ {
        print "INSTALLER_VERSION=\"" version "\""
        next
      }
      /^INSTALL_ROOT=/ { print "INSTALL_ROOT=\"" install_root "\""; next }
      /^ENV_FILE=/ { print "ENV_FILE=\"" env_file "\""; next }
      /^LOCK_FILE=/ { print "LOCK_FILE=\"" lock_file "\""; next }
      /^LOG_ROOT=/ { print "LOG_ROOT=\"" log_root "\""; next }
      /^temporary=\$\(mktemp -d / {
        print "mkdir \"$target\""
        print "quarantine_incomplete_release \"$target\""
        print "mkdir \"$target\""
        print "quarantine_incomplete_release \"$target\""
        print "exit 0"
        injected++
        next
      }
      { print }
      END { if (injected != 1) exit 1 }
    ' "$installer" > "$output"
  chmod 755 "$output"
}

expect_invalid_version() {
  version=$1
  label=$2
  sandbox="$temporary/$label"
  candidate="$temporary/$label.run"
  render_installer "$version" "$sandbox" "$candidate"
  if PATH="$stubs:$PATH" "$candidate" --bootstrap >"$temporary/$label.out" 2>&1; then
    printf 'Tampered installer version unexpectedly succeeded: %s\n' "$version" >&2
    exit 1
  fi
  grep -Fq 'the embedded release version is invalid' "$temporary/$label.out" || {
    printf 'Tampered installer version did not fail at semantic validation: %s\n' "$version" >&2
    exit 1
  }
  [ ! -e "$sandbox/opt/peeronq" ] || {
    printf 'Tampered installer version mutated the install root: %s\n' "$version" >&2
    exit 1
  }
}

expect_invalid_version '.' dot
expect_invalid_version '..' dotdot

valid_sandbox="$temporary/valid"
valid_installer="$temporary/valid.run"
render_installer '1.2.3-rc.1' "$valid_sandbox" "$valid_installer"
PATH="$stubs:$PATH" "$valid_installer" --bootstrap >/dev/null

retries=$(find "$valid_sandbox/opt/peeronq/failed-releases" \
  -mindepth 2 -maxdepth 2 -type d -name release | wc -l | tr -d '[:space:]')
[ "$retries" = "2" ] || {
  printf 'Valid same-version retry did not quarantine exactly two incomplete releases.\n' >&2
  exit 1
}
[ ! -e "$valid_sandbox/opt/peeronq/releases/1.2.3-rc.1" ] || {
  printf 'Valid retry left the incomplete release target in place.\n' >&2
  exit 1
}

printf 'Server installer semantic-version and containment tests passed.\n'
