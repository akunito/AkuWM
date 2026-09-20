# AkuWM

A tiling window manager for Windows 11 that is also its own hotkey daemon and
its own configuration UI — one MIT application in place of three programs that
had to agree with each other.

**Status: M1.** AkuWM can see the desk — every window, every monitor by its
EDID, and what it would do with each — and it changes nothing on it. Its view
was compared with the window manager actually in charge, 270 times over 45
minutes of real use, and agreed every time. Taking over is M2. The plan,
milestone by milestone, lives in the dotfiles repository at
`docs/akunito/infrastructure/desk-w11-akuwm-plan.md`.

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
