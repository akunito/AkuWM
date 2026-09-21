<#
.SYNOPSIS
    Writes down everything Zebar says to a window manager on port 6123.

.DESCRIPTION
    Nothing on this side knows which requests the bar makes, which events it
    subscribes to, or whether it re-queries after each one. The only way to
    find out is to let it connect to AkuWM and record what it said.

    AkuWM runs in shadow mode for this: it builds the real model of the desk
    and answers every question, and `Redraw` is never reached, so no window is
    moved, hidden or focused. The only disturbance is that the old window
    manager is stopped while the port is borrowed -- which leaves every window
    exactly where it is -- and started again at the end, whatever happens,
    including on Ctrl+C.

.PARAMETER Seconds
    How long to leave the bar connected.

.PARAMETER Exercise
    Also switch workspace a few times through the shim, so the capture shows
    what the bar does when an event arrives rather than only what it asks on
    connecting.
#>
[CmdletBinding()]
param(
    [int]$Seconds = 45,
    [string]$Akuwm = (Join-Path $env:TEMP 'akuwm-m2\akuwm.exe'),
    [string]$Shim  = (Join-Path $env:TEMP 'akuwm-m2-shim\glazewm.exe'),
    [string]$Out   = (Join-Path $env:TEMP 'akuwm-zebar-capture.jsonl'),
    [switch]$Exercise
)

$ErrorActionPreference = 'Stop'

$glazeDir = Join-Path $env:ProgramFiles 'glzr.io\GlazeWM'
$glazeCli = Join-Path $glazeDir 'cli\glazewm.exe'
$glazeExe = Join-Path $glazeDir 'glazewm.exe'
$zebarExe = Join-Path $env:ProgramFiles 'glzr.io\Zebar\zebar.exe'
$startup  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
$glazeLnk = Join-Path $startup 'GlazeWM.lnk'
$zebarLnk = Join-Path $startup 'Zebar.lnk'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Note($text) { Write-Host "    $text" -ForegroundColor DarkGray }
function Warn($text) { Write-Host "    $text" -ForegroundColor Yellow }

# Through explorer, with the Startup shortcut, so the shell is the parent.
# Started any other way from a shell that came through WSL interop, GlazeWM
# comes up WEDGED: the process runs, takes 6123, accepts connections and
# answers none of them, and its startup commands never run. Measured
# 2026-09-21 -- this script's own restore did it, and checked only that a
# process existed, so the desk had no working window manager for eight hours.
function StartByShell([string]$exe, [string]$shortcut) {
    if (Test-Path $shortcut) { Start-Process explorer.exe -ArgumentList $shortcut }
    else { Start-Process -FilePath $exe -WindowStyle Hidden }
}

# Whether it ANSWERS, not whether it runs.
function WmAnswers([string]$cli, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $out = Join-Path $env:TEMP 'akuwm-capture-probe.txt'

    while ((Get-Date) -lt $deadline) {
        Remove-Item $out -ErrorAction SilentlyContinue
        $probe = Start-Process -FilePath $cli -ArgumentList 'query', 'paused' `
            -RedirectStandardOutput $out -PassThru -WindowStyle Hidden
        if ($probe.WaitForExit(4000)) { return $true }
        $probe.Kill()
    }

    return $false
}

function PortOpen([int]$port) {
    $probe = New-Object System.Net.Sockets.TcpClient
    try { $probe.Connect('127.0.0.1', $port); $true } catch { $false } finally { $probe.Dispose() }
}

function WaitFor([scriptblock]$condition, [int]$ms, [string]$what) {
    $deadline = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 200
    }
    Warn "gave up waiting for $what"
    return $false
}

if (-not (Test-Path $Akuwm)) { throw "no akuwm.exe at $Akuwm -- run tools/publish-dev.sh first" }

$glazeWasRunning = [bool](Get-Process glazewm -ErrorAction SilentlyContinue)
$stoppedGlaze = $false

try {
    if ($glazeWasRunning) {
        Step 'Stopping the old window manager so the port is free'
        # Bounded: a wedged GlazeWM never answers wm-exit, and waiting on it
        # forever is how this script hung the first time it was run.
        $exit = Start-Process -FilePath $glazeCli -ArgumentList 'command', 'wm-exit' `
            -PassThru -WindowStyle Hidden
        if (-not $exit.WaitForExit(6000)) {
            $exit.Kill()
            Warn 'GlazeWM did not answer wm-exit; stopping it the hard way'
            Get-Process glazewm-watcher, glazewm -ErrorAction SilentlyContinue | Stop-Process -Force
        }
        $stoppedGlaze = $true
        [void](WaitFor { -not (Get-Process glazewm -ErrorAction SilentlyContinue) } 8000 'GlazeWM to stop')
        [void](WaitFor { -not (PortOpen 6123) } 8000 'port 6123 to be released')
    }

    Get-Process zebar -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item $Out -ErrorAction SilentlyContinue

    Step 'Starting AkuWM in shadow mode, capturing'
    Note "capture: $Out"
    Start-Process -FilePath $Akuwm `
        -ArgumentList "daemon --shadow --capture `"$Out`"" -WindowStyle Hidden
    if (-not (WaitFor { PortOpen 6123 } 15000 'AkuWM to listen on 6123')) {
        throw 'AkuWM never took the port'
    }

    Step 'Starting the bar'
    StartByShell $zebarExe $zebarLnk
    [void](WaitFor { (Get-Content $Out -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0 } `
        15000 'the bar to say something')

    if ($Exercise) {
        Step 'Switching workspace through the shim, to see what the bar does with an event'
        foreach ($workspace in @('12', '13', '11')) {
            & $Shim command focus --workspace $workspace | Out-Null
            Start-Sleep -Milliseconds 1200
        }
    }

    Step "Listening for $Seconds seconds"
    Start-Sleep -Seconds $Seconds
}
finally {
    Step 'Putting the desk back'
    Get-Process zebar -ErrorAction SilentlyContinue | Stop-Process -Force

    & $Akuwm exit --out (Join-Path $env:TEMP 'akuwm-zebar-exit.txt') 2>$null | Out-Null
    if (-not (WaitFor { -not (Get-Process akuwm -ErrorAction SilentlyContinue) } 8000 'AkuWM to stop')) {
        Get-Process akuwm -ErrorAction SilentlyContinue | Stop-Process -Force
    }

    if ($stoppedGlaze) {
        StartByShell $glazeExe $glazeLnk
        if (WmAnswers $glazeCli 25) {
            Note 'GlazeWM is answering on 6123 again'
        } else {
            Warn 'GlazeWM is NOT answering on 6123 -- the hotkeys are dead.'
            Warn "Stop it and start it from $glazeLnk"
        }

        if (-not (Get-Process zebar -ErrorAction SilentlyContinue)) {
            StartByShell $zebarExe $zebarLnk
        }
        Note 'the bar is back'
    }
}

if (-not (Test-Path $Out)) { throw "nothing was captured to $Out" }

$lines = Get-Content $Out | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }
$inbound = $lines | Where-Object { $_.dir -eq 'in' }

Write-Host ''
Step "$($lines.Count) frames, $($inbound.Count) of them from the bar"
Write-Host 'What the bar asked for:' -ForegroundColor Cyan
$inbound | Group-Object text | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("    {0,4}x  {1}" -f $_.Count, $_.Name)
}
