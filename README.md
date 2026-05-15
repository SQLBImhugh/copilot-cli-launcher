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
| 0. Skeleton + legacy move | 🟡 in progress |
| 1. Sessions tab MVP | ⏳ |
| 2. Saved Launches + New Launch wizard | ⏳ |
| 3. Briefings | ⏳ |
| 4. Repair & workarounds | ⏳ |
| 5. Polish (tray, autostart, .lnk export) | ⏳ |
| 6. Release infrastructure | ⏳ |

Detailed plan: see the [architecture doc](./docs/architecture.md) once Phase 0 lands.

## Building

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
