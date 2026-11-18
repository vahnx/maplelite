#!/usr/bin/env bash
# -----------------------------------------------------------------------------
#  build.sh: build a standalone MapleLite on Linux (Debian/Ubuntu) or macOS.
#
#  Run it from a terminal:   ./build.sh
#  (On a Mac you can double-click build-macos.command instead; it runs this.)
#
#  Result (Linux x64 shown; the name follows your OS and CPU):
#    bin/MapleLite-linux-x64/MapleLite        runs without .NET installed
#    bin/MapleLite-linux-x64.tar.gz           the same folder, packed to share
#  On macOS: bin/MapleLite-macos-arm64/ and bin/MapleLite-macos-arm64.zip
#
#  Needs the .NET 8 SDK (or newer) to build. If it's missing, the script offers to install it
#  just for your user (into ~/.dotnet) with Microsoft's official installer.
#
#  Options:  --rid <rid>   build for another target, e.g. osx-x64 or linux-arm64
# -----------------------------------------------------------------------------
set -euo pipefail

cd "$(dirname "$0")"
REPO="$(pwd)"

say()  { printf '%s\n' "$*"; }
fail() { printf '\n%s\n' "$*" >&2; exit 1; }

# 1. Which platform and CPU.
OS="$(uname -s)"
ARCH="$(uname -m)"
case "$OS" in
    Darwin) OS_RID=osx;   OS_NAME=macos ;;
    Linux)  OS_RID=linux; OS_NAME=linux ;;
    *) fail "This script is for Linux and macOS. On Windows, run build-windows.cmd." ;;
esac
case "$ARCH" in
    x86_64|amd64)  ARCH_RID=x64 ;;
    arm64|aarch64) ARCH_RID=arm64 ;;
    *) fail "Unsupported CPU: $ARCH" ;;
esac
RID="$OS_RID-$ARCH_RID"
if [ "${1:-}" = "--rid" ] && [ -n "${2:-}" ]; then RID="$2"; fi
case "$RID" in
    osx-*)   NAME="MapleLite-macos-${RID#osx-}" ;;
    linux-*) NAME="MapleLite-linux-${RID#linux-}" ;;
    *) fail "This script builds osx-* or linux-* targets; use build-windows.cmd for Windows." ;;
esac
OUT="$REPO/bin/$NAME"

say ""
say " MapleLite standalone build ($RID)"
say " ------------------------------------------"
say ""

# 2. The .NET SDK (a per-user install in ~/.dotnet counts too).
if [ -x "$HOME/.dotnet/dotnet" ] && ! command -v dotnet >/dev/null 2>&1; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi

have_sdk() {
    command -v dotnet >/dev/null 2>&1 || return 1
    local major
    major="$(dotnet --list-sdks 2>/dev/null | sed -n 's/^\([0-9][0-9]*\)\..*/\1/p' | sort -n | tail -1)"
    [ -n "$major" ] && [ "$major" -ge 8 ]
}

if ! have_sdk; then
    say " The .NET 8 SDK isn't installed. It's free, from Microsoft, and only needed to build."
    say " It can be installed just for your user into ~/.dotnet (no admin rights needed)."
    printf ' Install it now? [y/N] '
    read -r answer || answer=""
    case "$answer" in
        y|Y|yes|YES) ;;
        *) fail " Install it from https://dotnet.microsoft.com/download/dotnet/8.0 then run this script again." ;;
    esac
    installer="$(mktemp -t dotnet-install.XXXXXX)"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    elif command -v wget >/dev/null 2>&1; then
        wget -q https://dot.net/v1/dotnet-install.sh -O "$installer"
    else
        fail " Neither curl nor wget is installed. On Debian/Ubuntu: sudo apt install curl"
    fi
    bash "$installer" --channel 8.0 --install-dir "$HOME/.dotnet"
    rm -f "$installer"
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
    have_sdk || fail " The .NET install didn't finish. See the messages above."
    say ""
    say " .NET installed in ~/.dotnet. To use it in other terminals, add this to ~/.bashrc or ~/.zshrc:"
    say "   export DOTNET_ROOT=\"\$HOME/.dotnet\"; export PATH=\"\$HOME/.dotnet:\$PATH\""
    say ""
fi

# 3. Publish a self-contained copy (the .NET runtime is included, plugins are copied beside it).
say " Building... (the first build downloads packages and takes a few minutes)"
rm -rf "$OUT" "$REPO/bin/$NAME.tar.gz" "$REPO/bin/$NAME.zip"

publish() {
    dotnet publish "$REPO/src/MapleLite.Host" -f net8.0 -r "$RID" -c Release --self-contained \
        -o "$OUT" -nologo -v minimal \
        -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true \
        "-p:ArtifactsPath=$REPO/artifacts/standalone"
}
if ! publish; then
    # A stuck compiler server can report files that exist as missing. Restart it and try once more.
    say ""
    say " The build failed; restarting the .NET build servers and trying once more..."
    dotnet build-server shutdown >/dev/null 2>&1 || true
    publish || fail " The build didn't finish. See the messages above."
fi
[ -f "$OUT/MapleLite" ] || fail " The build finished but MapleLite is missing from $OUT"
chmod +x "$OUT/MapleLite"

# 4. Pack it for sharing.
say ""
say " Packing..."
cd "$REPO/bin"
if [ "$OS" = "Darwin" ]; then
    # Builds made here aren't quarantined, but clear the flag in case the repo came from a download.
    xattr -dr com.apple.quarantine "$NAME" 2>/dev/null || true
    ditto -c -k --keepParent "$NAME" "$NAME.zip" && ARCHIVE="$NAME.zip"
else
    tar -czf "$NAME.tar.gz" "$NAME" && ARCHIVE="$NAME.tar.gz"
fi

say ""
say " Done."
say "   Run:    ./bin/$NAME/MapleLite"
[ -n "${ARCHIVE:-}" ] && say "   Share:  bin/$ARCHIVE"
if [ "$OS" = "Linux" ]; then
    say ""
    say " On a fresh Debian/Ubuntu, the window needs a few desktop libraries:"
    say "   sudo apt install libx11-6 libice6 libsm6 libfontconfig1"
fi
say ""
