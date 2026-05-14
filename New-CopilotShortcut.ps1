<#
.SYNOPSIS
    Create an additional Copilot CLI shortcut after install.

.DESCRIPTION
    The installer creates one desktop shortcut. This helper lets you create
    more — typically one per project — without re-running the full installer
    (which would clobber your config.json and agents.md).

    Each shortcut can have its own:
      - Working directory (where Copilot starts; usually a project root)
      - --resume session name (or none, for fresh sessions)
      - AI summary on/off
      - --allow-all on/off
      - Label (becomes the .lnk filename)
      - Output location (Desktop default; can be any folder)

    Run from anywhere; the script self-locates the launcher kit via
    $PSScriptRoot and reads config.json for project-name defaults. It never
    writes to config.json or agents.md.

    Targets PowerShell 7+ (matches Launch-Copilot.ps1 and the installer
    conventions), but uses syntax that also works under Windows PowerShell
    5.1 since shortcuts may fall back to powershell.exe when pwsh is
    unavailable on a user's machine.

.PARAMETER Label
    The shortcut filename label. Becomes "<Label> Copilot.lnk".
    Default in interactive mode: leaf of -WorkingDirectory.

.PARAMETER WorkingDirectory
    Where Copilot starts when the shortcut is double-clicked. Usually your
    project root. Default in interactive mode: current directory.

.PARAMETER ResumeSession
    --resume value passed to copilot. Empty string means no resume (fresh
    session each launch). Default: empty.

.PARAMETER EnableAISummary
    Add -AISummary to the shortcut.

.PARAMETER EnableAllowAll
    Add --allow-all to the shortcut.

.PARAMETER ExtraCopilotArgs
    Optional extra arguments appended to the copilot command line. Useful for
    flags this script doesn't have a dedicated parameter for, such as
    "--max-autopilot-continues 100". Quoted values are preserved as single
    args (e.g. '--prompt "do the thing"').

.PARAMETER UseWindowsTerminal
    Target wt.exe instead of pwsh.exe so Copilot opens in a Windows Terminal
    tab. Equivalent to:
        wt.exe -w 0 -d "<workdir>" "<pwsh>" -NoExit -File "<launcher>" ...
    Default in interactive mode: prompted (yes if wt.exe is on PATH).

.PARAMETER NoWindowsTerminal
    Force-disable Windows Terminal targeting even if wt.exe is on PATH.

.PARAMETER ShortcutDir
    Folder to drop the .lnk into. Default: Desktop.

.PARAMETER Force
    Overwrite an existing shortcut with the same name without asking.

.PARAMETER Silent
    Skip all prompts and use parameter values + defaults.

.EXAMPLE
    # Interactive: prompts for everything, defaults from current dir + config.json
    cd C:\code\my-app
    pwsh "$env:USERPROFILE\copilot-launcher\New-CopilotShortcut.ps1"

