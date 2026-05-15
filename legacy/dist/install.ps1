# GitHub Copilot CLI Launcher - one-liner bootstrap.
#
# Usage:
#   iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/legacy/dist/install.ps1 | iex
#
# Why this file exists:
# The bundled installer (Install-CopilotLauncher.ps1) is written with a
# UTF-8 BOM so Windows PowerShell 5.1 decodes its non-ASCII characters
# correctly when invoked via `pwsh -File`. That same BOM, however, breaks
# `iwr | iex` and `iex "& { $(irm URL) } -args"` because PowerShell's
# Invoke-Expression parser does NOT strip a leading U+FEFF from a string
# it is asked to evaluate. Mis-parses the comment block, throws "Missing
# argument in parameter list", installer never runs.
#
# This bootstrap is plain ASCII (no BOM), has no `param()` block (so iex
# can evaluate it), downloads the bundled installer to a temp file, and
# invokes it via `&` so PowerShell's file-execution path handles the BOM
# correctly. The temp file is cleaned up after install.
#
# To pass parameters (silent mode etc.), don't use this bootstrap; use
# the two-liner documented in the README "Quick install" section.

$ErrorActionPreference = 'Stop'
$installerUrl = 'https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/legacy/dist/Install-CopilotLauncher.ps1'
$tempPath = Join-Path $env:TEMP 'Install-CopilotLauncher.ps1'

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {}

Write-Host ''
Write-Host 'Downloading GitHub Copilot CLI Launcher installer...' -ForegroundColor Cyan
Write-Host "  $installerUrl" -ForegroundColor DarkGray

try {
    Invoke-WebRequest -UseBasicParsing -Uri $installerUrl -OutFile $tempPath
} catch {
    Write-Host ''
    Write-Host "ERROR: Could not download installer: $_" -ForegroundColor Red
    Write-Host '  Check your network, or download dist/Install-CopilotLauncher.ps1 manually from:' -ForegroundColor Red
    Write-Host '  https://github.com/SQLBImhugh/copilot-cli-launcher' -ForegroundColor Red
    exit 1
}

try {
    & $tempPath
} finally {
    Remove-Item $tempPath -Force -ErrorAction SilentlyContinue
}
