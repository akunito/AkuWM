<#
.SYNOPSIS
    Exercises AkuWM against real Windows, touching only its own windows.

.DESCRIPTION
    The unit tests run the model against a desk made of numbers. This runs it
    against the real one -- real DWM cloaks, real SetWindowPos, real monitors
    -- without rearranging anything a person is using.

    The trick is a configuration whose only rule ignores every window whose
    title is not one of this script's, so AkuWM adopts exactly three console
    windows it opened itself and nothing else on the machine. The window
    manager currently running is asked to let go of those three as well, so
    the two are not fighting over them.

    Everything it creates is torn down at the end, and AkuWM's own restore
    puts the three windows back where they were before it is stopped.

.PARAMETER Akuwm
    The akuwm.exe to exercise.

.PARAMETER Shim
    The glazewm.exe shim, for the compatibility half of the test.

.PARAMETER KeepWindows
    Leave the three console windows open at the end, to look at them.
#>
[CmdletBinding()]
param(
    [string]$Akuwm = (Join-Path $env:TEMP 'akuwm-m2\akuwm.exe'),
    [string]$Shim  = (Join-Path $env:TEMP 'akuwm-m2-shim\glazewm.exe'),
    [switch]$KeepWindows
)

$ErrorActionPreference = 'Stop'

$root     = Join-Path $env:TEMP 'akuwm-smoke'
$config   = Join-Path $root 'config'
$state    = Join-Path $root 'state'
$titles   = @('AKUWM SMOKE ONE', 'AKUWM SMOKE TWO', 'AKUWM SMOKE THREE')
$failures = New-Object System.Collections.ArrayList

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Note($text) { Write-Host "    $text" -ForegroundColor DarkGray }
function Pass($text) { Write-Host "    PASS $text" -ForegroundColor Green }
function Fail($text) { Write-Host "    FAIL $text" -ForegroundColor Red; [void]$failures.Add($text) }

function Check($name, $condition, $detail) {
    if ($condition) { Pass "$name" } else { Fail "$name -- $detail" }
}

function Akuwm([string]$arguments) {
    $out = Join-Path $env:TEMP 'akuwm-smoke-out.txt'
    Remove-Item $out -ErrorAction SilentlyContinue
    # A uiAccess process cannot have its output redirected by the caller, so it
    # writes the answer itself.
    Start-Process -FilePath $Akuwm -ArgumentList "$arguments --out `"$out`"" -Wait -WindowStyle Hidden
    if (Test-Path $out) { Get-Content $out -Raw } else { '' }
}

function Shim([string]$arguments) {
    $out = Join-Path $env:TEMP 'akuwm-smoke-shim.txt'
    Remove-Item $out -ErrorAction SilentlyContinue
    Start-Process -FilePath $Shim -ArgumentList $arguments -Wait -WindowStyle Hidden `
        -RedirectStandardOutput $out
    if (Test-Path $out) { Get-Content $out -Raw } else { '' }
}

function SmokeWindows($json) {
    if (-not $json) { return @() }
    $parsed = $json | ConvertFrom-Json
    if (-not $parsed.success) { return @() }
    # Trimmed: `title X` leaves a trailing space in the window title, which is
    # invisible in every log and makes an exact comparison silently find
    # nothing.
    @($parsed.data.windows | Where-Object { $titles -contains $_.title.Trim() })
}

# --- setting up -------------------------------------------------------------

Step 'Preparing a configuration that ignores everything but this test'
New-Item -ItemType Directory -Force -Path $config, $state | Out-Null

$repoConfig = Join-Path $env:USERPROFILE '.dotfiles\templates\windows\DESK_W11\akuwm'
if (-not (Test-Path (Join-Path $repoConfig 'common.json'))) {
    throw "no configuration at $repoConfig"
}

Copy-Item (Join-Path $repoConfig 'common.json') $config -Force
Copy-Item (Join-Path $repoConfig 'DESK_W11.json') $config -Force

