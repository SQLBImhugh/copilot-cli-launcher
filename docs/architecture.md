# Copilot Launcher 2.0 — Architecture

> Living doc. The full plan with phased delivery, settings inventory, and risks lives in the session plan; this doc is the version pinned to the repo for contributors.

## Layered design

```
┌────────────────────────────────────────────────────────────────────┐
│ src/CopilotLauncher                  WinUI 3 desktop app           │
│   - App.xaml / MainWindow                                           │
│   - Pages/SessionsPage, SavedLaunchesPage, NewLaunchPage,           │
│     BriefingPage, SettingsPage                                      │
│   - ViewModels/...                                                  │
│   - DI container; depends on CopilotLauncher.Core                   │
└─────────────────────────┬──────────────────────────────────────────┘
                          │ uses (Services, Models, Helpers)
                          ▼
┌────────────────────────────────────────────────────────────────────┐
│ src/CopilotLauncher.Core             pure .NET 8 class library     │
│   - Models: CopilotSession, SavedLaunch, AppSettings (+sub-types)   │
│   - Services: SettingsService, SessionDiscoveryService, …           │
│   - Helpers: ArgQuoter, ProcessUtil, …                              │
│                                                                      │
│   No WinUI dependencies. Builds with just .NET 8 SDK.               │
└────────────────────────────────────────────────────────────────────┘
                          ▲
                          │ references
                          │
┌────────────────────────────────────────────────────────────────────┐
│ tests/CopilotLauncher.Tests          xUnit, .NET 8                 │
│   - References Core only (not the WinUI app)                        │
│   - Runs on any Windows runner with .NET 8 SDK                      │
└────────────────────────────────────────────────────────────────────┘
```

## Why the split

The Windows App SDK's MSBuild targets (specifically `MrtCore.PriGen.targets`) require AppxPackage build tasks that ship with **Visual Studio**, not the .NET CLI SDK. Building the WinUI 3 app outside of VS — including from CI runners that don't have VS installed — fails with `Could not load file or assembly 'Microsoft.Build.Packaging.Pri.Tasks.dll'`.

By splitting the testable / UI-independent code into `CopilotLauncher.Core` (a plain .NET 8 class library), we get:

- **Tests build and run anywhere** with a stock .NET 8 SDK.
- **Core logic can be exercised from the command line** without ever opening Visual Studio.
- **CI runs fast and reliably** because most of the build doesn't touch WinUI.

The `CopilotLauncher` (WinUI 3) project still requires VS or VS Build Tools with the Windows App SDK / WinUI workload to build locally. That's acceptable because it only contains XAML pages + their thin code-behind — almost no business logic lives there.

## Local build

### Run tests + build core (no Visual Studio needed)

```powershell
dotnet build src\CopilotLauncher.Core\CopilotLauncher.Core.csproj -c Release
dotnet test  tests\CopilotLauncher.Tests\CopilotLauncher.Tests.csproj -c Release
```

### Build the full WinUI app (Visual Studio Build Tools required)

Install **Visual Studio 2022** (Community is fine) or **Visual Studio Build Tools** with these workloads:
- "Windows application development" (or ".NET desktop development" with the WinUI 3 component)
- ".NET 8 SDK" (auto-included with most recent VS installs)

Then:

```powershell
pwsh scripts\build.ps1
```

Output: `dist\CopilotLauncher\CopilotLauncher.exe` (~70 MB self-contained single file).

### CI

`.github/workflows/ci.yml` runs on `windows-latest`, which has Visual Studio Build Tools preinstalled. The full solution (Core + WinUI app + tests) builds on every PR.

## Service layer (Phase 0 baseline)

