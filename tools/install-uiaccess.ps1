<#
.SYNOPSIS
    Signs akuwm.exe and installs it where Windows will grant it uiAccess.

.DESCRIPTION
    AkuWM's manifest asks for uiAccess, which is what lets its keyboard hook see
    the keys typed into a window running at a higher integrity level -- every
    game. Measured on this desk without it: an elevated window in front for 14
    of 30 seconds, typed into, and not one keystroke reached the hook.

    Windows grants uiAccess only when BOTH of these are true, and refuses to
    start the process at all when either is missing:

      1. the binary is Authenticode-signed with a certificate the machine
         trusts, and
      2. it sits in a secure directory -- one non-administrators cannot write
         to, which in practice means %ProgramFiles% or System32.

    This script does both, the same way the AutoHotkey installer does for
    AutoHotkey64_UIA.exe: it makes a code-signing certificate that exists only
    on this machine, trusts it, signs the binary with it, and copies the result
    into Program Files.

    The certificate is local and self-signed on purpose. A shared signature
    could be blacklisted once for everyone who runs AkuWM; one made here signs
    one machine's binary and nothing else. It also means AkuWM has no
    reputation with any anti-cheat vendor -- see docs/input-and-anticheat.md,
    which says plainly what AkuWM does to input and what it never does.

.PARAMETER Source
    The freshly published akuwm.exe. Defaults to ..\publish\akuwm.exe.

.PARAMETER Destination
    Where to install. Must be a directory non-administrators cannot write to.

.PARAMETER Force
    Replace an installed akuwm.exe that is currently running, after stopping it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install-uiaccess.ps1 -Source C:\path\to\akuwm.exe
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\akuwm.exe'),
    [string] $Shim = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\glazewm.exe'),
    [string] $Destination = (Join-Path $env:ProgramFiles 'AkuWM'),
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$subject = 'CN=AkuWM Local Signing'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Note($text) { Write-Host "    $text" -ForegroundColor DarkGray }
function Good($text) { Write-Host "    $text" -ForegroundColor Green }
function Bad ($text) { Write-Host "    $text" -ForegroundColor Red }

if (-not (Test-Path $Source)) {
    throw "no akuwm.exe at $Source. Publish it first: dotnet publish src/AkuWM.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish"
}

# --- 1. a certificate that exists only on this machine ----------------------
Step 'Code-signing certificate'
$cert = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Select-Object -First 1

if ($cert) {
    Good "already there, valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))"
} else {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation Cert:\LocalMachine\My `
        -KeyUsage DigitalSignature `
        -KeyLength 2048 `
        -KeyAlgorithm RSA `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(10)
    Good "created, thumbprint $($cert.Thumbprint)"
}

# Trusting it is what makes the signature valid on this machine: Root so the
# chain verifies, TrustedPublisher so nothing prompts about the publisher.
Step 'Trusting it, on this machine only'
$exported = Join-Path $env:TEMP 'akuwm-signing.cer'
Export-Certificate -Cert $cert -FilePath $exported -Force | Out-Null
foreach ($store in 'Root', 'TrustedPublisher') {
    if (-not (Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Thumbprint -eq $cert.Thumbprint)) {
        Import-Certificate -FilePath $exported -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        Good "added to LocalMachine\$store"
    } else {
        Note "already in LocalMachine\$store"
    }
}
Remove-Item $exported -ErrorAction SilentlyContinue

# --- 1b. is this even the right binary? -------------------------------------
# Choosing a manifest changes no file's timestamp, so an incremental build will
# happily hand you an executable built with the other one. That is how a binary
# asking for uiAccess="false" once ended up signed and installed in Program
# Files, quietly refusing every chord over a game. The manifest sits
# uncompressed in the PE resources, so checking is one read.
Step 'Checking the binary actually asks for uiAccess'
$bytes = [System.IO.File]::ReadAllBytes($Source)
$text  = [System.Text.Encoding]::ASCII.GetString($bytes)
if ($text -notmatch 'uiAccess="true"') {
    Bad 'this build does not ask for uiAccess.'
    Note 'It was published with -p:UiAccess=false, which is the development manifest.'
    Note 'Publish without that switch and run this again.'
    exit 1
}
Good 'it does'

# --- 2. a secure directory --------------------------------------------------
Step "Installing to $Destination"
$target = Join-Path $Destination 'akuwm.exe'