.EXAMPLE
    # Silent / scripted, full Windows-Terminal pattern
    pwsh New-CopilotShortcut.ps1 -Silent `
        -Label "MyApp" -WorkingDirectory "C:\code\my-app" `
        -ResumeSession "MyApp-Main" `
        -EnableAISummary -EnableAllowAll `
        -ExtraCopilotArgs "--max-autopilot-continues 100" `
        -UseWindowsTerminal
#>
[CmdletBinding()]
param(
    [string]$Label,
    [string]$WorkingDirectory,
    [string]$ResumeSession,
    [string]$ExtraCopilotArgs,
    [switch]$EnableAISummary,
    [switch]$EnableAllowAll,
    [switch]$UseWindowsTerminal,
    [switch]$NoWindowsTerminal,
    [string]$ShortcutDir,
    [switch]$Force,
    [switch]$Silent
)

try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding           = [System.Text.Encoding]::UTF8
} catch {}

$ErrorActionPreference = 'Stop'
$script:SilentMode = [bool]$Silent

# ============================================================================
# Self-location: install dir is the folder this script lives in.
# ============================================================================

$InstallDir   = $PSScriptRoot
$LauncherPath = Join-Path $InstallDir 'Launch-Copilot.ps1'
$ConfigPath   = Join-Path $InstallDir 'config.json'

if (-not (Test-Path $LauncherPath)) {
    Write-Host ''
    Write-Host "ERROR: Launch-Copilot.ps1 not found next to this script." -ForegroundColor Red
    Write-Host "  Expected:  $LauncherPath" -ForegroundColor Red
    Write-Host "  This script must live in the same folder as the launcher kit." -ForegroundColor Red
    Write-Host "  Re-run the installer if the kit was moved or partially copied." -ForegroundColor Red
    Write-Host ''
    exit 1
}

# Read config.json for sensible defaults. Tolerate missing/corrupt config —
# the new script must work even on a partial install.
$configProjectName = $null
if (Test-Path $ConfigPath) {
    try {
        $cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
        if ($cfg.projectName) { $configProjectName = [string]$cfg.projectName }
    } catch {
        Write-Host "  ! Could not parse $ConfigPath ($($_.Exception.Message)); using defaults." -ForegroundColor DarkYellow
    }
}

# ============================================================================
# Wizard helpers (mirror installer-template.ps1 conventions)
# ============================================================================

function Read-Default {
    param(
        [string]$Prompt,
        [string]$Default,
        [string[]]$Description,
        [switch]$AllowEmpty
    )
    if ($script:SilentMode) {
        return $Default
    }
    if ($Description) {
        Write-Host ''
        foreach ($line in $Description) {
            Write-Host "  $line" -ForegroundColor DarkGray
        }
    }
    $shown = if ($Default) { "[$Default]" } else { '' }
    $resp = Read-Host "$Prompt $shown"
    if ([string]::IsNullOrWhiteSpace($resp)) {
        if (-not $AllowEmpty -and -not $Default) {
            Write-Host "  Value required." -ForegroundColor Yellow
            return Read-Default -Prompt $Prompt -Default $Default -Description $Description -AllowEmpty:$AllowEmpty
        }
        return $Default
    }
    return $resp.Trim()
}

function Read-YesNo {
    param(
        [string]$Prompt,
        [bool]$Default,
        [string[]]$Description
    )
    if ($script:SilentMode) { return $Default }
    if ($Description) {
        Write-Host ''
        foreach ($line in $Description) {
            Write-Host "  $line" -ForegroundColor DarkGray
        }
    }
    $hint = if ($Default) { '[Y/n]' } else { '[y/N]' }
    $resp = Read-Host "$Prompt $hint"
    if ([string]::IsNullOrWhiteSpace($resp)) { return $Default }
    return $resp.Trim().ToLower() -in @('y', 'yes', 'true', '1')
}

function Format-ShortcutArgs {
    # Build the Arguments string for a Windows .lnk so each individual arg
    # survives Windows command-line re-parsing. Wraps any value containing
    # whitespace or double-quotes in double-quotes; embedded quotes are
    # escaped by doubling. Anything safe is passed through unwrapped.
    # (Duplicated from installer-template.ps1 — see the plan: deliberate
    # duplication to avoid bundling a shared helper.)
    # NOTE: parameter is named -Arguments (not -Args). $Args is a PowerShell
    # automatic variable and binding to it silently fails — produces empty
    # output with no error. Don't rename without re-testing the .lnk Arguments.
    param([string[]]$Arguments)
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($a in $Arguments) {
        if ([string]::IsNullOrEmpty($a)) { continue }
        if ($a -match '[\s"]') {
            $escaped = $a -replace '"', '""'
            $out.Add('"' + $escaped + '"')
        } else {
            $out.Add($a)
        }
    }
    return ($out -join ' ')
}

function Split-CommandLine {
    # Tokenize a Windows-style command-line fragment, preserving double-quoted
    # spans as a single token. Used for -ExtraCopilotArgs so users can pass
    # things like '--max-autopilot-continues 100' or '--prompt "do the thing"'
    # and have the words re-quoted correctly by Format-ShortcutArgs.
    param([string]$Line)
    $out = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($Line)) { return ,$out.ToArray() }
    foreach ($m in [regex]::Matches($Line, '"([^"]*)"|(\S+)')) {
        if ($m.Groups[1].Success) {
            $out.Add($m.Groups[1].Value)
        } else {
            $out.Add($m.Groups[2].Value)
        }
    }
    return ,$out.ToArray()
}

function Find-WindowsTerminal {
    # Locate wt.exe. Returns full path or $null.
    $cmd = Get-Command wt.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Test-ValidFilenameLabel {
    # Returns $true if the label is a usable filename component on Windows.
    # Rejects path separators, reserved characters, reserved device names,
    # trailing dots/spaces, and empty strings.
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    if ($Name -match '[\\/:*?"<>|]') { return $false }
    if ($Name -match '[\.\s]$')      { return $false }
    $reserved = @('CON','PRN','AUX','NUL',
                  'COM1','COM2','COM3','COM4','COM5','COM6','COM7','COM8','COM9',
                  'LPT1','LPT2','LPT3','LPT4','LPT5','LPT6','LPT7','LPT8','LPT9')
    if ($reserved -contains $Name.ToUpperInvariant()) { return $false }
    return $true
}

# ============================================================================
# Banner
# ============================================================================

Write-Host ''
Write-Host '====================================================================' -ForegroundColor Cyan
Write-Host '   GitHub Copilot CLI Launcher — New shortcut' -ForegroundColor Cyan
Write-Host '====================================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host "Launcher kit:   $InstallDir" -ForegroundColor DarkGray
if ($configProjectName) {
    Write-Host "Project name:   $configProjectName  (from config.json, used for shortcut description)" -ForegroundColor DarkGray
}
Write-Host ''

# ============================================================================
# Working directory
# ============================================================================

if (-not $WorkingDirectory) {
    $WorkingDirectory = Read-Default -Prompt 'Working directory' `
        -Default ((Get-Location).Path) `
        -Description @(
            'Where Copilot will START when you double-click the shortcut.',
            'Usually your project root, so Copilot can see your project files',
            'and your AGENTS.md.',
            'Default = the folder you ran this script from.'
        )
}
$WorkingDirectory = [Environment]::ExpandEnvironmentVariables($WorkingDirectory)
try {
    $WorkingDirectory = (Resolve-Path -LiteralPath $WorkingDirectory -ErrorAction Stop).Path
} catch {
    Write-Host "  ! Working directory does not exist: $WorkingDirectory" -ForegroundColor Yellow
    Write-Host "    The shortcut will still be created, but launching it will fail until" -ForegroundColor Yellow
    Write-Host "    you create the folder." -ForegroundColor Yellow
}

# ============================================================================
# Label
# ============================================================================

if (-not $Label) {
    $defaultLabel = Split-Path -Leaf $WorkingDirectory
    $Label = Read-Default -Prompt 'Shortcut label' -Default $defaultLabel `
        -Description @(
            'A short label that becomes the .lnk filename: "<Label> Copilot.lnk".',
            'Distinct from the project name baked into config.json — you can have',
            'several shortcuts under different labels all pointing at the same kit.',
            'Use letters, numbers, dashes, spaces. NO / \ : * ? " < > | characters.',
            'Default = the leaf folder name of the working directory above.'
        )
}
if (-not (Test-ValidFilenameLabel -Name $Label)) {
    Write-Host ''
    Write-Host "ERROR: '$Label' is not a valid Windows filename." -ForegroundColor Red
    Write-Host "  Avoid \ / : * ? `" < > | and reserved names (CON, PRN, AUX, NUL, COM1-9, LPT1-9)." -ForegroundColor Red
    Write-Host "  Avoid trailing dots or spaces." -ForegroundColor Red
    exit 1
}

