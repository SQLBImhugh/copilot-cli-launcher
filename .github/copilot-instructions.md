# Copilot CLI Launcher 2.0 — agent instructions

A WinUI 3 desktop app that lists existing GitHub Copilot CLI sessions, lets the user save reusable launches, and centralizes settings (default terminal, AI summary on/off, auto-update behavior, etc.). Replaces the legacy PowerShell launcher kit (now in `legacy/`).

> **For the legacy PowerShell kit**, see [`copilot-instructions-LEGACY.md`](./copilot-instructions-LEGACY.md). Bug-fix patches still land there; new features go to 2.0.

## Architecture

Three projects, layered:

```
src/CopilotLauncher.Core    pure .NET 8 class library — no WinUI deps
  ├ Models/                 CopilotSession, SavedLaunch, AppSettings (+ subtypes)
  ├ Helpers/                ArgQuoter (port of Format-ShortcutArgs from legacy)
  └ Services/               SettingsService, SessionDiscoveryService, …

src/CopilotLauncher         WinUI 3 / .NET 8 / Windows App SDK 1.6 desktop app
  ├ App.xaml + MainWindow   NavigationView with 5 tabs + Settings
  ├ Pages/                  Sessions, SavedLaunches, NewLaunch, Briefing, Settings
  └ ViewModels/             (added per-phase as features land)
  References → Core

tests/CopilotLauncher.Tests xUnit, .NET 8
  References → Core (NOT the WinUI app)
```

## Why the Core split

The Windows App SDK's `MrtCore.PriGen.targets` requires AppxPackage build tasks that ship with **Visual Studio**, not the .NET CLI SDK. Without VS Build Tools installed, the WinUI 3 csproj can't build at all. By keeping all testable logic in `CopilotLauncher.Core` (a plain .NET 8 class library), tests build and run with just the .NET 8 SDK — important for both fast local iteration and reliable CI.

This is a hard rule:

- ✅ All business logic, models, services, helpers go in **Core**
- ✅ Pages, view-models, code-behind, XAML → **CopilotLauncher** (WinUI app)
- ❌ Never put logic that could be unit-tested into the WinUI project; move it to Core

## File map

```
src/
  CopilotLauncher.Core/              ← class library (testable, .NET 8)
    Models/{CopilotSession,SavedLaunch,AppSettings}.cs
    Helpers/ArgQuoter.cs             ← port of legacy Format-ShortcutArgs
    Services/{SettingsService,SessionDiscoveryService}.cs
  CopilotLauncher/                   ← WinUI 3 app
    App.xaml(.cs)                    ← DI container, App.Services
    MainWindow.xaml(.cs)             ← NavigationView shell + Mica backdrop
    Pages/                           ← five page stubs; pages get real UI per phase
    app.manifest                     ← PerMonitorV2 DPI awareness
    CopilotLauncher.csproj
tests/
  CopilotLauncher.Tests/             ← xUnit, .NET 8, ProjectReference → Core only
    {ArgQuoterTests, SessionDiscoveryServiceTests}.cs
scripts/
  build.ps1                          ← wraps `dotnet publish` for single-file
docs/
  architecture.md                    ← living architecture doc
.github/
  workflows/ci.yml                   ← restore + build + test on PR (windows-latest)
  copilot-instructions.md            ← this file
  copilot-instructions-LEGACY.md     ← old PS-kit instructions (preserved)
legacy/                              ← entire 1.x PS launcher, unmodified
  ├ Launch-Copilot.ps1, New-CopilotShortcut.ps1, installer-template.ps1, …
  ├ dist/{Install-CopilotLauncher.ps1, install.ps1}
  └ README-LEGACY.md
CopilotLauncher.sln
README.md                            ← top-level user docs (points at 2.0)
.gitignore                           ← bin/, obj/, dist/, legacy/{config.json,agents.md}
```

## Workflow for changes

### Adding a new service

1. Define the interface in `src/CopilotLauncher.Core/Services/IFooService.cs`.
2. Implement in `FooService.cs` next to it.
3. Register in `App.xaml.cs::ConfigureServices()` as a singleton (Phase 0 baseline).
4. Add unit tests in `tests/CopilotLauncher.Tests/FooServiceTests.cs`.
5. Run `dotnet test tests\CopilotLauncher.Tests\CopilotLauncher.Tests.csproj -c Release` — must stay green.
6. Use the service from a Page or ViewModel via `App.Services.GetService(typeof(IFooService))!`.

### Adding a new page

1. Add `Pages/FooPage.xaml` + `FooPage.xaml.cs` in the WinUI app.
2. Add a `<NavigationViewItem Tag="foo" Content="Foo">` entry to `MainWindow.xaml`.
3. Wire the tag → `typeof(FooPage)` mapping in `MainWindow.xaml.cs::NavView_SelectionChanged`.

### Adding a new setting

