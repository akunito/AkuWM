# AkuWM

MIT tiling window manager for Windows 11, replacing GlazeWM + AutoHotkey on DESK_W11.
Plan: `~/.dotfiles/docs/akunito/infrastructure/desk-w11-akuwm-plan.md`.

## Layout

- `AkuWM.Core` — all logic, no Win32, tested on Linux. `Desk/` model, `Layout/` tiling
  tree, `Compat/` the GlazeWM-compatible IPC, `State/` the files that survive a crash,
  `Wm/` the single-threaded loop.
- `AkuWM.Platform` — Win32/COM/DWM. No unit tests (no desktop in CI); covered by the
  spikes and `tools/smoke-live.ps1` on the real machine.
- `AkuWM.App` — host and daemon. `AkuWM.Shim` — `glazewm.exe`, the drop-in CLI.

## Rules

- **Clean room.** Never read GlazeWM, glazewm-js, Zebar or AutoHotkey source. Allowed:
  Microsoft docs, PowerToys, Ciantic/VirtualDesktopAccessor, MScholtes/VirtualDesktop,
  CsWin32. `tools/licence-tripwire.sh` catches slips; protocol strings live in `Compat/`.
- **Code is read by AI, not humans.** Performance first, always. Comments only for what a
  future session needs: a decision and its reason, or a measured platform gotcha. Keep
  those verbatim; drop the exposition.
- **Budget: a gesture answers in under 5 ms.** The stack this replaces cost 47–110 ms.
  Measure with `akuwm bench`; say which numbers are measured and which are estimates.
- **Hiding a window has no undo.** Anything that can leave a window cloaked with nobody
  who knows is the worst class of bug here. Records are written before the change they
  describe, and read back after it.
- **Everything the person could want to change is configuration, not code.** The GUI in
  M5 edits the JSON; anything baked into C# is something the GUI can never offer, so a
  new behaviour arrives with its config key, its default, its validation and its entry in
  the effective-config round trip. A magic number in `Desk/` or `Layout/` is a bug
  report waiting to happen. Two questions for every option: does it survive a save, and
  does it take effect on `wm-reload-config` or only on a restart? The GUI has to say
  which, so the code has to know.
- Every change ships with its tests: `dotnet test tests/AkuWM.Tests/AkuWM.Tests.csproj`.

## Measured Windows facts — do not rediscover these

- `SetCloak(Shell, 1)` hides a window and **nothing** brings it back. The reversible pair
  is `(Default, 1)` ↔ `(Default, 0)`. Spike `s8`.
- `DeferWindowPos` refuses `SWP_ASYNCWINDOWPOS` with ERROR_INVALID_PARAMETER; the batch
  handle comes back null and every move in it is lost silently. `SetWindowPos` takes the
  same flag happily. Spike `s9`.
- An `async` loop is not one thread: after each `await` it resumes on the pool, and Win32
  state is thread-affine. The wm loop is a real thread with a blocking queue.
- A window that lands within ~32 px of where it was put is where it was put (a terminal
  rounds to character cells).
- `IVirtualDesktopManager::IsWindowOnCurrentVirtualDesktop` lies on this build.
- A uiAccess process cannot have its stdout redirected — use `akuwm <cmd> --out <file>` —
  and cannot be launched from WSL interop.
- Two manifests (uiAccess true/false) need separate `IntermediateOutputPath`.
- `powershell.exe` is Windows PowerShell 5.1: no ternaries in install scripts.
- `JsonArray.Add(x)` builds a node that cannot serialise without a TypeInfoResolver; cast
  to `(JsonNode)`.
- A **system-DPI-aware window** (Notepad++) whose OUTER rectangle -- frame plus the
  invisible 9 px border -- touches a monitor of another scale by ONE pixel is rescaled
  by Windows on the spot (3840 wide at x=0 fine, 3841 → 5761; 1000 wide straddling the
  seam → 2460). Measured 2026-09-22. `WindowSnapshot.PerMonitorDpi` is false for such
  windows and every placement is pulled inside the screen for them.

## Build

From WSL: `dotnet publish src/AkuWM.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish`.
Dev loop needs `-p:UiAccess=false` (a uiAccess binary will not start outside Program Files).
