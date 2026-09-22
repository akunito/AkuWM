<#
.SYNOPSIS
    Switches the desk between the old stack (GlazeWM + AutoHotkey) and AkuWM,
    for this session only.

.DESCRIPTION
    Two window managers cannot run at once, so changing over is one command
    either way.

    THE INVARIANT THIS SCRIPT KEEPS: it never touches the Startup folder.
    What runs at logon is decided there by tools\akuwm-autostart.ps1 (AkuWM
    since 2026-09-21, GlazeWM parked as GlazeWM.lnk.off), and this script only
    changes the current session, so whatever it does and however badly it
    goes, a restart comes up on whatever Startup says. GlazeWM is no longer
    installed on this desk; -To glazewm only works while its binary exists.

.PARAMETER To
    akuwm or glazewm.

.PARAMETER Yes
    Do not ask before switching to AkuWM.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\akuwm-switch.ps1 -To akuwm

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\akuwm-switch.ps1 -To glazewm
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('akuwm', 'glazewm')]
    [string]$To,

    [switch]$Yes,

    # Where akuwm.exe and the glazewm shim are. The installed, signed pair in
    # Program Files by default; a build directory when a change is being tried
    # on the desk before it is installed -- that one has no uiAccess, which
    # costs nothing in M2 because AutoHotkey is still the thing holding the
    # hooks and it has its own.
    [string]$Build = (Join-Path $env:ProgramFiles 'AkuWM'),

    # The shim, when it was published somewhere else (the dev loop puts it in
    # its own directory so the two single-file builds do not share one).
    [string]$Shim
)

$ErrorActionPreference = 'Stop'

$akuwm   = Join-Path $Build 'akuwm.exe'
$shim    = if ($Shim) { $Shim } else { Join-Path $Build 'glazewm.exe' }
$glazewm = Join-Path $env:ProgramFiles 'glzr.io\GlazeWM\glazewm.exe'
$glazeCli = Join-Path $env:ProgramFiles 'glzr.io\GlazeWM\cli\glazewm.exe'
$zebar   = Join-Path $env:ProgramFiles 'glzr.io\Zebar\zebar.exe'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$glazeLink = Join-Path $startup 'GlazeWM.lnk'
$zebarLink = Join-Path $startup 'Zebar.lnk'
$ahkExe  = Join-Path $env:ProgramFiles 'AutoHotkey\v2\AutoHotkey64_UIA.exe'
$ahk     = Join-Path $env:USERPROFILE '.dotfiles\templates\windows\DESK_W11\hyper-desktops.ahk'

# Which CLI the hotkey script shells out to. A file rather than an environment
# variable: the script is already running when the desk is switched, so a
# variable set now would not reach it.
$marker  = Join-Path $env:LOCALAPPDATA 'akuwm\wm-cli.txt'

function Say([string]$text) { Write-Host $text }
function Warn([string]$text) { Write-Host $text -ForegroundColor Yellow }

function Stop-Them([string]$name) {
    $running = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $running) { return $false }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    return $true
}

# Started through explorer, with the Startup shortcut when there is one, so the
# shell is the parent. Launched any other way from a shell that came through
# WSL interop, GlazeWM comes up WEDGED: the process runs, takes port 6123,
# accepts connections and answers none of them, and its own startup commands
# never run. Measured 2026-09-21, and it cost this desk its window manager for
# eight hours -- the restore had checked that a process existed, which it did.
function Start-Shell-App([string]$exe, [string]$shortcut, [string[]]$arguments) {
    if (Test-Path $shortcut) {
        Start-Process explorer.exe -ArgumentList $shortcut
        return
    }

    if ($arguments) {
        Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden
    } else {
        Start-Process -FilePath $exe -WindowStyle Hidden
    }
}

