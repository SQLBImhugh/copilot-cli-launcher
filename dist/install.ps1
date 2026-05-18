# Copilot CLI Launcher 2.0 — one-liner web installer.
#
# Default (portable .exe path — no cert prompts, no MSIX registration):
#   iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/dist/install.ps1 | iex
#
# Full MSIX install path (signed packages, registered with Windows,
# uninstall via Settings > Apps; auto-trusts the dev signing cert into
# the current user's TrustedPeople store — NO admin needed):
#   & ([scriptblock]::Create((iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/dist/install.ps1).Content)) -Msix
#
# MSIX path installs BOTH the standalone app and the PowerToys Command
# Palette extension. Use -Msix -SkipCmdPal to install only the main app,
# or -Msix -SkipMain to install only the Command Palette extension.
#
# This file is plain ASCII (no BOM), so `iex` can evaluate it cleanly.
param(
    [switch]$Msix,
    [switch]$SkipMain,
    [switch]$SkipCmdPal
)

$ErrorActionPreference = 'Stop'
$repoOwner = 'SQLBImhugh'
$repoName  = 'copilot-cli-launcher'

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
    exit 1
}
$tag = $release.tag_name

function Get-ReleaseAsset {
    param([string]$Pattern)
    return $release.assets | Where-Object { $_.name -like $Pattern } | Select-Object -First 1
}

function Save-ReleaseAsset {
    param($Asset, [string]$Dest)
    Write-Host "Downloading $($Asset.name) ($([math]::Round($Asset.size / 1MB, 1)) MB)..." -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri $Asset.browser_download_url -OutFile $Dest
}

if (-not $Msix) {
    # ----------------------------------------------------------------------
    # Portable .exe path (original behavior, default)
    # ----------------------------------------------------------------------
    $installRoot = Join-Path $env:LOCALAPPDATA 'CopilotLauncher\app'
    $tempZip = Join-Path $env:TEMP 'CopilotLauncher-portable.zip'

    $asset = Get-ReleaseAsset 'CopilotLauncher-portable*.zip'
    if (-not $asset) {
        Write-Host "ERROR: No CopilotLauncher-portable*.zip asset on release '$tag'." -ForegroundColor Red
        Write-Host 'Tip: pass -Msix to install the signed MSIX packages instead.' -ForegroundColor Yellow
        exit 1
    }
    Write-Host "Found release '$tag'" -ForegroundColor DarkGray

    Save-ReleaseAsset $asset $tempZip

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
    }

    Write-Host ''
    Write-Host "Installed Copilot CLI Launcher 2.0 to $installRoot" -ForegroundColor Green
    Write-Host 'Launching...' -ForegroundColor Cyan
    Start-Process -FilePath $exe
    exit 0
}

# ----------------------------------------------------------------------
# MSIX path (-Msix flag) — trust dev cert + Add-AppxPackage both MSIXs
# ----------------------------------------------------------------------
Write-Host "MSIX install mode (release '$tag')" -ForegroundColor Cyan
Write-Host ''

# Clean up any prior portable install — its Start Menu shortcut shares the
# MSIX's display name ("Copilot CLI Launcher") and would show up as a duplicate
# tile. Best-effort; never abort on failure.
$portableRoot     = Join-Path $env:LOCALAPPDATA 'CopilotLauncher\app'
$portableShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Copilot CLI Launcher.lnk'
$removedPortable  = $false
if (Test-Path $portableShortcut) {
    try {
        $sh = New-Object -ComObject WScript.Shell
        $sc = $sh.CreateShortcut($portableShortcut)
        # Only remove if it points at the portable-install exe (don't nuke
        # an unrelated user-created shortcut with the same filename).
        if ($sc.TargetPath -like "$portableRoot*") {
            Remove-Item $portableShortcut -Force -ErrorAction Stop
            $removedPortable = $true
        }
    } catch {
        Write-Host "  ! Could not remove legacy Start Menu shortcut: $($_.Exception.Message.Split([Environment]::NewLine)[0])" -ForegroundColor DarkYellow
    }
}
if (Test-Path $portableRoot) {
    try {
        foreach ($p in (Get-Process CopilotLauncher -ErrorAction SilentlyContinue)) {
            try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {}
        }
        Start-Sleep -Milliseconds 400
        Remove-Item -Recurse -Force $portableRoot -ErrorAction Stop
        $removedPortable = $true
    } catch {
        Write-Host "  ! Could not remove prior portable install at $portableRoot - $($_.Exception.Message.Split([Environment]::NewLine)[0])" -ForegroundColor DarkYellow
    }
}
if ($removedPortable) {
    Write-Host "Removed prior portable install (avoids duplicate Start Menu tile)." -ForegroundColor DarkGray
}