# ============================================================================
# Resume session
# ============================================================================

if (-not $PSBoundParameters.ContainsKey('ResumeSession')) {
    $ResumeSession = Read-Default -Prompt '--resume session name' -Default '' -AllowEmpty `
        -Description @(
            'Optional. If you set a name here, the shortcut passes "--resume <name>"',
            'to copilot, so you keep ONE persistent conversation across launches of',
            'this specific shortcut.',
            'Leave blank to start a fresh Copilot session each launch.',
            'NOTE: the project name (config.json) is NOT auto-used; you set this freely.'
        )
}

# ============================================================================
# AI summary
# ============================================================================

if (-not $PSBoundParameters.ContainsKey('EnableAISummary')) {
    $EnableAISummary = Read-YesNo -Prompt 'Enable AI-authored briefing summary?' -Default $false `
        -Description @(
            'When the Copilot CLI version changes, the launcher can ask an AI model',
            'to summarize the upstream changelog. Each summary costs ONE premium',
            'model request, only on a version bump.'
        )
}

# ============================================================================
# Allow-all
# ============================================================================

if (-not $PSBoundParameters.ContainsKey('EnableAllowAll')) {
    $EnableAllowAll = Read-YesNo -Prompt 'Pass --allow-all to copilot?' -Default $false `
        -Description @(
            'Adds --allow-all so Copilot auto-approves every tool call without asking.',
            'Faster, but bypasses the per-call safety prompt.',
            'Recommended only for scratch / dev environments you trust.'
        )
}

# ============================================================================
# Extra Copilot args
# ============================================================================

if (-not $PSBoundParameters.ContainsKey('ExtraCopilotArgs')) {
    $ExtraCopilotArgs = Read-Default -Prompt 'Extra copilot CLI args' -Default '' -AllowEmpty `
        -Description @(
            'Optional. Extra arguments appended to the copilot command line in',
            'the shortcut. Useful for flags not covered by the prompts above:',
            '    --max-autopilot-continues 100',
            'Leave blank for none. Quoted values are preserved as single args:',
            '    --prompt "do the thing"'
        )
}

