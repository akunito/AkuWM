# Installs or updates AkuWM on this machine from a GitHub release: downloads
# the win-x64 zip, unpacks it where the installer expects it, and runs the
# self-elevating installer (one UAC prompt). Idempotent: run it again for the
# next release.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File bootstrap.ps1            # latest release
#   powershell -NoProfile -ExecutionPolicy Bypass -File bootstrap.ps1 -Tag v0.2.0
#
# Or, with nothing checked out yet:
#   irm https://raw.githubusercontent.com/akunito/AkuWM/main/tools/bootstrap.ps1 | iex
param(
    [string] $Repo = 'akunito/AkuWM',
    [string] $Tag = '',
    [string] $Into = (Join-Path $env:LOCALAPPDATA 'Temp\akuwm-uia')
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$api = if ($Tag) { "https://api.github.com/repos/$Repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$Repo/releases/latest" }
Write-Host "==> Asking GitHub for $(if ($Tag) { $Tag } else { 'the latest release' }) of $Repo" -ForegroundColor Cyan
$release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'akuwm-bootstrap' }
$asset = $release.assets | Where-Object { $_.name -like 'akuwm-*-win-x64.zip' } | Select-Object -First 1
if (-not $asset) { throw "release $($release.tag_name) has no akuwm-*-win-x64.zip asset" }
Write-Host "    $($release.tag_name): $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)"

# A clean staging folder: an old installer script next to new exes is how a
# half-updated install happens. Storage Sense cleans %TEMP% on its own too.
if (Test-Path $Into) { Remove-Item $Into -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Into | Out-Null
$zip = Join-Path $env:TEMP $asset.name
Write-Host "==> Downloading to $zip" -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers @{ 'User-Agent' = 'akuwm-bootstrap' }
Write-Host "==> Unpacking into $Into" -ForegroundColor Cyan
Expand-Archive -Path $zip -DestinationPath $Into -Force
Remove-Item $zip -ErrorAction SilentlyContinue

$installer = Join-Path $Into 'uia-install.ps1'
if (-not (Test-Path $installer)) { throw "the release has no uia-install.ps1 (got: $((Get-ChildItem $Into).Name -join ', '))" }
Write-Host "==> Running the installer (one UAC prompt)" -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
