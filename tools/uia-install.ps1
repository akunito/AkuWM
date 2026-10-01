# Elevated: graceful exit of the running AkuWM, signed install into Program
# Files, Startup pointed at it, restart through the shell. Log in akuwm-diag.
# Self-elevating: run from any PowerShell, answer the UAC prompt. Nothing
# (not even the graceful exit) happens before the elevation is granted.
# Lives in the repo since 2026-09-30: the copy in %TEMP%\akuwm-uia vanished
# once (a Temp clean-up), and publish-uia.sh puts it back with every staging.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    Get-Content "$env:LOCALAPPDATA\Temp\akuwm-diag\uia-install.log" -EA SilentlyContinue | Select-String "daemon gone|granted|signed|installer exit|Program Files"
    exit
}
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\Temp\akuwm-diag" | Out-Null
$log = "$env:LOCALAPPDATA\Temp\akuwm-diag\uia-install.log"
Start-Transcript -Path $log -Force | Out-Null
$here = "$env:LOCALAPPDATA\Temp\akuwm-uia"
# Whichever copy is running answers `exit`: the installed one, or the dev
# copy in Programs\AkuWM. Never the uiAccess exe from this shell for
# anything else (it cannot redirect its output).
$cli = "$here\akuwm-cli.exe"
if (Get-Process akuwm -ErrorAction SilentlyContinue) {
    & $cli exit | Out-Null
    $t = 0; while ((Get-Process akuwm -ErrorAction SilentlyContinue) -and $t -lt 60) { Start-Sleep -Milliseconds 500; $t++ }
    "daemon gone after {0} ms" -f ($t * 500)
}
# Whatever the installer does, the daemon comes back: a terminating error
# inside it (a locked akuwm-cli.exe, 2026-10-01 11:51) ended this script
# before the restart and left the desk without a window manager.
try {
    & "$here\tools\install-uiaccess.ps1" -Source "$here\akuwm.exe" -Shim "$here\glazewm.exe" -Force
    "installer exit code $LASTEXITCODE"
} catch {
    "installer failed: $_"
} finally {
    explorer.exe "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\AkuWM.lnk"
}
$t = 0; while (-not (Get-Process akuwm -ErrorAction SilentlyContinue) -and $t -lt 60) { Start-Sleep -Milliseconds 500; $t++ }
Start-Sleep -Seconds 5
Get-Process akuwm -ErrorAction SilentlyContinue | Select-Object Id, Path | Format-Table -AutoSize | Out-String
Stop-Transcript | Out-Null
