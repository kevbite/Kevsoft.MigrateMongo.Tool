#!/bin/sh
# Installs the migrate-mongo Native AOT binary. No .NET needed.
#
#   curl -fsSL https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.sh | sh
#   curl -fsSL .../install.sh | sh -s -- --version 0.2.0 --install-dir /usr/local/bin
#
# Options (or environment variables):
#   --version <v>        MIGRATE_MONGO_VERSION        release to install, e.g. 0.2.0 (default: latest)
#   --install-dir <dir>  MIGRATE_MONGO_INSTALL_DIR    default: /usr/local/bin as root, otherwise ~/.local/bin
#   --download-url <u>   MIGRATE_MONGO_DOWNLOAD_URL   base URL of the release assets (for mirrors and testing)
set -eu

repository="kevbite/Kevsoft.MigrateMongo.Tool"
version="${MIGRATE_MONGO_VERSION:-}"
install_dir="${MIGRATE_MONGO_INSTALL_DIR:-}"
download_url="${MIGRATE_MONGO_DOWNLOAD_URL:-}"

fail() {
  echo "migrate-mongo install: $*" >&2
  exit 1
}

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || fail "--version needs a value"; version="$2"; shift 2 ;;
    --install-dir) [ $# -ge 2 ] || fail "--install-dir needs a value"; install_dir="$2"; shift 2 ;;
    --download-url) [ $# -ge 2 ] || fail "--download-url needs a value"; download_url="$2"; shift 2 ;;
    -h|--help)
      echo "Usage: install.sh [--version <v>] [--install-dir <dir>] [--download-url <url>]"
      echo "Environment: MIGRATE_MONGO_VERSION, MIGRATE_MONGO_INSTALL_DIR, MIGRATE_MONGO_DOWNLOAD_URL"
      exit 0 ;;
    *) fail "unknown option '$1'" ;;
  esac
done

version="${version#v}"
if [ -z "$download_url" ]; then
  if [ -z "$version" ] || [ "$version" = "latest" ]; then
    download_url="https://github.com/$repository/releases/latest/download"
  else
    download_url="https://github.com/$repository/releases/download/v$version"
  fi
fi
download_url="${download_url%/}"

case "$(uname -s)" in
  Linux) os="linux" ;;
  Darwin) os="osx" ;;
  *) fail "$(uname -s) isn't supported. On Windows use install.ps1, or install the .NET tool: dotnet tool install -g Kevsoft.MigrateMongo.Tool" ;;
esac

case "$(uname -m)" in
  x86_64|amd64) arch="x64" ;;
  aarch64|arm64) arch="arm64" ;;
  *) fail "$(uname -m) isn't supported. Install the .NET tool instead: dotnet tool install -g Kevsoft.MigrateMongo.Tool" ;;
esac

if [ "$os" = "linux" ] && { [ -f /etc/alpine-release ] || ls /lib/ld-musl-* >/dev/null 2>&1; }; then
  os="linux-musl"
fi

rid="$os-$arch"
asset="migrate-mongo-$rid.tar.gz"

if [ -z "$install_dir" ]; then
  if [ "$(id -u)" = "0" ]; then
    install_dir="/usr/local/bin"
  else
    [ -n "${HOME:-}" ] || fail "HOME isn't set; pass --install-dir"
    install_dir="$HOME/.local/bin"
  fi
fi

download() {
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL --retry 3 -o "$2" "$1"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$2" "$1"
  else
    fail "curl or wget is needed to download migrate-mongo"
  fi
}

temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT INT TERM

echo "Downloading $download_url/$asset"
download "$download_url/$asset" "$temp/$asset" || fail "couldn't download $download_url/$asset"
download "$download_url/SHA256SUMS" "$temp/SHA256SUMS" || fail "couldn't download $download_url/SHA256SUMS"

expected="$(awk -v name="$asset" '$2 == name || $2 == "*" name { print $1 }' "$temp/SHA256SUMS")"
[ -n "$expected" ] || fail "$asset isn't listed in SHA256SUMS"
if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$temp/$asset" | awk '{ print $1 }')"
elif command -v shasum >/dev/null 2>&1; then
  actual="$(shasum -a 256 "$temp/$asset" | awk '{ print $1 }')"
else
  fail "sha256sum or shasum is needed to verify the download"
fi
[ "$expected" = "$actual" ] || fail "checksum mismatch for $asset (expected $expected, got $actual)"

tar -xzf "$temp/$asset" -C "$temp" migrate-mongo
mkdir -p "$install_dir"
# Install via a temporary name so a running copy isn't overwritten in place.
cp "$temp/migrate-mongo" "$install_dir/.migrate-mongo.new"
chmod 755 "$install_dir/.migrate-mongo.new"
mv -f "$install_dir/.migrate-mongo.new" "$install_dir/migrate-mongo"

echo "Installed $("$install_dir/migrate-mongo" version) to $install_dir/migrate-mongo"
case ":${PATH:-}:" in
  *":$install_dir:"*) ;;
  *) echo "Add $install_dir to your PATH to run migrate-mongo, e.g. export PATH=\"$install_dir:\$PATH\"" ;;
esac