$tmp = Join-Path $env:TEMP "CopilotLauncherInstall-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# 1. Download + trust the dev cert (no admin needed for CurrentUser store).
$certAsset = Get-ReleaseAsset 'CopilotLauncher-*-DevCert.cer'
if (-not $certAsset) {
    Write-Host "ERROR: No DevCert.cer asset on release '$tag'." -ForegroundColor Red
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    exit 1
}
$certPath = Join-Path $tmp 'devcert.cer'
Save-ReleaseAsset $certAsset $certPath

# Compare thumbprint with whatever's already in TrustedPeople; skip import if same.
$incoming = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $certPath
$alreadyTrusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $incoming.Thumbprint }
if ($alreadyTrusted) {
    Write-Host "Cert $($incoming.Thumbprint.Substring(0,12))... already trusted." -ForegroundColor DarkGray
} else {
    Write-Host "Trusting cert $($incoming.Thumbprint.Substring(0,12))... in Cert:\CurrentUser\TrustedPeople" -ForegroundColor Cyan
    Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}

# 2. Standalone app MSIX
if (-not $SkipMain) {
    $mainAsset = Get-ReleaseAsset 'CopilotLauncher-v*.msix'
    if (-not $mainAsset) {
        Write-Host "  ! No standalone CopilotLauncher-v*.msix asset; skipping." -ForegroundColor DarkYellow
    } else {
        $mainMsix = Join-Path $tmp $mainAsset.name
        Save-ReleaseAsset $mainAsset $mainMsix
        Write-Host "Installing standalone app..." -ForegroundColor Cyan
        try {
            # Stop any running instance so Add-AppxPackage doesn't fail.
            foreach ($p in (Get-Process CopilotLauncher -ErrorAction SilentlyContinue)) {
                try { Stop-Process -Id $p.Id -Force -ErrorAction Stop } catch {}
            }
            Add-AppxPackage -Path $mainMsix -ErrorAction Stop
            Write-Host "  Installed: standalone app" -ForegroundColor Green
        } catch {
            Write-Host "  ! Standalone app install failed: $($_.Exception.Message.Split([Environment]::NewLine)[0])" -ForegroundColor Red
        }
    }
}

# 3. Command Palette extension MSIX
if (-not $SkipCmdPal) {
    $cmdpalAsset = Get-ReleaseAsset 'CopilotLauncher-CmdPal-*.msix'
    if (-not $cmdpalAsset) {
        Write-Host "  ! No CopilotLauncher-CmdPal-*.msix asset; skipping." -ForegroundColor DarkYellow
    } else {
        $cmdpalMsix = Join-Path $tmp $cmdpalAsset.name
        Save-ReleaseAsset $cmdpalAsset $cmdpalMsix
        Write-Host "Installing Command Palette extension..." -ForegroundColor Cyan
        try {
            # Stop the extension's COM-server process if running so Add-AppxPackage doesn't fail.
            Get-Process | Where-Object { $_.Path -like '*CopilotLauncher.CmdPal*' } |
                ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch {} }
            Start-Sleep -Milliseconds 400
            Add-AppxPackage -Path $cmdpalMsix -ErrorAction Stop
            Write-Host "  Installed: Command Palette extension" -ForegroundColor Green
        } catch {
            Write-Host "  ! CmdPal install failed: $($_.Exception.Message.Split([Environment]::NewLine)[0])" -ForegroundColor Red
            Write-Host "    If you see 'in use', close PowerToys Command Palette and retry." -ForegroundColor DarkYellow
        }
    }
}

# 4. Cleanup
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
if (-not $SkipMain) {
    Write-Host '  Standalone app: launch from Start Menu, or:' -ForegroundColor DarkGray
    Write-Host '    Get-AppxPackage CopilotLauncher | foreach { Start-Process "shell:appsFolder\$($_.PackageFamilyName)!App" }' -ForegroundColor DarkGray
}
if (-not $SkipCmdPal) {
    Write-Host '  Command Palette extension: open PT Command Palette (Win+Alt+Space)' -ForegroundColor DarkGray
    Write-Host '    Type "Reload" -> select "Reload Command Palette Extension" -> type "Copilot"' -ForegroundColor DarkGray
}
