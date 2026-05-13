# Copilot CLI Launcher

A portable PowerShell wrapper for [GitHub Copilot CLI](https://github.com/github/copilot-cli) on Windows that adds:

- **Auto-update** before every launch (so you're always on the latest version)
- **Version-change briefing** with the changelog of everything new since your last launch
- **Optional AI-authored summary** contextualized to your project (uses one premium request)
- **Rolling briefing history** appended to a single file so you never lose past briefings
- **Session repair** — auto-fixes corrupt sessions with dangling `tool_use` events that would otherwise reject on `--resume` with a 400 from the API
- **Known-bug workarounds** — patches the missing `native/win32/index.js` loader (issue [#3298](https://github.com/github/copilot-cli/issues/3298)) so `/keep-alive` works on Windows

Designed to be **portable**: copy this folder anywhere, edit two files, point a Windows shortcut at it. Or use the **single-file installer** for a wizard-driven setup that handles everything.

---

## Setup

You have two options:

### Option A — Single-file installer (recommended for sharing)

Hand a peer one file: `dist/Install-CopilotLauncher.ps1` (~115 KB; the launcher, repair script, templates, and README are all base64-embedded).

```powershell
# Wizard mode — prompts for project name, install dir, etc.
pwsh -ExecutionPolicy Bypass -File .\Install-CopilotLauncher.ps1

# Silent mode — for unattended/scripted installs
pwsh -ExecutionPolicy Bypass -File .\Install-CopilotLauncher.ps1 `
    -Silent `
    -ProjectName "MyApp" `
    -ResumeSession "MyApp-Main" `
    -EnableAISummary `
    -EnableAllowAll
```

The installer:

1. Asks for project name, install dir, state dir, AI-summary on/off, `--allow-all` on/off, shortcut on/off
2. Extracts all 5 source files to the install dir
3. Generates a `config.json` from your wizard inputs
4. Generates `agents.md` from the template (with `{ProjectName}` substituted)
5. Creates a Windows desktop shortcut targeting `Launch-Copilot.ps1` with the right args

Installer parameters:

| Param | Default | Notes |
|---|---|---|
| `-InstallDir` | `%USERPROFILE%\copilot-launcher` | Where the kit goes |
| `-ProjectName` | prompt (or current dir name in silent mode) | Used in messages and as the briefing-session name |
| `-StateDir` | `%USERPROFILE%\Desktop\CopilotCLI\<ProjectName>` | Where briefing logs and history live |
| `-ResumeSession` | empty | Default `--resume=<name>` for the shortcut |
| `-EnableAISummary` | prompt | Adds `-AISummary` to the shortcut |
| `-EnableAllowAll` | prompt | Adds `--allow-all` to the shortcut |
| `-NoDesktopShortcut` | off | Skip shortcut creation |
| `-Silent` | off | Skip all prompts; use defaults + supplied params |

### Option B — Manual file copy

If you'd rather see and edit each file yourself, skip the installer:

#### 1. Copy this folder

Copy `copilot-launcher/` anywhere you like — e.g. `C:\Tools\copilot-launcher\` or `Documents\copilot-launcher\`.

#### 2. Create `config.json`

```powershell
cd C:\Tools\copilot-launcher
Copy-Item config.example.json config.json
```

Edit `config.json` to set your project name and where state files should live. The keys you'll likely change:

| Key | Default | What it controls |
|---|---|---|
| `projectName` | current dir name | Used in messages and as the default briefing-session name |
| `stateDir` | `%USERPROFILE%/Desktop/CopilotCLI/<projectName>` | Where briefing logs, history, and caches live |
| `agentsMdPath` | `./agents.md` | Path to your AGENTS.md template (see below) |
| `briefingSessionName` | `<projectName>-Briefings` | Copilot CLI session name reused for AI summaries — gives the model continuity across launches |
| `trackedIssues` | `[3298]` | GitHub issue numbers to monitor; banner appears when one closes |
| `applyKnownWorkarounds` | `true` | Run the keep-alive loader patch + session repair on every launch |
| `autoUpdate` | `true` | Run `copilot update` before reading the version |

All other config keys have sensible defaults — leave them out of your `config.json` if you don't need to override.

#### 3. Customize `agents.md` (only needed for `-AISummary`)

```powershell
Copy-Item agents.example.md agents.md
notepad agents.md
```

Replace the `## Project Context` section with a 1-paragraph description of your project (language, framework, key surface area, how you use Copilot CLI). The AI summary uses this to decide which CLI changes are relevant.

The placeholder `{ProjectName}` is auto-substituted from your config at runtime.

#### 4. Create a Windows shortcut

Right-click your desktop → New → Shortcut. Target:

```
pwsh.exe -NoExit -NoLogo -ExecutionPolicy Bypass -File "C:\Tools\copilot-launcher\Launch-Copilot.ps1" -AISummary --resume=MyProject-Main --allow-all
```

| Argument | Purpose |
|---|---|
| `pwsh.exe` | PowerShell 7+ (use `powershell.exe` if you're on Windows PowerShell 5.1) |
| `-NoExit -NoLogo` | Keep the window open and skip the banner |
| `-ExecutionPolicy Bypass` | Allow the script to run regardless of your machine's policy |
| `-File "...\Launch-Copilot.ps1"` | The launcher |
| `-AISummary` | Generate the AI-authored briefing (omit if you don't want premium-request usage) |
| `--resume=MyProject-Main` | Resume a named Copilot CLI session — change to your session name, or omit to start a fresh session each time |
| `--allow-all` | Pass-through arg → Copilot CLI's auto-approve mode |

Anything after `-File "..."` and the launcher's own switches (`-AISummary`, `-NoUpdate`, `-ConfigPath`) is forwarded directly to `copilot`.

#### 5. Done

Double-click the shortcut. The first launch records the current Copilot CLI version with no briefing. From the next launch onward, you'll see a changelog briefing whenever the version has advanced.

---

## What runs on every launch

```
shortcut → Launch-Copilot.ps1
  ├─ Load config.json (or defaults)
  ├─ Check tracked GitHub issues for closure (cached 24h)
  ├─ Run `copilot update`             ← gets the freshest version on this launch
  ├─ Apply known-bug workarounds:
  │     • Repair-Win32NativeAddon      ← keep-alive loader (issue #3298)
  │     • Repair-CopilotSessionDanglingToolUses  ← scans every session
  ├─ If installed version > last-seen:
  │     • Render changelog briefing (bundled npm changelog or GitHub Releases)
  │     • If -AISummary: AI-authored summary using agents.md as context
  │     • Append briefing to briefing-history.log
  │     • Wait for keypress
  └─ Hand off to `copilot` with all extra args
```

Idempotent. Silent unless something actually happens.

---

## Files

### Source kit (this folder)

| File | Purpose | Customize? |
|---|---|---|
| `Launch-Copilot.ps1` | The launcher | No |
| `repair-copilot-sessions.py` | Session events.jsonl repair helper (called by launcher) | No |
| `config.example.json` | Sample config with comments | Copy to `config.json` and edit |
| `config.json` | Your config (gitignored) | **Yes** |
| `agents.example.md` | Sample AGENTS.md template | Copy to `agents.md` and edit |
| `agents.md` | Your AGENTS.md (gitignored) | **Yes** (only if using `-AISummary`) |
| `README.md` | This file | No |

### Installer / build (this folder)

| File | Purpose |
|---|---|
| `installer-template.ps1` | Template for the bundled installer; `{{X_B64}}` placeholders get substituted at build time |
| `build-installer.ps1` | Reads each source file, base64-encodes it, writes the bundled installer |
| `dist/Install-CopilotLauncher.ps1` | The bundled single-file installer to share with peers |

After editing any source file, re-run `pwsh build-installer.ps1` to refresh the bundled installer.

---

## Briefing history

Every launch where the version advanced appends to `<stateDir>/briefing-history.log`:

```
========================================================================
=== Briefing 1.0.46 -> 1.0.47  @  2026-05-13 09:56:18
========================================================================
... full changelog block ...

=== AI SUMMARY ===
Highlights for MyProject:
- /fork now accepts an optional name and shows origin in the sessions dialog.
  Follows up on the /fork command I flagged in v1.0.45...
...
```

Single rolling file (one source of truth). Add a retention helper if you want to prune entries older than N days.

---

## Switches (Launch-Copilot.ps1)

| Flag | Default | What it does |
|---|---|---|
| `-AISummary` | off | Generate the AI-authored briefing summary (one premium request per launch when an update is detected) |
| `-NoUpdate` | off | Skip the explicit `copilot update` invocation. Useful when offline. |
| `-ConfigPath <path>` | `./config.json` | Use a different config file (e.g. for different projects) |
| `<extra args>` | — | Anything else is passed through to `copilot` (e.g. `--resume=...`, `--prompt`, `--allow-all`) |

---

## Requirements

| Requirement | Why |
|---|---|
| **PowerShell 7+** | UTF-8 console, modern CmdletBinding |
| **Windows** | The keep-alive loader patch and pkg-cache layout are Windows-specific; the briefing flow itself works anywhere PowerShell 7 + Copilot CLI run |
| **Python 3.x** (`py` or `python` on PATH) | Used by `repair-copilot-sessions.py`. Skipped silently if not present. |
| **`gh` CLI** *(optional)* | Used for tracked-issue status checks and remote changelog fallback. Skipped silently if not present. |
| **GitHub Copilot CLI** | The thing you're launching |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `copilot update returned unexpected output` | The CLI's update output format changed. Open `Launch-Copilot.ps1`, find `Invoke-CopilotUpdate`, and add a new regex for the observed pattern. |
| Briefing fires on every launch even when nothing changed | Check `<stateDir>/last-seen-version.txt` — it should contain the current version. Delete it to reset. |
| AI summary is generic / doesn't reference your project | Edit `agents.md` — the model only knows what's in there. The first AI summary trains the named session; subsequent summaries reuse that context. |
| Native addon patch not applying | Run as a user that can write to `%LOCALAPPDATA%\copilot\pkg\universal\<version>\native\win32\`. Verify with `Test-Path` on that directory. |
| Session repair patches a session you're actively using | Active sessions have an `inuse.<pid>.lock` file and are skipped. If yours is corrupt while open, close the CLI first, then re-launch. |
| Installer says "payload not populated" | The bundled `dist/Install-CopilotLauncher.ps1` was built from an outdated template. Re-run `build-installer.ps1`. |

---

## License

MIT — see [LICENSE](LICENSE).
