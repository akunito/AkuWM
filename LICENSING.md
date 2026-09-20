# Licensing and the clean room

AkuWM is **MIT** (see `LICENSE`). It replaces three programs that are not:
GlazeWM 3 (GPL-3.0, and a private fork of it), AutoHotkey 2 (GPL-2.0) and, in
part, Zebar (GPL-3.0). Copying any of them into this repository would make
AkuWM GPL, so none of it is copied. This file is the rule, and it is checked on
every push.

## The rule

> A commit that needed a forbidden source to write is not made.

## What AkuWM may be written from

- **Microsoft's documentation and headers**: `SetWindowsHookEx`,
  `SetWinEventHook`, `SetWindowPos` / `DeferWindowPos`, `DwmGetWindowAttribute`
  (`DWMWA_CLOAKED`, `DWMWA_EXTENDED_FRAME_BOUNDS`, `DWMWA_BORDER_COLOR`),
  `ITaskbarList2::MarkFullscreenWindow`, `SystemParametersInfo`,
  `DisplayConfigGetDeviceInfo`, `WM_DISPLAYCHANGE`, `WM_POWERBROADCAST`.
- **MIT-licensed sources for the undocumented pieces**:
  [Ciantic/VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor)
  and [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop)
  for the ImmersiveShell interfaces (`IApplicationViewCollection`,
  `IApplicationView::SetCloak`, `IVirtualDesktopManager`);
  [microsoft/PowerToys](https://github.com/microsoft/PowerToys) (FancyZones,
  Always On Top, Keyboard Manager) for the Win32 window-management and
  low-level-hook patterns; [CsWin32](https://github.com/microsoft/CsWin32) for
  the bindings.
- **Our own material**, all of it Diego's copyright and relicensed here by its
  author: the two driven test suites (`tests/wm`, `tests/fullscreen`), the
  measurements written down in `desk-w11-glazewm-testing.md`, the AutoHotkey
  libraries, `fliptest.c` / `cloaktest.c`, the `sway-apps` Python and
  `app-toggle.sh`. The dotfiles repository those live in is GPL-3.0, which
  binds redistribution of that repository, not the author's own reuse of his
  own code.
- **The IPC wire format**, captured black-box from the running GlazeWM with a
  raw WebSocket client (recorded in the plan, section 7). A protocol
  reimplemented so two programs can talk is not a copy of the program that
  speaks it. AkuWM's server is written from that capture; the model behind it
  is ours.

## What is never opened while writing AkuWM

The GlazeWM repository and our fork of it, `glazewm-js`, Zebar's sources,
AutoHotkey's sources, **komorebi** (source-available under its own licence
since 2024, not MIT), FancyWM, bug.n.

The fork's nine commits are GPL derivatives. They are retired with it; their
*behaviour* lives in the test suites, which is where AkuWM takes it from.

## How the rule is kept

1. **Fresh design, our own words.** The subsystems are designed from the
   behaviour the suites describe, not from anyone's implementation. Where the
   design deliberately differs from GlazeWM's it is written down -- sticky
   windows belong to the monitor rather than to a workspace, and "hidden" means
   the cloak flag was read back rather than that a cloak call was sent. Those
   differences are also what makes the code non-derivative in substance and not
   only in text.
2. **The compat layer is quarantined.** GlazeWM's names appear in exactly one
   place, `src/AkuWM.App/Compat/`, as the strings of a protocol.
3. **A tripwire runs in CI**: `tools/licence-tripwire.sh` greps the tree for
   identifiers that exist only inside the programs AkuWM replaces. It is a
   heuristic, not a proof, and it is cheap.
4. **Ported by behaviour, not transcribed.** The Python prototype and the
   AutoHotkey libraries are reimplemented against their tests; the languages
   differ anyway, and the tests are the contract.

## Dependencies

| package | licence |
|---|---|
| YamlDotNet | MIT |
| xunit, xunit.runner.visualstudio | Apache-2.0 / MIT |
| Microsoft.NET.Test.Sdk | MIT |
| Avalonia (from M5) | MIT |
| CsWin32 (from M1) | MIT |

Deliberately **not** used: FluentAssertions (v8 changed to a commercial
licence), anything under GPL, anything source-available.
