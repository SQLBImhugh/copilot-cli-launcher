using System;
using System.Linq;
using CopilotLauncher.CmdPal.Commands;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal.Pages;

/// <summary>Lists the user's saved Shortcuts (Label + WorkingDirectory).
/// Selecting an item runs <see cref="LaunchShortcutCommand"/> which respects
/// the shortcut's per-shortcut config.</summary>
public sealed partial class LaunchShortcutPage : ListPage
{
    private readonly IShortcutsService _shortcuts;
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;

    public LaunchShortcutPage(
        IShortcutsService shortcuts,
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings)
    {
        _shortcuts = shortcuts;
        _launch = launch;
        _terminals = terminals;
        _settings = settings;

        Name = "Launch";
        Title = "Launch a saved shortcut";
        PlaceholderText = "Filter shortcuts…";
        Icon = new IconInfo("\uE734");
    }

    public override IListItem[] GetItems()
    {
        try
        {
            _shortcuts.Reload();
            return _shortcuts.All.Select(s =>
                new ListItem(new LaunchShortcutCommand(_launch, _terminals, _settings, s))
                {
                    Title = s.Label,
                    Subtitle = s.WorkingDirectory,
                    Icon = new IconInfo("\uE734"),
                }).ToArray<IListItem>();
        }
        catch
        {
            return Array.Empty<IListItem>();
        }
    }
}

