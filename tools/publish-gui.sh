#!/usr/bin/env bash
# Publishes the settings window where the Hyper+S shortcut and the startup
# entry expect it: %LOCALAPPDATA%\Programs\AkuWM\akuwm-gui.exe. Not uiAccess,
# not signed, no UAC: a normal-integrity client of the daemon's pipe, so the
# same file serves the dev loop and the installed desk.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${AKUWM_GUI_DIR:-/mnt/c/Users/diego/AppData/Local/Programs/AkuWM}"
stage="$(mktemp -d)"
dotnet publish "$root/src/AkuWM.Gui" -c Release -r win-x64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$stage" --nologo -v q
mkdir -p "$out"
cp "$stage/akuwm-gui.exe" "$out/akuwm-gui.exe"
rm -rf "$stage"
ls -la "$out/akuwm-gui.exe"
