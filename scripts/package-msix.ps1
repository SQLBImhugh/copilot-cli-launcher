<#
.SYNOPSIS
    Build + sign + package the launcher as an MSIX.

.DESCRIPTION
    Repeatable wrapper around the `winapp` CLI (Microsoft.WinAppCli, see
    `winui-packaging` skill). Produces a SIGNED MSIX at:
        dist\CopilotLauncher.msix
    using the self-signed dev cert at:
        build\msix-staging\devcert.pfx  (default password: "password")

    Workflow (idempotent — safe to re-run):
      1. Publish the WinUI app as LOOSE FILES (NOT single-file) to
         dist\CopilotLauncher-msix-loose\. MSIX activation goes through
         combase, which looks up WinRT class DLLs (Microsoft.UI.Xaml.dll
         etc.) relative to the package install root — single-file
         extraction to %TEMP% breaks that lookup with E_FAIL.
      2. Stage loose output + Assets/ + manifest into build\msix-staging\.
      3. Generate Package.appxmanifest if missing.
      4. Generate dev cert if missing.
      5. Run `winapp package` to create + sign the .msix.

    First-time install (one-time, requires admin elevation for the cert):
        winapp cert install build\msix-staging\devcert.pfx
        Add-AppxPackage dist\CopilotLauncher.msix

    For production, replace devcert.pfx with a real CA-signed cert and add
    --timestamp http://timestamp.digicert.com to the package call.

.EXAMPLE
    pwsh scripts\package-msix.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.1.9.0',
    [string]$Publisher = 'CN=SQLBImhugh',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

# Verify winapp is on PATH (installed by the winui-setup skill).
$winapp = Get-Command winapp -ErrorAction SilentlyContinue
if (-not $winapp) {
    Write-Host "ERROR: winapp CLI not found. Install it via:" -ForegroundColor Red
    Write-Host "  winget install Microsoft.WinAppCli" -ForegroundColor Red
    exit 1
}

$looseDir = Join-Path $repoRoot 'dist\CopilotLauncher-msix-loose'

