# AkuWM

A tiling window manager for Windows 11 with its own settings window and its
own CLI: one MIT application, one process, one configuration, with the desk's
tooling (a hotkey script, a bar, a few ops panels for the home lab) hanging
off it instead of standing next to it.

Built for one desk, and shaped by it: three monitors of three DPIs, a Sway
keymap to keep, games with anti-cheat, and a NixOS in WSL where the
configuration lives and the tests run.

**Release: [v0.2.1](https://github.com/akunito/AkuWM/releases/latest)** (2026-09-30).

## What it does

- **Workspaces per monitor**, identified by EDID, so a monitor that naps and
  comes back gets its own workspaces back. Ten per monitor, numbered the way
  Sway's `swaysome` numbers them, switchable from the keyboard, the bar or the
  settings window.
- **Tiling and floating** in the same workspace: a layout tree with gaps,
  splits, resize steps and a Sway keymap; floating windows above the tiled
  ones; sticky windows shown on every workspace of their monitor.
- **Rules by process, class or title**: float, tile, sticky, workspace, size,
  ignore, and `anticheat`, which puts the desk in game mode while such a window
  exists.
- **Fullscreen for games**: a game keeps the screen, its frame timing is not
  touched, and nothing is injected while it has the foreground. Measured with
  PresentMon against the suite in `tests/fullscreen`. Read
  [docs/input-and-anticheat.md](docs/input-and-anticheat.md) before playing
  anything that watches your process list: the daemon observes input, never
  fabricates it over a game, reads no other process's memory and has no macro
  feature.
- **Shortcuts as data**: `Hyper+<letter>` app toggles, launchers and settings
  pages live in the configuration, are edited in the settings window, and
  reach the hotkey script live (no reload, no chord lost).
- **Startup entries** the daemon launches after its pipe answers, in order.
- **A settings window** (`akuwm-gui.exe`, `Hyper+S`) with fourteen sections:
  rules, startup, apps, windows, shortcuts, monitors, tools, profiles, git,
  nodes, docker, monitoring, log, doctor. The last six are the home-lab panels:
  configuration profiles with snapshots and diffs, git sync of the
  configuration, ssh nodes, their Docker daemons, and a Prometheus/Grafana
  dashboard.
- **A CLI** (`akuwm-cli`) for all of it, including `doctor`, `rescue`,
  `debug on|off`, `state`, `bench`, and the ops verbs (`profiles`, `git`,
  `nodes`, `docker`, `monitor`).
- **A way back**: every window it hides or moves is written down before the
  change; `rescue`, a Desktop button, a watchdog, safe mode after two unclean
  runs, and a boot script that only trusts a pipe that answers. See
  [docs/recovery.md](docs/recovery.md).

## Installing

On the machine that will run it, from any PowerShell (one UAC prompt: the
daemon runs with `uiAccess` so chords work over elevated windows, and it is
signed on the machine with a certificate that exists only there):

```powershell
irm https://raw.githubusercontent.com/akunito/AkuWM/main/tools/bootstrap.ps1 | iex
```

The same line updates an existing install. From a checkout,
`tools\bootstrap.ps1 [-Tag v0.2.1]` picks a release. What lands where:

| file | where | what |
|---|---|---|
| `akuwm.exe` | `C:\Program Files\AkuWM` | the daemon (signed, `uiAccess`); never run it from a shell except for `rescue` |
| `akuwm-cli.exe` | `C:\Program Files\AkuWM` (on the PATH) | the client; every command in this README goes through it |
| `akuwm-gui.exe` | `%LOCALAPPDATA%\Programs\AkuWM` | the settings window, started hidden at boot, `Hyper+S` shows it |
| `akuwm-boot.ps1` | `%LOCALAPPDATA%\Programs\AkuWM` | what the Startup folder runs |
| `akuwm-rescue.cmd` | Desktop shortcut "Rescue my desk (AkuWM)" | stops the daemon, gives every window back |

State, logs and the journal: `%LOCALAPPDATA%\akuwm\`.

## Configuration

Two JSON layers in a git repository: `common.json` and `<PROFILE>.json` on top
of it, found under `templates/windows/<PROFILE>/akuwm/` of the dotfiles
checkout (`AKUWM_STATE_DIR` and `AKUWM_PROFILE` override the lookup). The
daemon watches the files; the settings window and the CLI edit them by id,
and `git sync` merges two machines' edits item by item.

```sh
akuwm-cli config show --layer effective
akuwm-cli config validate
akuwm-cli rules for --focused
akuwm-cli query windows
akuwm-cli profiles snapshot create "before the new monitor"
akuwm-cli doctor
```

Sections: `general`, `gaps`, `effects`, `layout`, `monitors`, `workspaces`,
`rules`, `shortcuts`, `startup`, `apps`, `nodes`, `settings`. An option the
build reads but does not act on is reported as a warning at start, never as
an error, and only when it is set.

## Repository

| project | what it is |
|---|---|
| `src/AkuWM.Core` | configuration, rules, the desk model and layout, the pipe protocol, the ops (profiles, git, nodes, docker, prometheus). No Win32: unit-tested on Linux |
| `src/AkuWM.Platform` | Win32, DWM, COM, the input hook, the display and power events (`net8.0-windows`) |
| `src/AkuWM.App` | the daemon: the window-manager thread, the pipe server, the hotkey host, game mode |
| `src/AkuWM.Cli` | the thin client |
| `src/AkuWM.Gui` | the settings window (Avalonia, Rosé Pine) |
| `src/AkuWM.Shim` | a compatibility shim for the bar and the hotkey script, until they speak the daemon's own protocol |
| `tests/AkuWM.Tests` | xUnit, Linux and Windows |
| `tests/AkuWM.Gui.Tests` | xUnit + Avalonia headless |
| `tools/` | `bootstrap.ps1`, `uia-install.ps1` + `install-uiaccess.ps1`, `akuwm-boot.ps1`, the publish scripts, the licence tripwire |
| `docs/` | recovery, input and anti-cheat, trimming |

The driven suites that exercise a real desk (`tests/wm`, 190 cases through
AutoHotkey; `tests/fullscreen`, 48 cases with PresentMon) live in the dotfiles
repository next to the configuration, together with the plan that carries
every measured Windows fact.

## Building

.NET 8, from WSL or from Windows:

```sh
dotnet build AkuWM.sln
dotnet test tests/AkuWM.Tests/AkuWM.Tests.csproj
dotnet test tests/AkuWM.Gui.Tests/AkuWM.Gui.Tests.csproj
tools/publish-uia.sh        # stages a signed install in %TEMP%\akuwm-uia; then uia-install.ps1
tools/publish-dev.sh        # an unsigned daemon for the no-UAC dev loop
```

A tag `v*` builds a release: tests, four single-file publishes, a check that
the daemon carries the `uiAccess` manifest, a zip with a sha256.

## Licence

MIT. The programs it replaced were GPL; AkuWM contains no line of them, and
[LICENSING.md](LICENSING.md) is the procedure that keeps it so, checked in CI.
