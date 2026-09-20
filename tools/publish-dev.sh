#!/usr/bin/env bash
# Publishes the three executables where the live scripts expect them, without
# uiAccess so they can be run from a normal shell and their output redirected.
# The installed, signed copy in Program Files is what `install-uiaccess.ps1`
# makes; this is the dev loop.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
win_temp="${AKUWM_DEV_DIR:-/mnt/c/Users/diego/AppData/Local/Temp}"

publish() {
    local project="$1" out="$2"
    dotnet publish "$root/$project" -c Release -r win-x64 --self-contained \
        -p:PublishSingleFile=true -p:UiAccess=false -o "$out" --nologo -v q
}

publish src/AkuWM.App "$win_temp/akuwm-m2"
publish src/AkuWM.Cli "$win_temp/akuwm-m2"
publish src/AkuWM.Shim "$win_temp/akuwm-m2-shim"

ls -la "$win_temp/akuwm-m2"/*.exe "$win_temp/akuwm-m2-shim"/*.exe
