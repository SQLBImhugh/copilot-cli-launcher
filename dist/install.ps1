# Copilot CLI Launcher 2.0 — one-liner web installer.
#
# Usage:
#   iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/dist/install.ps1 | iex
#
# Downloads the latest GitHub Release zip, extracts to
# %LOCALAPPDATA%\CopilotLauncher\app\, creates a Start Menu shortcut, and
# launches the app. No admin / .NET / Windows App Runtime install required —
# the distributable .exe is fully self-contained.
#
# This file is plain ASCII (no BOM), no `param()` block, so `iex` can
# evaluate it cleanly. The file it downloads (CopilotLauncher-portable.zip)
# is binary, no encoding concerns there.

$ErrorActionPreference = 'Stop'
$repoOwner = 'SQLBImhugh'
$repoName  = 'copilot-cli-launcher'
$installRoot = Join-Path $env:LOCALAPPDATA 'CopilotLauncher\app'
$tempZip = Join-Path $env:TEMP 'CopilotLauncher-portable.zip'

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {}

Write-Host ''
Write-Host 'Copilot CLI Launcher 2.0 - installer' -ForegroundColor Cyan
Write-Host ''

# Discover the latest release via the GitHub API.
Write-Host 'Looking up the latest release...' -ForegroundColor DarkGray
$apiUrl = "https://api.github.com/repos/$repoOwner/$repoName/releases/latest"
try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'CopilotLauncher-installer' }
} catch {
    Write-Host ''
    Write-Host "ERROR: Could not fetch release info from $apiUrl" -ForegroundColor Red
    Write-Host "  $_" -ForegroundColor Red
    Write-Host ''
    Write-Host 'If no GitHub Release has been published yet, you can grab a pre-release' -ForegroundColor Yellow
    Write-Host 'build artifact from the Actions tab instead:' -ForegroundColor Yellow
    Write-Host "  https://github.com/$repoOwner/$repoName/actions/workflows/ci.yml" -ForegroundColor Yellow
    exit 1
}

$asset = $release.assets | Where-Object { $_.name -like 'CopilotLauncher-portable*.zip' } | Select-Object -First 1
if (-not $asset) {
    Write-Host "ERROR: No CopilotLauncher-portable*.zip asset on release '$($release.tag_name)'." -ForegroundColor Red
    exit 1
}
$zipUrl = $asset.browser_download_url
$sizeMB = [math]::Round($asset.size / 1MB, 1)
Write-Host "Found release '$($release.tag_name)' ($sizeMB MB)" -ForegroundColor DarkGray

# Download.
Write-Host "Downloading $($asset.name)..." -ForegroundColor Cyan
try {
    Invoke-WebRequest -UseBasicParsing -Uri $zipUrl -OutFile $tempZip
} catch {
    Write-Host "ERROR: Download failed: $_" -ForegroundColor Red
    exit 1
}

# Clean any prior install in the same location, then extract.
if (Test-Path $installRoot) {
    Write-Host "Cleaning prior install at $installRoot..." -ForegroundColor DarkGray
    try {
        # Best-effort: stop a running CopilotLauncher first so we don't fail to delete.
        foreach ($p in (Get-Process CopilotLauncher -ErrorAction SilentlyContinue)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {}
        }
        Start-Sleep -Milliseconds 400
        Remove-Item -Recurse -Force $installRoot
    } catch {
        Write-Host "  ! Could not fully clean prior install: $_" -ForegroundColor DarkYellow
        Write-Host "    Proceeding anyway; some files may not get updated." -ForegroundColor DarkYellow
    }
}
Write-Host "Extracting to $installRoot..." -ForegroundColor Cyan
$null = New-Item -ItemType Directory -Force -Path $installRoot
Expand-Archive -Path $tempZip -DestinationPath $installRoot -Force
Remove-Item $tempZip -ErrorAction SilentlyContinue

$exe = Join-Path $installRoot 'CopilotLauncher.exe'
if (-not (Test-Path $exe)) {
    Write-Host "ERROR: $exe not found after extracting the release zip." -ForegroundColor Red
    exit 1
}

# Start Menu shortcut.
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Copilot CLI Launcher.lnk'
try {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($startMenuShortcut)
    $sc.TargetPath       = $exe
    $sc.WorkingDirectory = $installRoot
    $sc.Description      = 'Copilot CLI Launcher 2.0'
    $sc.IconLocation     = $exe
    $sc.Save()
    Write-Host "Start Menu shortcut: $startMenuShortcut" -ForegroundColor Green
} catch {
    Write-Host "  ! Could not create Start Menu shortcut: $_" -ForegroundColor DarkYellow
    Write-Host "    You can launch the app from $exe directly." -ForegroundColor DarkYellow
}

Write-Host ''
Write-Host "Installed Copilot CLI Launcher 2.0 to $installRoot" -ForegroundColor Green
Write-Host 'Launching...' -ForegroundColor Cyan
Start-Process -FilePath $exe
