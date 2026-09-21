<#
.SYNOPSIS
    Put AkuWM in the Startup folder, or take it out again.

.DESCRIPTION
    akuwm-switch.ps1 deliberately never touches Startup: while AkuWM was being
    built, a reboot coming up on GlazeWM with no code of ours running WAS the
    safety net. This is the deliberate step that changes that, and it is
    reversible with `-Mode off`.

    What it does:
      * copies the build somewhere permanent (%LOCALAPPDATA%\AkuWM), because a
        Startup shortcut must never point into %TEMP%;
      * writes akuwm.lnk into Startup, pointing at akuwm-boot.ps1, which starts
        AkuWM and falls back to GlazeWM if its pipe never answers;
      * moves GlazeWM.lnk aside to GlazeWM.lnk.off IF it is still there, so
        one rename brings the old stack back. On this desk it is already gone
        and GlazeWM is no longer installed, which is why akuwm-boot.ps1 falls
        back to AkuWM's own rescue rather than to another manager.

    The hotkey script needs nothing from this: it asks the pipe who is
    answering and follows whoever that is.

.PARAMETER Mode
    on (default) or off.

.PARAMETER Build
    The published akuwm.exe's directory. Required for -Mode on the first time.

.PARAMETER Shim
    The published glazewm.exe (the drop-in CLI) directory.
#>
param(
    [ValidateSet('on', 'off')] [string] $Mode = 'on',
    [string] $Build,
    [string] $Shim
)

$ErrorActionPreference = 'Stop'
$startup = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
# NOT %LOCALAPPDATA%\AkuWM: Windows does not distinguish case, so that is the
# SAME directory as AkuWM's runtime state -- geometry.bin, the cloak ledger,
# the logs. A build dropped in among them mixes what a rescue may clear with
# what it needs in order to run.
$home_   = Join-Path $env:LOCALAPPDATA 'Programs\AkuWM'
$lnk     = Join-Path $startup 'AkuWM.lnk'
$glazeLnk = Join-Path $startup 'GlazeWM.lnk'
$parked   = "$glazeLnk.off"

if ($Mode -eq 'off') {
    Remove-Item $lnk -ErrorAction SilentlyContinue
    if ((Test-Path $parked) -and -not (Test-Path $glazeLnk)) { Rename-Item $parked 'GlazeWM.lnk' }
    Remove-Item (Join-Path $env:LOCALAPPDATA 'akuwm\wm-cli.txt') -ErrorAction SilentlyContinue
    "autostart off: GlazeWM.lnk is back in Startup, AkuWM.lnk is gone, the marker is cleared."
    "A reboot now comes up on GlazeWM. The copy in $home_ is left alone."
    exit 0
}

if (-not $Build) { throw "-Build is required: the directory holding the published akuwm.exe" }
$src = Join-Path $Build 'akuwm.exe'
if (-not (Test-Path $src)) { throw "no akuwm.exe in $Build" }

# Permanent, because %TEMP% is not. A Startup shortcut into a temp directory is
# a window manager that disappears the first time anything cleans up.
New-Item -ItemType Directory -Force -Path $home_ | Out-Null
Copy-Item $src (Join-Path $home_ 'akuwm.exe') -Force
$bootSrc = Join-Path $PSScriptRoot 'akuwm-boot.ps1'
Copy-Item $bootSrc (Join-Path $home_ 'akuwm-boot.ps1') -Force

if ($Shim) {
    $shimSrc = Join-Path $Shim 'glazewm.exe'
    if (Test-Path $shimSrc) {
        New-Item -ItemType Directory -Force -Path (Join-Path $home_ 'shim') | Out-Null
        Copy-Item $shimSrc (Join-Path $home_ 'shim\glazewm.exe') -Force
    }
}

$pwsh = (Get-Command pwsh.exe -ErrorAction SilentlyContinue).Source
if (-not $pwsh) { $pwsh = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $pwsh
$s.Arguments  = "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $home_ 'akuwm-boot.ps1')`""
$s.WorkingDirectory = $home_
$s.WindowStyle = 7            # minimised: no console in the face at logon
$s.Description = 'AkuWM (falls back to GlazeWM if it does not answer)'
$s.Save()

if ((Test-Path $glazeLnk) -and -not (Test-Path $parked)) { Rename-Item $glazeLnk 'GlazeWM.lnk.off' }

"autostart on:"
"  AkuWM.lnk      -> $pwsh ... akuwm-boot.ps1"
"  build          -> $home_"
"  GlazeWM.lnk    -> parked as GlazeWM.lnk.off"
""
"The way back, from any shell:  akuwm-autostart.ps1 -Mode off"
