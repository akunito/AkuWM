# AkuWM, input, and anti-cheat

AkuWM installs a low-level keyboard hook and runs with `uiAccess`. Both are
things an anti-cheat looks at. This document says exactly what AkuWM does to
input and what it will never do, so that a person can judge the risk for the
games they play instead of guessing -- and so that the answer does not change
quietly as the code grows.

Nobody can promise "this will not get you banned". Anti-cheat vendors do not
publish their heuristics, and the ones that matter here (NCGuard and VIOLET on
NCSoft's Purple launcher, Easy Anti-Cheat, BattlEye, Vanguard) are deliberately
opaque. What can be promised is what the program does.

## The three things a tool can do to input, in order of risk

| | what it is | risk | AkuWM |
|---|---|---|---|
| **observe** | a hook that watches keystrokes and passes them on | the same thing the Discord, Steam and NVIDIA overlays do | **yes** -- this is the chord engine |
| **swallow** | a hook that stops a keystroke reaching the game | interference with the game's input | **only chords, and never while a game has the foreground** |
| **inject** | fabricating keystrokes the user did not press | indistinguishable from a macro; this is what gets people banned | **never while a game has the foreground** |

The third row is the one that matters. Reading or writing another process's
memory -- the thing anti-cheats exist to stop -- AkuWM does not do at all, has
no code for, and has no reason to acquire.

## The rules, in the code

- **No synthetic input near a game.** `Win32Focus` has a last-resort route that
  fabricates a keystroke to claim the foreground right. It is skipped when a
  game holds the foreground, and the focus request fails instead. Failing to
  move the focus is the cheaper mistake by a wide margin.
- **Chords pass through over a game.** Over a normal window a consumed chord is
  swallowed. Over a game it is observed and passed on: the game receives every
  key it would have received anyway. Hyper is Ctrl+Alt+Win+key, which no game
  binds, so nothing is lost by letting it through.
- **`send` shortcuts do not fire over a game.** The shortcut kind that injects a
  chord into another program (how the launcher is opened) is exactly a macro,
  and is refused while a game has the foreground.
- **A pause that really pauses.** `Hyper+Shift+Esc` uninstalls the hook rather
  than ignoring it, so during a session where even an observing hook is
  unwelcome there is no hook at all. AkuWM keeps managing windows; it simply
  stops watching the keyboard.
- **No macro features, ever.** No key sequences, no timed input, no repeat, no
  "press this every N seconds". Not as a default, not as an option. A window
  manager does not need them, and their presence would change what AkuWM is.

## Why `uiAccess` at all

Measured on this desk (spike S1): with an elevated window in front for 14 of 30
seconds and typed into, **not one keystroke reached an unprivileged hook**.
UIPI stops a lower-integrity process from seeing input destined for a higher
one, and every game runs elevated. Without `uiAccess` the chords simply stop
working the moment a game is focused.

`uiAccess` is not elevation. The process runs as the user; it is allowed to
drive the user interface of higher-integrity windows, which is the
accessibility case the flag exists for. Windows grants it only to a binary that
is Authenticode-signed and sits in a secure directory.

## What this replaces

This desk runs AutoHotkey today, as `AutoHotkey64_UIA.exe` -- the same
`uiAccess`, the same low-level hooks. AutoHotkey is the most widely recognised
tool of this class and is explicitly detected by several anti-cheats, because it
is also the standard tool for writing macros. AkuWM is narrower on purpose: it
has no scripting language, no macro primitives, and nothing that can send input
to a game.

That is a real improvement in substance. It is not a guarantee: an unknown
signed binary with a global hook has no reputation either, and reputation is
much of what whitelisting is.

## If you are not the author

AkuWM is MIT and you may well be running it with a game this desk has never
seen. The honest advice:

1. **Check your game's rules.** Some publishers ban any input hook outright;
   most care about automation. The distinction above is the one they usually
   draw.
2. **Start with the hook paused** (`Hyper+Shift+Esc`) for a session or two and
   see that everything else behaves.
3. **Sign your own build.** `tools/install-uiaccess.ps1` makes a certificate
   local to your machine. That is better than a shared signature, which could be
   blacklisted once for everybody -- and worse for reputation, which nobody has
   here anyway.
4. **If a vendor asks**, this document is the answer, and the source is there to
   check it against.