# Every window whose title is not one of ours is ignored. A negative lookahead
# is the whole containment: without it this would rearrange the desk.
$common = Get-Content (Join-Path $config 'common.json') -Raw | ConvertFrom-Json
$only = '^(?!AKUWM SMOKE ).*$'
$common.rules = @(
    [pscustomobject]@{
        id      = 'smoke-ignore-everything-else'
        name    = 'everything that is not this test'
        match   = @([pscustomobject]@{ title = "re:$only" })
        actions = @('ignore')
        enabled = $true
    }
)
$common | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $config 'common.json') -Encoding UTF8
Note "only windows titled 'AKUWM SMOKE ...' will be managed"

Step 'Opening three windows of its own'
foreach ($title in $titles) {
    Start-Process -FilePath $env:ComSpec -ArgumentList '/k', "title $title" -WindowStyle Normal
    Start-Sleep -Milliseconds 400
}
Start-Sleep -Seconds 1

# The window manager that is running now also sees them, and a cloak is one
# flag: two managers taking it on and off is a fight neither wins. It is asked
# to let go of these three, and then to stand still entirely for the length of
# the test -- nothing of the person's desk moves while it is paused.
$glazewm = Join-Path $env:ProgramFiles 'glzr.io\GlazeWM\cli\glazewm.exe'
$paused = $false

if (Test-Path $glazewm) {
    Step 'Asking the running window manager to let go and stand still'
    $running = & $glazewm query windows | ConvertFrom-Json
    foreach ($window in $running.data.windows) {
        if ($titles -contains $window.title.Trim()) {
            & $glazewm command ignore --id $window.id | Out-Null
            Note "ignored $($window.title.Trim())"
        }
    }

    & $glazewm command wm-toggle-pause | Out-Null
    $paused = $true
    Note 'paused'
    Start-Sleep -Milliseconds 800
}

# --- the test ---------------------------------------------------------------

$env:AKUWM_STATE_DIR = $config
$env:LOCALAPPDATA_ORIGINAL = $env:LOCALAPPDATA
$env:LOCALAPPDATA = $state

