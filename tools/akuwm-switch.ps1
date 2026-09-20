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
$glazewm = Join-Path $env:ProgramFiles 'glzr.io\GlazeWM\glazewm.exe'
$zebar   = Join-Path $env:ProgramFiles 'glzr.io\Zebar\zebar.exe'

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
    Start-Process -FilePath $zebar -WindowStyle Hidden
    Say '  Zebar restarted'
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

    if (Test-Path $glazewm) {
        if (Get-Process -Name glazewm -ErrorAction SilentlyContinue) {
            Say '  GlazeWM is already running'
        } else {
            Start-Process -FilePath $glazewm -ArgumentList 'start' -WindowStyle Hidden
            Say '  GlazeWM started'
        }
    } else {
        Say "  GlazeWM is not installed at $glazewm"
    }

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
    Say '  GlazeWM will be stopped. AutoHotkey keeps running, and its gestures'
    Say '  reach the window manager only once the glazewm shim is in place --'
    Say '  until then the Hyper chords that move windows will do nothing.'
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
} else {
    Say '  GlazeWM was not running'
}

Start-Process -FilePath $akuwm -ArgumentList 'daemon' -WindowStyle Hidden
Start-Sleep -Milliseconds 800

$doctor = Join-Path $env:TEMP 'akuwm-switch-doctor.txt'
# A uiAccess process is launched through AppInfo and its output cannot be
# redirected by the caller, so it writes the file itself.
& $akuwm doctor --out $doctor | Out-Null
if (Test-Path $doctor) {
    Get-Content $doctor | ForEach-Object { Say "  $_" }
}

Restart-Zebar
Say 'Done. The desk is on AkuWM -- until the next reboot, which returns to GlazeWM.'