if (-not $SkipPublish) {
    Write-Host "Step 1: publishing loose-file output for MSIX packaging..." -ForegroundColor Cyan

    # Stop any running launcher so we can rewrite the loose output.
    foreach ($p in (Get-Process CopilotLauncher -ErrorAction SilentlyContinue)) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction Stop; Start-Sleep -Milliseconds 200 } catch {}
    }
    if (Test-Path $looseDir) { Remove-Item -Recurse -Force $looseDir }

    # Find VS MSBuild via vswhere — the .NET CLI MSBuild can't see the
    # AppxPackage / MrtCore tasks the WinUI publish needs.
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        Write-Host "ERROR: Visual Studio installer not found at $vswhere" -ForegroundColor Red
        exit 1
    }
    $msbuild = & $vswhere -latest -find 'MSBuild\**\Bin\MSBuild.exe' -prerelease | Select-Object -First 1
    if (-not $msbuild -or -not (Test-Path $msbuild)) {
        Write-Host "ERROR: MSBuild.exe not found via vswhere" -ForegroundColor Red
        exit 1
    }

    # Loose-file publish — self-contained .NET 8 + self-contained WinAppSDK,
    # but NOT single-file. We need Microsoft.UI.Xaml.dll, WindowsAppRuntime
    # DLLs, etc. sitting next to the exe so combase can resolve them via the
    # package's WinRT class registrations.
    $proj = Join-Path $repoRoot 'src\CopilotLauncher\CopilotLauncher.csproj'
    $absLoose = [System.IO.Path]::GetFullPath($looseDir) + '\'
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
        /p:RuntimeIdentifier=win-x64 `
        /p:SelfContained=true `
        /p:PublishSingleFile=false `
        /p:WindowsAppSDKSelfContained=true `
        /p:WindowsPackageType=None `
        /p:EnableMsixTooling=true `
        /p:PublishDir=$absLoose `
        /v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path (Join-Path $looseDir 'CopilotLauncher.exe'))) {
    Write-Host "ERROR: $looseDir\CopilotLauncher.exe missing — rerun without -SkipPublish." -ForegroundColor Red
    exit 1
}
# Sanity check: must have the WinUI runtime DLLs loose alongside the exe.
foreach ($mustHave in 'Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.dll') {
    if (-not (Test-Path (Join-Path $looseDir $mustHave))) {
        Write-Host "ERROR: $mustHave missing from loose publish — would crash with E_FAIL on launch." -ForegroundColor Red
        exit 1
    }
}

# Stage layout.
$pkgDir = Join-Path $repoRoot 'build\msix-staging'
if (Test-Path $pkgDir) {
    Get-ChildItem $pkgDir -Exclude 'devcert.pfx','Package.appxmanifest' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $pkgDir -Force | Out-Null

Write-Host "Step 2: staging loose publish + Assets..." -ForegroundColor Cyan
Copy-Item (Join-Path $looseDir '*') $pkgDir -Recurse -Force
# Assets is already in the loose publish via the .csproj's <Content> entries,
# but copy explicitly in case a future publish layout drops them.
Copy-Item (Join-Path $repoRoot 'src\CopilotLauncher\Assets') $pkgDir -Recurse -Force

# Generate manifest if missing (or re-generate to pick up Version changes).
$manifest = Join-Path $pkgDir 'Package.appxmanifest'
Write-Host "Step 3: generating Package.appxmanifest..." -ForegroundColor Cyan
Push-Location $pkgDir
try {
    & winapp manifest generate `
        --package-name 'CopilotLauncher' `
        --publisher-name $Publisher `
        --version $Version `
        --description 'Copilot CLI Launcher — modern WinUI 3 app to manage and resume GitHub Copilot CLI sessions' `
        --executable 'CopilotLauncher.exe' `
        --logo-path 'Assets\AppIcon.png' `
        --template Packaged `
        --if-exists Overwrite `
        --quiet
    if ($LASTEXITCODE -ne 0) { throw "winapp manifest generate failed" }

    # Patch the generated manifest:
    #   - Replace MSBuild $targetnametoken$.exe with the literal exe name
    #     (we're packaging a static layout, not building from project).
    #   - Set human-readable DisplayName.
    $content = Get-Content $manifest -Raw
    $content = $content -replace 'Executable="\$targetnametoken\$\.exe"', 'Executable="CopilotLauncher.exe"'
    $content = $content -replace '<DisplayName>CopilotLauncher</DisplayName>', '<DisplayName>Copilot CLI Launcher</DisplayName>'
    $content = $content -replace 'DisplayName="CopilotLauncher"', 'DisplayName="Copilot CLI Launcher"'
    $content | Set-Content $manifest -Encoding utf8

    # Generate dev cert if missing (--if-exists Skip preserves existing).
    $cert = Join-Path $pkgDir 'devcert.pfx'
    if (-not (Test-Path $cert)) {
        Write-Host "Step 4: generating dev certificate..." -ForegroundColor Cyan
        & winapp cert generate `
            --manifest $manifest `
            --output $cert `
            --quiet
        if ($LASTEXITCODE -ne 0) { throw "winapp cert generate failed" }
    } else {
        Write-Host "Step 4: dev certificate exists, skipping" -ForegroundColor DarkGray
    }

    Write-Host "Step 5: packaging + signing..." -ForegroundColor Cyan
    $msix = Join-Path $repoRoot 'dist\CopilotLauncher.msix'
    & winapp package . `
        --manifest $manifest `
        --cert $cert `
        --cert-password password `
        --exe CopilotLauncher.exe `
        --output $msix `
        --quiet
    if ($LASTEXITCODE -ne 0) { throw "winapp package failed" }

    if (Test-Path $msix) {
        $sizeMB = [math]::Round((Get-Item $msix).Length / 1MB, 1)
        Write-Host ""
        Write-Host "Packaged $msix ($sizeMB MB)" -ForegroundColor Green
        Write-Host ""
        Write-Host "To install (one-time admin step for the cert):" -ForegroundColor DarkGray
        Write-Host "  winapp cert install $cert" -ForegroundColor DarkGray
        Write-Host "  Add-AppxPackage $msix" -ForegroundColor DarkGray
    }
}
finally {
    Pop-Location
}
