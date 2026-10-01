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

# Not /releases/latest: eighteen minutes after v0.2.4 was published it still
# answered v0.2.3 (2026-10-01 13:03), and the desk reinstalled the old one.
# The list is current; the newest published, non-draft, non-prerelease wins.
$headers = @{ 'User-Agent' = 'akuwm-bootstrap'; 'Cache-Control' = 'no-cache' }
Write-Host "==> Asking GitHub for $(if ($Tag) { $Tag } else { 'the newest release' }) of $Repo" -ForegroundColor Cyan
if ($Tag) {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $headers
} else {
    # PowerShell 5.1 hands a JSON array to the pipeline as ONE object; @() unrolls it.
    $all = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=10" -Headers $headers
    $release = @($all) |
        Where-Object { -not $_.draft -and -not $_.prerelease } |
        Sort-Object { [datetime]$_.published_at } -Descending |
        Select-Object -First 1
    if (-not $release) { throw "no published release found for $Repo" }
}
$asset = $release.assets | Where-Object { $_.name -like 'akuwm-*-win-x64.zip' } | Select-Object -First 1
if (-not $asset) { throw "release $($release.tag_name) has no akuwm-*-win-x64.zip asset" }
Write-Host "    $($release.tag_name): $($asset.name) ($([math]::Round($asset.size / 1MB)) MB)"

# A clean staging folder: an old installer script next to new exes is how a
# half-updated install happens. Storage Sense cleans %TEMP% on its own too.
if (Test-Path $Into) { Remove-Item $Into -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Into | Out-Null
$zip = Join-Path $env:TEMP $asset.name
Write-Host "==> Downloading to $zip" -ForegroundColor Cyan
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers $headers
Write-Host "==> Unpacking into $Into" -ForegroundColor Cyan
Expand-Archive -Path $zip -DestinationPath $Into -Force
Remove-Item $zip -ErrorAction SilentlyContinue

$installer = Join-Path $Into 'uia-install.ps1'
if (-not (Test-Path $installer)) { throw "the release has no uia-install.ps1 (got: $((Get-ChildItem $Into).Name -join ', '))" }
Write-Host "==> Running the installer (one UAC prompt)" -ForegroundColor Cyan
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
