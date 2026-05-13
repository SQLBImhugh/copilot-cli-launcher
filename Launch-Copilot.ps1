<#
.SYNOPSIS
    Portable GitHub Copilot CLI launcher with auto-update, version-change
    briefing, optional AI summary, session repair, and known-bug workarounds.

.DESCRIPTION
    Wraps `copilot` with a pre-flight pipeline:

      1. Trigger `copilot update` so the briefing flow sees the freshest version
         on the SAME launch (skip with -NoUpdate)
      2. Apply known-bug workarounds (Win32 native addon loader for /keep-alive,
         dangling tool_use auto-repair across all sessions)
      3. Detect installed version vs. last-seen
      4. If newer, render a changelog briefing (bundled npm changelog or remote
         GitHub releases as fallback)
      5. Optionally append an AI-authored summary contextualized to your project
         (-AISummary; uses one premium request)
      6. Append the briefing to a rolling history file
      7. Launch copilot, passing through any extra args

    Project-specific values (project name, AI prompt, state directory, etc.)
    are loaded from config.json next to this script. See config.example.json.

.PARAMETER AISummary
    Generate a Copilot-authored summary of the changes, contextualized to your
    project via the AGENTS.md template. Costs one premium request per launch.

.PARAMETER NoUpdate
    Skip the explicit `copilot update` invocation. Useful when offline.

.PARAMETER ConfigPath
    Path to a JSON config file. Defaults to ./config.json next to this script.

.PARAMETER CopilotArgs
    Additional arguments passed through to `copilot`. Use this to pass
    --resume=<session>, --prompt, --allow-all, --max-autopilot-continues, etc.

.EXAMPLE
    .\Launch-Copilot.ps1 -AISummary

.EXAMPLE
    .\Launch-Copilot.ps1 -AISummary --resume=MyProject-Main --allow-all

.EXAMPLE
    # Windows shortcut target:
    pwsh.exe -NoExit -NoLogo -ExecutionPolicy Bypass -File "C:\path\to\Launch-Copilot.ps1" -AISummary --resume=MyProject

.NOTES
    See README.md for setup instructions.
#>
[CmdletBinding()]
param(
    [switch]$AISummary,
    [switch]$NoUpdate,
    [string]$ConfigPath,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CopilotArgs
)

# Force UTF-8 console I/O so em-dashes / curly quotes / emoji from the AI
# summary render correctly instead of as mojibake (e.g. "Γçö" for "—").
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::InputEncoding  = [System.Text.Encoding]::UTF8
    $OutputEncoding           = [System.Text.Encoding]::UTF8
} catch {}

# ---------- config ----------

function Get-LauncherConfig {
    param([string]$ConfigPath)

    $defaults = @{
        projectName            = (Split-Path -Leaf (Get-Location).Path)
        stateDir               = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'CopilotCLI')
        agentsMdPath           = Join-Path $PSScriptRoot 'agents.md'
        briefingSessionName    = $null   # default: <projectName>-Briefings
        trackedIssues          = @(3298)
        applyKnownWorkarounds  = $true
        autoUpdate             = $true
    }

    if (-not $ConfigPath) {
        $ConfigPath = Join-Path $PSScriptRoot 'config.json'
    }
    if (Test-Path $ConfigPath) {
        try {
            $loaded = Get-Content $ConfigPath -Raw | ConvertFrom-Json
            foreach ($k in $loaded.PSObject.Properties.Name) {
                # Skip $comment* keys
                if ($k -like '`$*') { continue }
                $defaults[$k] = $loaded.$k
            }
        } catch {
            Write-Host "[Launch-Copilot] failed to parse $ConfigPath - using defaults: $_" -ForegroundColor DarkYellow
        }
    }

    if (-not $defaults.briefingSessionName) {
        $defaults.briefingSessionName = "$($defaults.projectName)-Briefings"
    }
    # Expand env vars in stateDir
    $defaults.stateDir = [Environment]::ExpandEnvironmentVariables($defaults.stateDir)
    # Expand relative agentsMdPath against script dir
    if ($defaults.agentsMdPath -and -not [IO.Path]::IsPathRooted($defaults.agentsMdPath)) {
        $defaults.agentsMdPath = Join-Path $PSScriptRoot $defaults.agentsMdPath
    }

    return [pscustomobject]$defaults
}

