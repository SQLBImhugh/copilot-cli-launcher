using System;
using CopilotLauncher.CmdPal.Pages;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal;

/// <summary>
/// Top-level command provider. Exposes two entries in the PowerToys
/// Command Palette:
///   - "Resume Copilot session…"  — opens <see cref="ResumeSessionPage"/>
///   - "Launch Copilot shortcut…" — opens <see cref="LaunchShortcutPage"/>
/// </summary>
public sealed partial class CopilotLauncherCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public CopilotLauncherCommandsProvider()
    {
        Id = "CopilotLauncher";
        DisplayName = "Copilot CLI Launcher";
        Icon = new IconInfo("ms-appx:///Assets/StoreLogo.png");

        var settings = new SettingsService();
        var sessions = new SessionDiscoveryService();
        var terminals = new TerminalDiscoveryService();
        var shortcuts = new ShortcutsService();
        var launch = new LaunchService();

        _commands = new ICommandItem[]
        {
            new CommandItem(new ResumeSessionPage(sessions, launch, terminals, settings))
            {
                Title = "Resume Copilot session…",
                Subtitle = "List your ~/.copilot/session-state/* sessions and resume one in your default terminal",
                Icon = new IconInfo("\uE823"),
            },
            new CommandItem(new LaunchShortcutPage(shortcuts, launch, terminals, settings))
            {
                Title = "Launch Copilot shortcut…",
                Subtitle = "Run one of your saved shortcuts (Sessions tab → Save as shortcut…)",
                Icon = new IconInfo("\uE734"),
            },
        };
    }

    public override ICommandItem[] TopLevelCommands() => _commands;
}