if (Test-Path $target) {
    $running = Get-Process -Name akuwm -ErrorAction SilentlyContinue |
               Where-Object { $_.Path -eq $target }
    if ($running) {
        if (-not $Force) {
            throw "$target is running (pid $($running.Id -join ', ')). Stop it, or pass -Force."
        }
        Note 'stopping the running AkuWM'
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path $Source -Destination $target -Force
Good "copied $([math]::Round((Get-Item $target).Length / 1MB)) MB"

# --- 3. the signature -------------------------------------------------------
Step 'Signing'
# Timestamping keeps the signature valid after the certificate expires. It
# needs the network, and not having it is not a reason to fail.
$signature = $null
try {
    $signature = Set-AuthenticodeSignature -FilePath $target -Certificate $cert `
                    -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
} catch {
    Note "no timestamp server ($($_.Exception.Message.Trim())); signing without one"
}
if (-not $signature -or $signature.Status -ne 'Valid') {
    $signature = Set-AuthenticodeSignature -FilePath $target -Certificate $cert -HashAlgorithm SHA256
}

if ($signature.Status -ne 'Valid') {
    Bad "the signature is $($signature.Status): $($signature.StatusMessage)"
    exit 1
}
Good "signed, status $($signature.Status)"

# --- 3a. the drop-in CLI ----------------------------------------------------
# The same words in, the same JSON out, answered by AkuWM. It is what the
# hotkey script and the test suites call, and it is why nothing outside AkuWM
# has to change for AkuWM to take over.
Step 'Installing the glazewm shim'
if (Test-Path $Shim) {
    $shimTarget = Join-Path $Destination 'glazewm.exe'
    Get-Process -Name glazewm -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $shimTarget } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Copy-Item $Shim $shimTarget -Force
    Good "installed $shimTarget"
} else {
    Note "no glazewm.exe at $Shim; the hotkeys will not reach AkuWM until it is built"
}

# --- 3a. reachable by name --------------------------------------------------
# On the USER's PATH, not the machine's: a per-user tool, and it needs no
# elevation to change. The variable is read raw so an expanded %SystemRoot% is
# not written back as a literal, which is how a PATH gets corrupted by a script
# that only meant to add one entry.
Step 'Putting AkuWM on the PATH'

$key = 'HKCU:\Environment'
$current = (Get-ItemProperty -Path $key -Name Path -ErrorAction SilentlyContinue).Path
$entries = @($current -split ';' | Where-Object { $_ })

if ($entries -contains $Destination) {
    Note "$Destination is already on the PATH"
} else {
    $kind = (Get-Item $key).GetValueKind('Path')
    if (-not $kind) { $kind = 'ExpandString' }
    Set-ItemProperty -Path $key -Name Path -Value (($entries + $Destination) -join ';') -Type $kind
    Good "added $Destination to your PATH"
    Note 'open a new terminal for it to take effect'

    # Tell the shell, so anything started from now on sees it without a logout.
    $signature = @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
    uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    $native = Add-Type -MemberDefinition $signature -Name 'AkuWmEnv' -Namespace 'AkuWM' -PassThru
    [UIntPtr]$unused = [UIntPtr]::Zero
    [void]$native::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 3000, [ref]$unused)
}

# --- 3b. the way out --------------------------------------------------------
# A window manager that can hide windows has to ship the button that gives them
# back, and that button has to be reachable when the window manager itself is
# the problem -- so it is a file on the Desktop, not a chord AkuWM handles.
Step 'Installing the rescue button'

$rescueSource = Join-Path $PSScriptRoot 'akuwm-rescue.cmd'
if (Test-Path $rescueSource) {
    $rescueTarget = Join-Path (Split-Path $target -Parent) 'akuwm-rescue.cmd'
    Copy-Item $rescueSource $rescueTarget -Force
    Good "installed $rescueTarget"

    $desktop = [Environment]::GetFolderPath('Desktop')
    $link = Join-Path $desktop 'Rescue my desk (AkuWM).lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $rescueTarget
    $shortcut.WorkingDirectory = Split-Path $target -Parent
    $shortcut.Description = 'Stop AkuWM and put every window back where it was'
    $shortcut.Save()
    Good "put 'Rescue my desk (AkuWM)' on the Desktop"
} else {
    Note "no akuwm-rescue.cmd next to this script; skipping the Desktop button"
}

# --- 4. does Windows agree? -------------------------------------------------
Step 'Checking that Windows actually grants uiAccess'
Note 'starting the installed binary and asking it what its token says'

# A uiAccess process is launched through AppInfo, exactly like an elevated one,
# and its standard output cannot be redirected by whoever starts it: PowerShell
# answers "the requested operation requires elevation" and hands back nothing.
# That is also the proof it worked, and it is why AkuWM writes the answer to a
# file of its own instead.
$report = Join-Path $env:TEMP 'akuwm-doctor.txt'
Remove-Item $report -ErrorAction SilentlyContinue
Start-Process -FilePath $target -ArgumentList 'doctor', '--out', "`"$report`"" -Wait -WindowStyle Hidden
$line = if (Test-Path $report) {
    Get-Content $report | Where-Object { $_ -match 'uiAccess' } | Select-Object -First 1
} else { $null }

if ($line -match 'granted:') {
    Good $line.Trim()
    Write-Host ''
    Write-Host "AkuWM is installed at $target and has uiAccess." -ForegroundColor Green
    Write-Host "Its keyboard hook will see keys typed into a game." -ForegroundColor Green
    Write-Host "What it does with them: docs\input-and-anticheat.md" -ForegroundColor DarkGray
    exit 0
}

if ($line) { Bad $line.Trim() } else { Bad 'doctor did not report on uiAccess' }
Write-Host ''
Write-Host 'Windows started the process but did not grant uiAccess. The usual causes:' -ForegroundColor Yellow
Write-Host '  - the destination is writable by non-administrators (it must not be)' -ForegroundColor Yellow
Write-Host '  - the certificate did not reach LocalMachine\Root' -ForegroundColor Yellow
Write-Host '  - the binary was modified after signing' -ForegroundColor Yellow
exit 1
