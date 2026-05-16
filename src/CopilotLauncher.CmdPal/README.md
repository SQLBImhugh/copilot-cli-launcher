# CopilotLauncher.CmdPal — PowerToys Command Palette extension

Sibling project to the main launcher. Reuses `CopilotLauncher.Core` to expose two top-level commands in the [PowerToys Command Palette](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview):

| Top-level command | What it does |
|---|---|
| **Resume Copilot session…** | Lists every session under `~/.copilot/session-state/`, sorted newest-first; selecting one spawns `copilot --resume=<id>` in your default terminal (uses your Sessions Resume defaults). |
| **Launch Copilot shortcut…** | Lists your saved Shortcuts (the same ones the main app shows in its Shortcuts tab); selecting one launches with that shortcut's per-shortcut config. |

## Status: starter scaffold

This is a **starter scaffold** verified to restore + reference the right Microsoft packages, with all the code wiring complete (`ICommandProvider`, `IListPage`, `IInvokableCommand` implementations using the Toolkit base classes). It has not yet been deploy-tested inside an actual PowerToys Command Palette instance — that requires Visual Studio's WinAppSDK toolchain (see **Building + deploying** below).

The base SDK + Toolkit ship together in one NuGet package (`Microsoft.CommandPalette.Extensions` 0.9.x, preview) so you don't need to clone the PowerToys repo.

What's implemented:
- ICommandProvider exposes two top-level commands
- ResumeSessionPage + LaunchShortcutPage list items via the same `CopilotLauncher.Core` services as the main app
- ResumeSessionCommand + LaunchShortcutCommand call `LaunchService.Spawn` for one-click resume/launch

What's intentionally minimal:
- No `IFallbackCommandItem` yet (lets the user type into the palette and get sessions matching their query as a top-level result — would be Phase 2)
- No `IDynamicListPage` yet (server-side filtering as the user types — relies on PT's built-in client-side filter for now)
- No `IContextItem` (right-click "Open in Explorer", "Copy session id", etc. — Phase 2)
- No settings page (`ICommandSettings`) — would surface our SessionsResume defaults inside the palette itself
- No tags / details panes — just title + subtitle

## Building + deploying

Like the main app, this project requires Visual Studio's installed Windows SDK + CsWinRT tooling (the AppxPackage MSBuild tasks + the `cswinrt.exe` projection generator). `dotnet build` from the standalone .NET 8 SDK will fail with `Could not find the Windows SDK in the registry` — same constraint as the main `CopilotLauncher` WinUI project.

Inside Visual Studio 2022 or higher (with the Windows App SDK / WinUI workload):

1. Open `CopilotLauncher.sln`
2. Right-click `CopilotLauncher.CmdPal` → **Deploy**
3. Open PowerToys Command Palette (Win+Alt+Space)
4. Run the **Reload** command (subtitle "Reload Command Palette Extension")
5. Type "Copilot" — both top-level commands should appear

After the first deploy, every subsequent `Deploy` + `Reload` updates the running extension. Don't just "Build" — you have to "Deploy" so the package is reregistered.

## CLSID

The COM class ID `50BE806C-4555-48BE-9D31-7E427ECA40C0` appears in three places:
- `[Guid(...)]` on `CopilotLauncherExtension`
- `<com:Class Id="..." />` in `Package.appxmanifest`
- `<CreateInstance ClassId="..." />` in the same manifest

Keep all three in sync if you regenerate.

## Why this layout vs. a plugin

PowerToys ships **two** extensibility frameworks for the launcher surface:
- **Run plugins** (legacy) — DLLs dropped into a folder, `IPlugin` interface, single-window
- **Command Palette extensions** (modern, this project) — MSIX-packaged, AppExtension framework, `ICommandProvider`-based, multi-page, much richer

Microsoft is steering everyone toward the Command Palette. The standalone launcher MSIX continues to ship in parallel — users can install either or both.
