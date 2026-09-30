# AkuWM

A tiling window manager for Windows 11 that is also its own hotkey daemon and
its own configuration UI — one MIT application in place of three programs that
had to agree with each other.

**Status: M6 landed (2026-09-30).** AkuWM has had the desk since 2026-09-21:
tiling and floating layouts on named workspaces bound to monitors by EDID,
sticky windows, fullscreen handling built for games, a signed `uiAccess`
install so chords work over elevated windows, a GlazeWM-compatible IPC and
`glazewm` shim so the existing AutoHotkey script and Zebar kept working
unchanged, and a settings window (`akuwm-gui.exe`, `Hyper+S`) with fourteen
sections -- rules, startup, apps, windows, shortcuts, monitors, tools,
profiles, git, nodes, docker, monitoring, log, doctor. The plan, milestone by
milestone and with every measured Windows fact, lives in the dotfiles
repository at `docs/akunito/infrastructure/desk-w11-akuwm-plan.md`.

## Installing

From a GitHub release, on the machine that will run it (one UAC prompt: the
daemon is signed there with a certificate that exists only there):

```powershell
irm https://raw.githubusercontent.com/akunito/AkuWM/main/tools/bootstrap.ps1 | iex
```

Or from a checkout: `powershell -ExecutionPolicy Bypass -File tools\bootstrap.ps1
[-Tag v0.2.0]`. It downloads `akuwm-<tag>-win-x64.zip`, unpacks it into
`%TEMP%\akuwm-uia` and runs `uia-install.ps1`, which stops the running daemon,
signs and installs `akuwm.exe`, `akuwm-cli.exe` and `glazewm.exe` into
`C:\Program Files\AkuWM`, puts `akuwm-gui.exe` into `%LOCALAPPDATA%\Programs\AkuWM`,
points the Startup folder at the boot script and starts the daemon again.
Getting the desk back if anything goes wrong: `docs/recovery.md`.

## Why

The desk it replaces ran GlazeWM (the window manager), AutoHotkey (the chords,
the app toggles, the layout repair) and a hand-edited configuration split
across both. Every order one gave the other was a **47 ms** CLI round trip, and
it acted on a state that could already be stale — half the bugs fixed in the
week before this was started were exactly that. In one process those are
function calls, and what is left is the Win32 work itself.

## Layout

| project | what it is |
|---|---|
| `AkuWM.Core` | the configuration, the rule matcher, the importers, the model. No Win32, so it is unit-tested on Linux |
| `AkuWM.Platform` | Win32, COM and DWM. `net8.0-windows`; the shape is here, the code lands at M1 |
| `AkuWM.App` | the host: the pipe server, the commands, and from M2 the window manager and the IPC on 6123 |
| `AkuWM.Cli` | the thin client the `glazewm` shim calls |
| `AkuWM.Tests` | xUnit, Linux |

## Building

From WSL or from Windows, with .NET 8:

```sh
dotnet build AkuWM.sln
dotnet test tests/AkuWM.Tests/AkuWM.Tests.csproj
dotnet publish src/AkuWM.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

On NixOS the SDK comes from the dotfiles flag `dotnetDevEnable`.

## Using what exists

```sh
akuwm config import glazewm     # today's GlazeWM + AutoHotkey setup → common.json
akuwm config validate
akuwm doctor
akuwm daemon                    # the pipe, and from M1 the window manager
```

The configuration is two JSON layers in the dotfiles repository —
`templates/windows/DESK_W11/akuwm/common.json` and `DESK_W11.json` on top of it
— found through `AKUWM_STATE_DIR`. Logs and the journal live in
`%LOCALAPPDATA%\akuwm\`.

## Games

AkuWM watches the keyboard and runs with `uiAccess`, so it is worth knowing
exactly what it does to input before playing anything with an anti-cheat:
[docs/input-and-anticheat.md](docs/input-and-anticheat.md). The short version
is that it observes, never fabricates input while a game is in front, never
reads another process's memory, and has no macro features at all.

## Licence

MIT. AkuWM replaces GPL programs and contains no line of them; the procedure
that keeps that true is in [LICENSING.md](LICENSING.md) and is checked in CI.