# ---------- common utilities ----------

function Compare-SemVer {
    param([string]$Left, [string]$Right)
    if (-not $Left -and -not $Right) { return 0 }
    if (-not $Left)  { return -1 }
    if (-not $Right) { return  1 }
    $L = $Left  -split '[.-]' | ForEach-Object { if ($_ -match '^\d+$') { [int]$_ } else { 0 } }
    $R = $Right -split '[.-]' | ForEach-Object { if ($_ -match '^\d+$') { [int]$_ } else { 0 } }
    $max = [Math]::Max($L.Count, $R.Count)
    for ($i = 0; $i -lt $max; $i++) {
        $a = if ($i -lt $L.Count) { $L[$i] } else { 0 }
        $b = if ($i -lt $R.Count) { $R[$i] } else { 0 }
        if ($a -gt $b) { return  1 }
        if ($a -lt $b) { return -1 }
    }
    return 0
}

function Get-InstalledCopilotVersion {
    try {
        $out = & copilot --version 2>$null | Select-Object -First 1
        if ($out -match '(\d+\.\d+\.\d+(?:[-.]?\w+)*)') {
            return $matches[1]
        }
    } catch {}
    return $null
}

# ---------- copilot update ----------

function Invoke-CopilotUpdate {
    param([switch]$Skip)
    if ($Skip) {
        return [pscustomobject]@{ Updated = $false; OldVersion = $null; NewVersion = $null; Skipped = $true }
    }
    if (-not (Get-Command copilot -ErrorAction SilentlyContinue)) {
        Write-Host '[Launch-Copilot] copilot CLI not on PATH; skipping update' -ForegroundColor DarkYellow
        return [pscustomobject]@{ Updated = $false; OldVersion = $null; NewVersion = $null; Skipped = $true }
    }
    Write-Host '[Launch-Copilot] checking for Copilot CLI updates...' -ForegroundColor DarkGray
    $oldVersion = Get-InstalledCopilotVersion
    $output = ''
    try {
        $output = (& copilot update 2>&1 | Out-String).Trim()
    } catch {
        Write-Host "[Launch-Copilot] copilot update failed: $_" -ForegroundColor DarkYellow
        return [pscustomobject]@{ Updated = $false; OldVersion = $oldVersion; NewVersion = $oldVersion; Skipped = $false }
    }
    $updated = $false
    $newVersion = $oldVersion
    if ($output -match 'Copilot CLI version\s+([\d.]+)\s+installed') {
        $updated = $true
        $newVersion = $matches[1]
        Write-Host "[Launch-Copilot] updated $oldVersion -> $newVersion" -ForegroundColor Green
    } elseif ($output -notmatch 'No update needed') {
        Write-Host "[Launch-Copilot] copilot update returned unexpected output:" -ForegroundColor DarkYellow
        Write-Host $output -ForegroundColor DarkYellow
    }
    return [pscustomobject]@{ Updated = $updated; OldVersion = $oldVersion; NewVersion = $newVersion; Skipped = $false }
}

# ---------- known-bug workarounds ----------