| Service | Phase landed | Responsibility |
|---|---|---|
| `ISettingsService` | 0 | Load/save `settings.json` under `%LOCALAPPDATA%\CopilotLauncher\`. Atomic writes. Tolerates corrupt JSON via backup + reset. |
| `ISessionDiscoveryService` | 0 | Enumerate sessions from `~/.copilot/session-state/<uuid>/workspace.yaml`. Detects active sessions via `inuse.*.lock` files. Tolerates malformed YAML. |

Subsequent phases add: `ITerminalDiscoveryService`, `ILaunchService`, `ISavedLaunchesService`, `IUpdateCheckService`, `IBriefingService`, `IAISummaryService`, `ISessionRepairService`, `IKnownBugWorkaroundService`, `IMigrationService`, `IShortcutExportService`.

### Per-project launch profiles

| Service | Responsibility |
|---|---|
| `IProjectsService` | Load/save `projects.json` under `%LOCALAPPDATA%\CopilotLauncher\` (same atomic-write + corrupt-backup contract as `IShortcutsService`). Also resolves a working directory to its governing `ProjectProfile`. |
| `IRepoConfigService` | Read/write the Copilot CLI config that lives *inside* a project directory. `Inspect(dir)` reports which config files the folder supplies; `WriteEnabledPlugins` / `ClearEnabledPlugins` manage `.github/copilot/settings.json` → `enabledPlugins`. |
| `IProjectLaunchService` | The single launch path: resolve the governing profile → pre-approve extensions → sync repo config → spawn → apply the after-launch action. Used by the Sessions tab (Resume / new session here) and the Projects tab's **▶ New session** button so a project starts identically from either. |

`ProjectMatcher` (in `Helpers/`) holds the precedence rules as pure static methods so they're testable without disk or UI:

- **Match** — exact path wins; otherwise the longest ancestor path with `IncludeSubdirectories`. Case-insensitive, separator-normalized, and prefix-safe (`C:\repos\app` does not match `C:\repos\app-tools`). Disabled profiles are skipped.
- **Resolve** — merges the profile over `AppSettings.SessionsResume`. Every override is nullable; `null` inherits. `Capabilities` replaces the global default wholesale rather than merging field-by-field, because a partial capability merge has no unambiguous meaning (is an empty tool list "inherit" or "clear"?).
- **No match** — returns exactly the pre-Projects behavior, including a `null` capability set, so adding the feature can't change how uncovered directories launch.

`SessionsViewModel.ResumeSession` and `StartNewSessionAt` both funnel through one private `LaunchAt`, which delegates to `IProjectLaunchService` — the same service the Projects tab's **▶ New session** button uses, so the two launch paths cannot drift.

### Structured copilot args

`Models/CopilotArgSpec.cs` holds a static catalog of the supported `copilot` CLI flags (46 of them) with their value kind (`Switch` / `Choice` / `Text` / `Number` / `Path`), choices, and category. `Helpers/CopilotArgSet.cs` parses a free-text args string into that structure and formats it back.

The round-trip is deliberately **lossless**: any token the catalog doesn't recognize is preserved verbatim in `UnrecognizedText` and re-emitted on `Format()`, so a user's hand-typed args survive being opened in the structured editor. Both `--flag value` and `--flag=value` are accepted; `--flag value` is emitted.

Flags already owned by other UI are deliberately **excluded** from the catalog to avoid emitting duplicates: `--allow-all` (its own toggle), `--agent` / `--available-tools` / `--excluded-tools` / `--disable-mcp-server` / `--disable-builtin-mcps` (the CapabilitiesEditor), and `--resume` (a dedicated field).

`ProjectImportPlanner` (also in `Helpers/`, pure static) powers the **Import from sessions** button:

- Groups sessions by `git_root` when the session recorded one, else by `cwd` — so a project covers the whole repo rather than whichever subfolder a session happened to start in.
- Names the project after the git repo: `repository` is `owner/repo`, so the label is the `repo` part (`.git` stripped). Falls back to the folder name when there's no remote, and to the full path for a drive root.
- Skips the user-profile root (that's where session with no project land).
- Lists everything but only pre-ticks rows that aren't already a project, still exist on disk, and aren't under an install/tooling root (`~/AppData`, `~/.copilot`, `~/.vscode`, `~/.nuget`, `~/.dotnet`, `%ProgramData%`, Program Files, Windows).
- Orders most-recently-used first within the recommended group, and surfaces each folder's last-used date. Two checkouts can share a repo name (`C:\Copilot\MSXInsights` vs `C:\Users\me\msxinsights`), and recency is what tells them apart — session count doesn't, since a stale folder can easily have more history.

Date formatting is shared with the Sessions list via `Helpers/RelativeTime`.

### In-repo vs. startup-flag settings

Verified against the `@github/copilot` bundle (v1.0.x). The CLI merges `.github/copilot/settings.json` over the user config at session start, so anything expressible there applies to *every* session in that folder — including ones started outside this launcher.

| Capability | In-repo file | Launcher role |
|---|---|---|
| `enabledPlugins` | `.github/copilot/settings.json` | **Managed** — written as a complete allowlist. |
| `hooks`, `disableAllHooks`, `mergeStrategy`, `extraKnownMarketplaces` | `.github/copilot/settings.json` | Detected only. |
| Workspace MCP servers | `.mcp.json`, `.github/mcp.json` | Detected only. |
| Instructions | `.github/copilot-instructions.md`, `AGENTS.md`, `CLAUDE.md` | Detected only. |
| Repo agents / skills | `.github/agents/`, `.github/skills/` | Detected only. |
| Language servers | `.github/lsp.json` | Detected only. |
| `--agent`, `--available-tools`, `--excluded-tools`, `--allow-all`, `--disable-mcp-server` | *(none)* | Always startup flags. |

> **`enabledPlugins` is an allowlist, not a patch.** The CLI keeps only the plugins whose key maps to `true`, so a partial map silently disables everything else. `RepoConfigService.WriteEnabledPlugins` therefore takes the full installed-plugin list and writes an explicit `true`/`false` for each. Keys are `name@marketplace` (e.g. `winui@awesome-copilot`).

## Models

- **`CopilotSession`** — one entry per discovered session. Source: `workspace.yaml`. Includes `Id`, `Cwd`, `Repository`, `Branch`, `UserNamed`, `SummaryCount`, `IsLocked`, `SizeBytes`, paths, timestamps.
- **`SavedLaunch`** — user-defined launch shortcut. Source: `launches.json`. Includes `Label`, `WorkingDirectory`, `ResumeTarget`, flags, optional `TerminalOverride`.
- **`ProjectProfile`** — per-directory launch overrides. Source: `projects.json`. `Path` + `IncludeSubdirectories` are the match key; `EnableAllowAll`, `PreApproveExtensions`, `ExtraCopilotArgs`, `TerminalOverride`, and `Capabilities` are nullable overrides; `RepoEnabledPlugins` + `SyncRepoConfigOnLaunch` drive the in-repo plugin allowlist.
- **`InstalledPluginInfo`** — one entry from `~/.copilot/config.json` → `installedPlugins`, carrying the `name@marketplace` key the CLI uses for `enabledPlugins`.
- **`AppSettings`** — root settings object with 7 nested sub-settings groups (Terminal, CopilotCli, Briefings, Repair, SessionListing, LauncherBehavior, Storage) matching the architecture plan's Settings inventory.

## Helpers

- **`ArgQuoter`** — direct port of the legacy PS launcher's `Format-ShortcutArgs`. `Format(args)` → quoted command-line string for `.lnk` Arguments. `Split(line)` → tokenize a user-entered command-line fragment preserving quoted spans. Round-trip safe; covered by `ArgQuoterTests`.
- **`ProjectMatcher`** — pure static path matching + override merging for `ProjectProfile`. Returns a `ResolvedLaunchProfile`. Covered by `ProjectMatcherTests`.
- **`ProjectImportPlanner`** — turns past-session history into de-duplicated `ProjectImportCandidate`s for the Projects tab's bulk import. Covered by `ProjectImportPlannerTests`.

## Cross-cutting concerns

- **DI**: `Microsoft.Extensions.DependencyInjection` configured in `App.xaml.cs`. Services registered as singletons; ViewModels resolved per-page.
- **MVVM**: `CommunityToolkit.Mvvm` with `[ObservableProperty]` and `[RelayCommand]` source generators (added in Phase 1+ as ViewModels land).
- **JSON**: `System.Text.Json` with `PropertyNamingPolicy.CamelCase`. Settings + launches are JSON; bake in source generation later if AOT becomes a goal.
- **YAML**: `YamlDotNet` for parsing `workspace.yaml`. Wrapped behind `SessionDiscoveryService`; no other code touches the YAML library directly.

## Testing strategy

Each Phase's services land alongside unit tests in `CopilotLauncher.Tests`. Current coverage:

- `ArgQuoterTests` — 8 tests covering pass-through, space-quoting, embedded-quote escaping, null/empty handling, theory-driven Split fixtures, and round-trip preservation.
- `SessionDiscoveryServiceTests` — 5 tests covering missing root, no-yaml folders, full field parsing, lock detection, and malformed YAML tolerance.

Total: 15 tests. CI runs `dotnet test` on every PR.

## Distribution (planned, not Phase 0)

- `dist\install.ps1` — BOM-free bootstrap that downloads the latest GitHub Release zip, extracts to `%LOCALAPPDATA%\CopilotLauncher\app\`, creates a Start Menu shortcut, and launches the app.
- `.github/workflows/release.yml` — triggers on `v*.*.*` tags; builds single-file exe, zips, uploads to a GitHub Release with auto-generated changelog.
- One-liner install (preserved from legacy):
  ```powershell
  iwr -useb https://github.com/SQLBImhugh/copilot-cli-launcher/raw/main/dist/install.ps1 | iex
  ```