# Whether it ANSWERS, not whether it is running. A wedged GlazeWM is a running
# GlazeWM, and every hotkey on this desk goes through that socket.
function Wait-For-Wm([string]$cli, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $out = Join-Path $env:TEMP 'akuwm-switch-probe.txt'

    while ((Get-Date) -lt $deadline) {
        Remove-Item $out -ErrorAction SilentlyContinue
        $probe = Start-Process -FilePath $cli -ArgumentList 'query', 'paused' `
            -RedirectStandardOutput $out -PassThru -WindowStyle Hidden
        if ($probe.WaitForExit(4000)) { return $true }
        $probe.Kill()
    }

    return $false
}

function Restart-Zebar {
    if (-not (Test-Path $zebar)) { return }
    Stop-Them 'zebar' | Out-Null
    Start-Sleep -Milliseconds 300
    Start-Shell-App $zebar $zebarLink @()
    Say '  Zebar restarted'
}

function Restart-Hotkeys {
    # The hotkey script reads which window manager to talk to once, when it
    # starts. Switching the desk therefore means restarting it -- and it is the
    # thing that has to keep working, so it goes back up before anything else.
    if (-not (Test-Path $ahkExe) -or -not (Test-Path $ahk)) {
        Say '  AutoHotkey is not where it was expected; the hotkeys were left alone'
        return
    }

    Get-Process -Name 'AutoHotkey64_UIA' -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like '*hyper-desktops*' -or $true } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    Start-Process -FilePath $ahkExe -ArgumentList "`"$ahk`""
    Say '  hotkeys restarted'
}

if ($To -eq 'glazewm') {
    Say 'Switching back to GlazeWM.'

    if (Test-Path $akuwm) {
        # Not "stop akuwm": rescue stops it AND gives back every window it
        # hid or moved, working from the files on disk rather than asking a
        # process that may be the reason this is being run.
        Say '  asking AkuWM to stand down and put the desk back'
        & $akuwm rescue
    } else {
        Stop-Them 'akuwm' | Out-Null
    }

    if (Test-Path $marker) {
        Remove-Item $marker -Force -ErrorAction SilentlyContinue
        Say '  the hotkeys point back at GlazeWM'
    }

    if (Test-Path $glazewm) {
        if (Get-Process -Name glazewm -ErrorAction SilentlyContinue) {
            Say '  GlazeWM is already running'
        } else {
            Start-Shell-App $glazewm $glazeLink @('start')
            Say '  GlazeWM started'
        }

        # This is the way back. It has to be proven, not assumed.
        if (Wait-For-Wm $glazeCli 20) {
            Say '  GlazeWM is answering on 6123'
        } else {
            Say ''
            Say 'GlazeWM is running but NOT answering on 6123, so the hotkeys are dead.'
            Say 'Stop it and start it from the Start menu shortcut:'
            Say "  $glazeLink"
            exit 1
        }
    } else {
        Say "  GlazeWM is not installed at $glazewm"
    }

    Restart-Hotkeys
    Restart-Zebar
    Say 'Done. The desk is on the old stack.'
    exit 0
}

# ---- to AkuWM -------------------------------------------------------------

if (-not (Test-Path $akuwm)) {
    Write-Error "AkuWM is not installed at $akuwm. Run tools\install-uiaccess.ps1 first."
    exit 1
}

if (-not $Yes) {
    Say ''
    Say 'Switching the desk to AkuWM.'
    Say ''
    Say '  GlazeWM stops. AutoHotkey is restarted pointing at AkuWM, so every'
    Say '  Hyper chord keeps working -- the same words, answered by AkuWM.'
    Say ''
    Say '  To come back:  tools\akuwm-switch.ps1 -To glazewm'
    Say '  In a hurry:    akuwm rescue     (or restart the machine)'
    Say ''
    $answer = Read-Host 'Go ahead? [y/N]'
    if ($answer -ne 'y' -and $answer -ne 'Y') {
        Say 'Left alone.'
        exit 0
    }
}

# An AkuWM that is already running has the pipe and the port, and a second one
# does not fail -- it manages the desk alongside the first, and every gesture
# is carried out twice. Found by doing it, 2026-09-21: switching twice left two
# daemons and a doctor that reported the older one's state.
if (Get-Process -Name akuwm -ErrorAction SilentlyContinue) {
    # ASKED, not killed. A killed daemon is an unclean run, two of those in a
    # row is safe mode, and safe mode looks exactly like a window manager that
    # has stopped working: it arranges nothing and says nothing on screen.
    # Switching twice in a row used to be enough to cause it.
    & $akuwm exit --out (Join-Path $env:TEMP 'akuwm-switch-exit.txt') 2>$null | Out-Null

    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name akuwm -ErrorAction SilentlyContinue)) {
        Start-Sleep -Milliseconds 200
    }

    if (Stop-Them 'akuwm') {
        Warn '  a running AkuWM would not stop and was killed; the next start may be in safe mode'
    } else {
        Say '  a running AkuWM was asked to stand down first'
    }

    Start-Sleep -Milliseconds 400
}