1. Add the property to the relevant sub-settings class in `Models/AppSettings.cs`.
2. Use `[ObservableProperty]` on the SettingsViewModel (when VMs land).
3. Add a `SettingsCard` to `Pages/SettingsPage.xaml` bound to the property.
4. The `SettingsService` writes/reads the entire `AppSettings` object as JSON; no extra serialization wiring needed.

### Building locally

The convention here is **local-first dev**: routine builds and tests run on the contributor's machine, not on GitHub Actions. CI exists only as an on-demand artifact builder + a future PR safety net.

```powershell
# One-command local validation. Restore + build Core + run xUnit (~5s).
# Use before every push. Does NOT need Visual Studio Build Tools.
pwsh scripts\test.ps1

# Full app + portable .exe. Requires Visual Studio 2022 (Community is fine)
# or VS Build Tools 2022 with the "Windows application development"
# workload. Output: dist\CopilotLauncher\CopilotLauncher.exe (~70-80 MB).
pwsh scripts\build.ps1
```

CI (`.github/workflows/ci.yml`) only triggers on `pull_request` (with path filter to skip doc-only changes) and manual `workflow_dispatch`. It does NOT trigger on pushes to `main`. Direct pushes burn zero CI minutes; PR opens trigger a `build-and-test` validation; manual dispatch with `publish=true` produces a downloadable `.exe` artifact.

## Coding conventions

### C#

- **C# 12** with `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`.
- File-scoped namespaces (`namespace CopilotLauncher.Services;`).
- One public class per file.
- Use `required` for non-nullable model properties that must be initialized at construction.
- For services, `internal` test-only constructors (e.g. for path overrides) — use `BindingFlags.NonPublic` + reflection in tests rather than making things public.

### XAML

- One Page per `.xaml` + `.xaml.cs` pair. Code-behind is `InitializeComponent()` and trivial event-to-VM wiring only.
- Prefer `x:Bind` with `Mode=OneWay` over `Binding`. Faster, type-checked at compile time.
- Use built-in styles: `TitleLargeTextBlockStyle`, `SubtitleTextBlockStyle`, `BodyTextBlockStyle`, `CaptionTextBlockStyle`.
- Glyph icons from Segoe Fluent Icons; Mica backdrop applied once in `MainWindow.xaml.cs` ctor.

### Settings

- Settings persist as `%LOCALAPPDATA%\CopilotLauncher\settings.json` via atomic write (write `.tmp` then `File.Replace`).
- A corrupt JSON triggers a `.corrupt-<timestamp>` backup + reset to defaults. Never silently overwrite without a backup.
- Default values live on the model property declarations in `AppSettings.cs`, not in `SettingsService`. Single source of truth.

### Sessions

- Session metadata source of truth is `~/.copilot/session-state/<uuid>/workspace.yaml`. Don't parse `events.jsonl` for metadata — it can be 100s of MB.
- Active sessions are detected by sibling `inuse.*.lock` files. Never modify or repair a locked session.

## Testing

### Unit (xUnit, runs in CI)

- Each service that has side effects beyond a getter gets a `*Tests.cs` companion.
- Use `Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid())` for per-test temp directories. Implement `IDisposable` to clean up.
- For services with default ctors that read from `%USERPROFILE%`, expose an `internal` ctor that takes the root path. Construct via reflection in tests so production code stays clean.

### Manual smoke (per phase)

Documented in the session plan.md and run before each phase commit.

## Known patterns to preserve (carried from legacy)

- **`copilot.cmd` resolution for `Process.Start`** — npm on Windows ships `copilot`, `copilot.cmd`, and `copilot.ps1`. `Get-Command copilot` may resolve the `.ps1`, which `Process.Start` cannot run. The C# port (when added in Phase 1+) must look for the `.cmd` sibling first. See `legacy/Launch-Copilot.ps1::Resolve-CopilotProcessTarget` for the well-tested logic.
- **DateTime auto-deserialization in changelog API** — GitHub Releases API `published_at` field auto-deserializes to `DateTime`, not `string`. The C# port must use `.ToString("yyyy-MM-dd")`, not `.Substring(0, 10)`.
- **PowerShell parameter naming gotcha** — `$Args` is a reserved automatic variable. Carried-over PS code must use `$Arguments` or another name.
- **UTF-8 BOM on .ps1 files invoked under PS 5.1** — preserved in `legacy/dist/install.ps1` (no BOM, ASCII-only) vs `legacy/dist/Install-CopilotLauncher.ps1` (BOM-bearing). If the new app ever ships PS scripts, follow the same rule.

## Upstream issues to watch

| Issue | Why it matters |
|---|---|
| [github/copilot-cli#3298](https://github.com/github/copilot-cli/issues/3298) | The Win32 keep-alive `native/win32/index.js` loader regression. The KnownBugWorkaroundService (Phase 4) ports the legacy fix; auto-disables when issue closes via the daily-cached `gh` API check. |

When upstream closes a tracked issue and the fix actually ships, remove the corresponding workaround from `KnownBugWorkaroundService` and from the default `TrackedGitHubIssues` list in `RepairSettings`. Update `legacy/Launch-Copilot.ps1` similarly so the legacy kit stays in sync.
