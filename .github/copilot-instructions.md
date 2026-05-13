# Copilot CLI Launcher

Windows launcher kit for [GitHub Copilot CLI](https://github.com/github/copilot-cli) that adds auto-update, version-change briefings, AI-authored summaries, session repair, and known-bug workarounds. Distributed as a portable folder + a single-file installer.

## Architecture

The repo is **3 user-facing files + 2 build-time files + a generated installer**:

- **`Launch-Copilot.ps1`** — The wrapper. PowerShell 7+ script that runs on every Copilot CLI launch. Pre-flight pipeline: load config → check tracked GitHub issues for closure → run `copilot update` → apply known-bug workarounds → detect version change → render changelog briefing → optionally generate AI summary → append to history → hand off to `copilot` with passthrough args.
- **`repair-copilot-sessions.py`** — Standalone Python helper. Walks every session under `~/.copilot/session-state/<uuid>/events.jsonl`, finds tool_use IDs without matching tool.execution_complete (typical cause: Ctrl+C aborted a tool mid-flight), and inserts a synthetic `success: false` completion event. Idempotent. Skips active sessions (lock file present). Backs up `events.jsonl` before mutating.
- **`config.example.json` + `agents.example.md`** — Templates copied to `config.json` + `agents.md` by the user (or by the installer wizard with substitutions). The two `.example` files ship in the repo; the unsuffixed user copies are gitignored.
- **`installer-template.ps1` + `build-installer.ps1`** — Build pipeline. The template has `{{LAUNCH_COPILOT_PS1_B64}}`, `{{REPAIR_SESSIONS_PY_B64}}`, etc. placeholders. The build script reads each source file, base64-encodes it, substitutes the placeholders, and writes `dist/Install-CopilotLauncher.ps1` — the single-file distributable.
- **`dist/Install-CopilotLauncher.ps1`** — The bundled installer (committed to the repo). Self-contained; downloading this one file is sufficient to install the kit. Wizard mode prompts for project name / install dir / shortcut options. Silent mode accepts all params on command line.

## File map

```
Launch-Copilot.ps1            ← the launcher (no project hardcoding)
repair-copilot-sessions.py    ← session events.jsonl repair helper
config.example.json           ← annotated config template
agents.example.md             ← AGENTS.md template with {ProjectName} placeholder
installer-template.ps1        ← installer with {{X_B64}} placeholders
build-installer.ps1           ← bundles source files into dist/
dist/Install-CopilotLauncher.ps1  ← bundled distributable (regenerate with build-installer.ps1)
dist/install.ps1              ← BOM-free bootstrap for `iwr | iex` one-liner (NOT generated; edit directly)
README.md                     ← user-facing docs
LICENSE                       ← MIT
.gitignore                    ← excludes config.json, agents.md, OS noise
```

## Workflow for changes

After editing **any** of the source files, regenerate the installer so `dist/Install-CopilotLauncher.ps1` stays in sync:

```powershell
pwsh build-installer.ps1
git add -A
git commit -m "..."
```

The build script verifies all `{{X}}` placeholders were substituted and warns if any remain.

To smoke-test the installer end-to-end without affecting your real install:

```powershell
$test = "$env:TEMP\copilot-launcher-test"
Remove-Item -Recurse -Force $test -ErrorAction SilentlyContinue
pwsh dist\Install-CopilotLauncher.ps1 `
    -Silent -InstallDir $test -ProjectName "TestProj" `
    -StateDir "$test\state" -NoDesktopShortcut
# Verify files written, then:
Remove-Item -Recurse -Force $test
```

## Coding conventions

### PowerShell

- **PowerShell 7+** target. Don't rely on Windows PowerShell 5.1 quirks.
- Use `[CmdletBinding()]` and typed `param()` blocks at the top of every script and function.
- Force UTF-8 console encoding at script start (`[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`) so em-dashes and emoji from AI summaries render correctly instead of mojibake (`Γçö` for `—`).
- Function naming: `Verb-Noun` (PascalCase), prefer approved verbs (`Get-`, `Set-`, `Test-`, `Invoke-`, `Repair-`, `New-`).
- Helper functions live above `# ---------- main ----------` in `Launch-Copilot.ps1`. Code below that line runs at top level — keep it linear and readable.
- For `[Nullable[bool]]` parameters, prefer `[switch]` + `$PSBoundParameters.ContainsKey('X')` checks. Nullable bools don't survive the child-process boundary cleanly when invoked via `pwsh -File ...`.
- Color conventions: `DarkGray` for diagnostic chatter, `DarkYellow` for non-fatal warnings, `Green` for success/state-change, `Yellow` for "needs attention", `Cyan/Magenta` for section headers.
- Idempotency is mandatory. Every `Repair-*` function must be a no-op when nothing needs fixing, must back up before mutating, and must log only when it actually changed something.

### Python

- Single-file scripts only (no packages). Each script is invoked as `py -3 script.py` from PowerShell.
- Standard library only — no pip dependencies. The launcher needs to work on a vanilla Windows machine with just Python installed.
- Use `from __future__ import annotations` for modern type hints on Python 3.9+.
- Never raise unhandled exceptions to the caller. Failures should print a clear message to stderr and return a sensible exit code (0 = no-op or success, nonzero = hard error). The launcher swallows the script's output and continues regardless — silent failure is preferable to crashing the launch.

### Markdown

- The user-facing `README.md` is the source of truth for setup. Keep it current with any UX changes.
- The `agents.example.md` template uses `{ProjectName}` as the only placeholder. The installer substitutes this when generating the user's `agents.md`. The launcher also substitutes it at runtime if the user edits the file directly.

## Testing approach

Manual smoke tests are the standard. There's no automated test suite — the launcher is interactive and Copilot CLI's behavior changes between versions, so the cost of maintaining tests against a moving target is higher than the value.

When you make a change, run **both** of these before committing:

1. **Direct launcher test** — verifies the script itself works:
   ```powershell
   pwsh -NoProfile -File Launch-Copilot.ps1 -NoUpdate --version
   ```
   Should print the bridge messages, then `GitHub Copilot CLI X.Y.Z.`, then exit cleanly.

2. **Installer roundtrip test** — verifies the bundled installer extracts and runs correctly:
   ```powershell
   pwsh build-installer.ps1
   $test = "$env:TEMP\copilot-launcher-test"
   pwsh dist\Install-CopilotLauncher.ps1 -Silent -InstallDir $test `
       -ProjectName "Test" -StateDir "$test\state" -NoDesktopShortcut
   pwsh "$test\Launch-Copilot.ps1" -NoUpdate --version
   Remove-Item -Recurse -Force $test
   ```

Verify any new `Repair-*` function with both code paths (file present → no-op, file absent → patches and logs).

## Distribution

The installer is published in the repo at `dist/Install-CopilotLauncher.ps1`. Direct raw download URL:

```
https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/dist/Install-CopilotLauncher.ps1
```

Peers download that one file and run it with `pwsh -ExecutionPolicy Bypass -File .\Install-CopilotLauncher.ps1`. The wizard handles everything else.

For the **one-liner web install** (`iwr -useb .../dist/install.ps1 | iex`), there is a separate tiny bootstrap at `dist/install.ps1`. **Why two files**: the bundled installer is written with a UTF-8 BOM (added in commit `b919961` to fix Windows PowerShell 5.1 mojibake on em-dashes/emoji). PowerShell's `iex` does not strip a leading U+FEFF from a string it is asked to evaluate, so piping the bundled installer through `iwr | iex` (or `iex "& { $(irm URL) } -args"`) breaks with `Missing argument in parameter list`. The bootstrap is plain ASCII (no BOM), no `param()` block, downloads the bundled installer to `$env:TEMP`, and invokes it via `& $tempPath` so PowerShell's normal file-execution path handles the BOM correctly. Don't add a BOM to `dist/install.ps1` and don't add user-visible non-ASCII text to it. If you ever need to update the bootstrap's URL or behavior, edit it directly — it isn't generated by `build-installer.ps1`.

When releasing a new version:

1. Edit source files
2. Run `pwsh build-installer.ps1` to regenerate `dist/`
3. Bump any version reference in `README.md` if the UX changed
4. Commit with a descriptive message; the bundled installer ships in the same commit as the source changes (one source of truth per commit)
5. Optionally tag with `git tag vX.Y.Z && git push --tags` if the change is user-facing

## Known patterns to preserve

- **`copilot update` output parsing**: The CLI emits either `"No update needed, current version is X.Y.Z, ..."` or `"Copilot CLI version X.Y.Z installed."`. Both patterns are matched in `Invoke-CopilotUpdate`. If GitHub changes the output format, add a new regex branch and surface unrecognized output as a warning (we already do this).
- **Tracked-issue caching**: `Test-CopilotCliIssueClosed` caches the GitHub API response for 24 hours in `<stateDir>/issue-<N>-status.json`. Don't lower the TTL — there's no benefit to hitting the API more than daily, and `gh` rate limits matter on shared workstations.
- **Session lock detection**: `repair-copilot-sessions.py` skips sessions with `inuse.*.lock` files. Never try to repair an active session — the live writer would race with our patcher.
- **Windows-only assumptions**: `Repair-Win32NativeAddon` writes to `%LOCALAPPDATA%\copilot\pkg\universal\<version>\native\win32\`. This path is Windows-specific; the function early-returns if the directory doesn't exist (so the launcher works on macOS/Linux too, just without the keep-alive workaround).

## Upstream issues to watch

| Issue | Why it matters |
|---|---|
| [github/copilot-cli#3298](https://github.com/github/copilot-cli/issues/3298) | The keep-alive `native/win32/index.js` loader regression. When closed and the fix ships, the launcher's banner will tell the user to remove `Repair-Win32NativeAddon` from their setup. |

When upstream closes a tracked issue and the fix actually ships in a new CLI version, remove the corresponding `Repair-*` function from `Launch-Copilot.ps1` and the issue from the default `trackedIssues` list in `config.example.json`. Update the README's troubleshooting table accordingly.
