#!/usr/bin/env bash
# A cheap check that no GPL source was transcribed into AkuWM.
#
# It greps the tree for identifiers that exist only inside GlazeWM, Zebar or
# AutoHotkey. Finding one does not prove anything was copied, and finding none
# proves even less -- the real guarantee is the procedure in LICENSING.md. What
# this catches is the accident: a name pasted along with an idea, which is
# exactly how a clean room stops being one.
#
# Exclusions: the compat layer, where GlazeWM's protocol strings legitimately
# appear (they are a wire format AkuWM imitates for Zebar and the test suites),
# and the test fixtures, which are copies of this desk's own configuration.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

patterns=(
  platform_sync
  redraw_containers
  WindowZOrder
  windows_to_bring_to_front
  glzr
  hide_method
  A_TickCount
  A_ScriptDir
  DllCall
  WinGetPos
  swaymsg
)

paths=(src tests)
excludes=(
  ':!src/AkuWM.App/Compat'
  ':!src/AkuWM.Core/Compat'
  ':!tests/AkuWM.Tests/Fixtures'
  ':!tools/licence-tripwire.sh'
)

found=0
for pattern in "${patterns[@]}"; do
  if git grep -n -I -- "$pattern" "${paths[@]}" "${excludes[@]}" 2>/dev/null; then
    echo "  ^ '$pattern' belongs to a program AkuWM replaces, not to AkuWM" >&2
    found=1
  fi
done

if [ "$found" -ne 0 ]; then
  echo >&2
  echo "licence tripwire: see LICENSING.md. If the name is genuinely AkuWM's," >&2
  echo "rename it; if it is a protocol string, it belongs in AkuWM.App/Compat." >&2
  exit 1
fi

echo "licence tripwire: clean (${#patterns[@]} patterns over ${paths[*]})"
