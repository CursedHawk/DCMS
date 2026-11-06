#!/bin/sh
# Installs dcms, the DCMS command line (ADR 0032), from the console that served this script:
#   curl -fsSL __DCMS_ORIGIN__/downloads/install.sh | sh
# Linux and macOS, x64 and arm64. Puts it in $DCMS_INSTALL_DIR, else ~/.local/bin.
set -eu

server="__DCMS_ORIGIN__"
dir="${DCMS_INSTALL_DIR:-$HOME/.local/bin}"

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  *) echo "dcms: no build for $(uname -s); on Windows use install.ps1." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) echo "dcms: no build for $(uname -m)." >&2; exit 1 ;;
esac
name="dcms-$os-$arch"

fetch() { if command -v curl >/dev/null 2>&1; then curl -fsSL "$1" -o "$2"; else wget -qO "$2" "$1"; fi; }
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
fetch "$server/downloads/$name" "$tmp/dcms"
fetch "$server/downloads/SHA256SUMS" "$tmp/SHA256SUMS"

expected="$(awk -v n="$name" '$2 == n || $2 == "*" n { print $1 }' "$tmp/SHA256SUMS")"
if command -v sha256sum >/dev/null 2>&1; then actual="$(sha256sum "$tmp/dcms" | awk '{ print $1 }')"
else actual="$(shasum -a 256 "$tmp/dcms" | awk '{ print $1 }')"; fi
if [ -z "$expected" ] || [ "$expected" != "$actual" ]; then
  echo "dcms: the download is not what $server's SHA256SUMS says; not installed." >&2
  exit 1
fi

mkdir -p "$dir"
chmod 755 "$tmp/dcms"
if [ "$os" = osx ]; then
  # Downloaded with curl it carries no quarantine; this only keeps a browser download working too.
  xattr -d com.apple.quarantine "$tmp/dcms" 2>/dev/null || true
fi
mv "$tmp/dcms" "$dir/dcms"
echo "Installed dcms $("$dir/dcms" --version) to $dir/dcms."
case ":$PATH:" in
  *":$dir:"*) ;;
  *) echo "Add $dir to your PATH, e.g. echo 'export PATH=\"$dir:\$PATH\"' >> ~/.profile" ;;
esac
echo "Next: dcms login --server $server"
