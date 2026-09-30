#!/usr/bin/env bash
# Stages the signed install: the daemon WITH uiAccess, the CLI and the shim
# without, and the tools, in %TEMP%\akuwm-uia -- what uia-install.ps1 (one
# UAC prompt) signs and copies into Program Files. Same one-project-per-stage
# rule as publish-dev.sh (a shared output directory produces a bundle that
# does not run).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${AKUWM_UIA_DIR:-/mnt/c/Users/diego/AppData/Local/Temp/akuwm-uia}"
publish() {
    local project="$1" exe="$2" uiaccess="$3"
    local stage
    stage="$(mktemp -d)"
    dotnet publish "$root/$project" -c Release -r win-x64 --self-contained \
        -p:PublishSingleFile=true -p:UiAccess="$uiaccess" -o "$stage" --nologo -v q
    mkdir -p "$out"
    cp "$stage/$exe" "$out/$exe"
    rm -rf "$stage"
}
publish src/AkuWM.App akuwm.exe true
publish src/AkuWM.Cli akuwm-cli.exe false
publish src/AkuWM.Shim glazewm.exe false
mkdir -p "$out/tools"
cp "$root"/tools/*.ps1 "$root"/tools/*.cmd "$out/tools/"
ls -la "$out"/*.exe
