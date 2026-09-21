#!/usr/bin/env bash
# Publishes the three executables where the live scripts expect them, without
# uiAccess so they can be run from a normal shell and their output redirected.
# The installed, signed copy in Program Files is what `install-uiaccess.ps1`
# makes; this is the dev loop.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
win_temp="${AKUWM_DEV_DIR:-/mnt/c/Users/diego/AppData/Local/Temp}"

# Each project into its OWN directory, then the one executable is copied where
# the scripts look for it. Two single-file publishes sharing an output
# directory produce a bundle that does not run: the second overwrites the
# host files of the first, and the exe then dies with "The application to
# execute does not exist: akuwm-cli.dll" (measured 2026-09-21, after months of
# this script getting away with it).
publish() {
    local project="$1" exe="$2" out="$3"
    local stage
    stage="$(mktemp -d)"
    dotnet publish "$root/$project" -c Release -r win-x64 --self-contained \
        -p:PublishSingleFile=true -p:UiAccess=false -o "$stage" --nologo -v q
    mkdir -p "$out"
    cp "$stage/$exe" "$out/$exe"
    rm -rf "$stage"
}

publish src/AkuWM.App akuwm.exe "$win_temp/akuwm-m2"
publish src/AkuWM.Cli akuwm-cli.exe "$win_temp/akuwm-m2"
publish src/AkuWM.Shim glazewm.exe "$win_temp/akuwm-m2-shim"

ls -la "$win_temp/akuwm-m2"/*.exe "$win_temp/akuwm-m2-shim"/*.exe
