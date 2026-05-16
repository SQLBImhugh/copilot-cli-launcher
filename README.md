# Copilot CLI Launcher

A modern WinUI 3 desktop app to manage and resume GitHub Copilot CLI sessions on Windows. Replaces the legacy PowerShell launcher kit (now in [`legacy/`](./legacy/)).

[![Latest release](https://img.shields.io/github/v/release/SQLBImhugh/copilot-cli-launcher?display_name=tag&logo=github)](https://github.com/SQLBImhugh/copilot-cli-launcher/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)

## What it does

- **Sessions browser** — every `~/.copilot/session-state/` session in one list with filter chips (Recent / Named / Heavily used / Show all), search across path / repo / branch / id, multi-key sort, and one-click **Resume** in your preferred terminal. Includes a *Save as shortcut…* button so any session becomes a reusable launch.
- **Saved Shortcuts** — pinned project + workdir + flags + terminal combos. One click to launch; one click to export to a Windows `.lnk` so you can pin to taskbar / start menu.
- **New Shortcut wizard** — live command preview as you toggle `--allow-all`, `--no-color`, AI summary, agents.md context override, etc.
- **Briefing tab** — version-bump changelog history. Optional AI summary uses your local `copilot --prompt` to write a 4-6 bullet "what changed for you" rendered as Markdown.
- **Settings** — terminal default, Sessions Resume defaults, AI Summary toggle + context file, briefing frequency, repair/workaround toggles, launcher behavior (after-launch action, start-with-Windows, theme), storage paths, about.
- **Compact mode** — title-bar toggle resizes the window to a 320×640 mini-launcher showing just your saved shortcuts. Great for keeping the launcher pinned to a corner.
- **System tray icon** — right-click for Show / Quit. "Hide to tray" can replace minimize-on-close.
- **Migration** — detects an old PowerShell-launcher install on first run and offers to import its `agents.md`. Non-destructive.
- **Session repair** — silently fixes Copilot sessions with dangling `tool_use` events (Anthropic API tool-pairing bug) so `--resume` works again. Backed up before mutating; skips locked / in-use sessions.
- **Known-bug workarounds** — port of the legacy `Repair-Win32NativeAddon` for [github/copilot-cli#3298](https://github.com/github/copilot-cli/issues/3298). Settings-gated, idempotent.
- **Auto-update awareness** — runs `copilot update` on a configurable schedule (every launch / daily / weekly / manual) and surfaces version-bump briefings automatically.

## Themes

Switch via the title-bar palette button or **Settings → Launcher behavior → Theme**.

| Theme | Description |
|---|---|
| **Copilot CLI** *(default)* | Sampled from the GitHub Copilot CLI welcome banner — near-black `#0A0A0A` backdrop, cyan `#0099CC` accents, bright pink `#CC66CC` card outlines, green `#30C868` checkboxes & toggles. **VT323 pixel font** on page titles + Settings section subheadings, **Consolas** body text. |
| Follow system | Matches Windows light/dark mode. |
| Light | Force Fluent light. |
| Dark | Force Fluent dark. |

## Install

The signed MSIX is the recommended install. The portable `.exe` is for one-off use without registration.

### Option A — `.msix` (signed, modern install)

The MSIX is signed with a self-signed dev cert. To install on a machine that hasn't trusted that cert yet:

1. Download both files from the [latest release](https://github.com/SQLBImhugh/copilot-cli-launcher/releases/latest):
   - `CopilotLauncher-vX.Y.Z.msix`
   - `CopilotLauncher-vX.Y.Z-DevCert.cer`
2. Trust the cert (one-time, **no admin needed**):
   ```powershell
   Import-Certificate -FilePath .\CopilotLauncher-*-DevCert.cer `
     -CertStoreLocation Cert:\CurrentUser\TrustedPeople
   ```
3. Install:
   ```powershell
   Add-AppxPackage .\CopilotLauncher-*.msix
   ```
   Or just double-click the `.msix` in Explorer.

To uninstall: Windows Settings → Apps → "Copilot CLI Launcher" → Uninstall.

### Option B — Portable `.exe`

Download `CopilotLauncher-vX.Y.Z.exe` from the [latest release](https://github.com/SQLBImhugh/copilot-cli-launcher/releases/latest) and run. Self-contained — bundles the .NET 8 runtime AND the Windows App SDK, no separate downloads. ~65 MB.

### Verifying downloads

```powershell
Get-FileHash CopilotLauncher-*.msix -Algorithm SHA256
```

Compare against `SHA256SUMS.txt` in the same release.

## Requirements

- Windows 10 build 17763 (1809) or newer; Windows 11 recommended (Mica backdrop on non-Copilot-CLI themes)
- [GitHub Copilot CLI](https://github.com/github/copilot-cli) installed and on `PATH` (the launcher resolves `copilot.cmd` / `copilot.exe` / `copilot.ps1` shims automatically)
- Optional: Windows Terminal (auto-detected as the preferred terminal)

The .NET 8 runtime is bundled — you do not need to install it separately.

## Project layout

```
src/
  CopilotLauncher.Core/   pure .NET 8 class library — all testable logic
                          (services, ViewModels, models, helpers)
  CopilotLauncher/        WinUI 3 / Windows App SDK 1.6 desktop app
                          (Pages, MainWindow, App.xaml, ThemeManager)
tests/
  CopilotLauncher.Tests/  xUnit, .NET 8, references Core only

scripts/
  test.ps1                one-command local validation (~5s, no VS needed)
  build.ps1               WinUI publish to dist\CopilotLauncher\CopilotLauncher.exe
                          (requires Visual Studio MSBuild — see below)
  package-msix.ps1        sign + bundle .msix from the build output
                          (requires the WinAppCli — winget install Microsoft.WinAppCli)

docs/
  architecture.md         Why Core/UI is split, key conventions
legacy/                   Frozen 1.x PowerShell launcher kit
  Launch-Copilot.ps1, New-CopilotShortcut.ps1, dist/install.ps1, …
```

## Building from source

### Tests + Core library (no Visual Studio required)

```powershell
pwsh scripts\test.ps1
```

Restores, builds the Core class library, runs all xUnit tests in ~5 seconds. Use this as your local validation before every push.

### Full WinUI app + portable `.exe` (requires Visual Studio MSBuild)

WinUI 3's MSBuild tasks (`MrtCore.PriGen.targets`, the AppxPackage tasks) ship with **Visual Studio**, not the .NET CLI SDK. Install one of:

- Visual Studio 2022 Community or higher with the "Windows application development" workload
- Visual Studio Build Tools 2022 with the same workload

Then:

```powershell
pwsh scripts\build.ps1
```

Output: `dist\CopilotLauncher\CopilotLauncher.exe` (~65 MB self-contained).

### Building the signed MSIX

After `build.ps1` produces the `.exe`:

```powershell
pwsh scripts\package-msix.ps1
```

Requires the [WinAppCli](https://github.com/microsoft/WinAppCLI) (`winget install Microsoft.WinAppCli`). Outputs `dist\CopilotLauncher.msix` signed with a per-machine dev cert at `build\msix-staging\devcert.pfx`.

For a production release, swap that dev cert for a real CA-signed cert and add `--timestamp http://timestamp.digicert.com` to the `winapp package` call inside the script so signatures don't expire when the cert does.

## Architecture (one-paragraph version)

Strict Core / UI split. All testable logic lives in `CopilotLauncher.Core` — a plain .NET 8 class library with no WinUI dependencies — so tests build and run with just the .NET 8 SDK. The WinUI 3 project (`CopilotLauncher`) holds Pages, ViewModels (thin), code-behind, the title bar, and `ThemeManager`. ViewModels live in Core but expose events that the WinUI side wires up to platform-specific concerns (autostart registry writes, tray icon lifecycle, dispatcher marshalling for off-thread refresh). The full architecture doc is at [`docs/architecture.md`](./docs/architecture.md).

## Releases

Manual / on-demand. Routine dev runs locally; CI is `workflow_dispatch`-only to keep GitHub Actions minute usage at zero on direct pushes. Releases are typically built locally + uploaded via `gh release create` (zero CI minutes), but the `release.yml` workflow exists for cases where building on a fresh runner is desirable.

The `.github/workflows/ci.yml` workflow exists only for `pull_request` events (path-filtered to skip docs-only changes) and manual `workflow_dispatch`. Direct pushes to `main` do not trigger CI.

## The legacy PowerShell launcher

The original kit lives at [`legacy/`](./legacy/) and remains fully functional. Bug fixes still land there too. To install the legacy launcher:

```powershell
iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/legacy/dist/install.ps1 | iex
```

The 2.0 app detects that install on first run and offers to migrate the legacy `agents.md` into its own AppData folder. Non-destructive — leaves the legacy install intact.

For the legacy README's full feature description, see [`legacy/README-LEGACY.md`](./legacy/README-LEGACY.md). For the previous (pre-launch) version of this README, see [`legacy/README-2.0-PRELAUNCH.md`](./legacy/README-2.0-PRELAUNCH.md).

## Contributing

Conventions for code changes are documented in [`.github/copilot-instructions.md`](./.github/copilot-instructions.md). Highlights:

- All testable logic in Core, no UI types leaking in
- `pwsh scripts\test.ps1` must stay green before every push
- Every commit subject contains `[skip ci]` to protect the user's GitHub Actions minute budget — direct pushes shouldn't burn minutes since `ci.yml` is filtered to PR-only

## License

[MIT](./LICENSE)
