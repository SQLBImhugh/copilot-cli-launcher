<#
.SYNOPSIS
    Build CopilotLauncher.exe as a self-contained single-file Windows binary.

.DESCRIPTION
    Wraps `dotnet publish` with the right flags for a portable distributable.
    Output: dist/CopilotLauncher/CopilotLauncher.exe (+ a few satellite files).

.EXAMPLE
    pwsh scripts\build.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist\CopilotLauncher')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$proj = Join-Path $repoRoot 'src\CopilotLauncher\CopilotLauncher.csproj'

Write-Host "Publishing $proj" -ForegroundColor Cyan
Write-Host "  configuration: $Configuration"
Write-Host "  runtime:       $Runtime"
Write-Host "  output:        $OutDir"
Write-Host ''

dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $OutDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $OutDir 'CopilotLauncher.exe'
if (Test-Path $exe) {
    $sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Built $exe ($sizeMB MB)" -ForegroundColor Green
}