# The watcher first. It is a child of `glazewm.exe start` (the only thing in
# the Startup folder), it exists to put the desk back when the manager dies,
# and leaving it alive while the manager is force-stopped is asking it to do
# exactly that -- possibly by starting the manager again, which would take
# port 6123 back from under AkuWM. It returns with GlazeWM.
Stop-Them 'glazewm-watcher' | Out-Null

if (Stop-Them 'glazewm') {
    Say '  GlazeWM stopped'
    Start-Sleep -Milliseconds 600
} else {
    Say '  GlazeWM was not running'
}

# GlazeWM hides workspaces by cloaking too, and a cloak outlives the process
# that applied it. Whatever it left hidden is an orphan now: give it back
# before AkuWM decides what the desk contains.
& $akuwm uncloak-all --out "$env:TEMP\akuwm-switch-uncloak.txt" | Out-Null
if (Test-Path "$env:TEMP\akuwm-switch-uncloak.txt") {
    $given = (Get-Content "$env:TEMP\akuwm-switch-uncloak.txt" -Raw | ConvertFrom-Json).uncloaked
    Say "  $given window(s) the old manager had hidden are visible again"
}

if (-not (Test-Path $shim)) {
    Write-Error "the glazewm shim is missing at $shim. Re-run tools\install-uiaccess.ps1."
    exit 1
}

New-Item -ItemType Directory -Force -Path (Split-Path $marker -Parent) | Out-Null
# WriteAllText with an encoding that emits no preamble, not Set-Content:
# Windows PowerShell 5.1's -Encoding UTF8 writes a BOM, and every reader of
# this file then has three invisible bytes in front of the path. AutoHotkey's
# FileExist would fail on it and the hotkeys would quietly keep talking to
# GlazeWM with nothing in any log to say why.
[IO.File]::WriteAllText($marker, $shim, (New-Object Text.UTF8Encoding $false))
Say '  the hotkeys now talk to AkuWM'

Start-Process -FilePath $akuwm -ArgumentList 'daemon' -WindowStyle Hidden
Start-Sleep -Seconds 2

$doctor = Join-Path $env:TEMP 'akuwm-switch-doctor.txt'
# A uiAccess process is launched through AppInfo and its output cannot be
# redirected by the caller, so it writes the file itself.
& $akuwm doctor --out $doctor | Out-Null
$report = if (Test-Path $doctor) { Get-Content $doctor } else { @() }
$report | ForEach-Object { Say "  $_" }

Restart-Hotkeys
Restart-Zebar

# The one failure that has to shout. If AkuWM did not get port 6123 -- the old
# manager restarted by its watcher, a leftover process -- then the hotkeys and
# the bar are talking to one window manager while another arranges the desk,
# and the desk does two things for every gesture.
$bar = $report | Where-Object { $_ -match 'bar and scripts' }
if ($bar -and $bar -notmatch '^ok') {
    Say ''
    Say 'The bar and the scripts cannot reach AkuWM:'
    Say "  $bar"
    Say 'Two window managers may now be arguing over the desk. Go back with:'
    Say '  tools\akuwm-switch.ps1 -To glazewm'
    exit 1
}

Say 'Done. The desk is on AkuWM -- until the next reboot, which returns to GlazeWM.'
