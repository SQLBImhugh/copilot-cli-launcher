<#
.SYNOPSIS
    Bundle the Copilot CLI Launcher kit into a single distributable installer.

.DESCRIPTION
    Reads each source file in tools/copilot-launcher/, base64-encodes it, and
    substitutes the encoded content into installer-template.ps1's placeholders.
    Writes the bundled installer to dist/Install-CopilotLauncher.ps1.

    Run after editing any source file to refresh the bundled distribution.

.EXAMPLE
    .\build-installer.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$kitDir   = $PSScriptRoot
$template = Join-Path $kitDir 'installer-template.ps1'
$outDir   = Join-Path $kitDir 'dist'
$outFile  = Join-Path $outDir 'Install-CopilotLauncher.ps1'

if (-not (Test-Path $template)) {
    throw "Template not found: $template"
}

$null = New-Item -ItemType Directory -Force -Path $outDir

function Read-AsBase64 {
    param([string]$Path)
    if (-not (Test-Path $Path)) { throw "Source file not found: $Path" }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return [Convert]::ToBase64String($bytes)
}

# Map placeholders to source files
$payloadMap = [ordered]@{
    'LAUNCH_COPILOT_PS1_B64'    = 'Launch-Copilot.ps1'
    'REPAIR_SESSIONS_PY_B64'    = 'repair-copilot-sessions.py'
    'AGENTS_EXAMPLE_MD_B64'     = 'agents.example.md'
    'CONFIG_EXAMPLE_JSON_B64'   = 'config.example.json'
    'README_MD_B64'             = 'README.md'
}

# Read template
$content = Get-Content $template -Raw

Write-Host 'Bundling Copilot CLI Launcher installer...' -ForegroundColor Cyan
Write-Host ''

# Substitute each payload
$totalBytes = 0
foreach ($placeholder in $payloadMap.Keys) {
    $sourceName = $payloadMap[$placeholder]
    $sourcePath = Join-Path $kitDir $sourceName
    $b64 = Read-AsBase64 -Path $sourcePath
    $sourceSize = (Get-Item $sourcePath).Length
    $encodedSize = $b64.Length
    $totalBytes += $encodedSize
    $content = $content.Replace("{{$placeholder}}", $b64)
    Write-Host ("  {0,-32}  {1,8} -> {2,8} chars (b64)" -f $sourceName, $sourceSize, $encodedSize) -ForegroundColor DarkGray
}

# Substitute build metadata
$buildDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
$repoUrl = ''
try {
    $repoUrl = (& git -C $kitDir config --get remote.origin.url 2>$null).Trim()
} catch {}
if (-not $repoUrl) { $repoUrl = '(unknown)' }

$content = $content.Replace('{{BUILD_DATE}}', $buildDate)
$content = $content.Replace('{{REPO_URL}}', $repoUrl)

# Verify all placeholders were replaced
$leftover = [regex]::Matches($content, '\{\{[A-Z_0-9]+\}\}')
if ($leftover.Count -gt 0) {
    Write-Host ''
    Write-Host 'WARNING: unsubstituted placeholders remain:' -ForegroundColor Yellow
    $leftover | ForEach-Object { Write-Host "  $($_.Value)" -ForegroundColor Yellow }
}

# Write output
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($outFile, $content, $utf8NoBom)

$outSize = (Get-Item $outFile).Length
Write-Host ''
Write-Host "Wrote $outFile" -ForegroundColor Green
Write-Host ("  Total payload size: {0:N0} chars (base64)" -f $totalBytes) -ForegroundColor DarkGray
Write-Host ("  Final installer:    {0:N0} bytes" -f $outSize) -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Test by running:' -ForegroundColor Cyan
Write-Host "  pwsh -ExecutionPolicy Bypass -File `"$outFile`"" -ForegroundColor Gray