try {
    Step 'Starting AkuWM, managing'
    Start-Process -FilePath $Akuwm -ArgumentList 'daemon --compat-port 6199' -WindowStyle Hidden
    Start-Sleep -Seconds 3

    Step 'It adopted exactly its own windows'
    $windows = SmokeWindows (Shim 'query windows')
    Check 'all three adopted' ($windows.Count -eq 3) "it sees $($windows.Count)"

    $all = (Shim 'query windows') | ConvertFrom-Json
    $managed = @($all.data.windows).Count
    Check 'and nothing else' ($managed -eq 3) "it is managing $managed windows in total"

    Step 'They are tiled, side by side, inside the work area'
    $sorted = @($windows | Sort-Object x)
    if ($sorted.Count -eq 3) {
        $widths = @($sorted | ForEach-Object { $_.width })
        $spread = ($widths | Measure-Object -Maximum).Maximum - ($widths | Measure-Object -Minimum).Minimum
        Check 'three columns of roughly equal width' ($spread -le 4) "widths are $($widths -join ', ')"
        Check 'the first starts at the left edge of the work area' ($sorted[0].x -le 1) "x is $($sorted[0].x)"
        Check 'they do not overlap' (
            $sorted[0].x + $sorted[0].width -le $sorted[1].x -and
            $sorted[1].x + $sorted[1].width -le $sorted[2].x) 'they overlap'
    } else {
        Fail 'three columns -- there are not three windows to measure'
    }

    Step 'A workspace switch hides them, and coming back shows them'
    Shim 'command focus --workspace 12' | Out-Null
    Start-Sleep -Seconds 1
    $hidden = @(SmokeWindows (Shim 'query windows') | Where-Object { $_.displayState -eq 'hidden' })
    Check 'all three hidden' ($hidden.Count -eq 3) "$($hidden.Count) of 3 are hidden"

    Shim 'command focus --workspace 11' | Out-Null
    Start-Sleep -Seconds 1
    $shown = @(SmokeWindows (Shim 'query windows') | Where-Object { $_.displayState -eq 'shown' })
    Check 'all three shown again' ($shown.Count -eq 3) "$($shown.Count) of 3 are shown"

    Step 'A window moved to another workspace goes away and comes back'
    $one = @(SmokeWindows (Shim 'query windows') | Where-Object { $_.title.Trim() -eq $titles[0] })[0]
    Shim "command --id $($one.id) move --workspace 13" | Out-Null
    Start-Sleep -Seconds 1
    $left = @(SmokeWindows (Shim 'query windows') | Where-Object { $_.title.Trim() -eq $titles[0] })[0]
    Check 'it is hidden on the other workspace' ($left.displayState -eq 'hidden') "it is $($left.displayState)"

    $remaining = @(SmokeWindows (Shim 'query windows') |
        Where-Object { $_.title.Trim() -ne $titles[0] -and $_.displayState -eq 'shown' })
    Check 'the other two took the space' (
        $remaining.Count -eq 2 -and
        ($remaining | Measure-Object -Property width -Sum).Sum -gt ($sorted[0].width * 2.5)) 'they did not grow'

    Shim "command --id $($one.id) move --workspace 11" | Out-Null
    Start-Sleep -Seconds 1
    $back = @(SmokeWindows (Shim 'query windows') | Where-Object { $_.title.Trim() -eq $titles[0] })[0]
    Check 'it comes back visible' ($back.displayState -eq 'shown') "it is $($back.displayState)"

    Step 'Fullscreen covers the whole screen, taskbar included'
    Shim "command --id $($back.id) toggle-fullscreen" | Out-Null
    Start-Sleep -Milliseconds 800
    $full = @(SmokeWindows (Shim 'query windows') | Where-Object { $_.title.Trim() -eq $titles[0] })[0]
    Check 'it is fullscreen' ($full.state.type -eq 'fullscreen') "it is $($full.state.type)"
    Check 'and covers more than the work area' ($full.height -ge 2160) "it is $($full.height) tall"

    Shim "command --id $($full.id) toggle-fullscreen" | Out-Null
    Start-Sleep -Milliseconds 800
    $tiled = @(SmokeWindows (Shim 'query windows') | Where-Object { $_.title.Trim() -eq $titles[0] })[0]
    Check 'and goes back to tiling' ($tiled.state.type -eq 'tiling') "it is $($tiled.state.type)"

    Step 'Stopping AkuWM puts them back where it found them'
    $beforeStop = @(SmokeWindows (Shim 'query windows'))
    Akuwm 'rescue' | Out-Null
    Start-Sleep -Seconds 2

    $stillRunning = Get-Process -Name akuwm -ErrorAction SilentlyContinue
    Check 'the daemon stopped' (-not $stillRunning) 'it is still running'

    $ledger = Join-Path $state 'akuwm\cloaked.json'
    $left = if (Test-Path $ledger) { (Get-Content $ledger -Raw | ConvertFrom-Json).Count } else { 0 }
    Check 'nothing is left hidden' ($left -eq 0) "$left window(s) still in the ledger"
}
finally {
    Get-Process -Name akuwm -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $env:LOCALAPPDATA = $env:LOCALAPPDATA_ORIGINAL

    if (-not $KeepWindows) {
        # Closed by message, by handle. A console window on this machine is
        # hosted by Windows Terminal, one process for all of them, so killing
        # by process name would take the person's terminals with it.
        Step 'Closing the three windows'
        Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class SmokeClose {
  [DllImport("user32.dll")] public static extern bool EnumWindows(Proc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public delegate bool Proc(IntPtr h, IntPtr l);
  public static string Title(IntPtr h){ var s = new StringBuilder(512); GetWindowTextW(h, s, 512); return s.ToString(); }
  public static void CloseStartingWith(string prefix){
    EnumWindows((h, l) => { if (Title(h).StartsWith(prefix)) PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero);
  }
}
"@ -ErrorAction SilentlyContinue
        [SmokeClose]::CloseStartingWith('AKUWM SMOKE')
    }

    if ($paused) {
        Step 'Letting the other window manager move again'
        & $glazewm command wm-toggle-pause | Out-Null
        Note 'unpaused'
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'smoke-live: everything passed' -ForegroundColor Green
    exit 0
}

Write-Host "smoke-live: $($failures.Count) check(s) failed" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
