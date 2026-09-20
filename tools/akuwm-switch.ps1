<#
.SYNOPSIS
    Switches the desk between the old stack (GlazeWM + AutoHotkey) and AkuWM,
    for this session only.

.DESCRIPTION
    Two window managers cannot run at once, so changing over is one command
    either way.

    THE INVARIANT THIS SCRIPT KEEPS: it never touches the Startup folder. The
    shortcuts there -- GlazeWM, hyper-desktops, Zebar -- are what Windows runs
    at logon, and they stay as they are. So whatever this script does, and
    however badly it goes, restarting the machine brings back the stack that
    was there before AkuWM existed. Nothing in AkuWM has to work for that to
    be true, which is the point: it is a property of the machine, not a
    feature of the program.

    That also means switching to AkuWM does not survive a reboot, and is not
    meant to. Moving AkuWM into Startup is a deliberate step for a later
    milestone, once it has earned it.

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

    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$akuwm   = Join-Path $env:ProgramFiles 'AkuWM\akuwm.exe'
$shim    = Join-Path $env:ProgramFiles 'AkuWM\glazewm.exe'
$glazewm = Join-Path $env:ProgramFiles 'glzr.io\GlazeWM\glazewm.exe'
$zebar   = Join-Path $env:ProgramFiles 'glzr.io\Zebar\zebar.exe'
$ahkExe  = Join-Path $env:ProgramFiles 'AutoHotkey\v2\AutoHotkey64_UIA.exe'
$ahk     = Join-Path $env:USERPROFILE '.dotfiles\templates\windows\DESK_W11\hyper-desktops.ahk'

# Which CLI the hotkey script shells out to. A file rather than an environment
# variable: the script is already running when the desk is switched, so a
# variable set now would not reach it.
$marker  = Join-Path $env:LOCALAPPDATA 'akuwm\wm-cli.txt'

function Say([string]$text) { Write-Host $text }

function Stop-Them([string]$name) {
    $running = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $running) { return $false }
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    return $true
}

function Restart-Zebar {
    if (-not (Test-Path $zebar)) { return }
    Stop-Them 'zebar' | Out-Null
    Start-Sleep -Milliseconds 300
    Start-Process -FilePath $zebar -WindowStyle Hidden
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
            Start-Process -FilePath $glazewm -ArgumentList 'start' -WindowStyle Hidden
            Start-Sleep -Seconds 2
            Say '  GlazeWM started'
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
Set-Content -Path $marker -Value $shim -Encoding UTF8 -NoNewline
Say '  the hotkeys now talk to AkuWM'

Start-Process -FilePath $akuwm -ArgumentList 'daemon' -WindowStyle Hidden
Start-Sleep -Seconds 2

$doctor = Join-Path $env:TEMP 'akuwm-switch-doctor.txt'
# A uiAccess process is launched through AppInfo and its output cannot be
# redirected by the caller, so it writes the file itself.
& $akuwm doctor --out $doctor | Out-Null
if (Test-Path $doctor) {
    Get-Content $doctor | ForEach-Object { Say "  $_" }
}

Restart-Hotkeys
Restart-Zebar
Say 'Done. The desk is on AkuWM -- until the next reboot, which returns to GlazeWM.'
