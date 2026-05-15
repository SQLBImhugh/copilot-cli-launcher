<#
.SYNOPSIS
    One-command local validation: restore + build Core + run all xUnit tests.

.DESCRIPTION
    Use this as your "did I break anything" check before pushing. Takes
    a few seconds on a warm machine. Replaces the GH Actions push-trigger
    workflow we used to run on every commit (which was burning quota minutes).

    Does NOT build the WinUI 3 app project (src/CopilotLauncher) because
    that requires Visual Studio Build Tools — not available on every
    contributor machine. To build the full app + .exe locally, install
    VS 2022 / VS Build Tools with the "Windows application development"
    workload, then run scripts/build.ps1.

.EXAMPLE
    pwsh scripts/test.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

Write-Host ''
Write-Host '── Restore ───────────────────────────────────────────────' -ForegroundColor Cyan
dotnet restore src/CopilotLauncher.Core/CopilotLauncher.Core.csproj
dotnet restore tests/CopilotLauncher.Tests/CopilotLauncher.Tests.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host '── Build Core ────────────────────────────────────────────' -ForegroundColor Cyan
dotnet build src/CopilotLauncher.Core/CopilotLauncher.Core.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host '── Test ──────────────────────────────────────────────────' -ForegroundColor Cyan
dotnet test tests/CopilotLauncher.Tests/CopilotLauncher.Tests.csproj -c Release --no-restore --verbosity minimal
$testExit = $LASTEXITCODE

Write-Host ''
if ($testExit -eq 0) {
    Write-Host '✓ All checks passed' -ForegroundColor Green
} else {
    Write-Host '✗ Tests failed' -ForegroundColor Red
}
exit $testExit