function Test-CopilotCliIssueClosed {
    <#
    Cached check (24h) for whether a copilot-cli GitHub issue has closed.
    Returns the latest state so the launcher can print a reminder banner
    when a tracked workaround can be removed.
    #>
    param([int]$IssueNumber, [string]$CacheDir)
    $cacheFile = Join-Path $CacheDir "issue-$IssueNumber-status.json"
    if (Test-Path $cacheFile) {
        try {
            $cached = Get-Content $cacheFile -Raw | ConvertFrom-Json
            $age = (Get-Date) - [DateTime]::Parse($cached.fetched_at)
            if ($age.TotalHours -lt 24) {
                return [pscustomobject]@{ State = $cached.state; StateReason = $cached.state_reason; Cached = $true }
            }
        } catch {}
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { return $null }
    try {
        $json = & gh api "repos/github/copilot-cli/issues/$IssueNumber" --jq '{state, state_reason, closed_at}' 2>$null
        if (-not $json) { return $null }
        $data = $json | ConvertFrom-Json
        @{ state = $data.state; state_reason = $data.state_reason; closed_at = $data.closed_at; fetched_at = (Get-Date).ToString('o') } | ConvertTo-Json | Set-Content -Path $cacheFile -Encoding utf8
        return [pscustomobject]@{ State = $data.state; StateReason = $data.state_reason; Cached = $false }
    } catch { return $null }
}

function Repair-Win32NativeAddon {
    <#
    Workaround for github/copilot-cli#3298: hand-write the missing
    native/win32/index.js loader stub so /keep-alive works on Windows.
    Idempotent. Re-applied after every `copilot update` since each
    update overwrites the version directory.
    #>
    param([string]$Version)
    if (-not $Version) { return $false }
    $win32Dir = Join-Path $env:LOCALAPPDATA "copilot\pkg\universal\$Version\native\win32"
    if (-not (Test-Path $win32Dir)) { return $false }
    $loader = Join-Path $win32Dir 'index.js'
    if (Test-Path $loader) { return $false }
    $binary = Join-Path $win32Dir 'win32-native.win32-x64-msvc.node'
    if (-not (Test-Path $binary)) { return $false }
    $stub = @'
// Workaround for https://github.com/github/copilot-cli/issues/3298
// The auto-generated NAPI-RS loader for win32-native is missing from the
// prebuild bundle. This hand-written shim loads the right .node binary so
// /keep-alive (and getErrorMode, enableCrashReporting, installExceptionFilter)
// work on Windows. Re-applied by Launch-Copilot.ps1 after every CLI update.
const path = require('node:path')
let nativeBinding = null
const loadErrors = []
if (process.platform === 'win32') {
  if (process.arch === 'x64') {
    try { nativeBinding = require('./win32-native.win32-x64-msvc.node') }
    catch (e) { loadErrors.push(e) }
  } else if (process.arch === 'arm64') {
    try { nativeBinding = require('./win32-native.win32-arm64-msvc.node') }
    catch (e) { loadErrors.push(e) }
  } else {
    loadErrors.push(new Error(`Unsupported Windows architecture: ${process.arch}`))
  }
} else {
  module.exports = {}
  return
}
if (!nativeBinding) {
  throw new Error(`Failed to load win32-native: ${loadErrors.map(e => e.message).join('; ')}`)
}
module.exports = nativeBinding
'@
    try {
        $stub | Set-Content -Path $loader -Encoding utf8 -NoNewline
        Write-Host "[Launch-Copilot] patched win32 native addon loader (issue #3298) for $Version" -ForegroundColor DarkGray
        return $true
    } catch {
        Write-Host "[Launch-Copilot] could not patch win32 loader: $_" -ForegroundColor DarkYellow
        return $false
    }
}

function Repair-CopilotSessionDanglingToolUses {
    <#
    Auto-repair sessions whose events.jsonl has a tool_use without a matching
    tool.execution_complete (typical cause: Ctrl+C while a tool was running).
    Without this, --resume hits a 400 from the Anthropic API.
    Idempotent. See repair-copilot-sessions.py for the implementation.
    #>
    $script = Join-Path $PSScriptRoot 'repair-copilot-sessions.py'
    if (-not (Test-Path $script)) { return }
    $py = Get-Command py -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $py) { return }
    try {
        & $py.Source -3 $script 2>&1 | ForEach-Object {
            if ($_ -match '\[repair-copilot-sessions\]') {
                Write-Host $_ -ForegroundColor DarkGray
            }
        }
    } catch {
        Write-Host "[Launch-Copilot] session repair scan failed: $_" -ForegroundColor DarkYellow
    }
}

# ---------- changelog discovery ----------

function Resolve-ChangelogPath {
    try {
        $copilotCmd = Get-Command copilot -ErrorAction SilentlyContinue
        if (-not $copilotCmd) { return $null }
        $copilotDir = Split-Path $copilotCmd.Source -Parent
        $candidates = @(
            (Join-Path $copilotDir 'node_modules\@github\copilot\changelog.json'),
            (Join-Path $copilotDir '..\node_modules\@github\copilot\changelog.json'),
            (Join-Path $env:APPDATA 'npm\node_modules\@github\copilot\changelog.json'),
            (Join-Path $env:LOCALAPPDATA 'copilot\pkg\universal\latest\changelog.json')
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { return (Resolve-Path $c).Path }
        }
    } catch {}
    return $null
}

