# Copilot Launcher 2.0

> **Status: rebuild in progress.** A WinUI 3 desktop app that replaces the old PowerShell-based launcher kit (now in [`legacy/`](./legacy/README-LEGACY.md)) with a session picker, saved-launches manager, and centralized settings.

A single Windows desktop app that:

- **Lists every Copilot CLI session** on your machine with smart filters (Recent / All named / Heavily used / Show all). One-click resume.
- **Saves reusable launches** (project + workdir + flags + terminal) inside the app — no scattered `.lnk` shortcuts on your Desktop.
- **Configurable default terminal** — Windows Terminal, pwsh.exe, ConEmu, Hyper, Tabby, anything with a token-based arg template.
- **Auto-update + version-change briefings** (with optional AI-authored summaries) carried over from the PS launcher.
- **Session repair + known-bug workarounds** ported to C# from the old `.py` and `.ps1` helpers.
- **Migrates from the legacy install** on first run; opt-in, non-destructive.

## Status

| Phase | Status |
|---|---|
| 0. Skeleton + legacy move | ✅ done |
| 1. Sessions tab MVP | 🟡 services + Sessions tab live; Saved Launches + New Launch wizard pending |
| 2. Saved Launches + New Launch wizard | ⏳ |
| 3. Briefings | 🟡 services done; UI pending |
| 4. Repair & workarounds | 🟡 SessionRepairService done; KnownBugWorkaroundService + UI pending |
| 5. Polish (tray, autostart, .lnk export) | ⏳ |
| 6. Release infrastructure | 🟡 CI artifact upload working; install.ps1 bootstrap pending |

Detailed plan: see the [architecture doc](./docs/architecture.md) once Phase 0 lands.

## Try it (build artifact from CI — manual only)

There's no installer yet — that's Phase 6. Builds are produced **on demand** from GitHub Actions because routine dev runs locally (see "Building locally" below) and we want CI minutes for genuine artifact requests:

1. Go to **[Actions → ci](https://github.com/SQLBImhugh/copilot-cli-launcher/actions/workflows/ci.yml)**.
2. Click **"Run workflow"** (top-right green button), leave **publish=true** checked, click the green **Run workflow** button.
3. Wait ~3-4 minutes for the run to finish. It builds, tests, then publishes the portable `.exe` and uploads it.
4. Click into the completed run, scroll to **Artifacts**, and download `CopilotLauncher-portable-<sha>` (~70-80 MB zip).
5. Unzip anywhere and double-click `CopilotLauncher.exe`. The .NET 8 runtime AND the Windows App Runtime are both baked in — no installs of any kind required, the app just runs.

> **CI policy**: direct pushes to `main` do not trigger CI at all. Only PR opens (none yet) and manual `workflow_dispatch` do. This keeps GH Actions minute usage at zero unless you explicitly want a build.

What you'll see in the build:

- **Sessions tab** (default): real list of all your `~/.copilot/session-state/` sessions with filter chips (Recent / All named / Heavily used / Show all), free-text search over cwd / repo / branch / id, and a working **Resume** button that opens Windows Terminal (or your chosen terminal from Settings) with `copilot --resume=<id>` in the session's working directory.
- **Settings tab**: pick your default terminal (Auto-detect / Windows Terminal / pwsh / powershell / cmd), see the app data folder path, and open it with one click.
- **Saved Launches / New Launch / Briefing**: still placeholders — those phases land next.

## Building locally

Routine dev runs **locally**, not on GitHub Actions, to keep CI minute usage at zero on direct pushes.

### Tests + Core (no extra install required)

```powershell
pwsh scripts\test.ps1
```

That restores, builds the Core class library, and runs all xUnit tests (~5 seconds on a warm machine). Use this before every push.

### Full WinUI app + .exe (requires Visual Studio Build Tools)

The `src/CopilotLauncher` (WinUI 3) project needs build-time MSBuild tasks that ship with Visual Studio, not the .NET CLI SDK. To build the full app + portable .exe locally, install one of:

- **Visual Studio 2022 Community** (free, ~6 GB) with the "Windows application development" workload, OR
- **Visual Studio Build Tools 2022** (free, ~3-5 GB) with the same workload

Then:

```powershell
pwsh scripts\build.ps1
```

Output: `dist\CopilotLauncher\CopilotLauncher.exe` (~70-80 MB self-contained portable).

If you don't want to install VS Build Tools, you can still get a built .exe via "Try it" above — manually trigger CI's `publish` job to produce the artifact for download.

See [docs/architecture.md](./docs/architecture.md) for why the project is split this way.

## Using the legacy launcher

The original PowerShell kit lives at [`legacy/`](./legacy/README-LEGACY.md) and remains fully functional. Install it with:

```powershell
iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/legacy/dist/install.ps1 | iex
```

The 2.0 app will detect that install on first run and offer to migrate your settings + shortcuts.

## License

[MIT](./LICENSE)
