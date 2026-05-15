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

## Models

- **`CopilotSession`** — one entry per discovered session. Source: `workspace.yaml`. Includes `Id`, `Cwd`, `Repository`, `Branch`, `UserNamed`, `SummaryCount`, `IsLocked`, `SizeBytes`, paths, timestamps.
- **`SavedLaunch`** — user-defined launch shortcut. Source: `launches.json`. Includes `Label`, `WorkingDirectory`, `ResumeTarget`, flags, optional `TerminalOverride`.
- **`AppSettings`** — root settings object with 7 nested sub-settings groups (Terminal, CopilotCli, Briefings, Repair, SessionListing, LauncherBehavior, Storage) matching the architecture plan's Settings inventory.

## Helpers

- **`ArgQuoter`** — direct port of the legacy PS launcher's `Format-ShortcutArgs`. `Format(args)` → quoted command-line string for `.lnk` Arguments. `Split(line)` → tokenize a user-entered command-line fragment preserving quoted spans. Round-trip safe; covered by `ArgQuoterTests`.

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
