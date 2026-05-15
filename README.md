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

## Try it (build artifact from CI)

There's no installer yet — that's Phase 6. In the meantime, builds are produced **on demand** from GitHub Actions to keep CI minutes/storage under the free-tier budget:

1. Go to **[Actions → ci](https://github.com/SQLBImhugh/copilot-cli-launcher/actions/workflows/ci.yml)**.
2. Click **"Run workflow"** (top-right green button), leave **publish=true** checked, click the green **Run workflow** button.
3. Wait ~3-4 minutes for the run to finish. It builds, tests, then publishes the portable `.exe` and uploads it.
4. Click into the completed run, scroll to **Artifacts**, and download `CopilotLauncher-portable-<sha>` (~38 MB zip).
5. Unzip anywhere and double-click `CopilotLauncher.exe`. The runtime is baked in — no .NET install required.

> **Why not on every push?** Each Windows CI run consumes 2x quota minutes against GitHub's free tier (2,000 min/month), and each artifact is ~38 MB against the 0.5 GB included storage. Push events (and PRs) only run the lightweight build+test job; the slow publish+upload step is gated on a manual `workflow_dispatch` so we only build artifacts when someone actually wants one.

What you'll see in the build:

- **Sessions tab** (default): real list of all your `~/.copilot/session-state/` sessions with filter chips (Recent / All named / Heavily used / Show all), free-text search over cwd / repo / branch / id, and a working **Resume** button that opens Windows Terminal (or your chosen terminal from Settings) with `copilot --resume=<id>` in the session's working directory.
- **Settings tab**: pick your default terminal (Auto-detect / Windows Terminal / pwsh / powershell / cmd), see the app data folder path, and open it with one click.
- **Saved Launches / New Launch / Briefing**: still placeholders — those phases land next.

## Building locally

Requires:
- .NET 8 SDK or newer
- For the full WinUI 3 app: **Visual Studio 2022** (Community is fine) or **Visual Studio Build Tools** with the "Windows application development" workload. The Windows App SDK's PRI generation MSBuild tasks ship with VS, not the .NET CLI SDK alone.
- For just running tests: any .NET 8 SDK is enough — the testable code lives in a separate `CopilotLauncher.Core` class library that has no WinUI dependencies.

### Tests + core (no VS needed)

```powershell
dotnet test tests\CopilotLauncher.Tests\CopilotLauncher.Tests.csproj -c Release
```

### Full app

```powershell
pwsh scripts\build.ps1
```

A first-run release zip + GitHub Releases workflow + `iwr | iex` bootstrap will land in Phase 6.

See [docs/architecture.md](./docs/architecture.md) for the layering rationale.

## Using the legacy launcher

The original PowerShell kit lives at [`legacy/`](./legacy/README-LEGACY.md) and remains fully functional. Install it with:

```powershell
iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/legacy/dist/install.ps1 | iex
```

The 2.0 app will detect that install on first run and offer to migrate your settings + shortcuts.

## License

[MIT](./LICENSE)