function Get-VersionsBetween {
    param($Changelog, [string]$After, [string]$UpTo)
    if (-not $Changelog) { return @() }
    $entries = if ($Changelog.versions) { $Changelog.versions } elseif ($Changelog -is [array]) { $Changelog } else { @($Changelog) }
    $matched = @()
    foreach ($e in $entries) {
        $v = if ($e.version) { $e.version } elseif ($e.tag) { $e.tag } else { $null }
        if (-not $v) { continue }
        if ($After  -and (Compare-SemVer $v $After) -le 0) { continue }
        if ($UpTo   -and (Compare-SemVer $v $UpTo)  -gt 0) { continue }
        $matched += $v
    }
    return $matched
}

function Format-Briefing {
    param($Changelog, [string[]]$Versions, [string]$From, [string]$To)
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor Magenta
    Write-Host (" Copilot CLI updated: $From  ->  $To") -ForegroundColor Magenta
    Write-Host (" {0} version(s) since you last launched (bundled changelog)" -f $Versions.Count) -ForegroundColor Magenta
    Write-Host ('=' * 72) -ForegroundColor Magenta
    Write-Host ''
    $entries = if ($Changelog.versions) { $Changelog.versions } else { @($Changelog) }
    foreach ($v in $Versions) {
        $entry = $entries | Where-Object { ($_.version -eq $v) -or ($_.tag -eq $v) } | Select-Object -First 1
        if (-not $entry) { continue }
        Write-Host (" v$v") -ForegroundColor Cyan
        if ($entry.date) { Write-Host ("  $($entry.date)") -ForegroundColor DarkGray }
        $body = if ($entry.changes) { $entry.changes } elseif ($entry.body) { $entry.body } else { ($entry | ConvertTo-Json) }
        if ($body -is [array]) { $body | ForEach-Object { Write-Host "  - $_" } } else { Write-Host "  $body" }
        Write-Host ''
    }
    Write-Host ' Tip: run /changelog inside copilot for the canonical list.' -ForegroundColor DarkGray
    Write-Host ('=' * 72) -ForegroundColor Magenta
    Write-Host ''
}

function Format-RemoteBriefing {
    param($Entries, [string]$From, [string]$To)
    Write-Host ''
    Write-Host ('=' * 72) -ForegroundColor Magenta
    Write-Host (" Copilot CLI updated: $From  ->  $To") -ForegroundColor Magenta
    Write-Host (" {0} release(s) since you last launched (sourced from GitHub Releases)" -f $Entries.Count) -ForegroundColor Magenta
    Write-Host ('=' * 72) -ForegroundColor Magenta
    Write-Host ''
    foreach ($e in $Entries) {
        $tag = $e.tag_name
        Write-Host " $tag" -ForegroundColor Cyan
        if ($e.published_at) { Write-Host "  $($e.published_at.Substring(0,10))" -ForegroundColor DarkGray }
        if ($e.body) {
            $e.body -split "(`r`n|`n)" | ForEach-Object {
                $line = $_.Trim()
                if ($line) { Write-Host "  $line" }
            }
        }
        Write-Host ''
    }
    Write-Host ' Tip: run /changelog inside copilot for the canonical list.' -ForegroundColor DarkGray
    Write-Host ('=' * 72) -ForegroundColor Magenta
    Write-Host ''
}

function Get-RemoteReleases {
    param([string]$After, [string]$UpTo, [string]$CacheDir)
    $cacheFile = Join-Path $CacheDir 'releases-cache.json'
    $entries = $null
    # Refresh if cache > 1 hour old
    if (Test-Path $cacheFile) {
        $age = (Get-Date) - (Get-Item $cacheFile).LastWriteTime
        if ($age.TotalHours -lt 1) {
            try { $entries = Get-Content $cacheFile -Raw | ConvertFrom-Json } catch {}
        }
    }
    if (-not $entries) {
        $gh = Get-Command gh -ErrorAction SilentlyContinue
        if ($gh) {
            try {
                $json = & gh api 'repos/github/copilot-cli/releases?per_page=30' --paginate 2>$null
                if ($json) {
                    $entries = $json | ConvertFrom-Json
                    $entries | ConvertTo-Json -Depth 10 | Set-Content -Path $cacheFile -Encoding utf8
                }
            } catch {}
        }
    }
    if (-not $entries) { return @() }
    return $entries | Where-Object {
        $tag = $_.tag_name -replace '^v', ''
        ($After -and (Compare-SemVer $tag $After) -gt 0) -and (-not $UpTo -or (Compare-SemVer $tag $UpTo) -le 0)
    } | Sort-Object { [DateTime]$_.published_at }
}

