#!/usr/bin/env bash
# build-macos.command: double-click in Finder to build a standalone MapleLite for this Mac
# (Apple silicon or Intel, picked automatically). It runs build.sh in a Terminal window.
#
# Result: bin/MapleLite-macos-arm64/MapleLite (and a .zip beside it to share).
#
# If macOS says the file can't be opened because it's from an unidentified developer,
# right-click it, choose Open, then Open again. If it says you don't have permission, run once in Terminal:
#   chmod +x build-macos.command build.sh
cd "$(dirname "$0")"
bash ./build.sh "$@"
status=$?
echo
read -r -p " Press Enter to close this window. " _ || true
exit $status