# ============================================================================
# Windows Terminal
# ============================================================================

$wtPath = Find-WindowsTerminal
if ($PSBoundParameters.ContainsKey('NoWindowsTerminal')) {
    $UseWindowsTerminal = -not [bool]$NoWindowsTerminal
} elseif ($PSBoundParameters.ContainsKey('UseWindowsTerminal')) {
    # Already bound
} else {
    $defaultWT = [bool]$wtPath
    $UseWindowsTerminal = Read-YesNo -Prompt 'Launch via Windows Terminal (wt.exe)?' -Default $defaultWT `
        -Description @(
            'When enabled, the shortcut targets wt.exe instead of pwsh.exe and',
            'opens Copilot in a new Windows Terminal tab/window. Equivalent to:',
            '    wt.exe -w 0 -d "<workdir>" "<pwsh>" -NoExit -File "<launcher>" ...',
            "Default = $(if ($defaultWT) { 'yes (wt.exe found on PATH)' } else { 'no (wt.exe not found)' })."
        )
}
if ($UseWindowsTerminal -and -not $wtPath) {
    Write-Host '  ! Windows Terminal selected but wt.exe not on PATH; falling back to pwsh.exe target.' -ForegroundColor Yellow
    $UseWindowsTerminal = $false
}

# ============================================================================
# Shortcut location
# ============================================================================

if (-not $ShortcutDir) {
    $defaultShortcutDir = [Environment]::GetFolderPath('Desktop')
    $ShortcutDir = Read-Default -Prompt 'Shortcut location' -Default $defaultShortcutDir `
        -Description @(
            'Folder where the .lnk file will be created.',
            'Default = your Desktop. You can put it anywhere (e.g., a "shortcuts"',
            'folder you pin to the Start menu).'
        )
}
$ShortcutDir = [Environment]::ExpandEnvironmentVariables($ShortcutDir)
if (-not (Test-Path $ShortcutDir)) {
    if ($script:SilentMode -or (Read-YesNo -Prompt "Create folder '$ShortcutDir'?" -Default $true)) {
        $null = New-Item -ItemType Directory -Force -Path $ShortcutDir
    } else {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        exit 0
    }
}

# ============================================================================
# Confirm + idempotency
# ============================================================================

$shortcutName = "$Label Copilot.lnk"
$shortcutPath = Join-Path $ShortcutDir $shortcutName