function Get-PlainBriefingText {
    param($BundledChangelog, [string[]]$BundledVersions, $RemoteEntries, [string]$From, [string]$To)
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("Copilot CLI updated: $From -> $To")
    [void]$sb.AppendLine()
    if ($BundledChangelog -and $BundledVersions.Count -gt 0) {
        $entries = if ($BundledChangelog.versions) { $BundledChangelog.versions } else { @($BundledChangelog) }
        foreach ($v in $BundledVersions) {
            $entry = $entries | Where-Object { ($_.version -eq $v) -or ($_.tag -eq $v) } | Select-Object -First 1
            if (-not $entry) { continue }
            [void]$sb.AppendLine("## v$v")
            if ($entry.date) { [void]$sb.AppendLine("$($entry.date)") }
            [void]$sb.AppendLine()
            $body = if ($entry.changes) { $entry.changes } elseif ($entry.body) { $entry.body } else { $null }
            if ($body -is [array]) { $body | ForEach-Object { [void]$sb.AppendLine("- $_") } }
            elseif ($body) { [void]$sb.AppendLine($body) }
            [void]$sb.AppendLine()
        }
    } elseif ($RemoteEntries) {
        foreach ($e in $RemoteEntries) {
            [void]$sb.AppendLine("## $($e.tag_name)")
            if ($e.published_at) { [void]$sb.AppendLine($e.published_at.Substring(0,10)) }
            [void]$sb.AppendLine()
            if ($e.body) { [void]$sb.AppendLine($e.body) }
            [void]$sb.AppendLine()
        }
    }
    return $sb.ToString()
}

function Compress-ChangelogForPrompt {
    param([string]$Text, [int]$MaxChars = 26000)
    if ($Text.Length -le $MaxChars) { return $Text }
    # Drop blank lines and reduce repeated whitespace
    $compact = ($Text -split "`n" | Where-Object { $_.Trim() -ne '' }) -join "`n"
    if ($compact.Length -le $MaxChars) { return $compact }
    return $compact.Substring(0, $MaxChars)
}

# ---------- AI summary ----------

