<#
.SYNOPSIS
    What the Startup shortcut runs: start AkuWM, and fall back to GlazeWM if it
    never answers.

.DESCRIPTION
    Until now nothing of AkuWM was in the Startup folder, and that WAS the
    safety net: a reboot always came up on GlazeWM with no code of ours
    running. Putting AkuWM there removes that net, so this replaces it with one
    that works while nobody is watching.

    It starts AkuWM and then waits for its PIPE to answer -- not for a process
    to exist. A process that exists and answers nothing is exactly how this
    desk lost its window manager for eight hours (2026-09-21): the thing to
    check is always that it ANSWERS.

    If the pipe never answers, the fallback is AkuWM's OWN rescue -- it gives
    every window back where it found it and leaves the desk unmanaged but
    usable -- and the marker is removed so the hotkeys stop talking to it.

    The fallback used to be "start GlazeWM instead". It is not, because
    GlazeWM is no longer installed on this machine: winget lists the package
    at version 0.0.0 and there is no glazewm.exe anywhere on disk outside
    AkuWM's own shim copies (looked for, 2026-09-21). A fallback that cannot
    run is worse than an honest one that says so in the log.

.PARAMETER Build
    Where akuwm.exe lives. The autostart installer points this at the copy it
    made, never at %TEMP%.
#>
param(
    # The Startup shortcut passes NO arguments, so these defaults are what runs
    # at logon. Programs\AkuWM, not AkuWM: the second is AkuWM's runtime state
    # directory, and Windows does not distinguish the case.
    [string] $Build   = (Join-Path $env:LOCALAPPDATA 'Programs\AkuWM'),
    [string] $Shim    = (Join-Path $env:LOCALAPPDATA 'Programs\AkuWM\shim'),
    [int]    $WaitSec = 40
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $env:LOCALAPPDATA 'akuwm\logs\boot.log'
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
function Say($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" | Add-Content -Path $log }

function Pipe-Answers {
    try {
        $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'akuwm', [System.IO.Pipes.PipeDirection]::InOut)
        $c.Connect(300)
        $c.Dispose()
        return $true
    } catch { return $false }
}

function Give-The-Desk-Back {
    # No window manager is bad; a window manager that hid windows and then
    # died is worse. Rescue puts every window back where it was found and
    # takes the cloak off anything hidden, so the desk is usable by hand.
    Remove-Item $marker -ErrorAction SilentlyContinue
    if (Test-Path $akuwm) {
        Say "boot: running rescue so the desk is usable"
        $r = Start-Process $akuwm -ArgumentList 'rescue', '--forgive' -PassThru -WindowStyle Hidden
        [void]$r.WaitForExit(30000)
    }
    if (Test-Path $glaze) {
        Say "boot: GlazeWM is installed; start it by hand if you want it: $glaze start"
    }
}

$akuwm  = Join-Path $Build 'akuwm.exe'
$marker = Join-Path $env:LOCALAPPDATA 'akuwm\wm-cli.txt'
$glaze  = Join-Path $env:ProgramFiles 'glzr.io\GlazeWM\cli\glazewm.exe'

Say "boot: starting $akuwm"

if (-not (Test-Path $akuwm)) {
    Say "boot: $akuwm is not there; the desk has no window manager"
    Give-The-Desk-Back
    exit 1
}

Start-Process -FilePath $akuwm -ArgumentList 'daemon' -WindowStyle Hidden

$deadline = (Get-Date).AddSeconds($WaitSec)
while ((Get-Date) -lt $deadline) {
    if (Pipe-Answers) {
        # Only now: the marker is a promise that something is listening.
        $shimExe = Join-Path $Shim 'glazewm.exe'
        if (Test-Path $shimExe) {
            [IO.File]::WriteAllText($marker, $shimExe, (New-Object Text.UTF8Encoding $false))
        }
        Say "boot: AkuWM answering after $([int]((Get-Date) - $deadline.AddSeconds(-$WaitSec)).TotalSeconds) s"
        exit 0
    }
    Start-Sleep -Milliseconds 500
}

Say "boot: AkuWM never answered in $WaitSec s; stopping it and giving the desk back"
Get-Process akuwm -ErrorAction SilentlyContinue | Stop-Process -Force
Give-The-Desk-Back
exit 2