Write-Host ''
Write-Host 'Summary:' -ForegroundColor Cyan
Write-Host "  Shortcut path:      $shortcutPath"
Write-Host "  Working directory:  $WorkingDirectory"
Write-Host "  Launcher target:    $LauncherPath"
Write-Host "  Resume session:     $(if ($ResumeSession) { $ResumeSession } else { '(none — fresh session each launch)' })"
Write-Host "  AI summary:         $(if ($EnableAISummary) { 'enabled' } else { 'disabled' })"
Write-Host "  --allow-all:        $(if ($EnableAllowAll) { 'enabled' } else { 'disabled' })"
Write-Host "  Extra copilot args: $(if ($ExtraCopilotArgs) { $ExtraCopilotArgs } else { '(none)' })"
Write-Host "  Windows Terminal:   $(if ($UseWindowsTerminal) { 'yes' } else { 'no (pwsh direct)' })"
Write-Host ''

if (Test-Path $shortcutPath) {
    if ($Force) {
        Write-Host "  Overwriting existing shortcut (-Force)." -ForegroundColor DarkYellow
    } elseif ($script:SilentMode) {
        Write-Host "ERROR: shortcut already exists: $shortcutPath" -ForegroundColor Red
        Write-Host "  Pass -Force to overwrite, or pick a different -Label." -ForegroundColor Red
        exit 1
    } else {
        $overwrite = Read-YesNo -Prompt 'Shortcut already exists. Overwrite?' -Default $false
        if (-not $overwrite) {
            Write-Host 'Cancelled.' -ForegroundColor Yellow
            exit 0
        }
    }
}

if (-not $script:SilentMode) {
    $confirm = Read-Host 'Create shortcut? [Y/n]'
    if ($confirm.Trim().ToLower() -in @('n', 'no')) {
        Write-Host 'Cancelled.' -ForegroundColor Yellow
        exit 0
    }
}

# ============================================================================
# Create the shortcut
# ============================================================================

$pwshCmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
if (-not $pwshCmd) {
    $pwshCmd = Get-Command powershell.exe -ErrorAction SilentlyContinue
}
if (-not $pwshCmd) {
    Write-Host 'ERROR: Could not find pwsh.exe or powershell.exe on PATH.' -ForegroundColor Red
    exit 1
}

# Inner pwsh + launcher args (always built; either consumed directly as the
# shortcut Arguments, or as the tail end of a wt.exe argline).
$pwshLauncherArgs = @(
    '-NoExit',
    '-NoLogo',
    '-ExecutionPolicy', 'Bypass',
    '-File', $LauncherPath
)
if ($EnableAISummary)  { $pwshLauncherArgs += '-AISummary' }
if ($EnableAllowAll)   { $pwshLauncherArgs += '--allow-all' }
if ($ResumeSession)    { $pwshLauncherArgs += "--resume=$ResumeSession" }
if ($ExtraCopilotArgs) { $pwshLauncherArgs += (Split-CommandLine $ExtraCopilotArgs) }

if ($UseWindowsTerminal -and $wtPath) {
    $targetExe = $wtPath
    $argList = @('-w', '0', '-d', $WorkingDirectory, $pwshCmd.Source) + $pwshLauncherArgs
} else {
    $targetExe = $pwshCmd.Source
    $argList = $pwshLauncherArgs
}

$descLabel = if ($configProjectName) { $configProjectName } else { $Label }

try {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($shortcutPath)
    $sc.TargetPath       = $targetExe
    $sc.Arguments        = Format-ShortcutArgs -Arguments $argList
    $sc.WorkingDirectory = $WorkingDirectory
    $sc.Description      = "Launch GitHub Copilot CLI for $descLabel with version-change briefing"
    $sc.IconLocation     = $pwshCmd.Source
    $sc.Save()
    Write-Host ''
    Write-Host "Created shortcut: $shortcutPath" -ForegroundColor Green
    Write-Host ''
} catch {
    Write-Host ''
    Write-Host "ERROR: Failed to create shortcut: $_" -ForegroundColor Red
    exit 1
}
