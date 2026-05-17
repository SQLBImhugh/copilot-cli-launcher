using System;
using CopilotLauncher.CmdPal.Pages;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal;

/// <summary>
/// Top-level command provider. Exposes the session + shortcut pages, a live
/// fallback result for "copilot …" queries, and an in-palette settings page
/// for resume defaults.
/// </summary>
public sealed partial class CopilotLauncherCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly IFallbackCommandItem[] _fallbackCommands;

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

        _commands =
        [
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
        ];

        _fallbackCommands =
        [
            new CopilotFallbackHandler(sessions, launch, terminals, settings),
        ];

        Settings = new ProviderSettings(new CopilotSettingsPage(settings));
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override IFallbackCommandItem[] FallbackCommands() => _fallbackCommands;

    /// <summary>Minimal settings wrapper when the toolkit helper isn't present.</summary>
    private sealed partial class ProviderSettings : ICommandSettings
    {
        public ProviderSettings(IContentPage settingsPage)
        {
            SettingsPage = settingsPage;
        }

        public IContentPage SettingsPage { get; }
    }
}

