<#
.SYNOPSIS
    Build CopilotLauncher.exe locally as a self-contained single-file Windows binary.

.DESCRIPTION
    Wraps Visual Studio's MSBuild (NOT the .NET CLI MSBuild) with the right
    flags for a portable distributable. Output:
    dist\CopilotLauncher\CopilotLauncher.exe (~63 MB self-contained).

    Why MSBuild and not `dotnet build`: the .NET CLI's standalone MSBuild
    cannot find the Microsoft.WindowsAppSDK AppxPackage tasks
    (Microsoft.Build.AppxPackage.dll, Microsoft.Build.Packaging.Pri.Tasks.dll).
    Those ship with Visual Studio, not the .NET SDK. Calling VS's
    MSBuild.exe directly resolves them.

    Requires Visual Studio 2022/2026 (Community/Professional/Enterprise) or
    VS Build Tools with the ".NET WinUI app development tools" workload.

.EXAMPLE
    pwsh scripts\build.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$OutDir        = (Join-Path $PSScriptRoot '..\dist\CopilotLauncher')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

# Find VS MSBuild via vswhere.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Host "ERROR: Visual Studio installer not found at $vswhere" -ForegroundColor Red
    Write-Host "  Install Visual Studio 2022 or VS Build Tools 2022 with the" -ForegroundColor Red
    Write-Host "  '.NET WinUI app development tools' workload." -ForegroundColor Red
    exit 1
}
$msbuild = & $vswhere -latest -find 'MSBuild\**\Bin\MSBuild.exe' -prerelease | Select-Object -First 1
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    Write-Host "ERROR: MSBuild.exe not found via vswhere" -ForegroundColor Red
    Write-Host "  Make sure your VS install includes the .NET / WinUI workload." -ForegroundColor Red
    exit 1
}

$proj = Join-Path $repoRoot 'src\CopilotLauncher\CopilotLauncher.csproj'

# Resolve OutDir to absolute (PublishDir prefers absolute paths).
$absOutDir = [System.IO.Path]::GetFullPath($OutDir)

Write-Host ''
Write-Host "Building $proj" -ForegroundColor Cyan
Write-Host "  msbuild:        $msbuild"
Write-Host "  configuration:  $Configuration"
Write-Host "  runtime:        $Runtime"
Write-Host "  output:         $absOutDir"
Write-Host ''

# Restore + publish in one MSBuild invocation.
& $msbuild $proj `
    /t:Restore `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $msbuild $proj `
    /t:Publish `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /p:RuntimeIdentifier=$Runtime `
    /p:SelfContained=true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:EnableMsixTooling=true `
    /p:WindowsAppSDKSelfContained=true `
    /p:AppxPackage=false `
    /p:WindowsPackageType=None `
    /p:PublishDir="$absOutDir\" `
    /v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $absOutDir 'CopilotLauncher.exe'
if (Test-Path $exe) {
    $sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ''
    Write-Host "✓ Built $exe ($sizeMB MB)" -ForegroundColor Green
    Write-Host '  Double-click the .exe to test, or zip the dist/CopilotLauncher folder to share.' -ForegroundColor DarkGray
}