function Invoke-AISummary {
    param(
        [string]$ChangelogText,
        [string]$LogPath,
        [string]$StateDir,
        [string]$ProjectName,
        [string]$BriefingSessionName,
        [string]$AgentsMdPath
    )

    $sessionMarker = if ($StateDir) { Join-Path $StateDir 'briefing-session-initialized.txt' } else { $null }
    $sessionExists = $sessionMarker -and (Test-Path $sessionMarker)

    $originalLen = $ChangelogText.Length
    $ChangelogText = Compress-ChangelogForPrompt -Text $ChangelogText -MaxChars 26000
    if ($ChangelogText.Length -lt $originalLen) {
        Write-Host (' Changelog compressed: {0} -> {1} chars (32KB CreateProcess limit)' -f $originalLen, $ChangelogText.Length) -ForegroundColor DarkGray
    }

    # Load AGENTS.md content (the briefing assistant's project context + format spec).
    # On first run, the script copies it into the state dir so the session has a stable
    # reference to it. On subsequent runs, the model already has it from the resumed session.
    $agentsMdContent = ''
    if ($AgentsMdPath -and (Test-Path $AgentsMdPath)) {
        $agentsMdContent = (Get-Content $AgentsMdPath -Raw).Replace('{ProjectName}', $ProjectName)
    } else {
        Write-Host "[Launch-Copilot] AGENTS.md not found at $AgentsMdPath - using minimal default" -ForegroundColor DarkYellow
        $agentsMdContent = "You are the briefing assistant for $ProjectName. Produce a SHORT briefing (max 250 words, plain text) covering Highlights, Watch out for, and Skip sections."
    }

    if ($sessionExists) {
        $prompt = @"
NEW CHANGELOG (since last briefing):
$ChangelogText

TASK:
Produce the SHORT briefing per your AGENTS.md instructions. Reference prior briefings if a feature you previously flagged has been refined, fixed, or reverted.
"@
    } else {
        $prompt = @"
$agentsMdContent

---

CHANGELOG (covers all versions since the developer's last launch):
$ChangelogText

TASK:
This is your FIRST briefing. Memorize the project context above for future runs, then produce the SHORT briefing per the format above.
"@
    }

    $tempPrompt = Join-Path $env:TEMP "copilot-summary-prompt-$([guid]::NewGuid().Guid.Substring(0,8)).txt"
    Set-Content -Path $tempPrompt -Value $prompt -Encoding utf8

    Write-Host ''
    if ($sessionExists) {
        Write-Host (' Generating AI summary (resuming session "{0}", one premium request)...' -f $BriefingSessionName) -ForegroundColor DarkCyan
    } else {
        Write-Host (' Generating AI summary (NEW session "{0}", one premium request)...' -f $BriefingSessionName) -ForegroundColor DarkCyan
    }
    Write-Host ''

    try {
        $copilotCmd = Get-Command copilot -ErrorAction SilentlyContinue
        if (-not $copilotCmd) {
            Write-Host '[Launch-Copilot] copilot CLI not found - skipping AI summary' -ForegroundColor DarkYellow
            return
        }

        $promptText = Get-Content $tempPrompt -Raw
        $args = @('--prompt', $promptText, '--no-color', '--allow-all-tools', '--resume', $BriefingSessionName, '--working-directory', $StateDir)

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'copilot'
        foreach ($a in $args) { [void]$psi.ArgumentList.Add($a) }
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
        $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

        $proc = [System.Diagnostics.Process]::Start($psi)
        $output = $proc.StandardOutput.ReadToEnd()
        $proc.WaitForExit()

        if ($output) {
            Write-Host ('=' * 72) -ForegroundColor DarkMagenta
            Write-Host ' AI SUMMARY' -ForegroundColor DarkMagenta
            Write-Host ('=' * 72) -ForegroundColor DarkMagenta
            Write-Host $output
            Write-Host ('=' * 72) -ForegroundColor DarkMagenta
            if ($LogPath) { "`n=== AI SUMMARY ===`n$output" | Add-Content -Path $LogPath }
        }

        if (-not $sessionExists -and $sessionMarker) {
            Set-Content -Path $sessionMarker -Value (Get-Date).ToString('o') -Encoding utf8
            Write-Host (' (Session "{0}" saved - future briefings will build on this context)' -f $BriefingSessionName) -ForegroundColor DarkGray
        }
    } finally {
        Remove-Item $tempPrompt -ErrorAction SilentlyContinue
    }
}

# ---------- main ----------

$config = Get-LauncherConfig -ConfigPath $ConfigPath

$stateDir = $config.stateDir
$null = New-Item -ItemType Directory -Force -Path $stateDir
$stateFile = Join-Path $stateDir 'last-seen-version.txt'
$briefingLog = Join-Path $stateDir 'last-briefing.log'
$briefingHistory = Join-Path $stateDir 'briefing-history.log'

# Run known-bug workarounds + update + repair before reading version
if ($config.applyKnownWorkarounds) {
    foreach ($issue in $config.trackedIssues) {
        $status = Test-CopilotCliIssueClosed -IssueNumber $issue -CacheDir $stateDir
        if ($status -and $status.State -eq 'closed') {
            Write-Host ''
            Write-Host '====================================================================' -ForegroundColor Green
            Write-Host " github/copilot-cli#$issue is CLOSED. If the workaround for this" -ForegroundColor Green
            Write-Host ' issue is still firing, the upstream fix may not have shipped yet.' -ForegroundColor Green
            Write-Host ' Otherwise consider removing it from your config.trackedIssues.' -ForegroundColor Green
            Write-Host '====================================================================' -ForegroundColor Green
            Write-Host ''
        }
    }
}

# Update first so the briefing flow sees the freshest version on the same launch
$shouldAutoUpdate = $config.autoUpdate -and -not $NoUpdate
$null = Invoke-CopilotUpdate -Skip:(-not $shouldAutoUpdate)

if ($config.applyKnownWorkarounds) {
    $current = Get-InstalledCopilotVersion
    $null = Repair-Win32NativeAddon -Version $current
    Repair-CopilotSessionDanglingToolUses
}

$current = Get-InstalledCopilotVersion
$lastSeen = if (Test-Path $stateFile) { (Get-Content $stateFile -Raw).Trim() } else { $null }
$briefingShown = $false

if (-not $current) {
    Write-Host '[Launch-Copilot] could not detect copilot version; launching anyway.' -ForegroundColor DarkYellow
}
elseif (-not $lastSeen) {
    Write-Host "[Launch-Copilot] first run; recording current version $current. No briefing." -ForegroundColor DarkGray
    Set-Content -Path $stateFile -Value $current -Encoding utf8
}
elseif ((Compare-SemVer $current $lastSeen) -gt 0) {
    try { Start-Transcript -Path $briefingLog -Force | Out-Null } catch {}

    $changelogPath = Resolve-ChangelogPath
    $rendered = $false
    $bundledChangelog = $null
    $bundledVersions = @()
    $remoteEntries = @()

    if ($changelogPath) {
        try {
            $bundledChangelog = Get-Content $changelogPath -Raw | ConvertFrom-Json
            $bundledVersions = Get-VersionsBetween -Changelog $bundledChangelog -After $lastSeen -UpTo $current
            if ($bundledVersions.Count -gt 0) {
                Format-Briefing -Changelog $bundledChangelog -Versions $bundledVersions -From $lastSeen -To $current
                $rendered = $true
            }
        } catch {}
    }
    if (-not $rendered) {
        $remoteEntries = Get-RemoteReleases -After $lastSeen -UpTo $current -CacheDir $stateDir
        if ($remoteEntries.Count -gt 0) {
            Format-RemoteBriefing -Entries $remoteEntries -From $lastSeen -To $current
            $rendered = $true
        }
    }
    if (-not $rendered) {
        Write-Host ''
        Write-Host " Copilot CLI updated $lastSeen -> $current, but no changelog entries are available (bundled or remote)." -ForegroundColor Yellow
        Write-Host ''
    }
    $briefingShown = $rendered
    try { Stop-Transcript | Out-Null } catch {}

    if ($AISummary -and $rendered) {
        $plain = Get-PlainBriefingText -BundledChangelog $bundledChangelog -BundledVersions $bundledVersions -RemoteEntries $remoteEntries -From $lastSeen -To $current
        Invoke-AISummary `
            -ChangelogText $plain `
            -LogPath $briefingLog `
            -StateDir $stateDir `
            -ProjectName $config.projectName `
            -BriefingSessionName $config.briefingSessionName `
            -AgentsMdPath $config.agentsMdPath
    }

    # Append this briefing to the rolling history file
    if ($briefingShown -and (Test-Path $briefingLog)) {
        try {
            $separator = ('=' * 72)
            $header = "`n$separator`n=== Briefing $lastSeen -> $current  @  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n$separator`n"
            Add-Content -Path $briefingHistory -Value $header -Encoding utf8
            Get-Content -Path $briefingLog -Raw | Add-Content -Path $briefingHistory -Encoding utf8
        } catch {
            Write-Host "[Launch-Copilot] could not append to briefing history: $_" -ForegroundColor DarkYellow
        }
    }

    Set-Content -Path $stateFile -Value $current -Encoding utf8
}
elseif ((Compare-SemVer $current $lastSeen) -lt 0) {
    Write-Host "[Launch-Copilot] installed version ($current) is older than last-seen ($lastSeen); resetting marker." -ForegroundColor DarkYellow
    Set-Content -Path $stateFile -Value $current -Encoding utf8
}

if ($briefingShown) {
    Write-Host " Saved to: $briefingLog" -ForegroundColor DarkGray
    Write-Host " History:  $briefingHistory" -ForegroundColor DarkGray
    Write-Host ''
    Write-Host ' Press any key to launch Copilot (or Ctrl+C to abort)...' -ForegroundColor Yellow -NoNewline
    try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Read-Host ' [Enter]' }
    Write-Host ''
}

# Hand off to copilot
& copilot @CopilotArgs
