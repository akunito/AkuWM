# Getting the desk back

AkuWM hides windows by cloaking them and moves the rest into a layout. Both
are reversible, and this is the file that says how -- written for the moment
when something has gone wrong and reading source code is not an option.

## The short version

1. **Double-click "Rescue my desk (AkuWM)" on the Desktop.** It stops AkuWM and
   gives back every window AkuWM hid or moved. The shortcut is installed by
   `tools\install-uiaccess.ps1`; if it is not there, run
   `akuwm rescue --forgive` from any terminal (`%LOCALAPPDATA%\Programs\AkuWM\akuwm.exe`).
2. **If that will not run, restart the machine.** Since 2026-09-21 the Startup
   folder runs `akuwm-boot.ps1`, which starts the daemon and waits for its pipe
   to answer; if it never does, the script runs the rescue itself, clears the
   marker the hotkeys read, and leaves the desk usable by hand. GlazeWM is no
   longer installed, so there is no older stack to come up on.

Neither step needs AkuWM to be working. A cloak does not survive a logon
either, so a restart cannot leave anything hidden.

## What the boot script guarantees

`akuwm-boot.ps1` never trusts that a process exists -- a wedged manager is a
running one (that cost this desk eight hours once). It waits for the named
pipe to answer, and only then writes the marker that points the hotkeys at
AkuWM. On a timeout it kills the daemon, runs `akuwm rescue --forgive` and
removes the marker. A reboot is also not counted as a bad ending: a run that
started before the current boot is one the machine ended, not a crash, so two
reboots in a row can never put the third start into safe mode.

A cloak does not survive a logon either. Cloaking is a property of a live
window, so logging out and back in cannot leave anything hidden: the windows
are gone with the session and the applications start fresh.

## What AkuWM writes down, and when

Two files in `%LOCALAPPDATA%\akuwm\`, both written **before** the change they
describe, atomically, so a crash in the middle cannot lose them:

| file | holds | used by |
|---|---|---|
| `cloaked.json` | every window AkuWM has hidden | the next start, and `akuwm rescue` |
| `geometry.json` | where each window was before AkuWM first moved it | the same two |
| `session.json` | whether the last run reached its own shutdown | safe mode |

Handles are reused by Windows, so every entry is checked against whatever owns
that handle now before it is acted on. An entry that matches nothing is
dropped rather than applied to a stranger's window.

## The four ways the desk gets restored

All four run the same restore, and running it twice is harmless.

- **On the way out.** A clean stop, `Ctrl+C`, and an unhandled exception all
  restore before the process ends.
- **On the next start.** Whatever the last run missed is given back before
  AkuWM starts listening.
- **By the watchdog.** The window-manager loop leaves a heartbeat. If it stops
  for ten seconds, a thread that shares nothing with it restores the desk and
  ends the process -- because a manager that cannot manage should be absent,
  not present and frozen. Proven rather than promised:
  `akuwm daemon --stall-test 30` wedges the loop on purpose so the watchdog
  can be watched doing it.
- **By hand.** `akuwm rescue` stops the daemon and restores from the files,
  without asking the daemon for anything. It is never handed to a running
  AkuWM to execute, because the daemon is what it is rescuing from.

## Safe mode

Two runs in a row that never reached their own shutdown, and the third comes
up managing nothing and says why. One bad ending is an accident; two is a
pattern, and taking over the desk a third time is not what the person in front
of it wants.

- `akuwm daemon --force` takes over anyway.
- `akuwm rescue --forgive` puts the desk back and clears the count.
- `akuwm doctor` shows the state under **last run**.

## `akuwm rescue`

```
akuwm rescue                 stop AkuWM, give back what AkuWM hid or moved
akuwm rescue --all           also uncloak every other hidden window
akuwm rescue --keep-daemon   restore without stopping AkuWM
akuwm rescue --forgive       also clear the safe-mode count
```

`--all` exists for the case where the records themselves were lost. It is not
the default on purpose: while another window manager is running, its hidden
workspaces would all appear at once, which is a different mess from the one
being cleaned up. Windows parked on another native virtual desktop are left
alone either way -- uncloaking one drags it onto this desktop.
