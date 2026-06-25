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

# Stop any running CopilotLauncher.exe instances so they don't lock the
# output file during the GenerateBundle step. Self-extracting single-file
# .exes hold a handle on the on-disk .exe even after extraction to %TEMP%,
# which produces an UnauthorizedAccessException at publish time. Best-effort
# cleanup; ignore failures.
foreach ($p in (Get-Process CopilotLauncher -ErrorAction SilentlyContinue)) {
    try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Start-Sleep -Milliseconds 200 } catch {}
}

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

# --- Stage resources.pri next to the .exe --------------------------------
# PublishSingleFile bundles managed + native DLLs into the self-extracting
# .exe, but it does NOT emit resources.pri (it's loose MRT content, never
# bundled). Without resources.pri beside the .exe, Microsoft.UI.Xaml
# fail-fasts at startup (exit 0xC000027B, "Cannot locate resource from
# ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml") and the window
# never appears. Copy the merged PRI the build produced in the intermediate
# output. EnableMsixTooling=true (above + in the .csproj) makes that PRI
# include the framework theme resources, so it's ~1.3 MB; an un-merged,
# app-only PRI is ~38 KB and crashes the exact same way — hence the size
# guard below, which fails the build loudly rather than shipping a crasher.
$binPri = Get-ChildItem (Join-Path $repoRoot "src\CopilotLauncher\bin\x64\$Configuration") `
            -Recurse -Filter 'resources.pri' -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -like "*\$Runtime\resources.pri" } |
          Sort-Object Length -Descending | Select-Object -First 1
if (-not $binPri) {
    Write-Host "ERROR: resources.pri not found in build output." -ForegroundColor Red
    Write-Host "  Without it the published .exe crashes at startup (0xC000027B)." -ForegroundColor Red
    exit 1
}
if ($binPri.Length -lt 200KB) {
    Write-Host "ERROR: resources.pri is only $([math]::Round($binPri.Length/1KB)) KB — the Windows App SDK" -ForegroundColor Red
    Write-Host "  framework theme resources were NOT merged in, so the .exe would crash at" -ForegroundColor Red
    Write-Host "  startup (0xC000027B). This usually means a stale obj/ from a build that ran" -ForegroundColor Red
    Write-Host "  with EnableMsixTooling=false. Delete src\CopilotLauncher\obj and re-run." -ForegroundColor Red
    exit 1
}
Copy-Item $binPri.FullName (Join-Path $absOutDir 'resources.pri') -Force
Write-Host "  staged resources.pri ($([math]::Round($binPri.Length/1KB)) KB) next to the .exe" -ForegroundColor DarkGray

$exe = Join-Path $absOutDir 'CopilotLauncher.exe'
if (Test-Path $exe) {
    $sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ''
    Write-Host "✓ Built $exe ($sizeMB MB)" -ForegroundColor Green
    Write-Host '  Ship the whole dist/CopilotLauncher folder (the .exe needs resources.pri beside it).' -ForegroundColor DarkGray
}

