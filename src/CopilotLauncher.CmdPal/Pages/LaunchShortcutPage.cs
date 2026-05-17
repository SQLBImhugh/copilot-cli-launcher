using System;
using System.Linq;
using CopilotLauncher.CmdPal.Commands;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
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
            return _shortcuts.All.Select(shortcut =>
                new ListItem(new LaunchShortcutCommand(_launch, _terminals, _settings, shortcut))
                {
                    Title = shortcut.Label,
                    Subtitle = shortcut.WorkingDirectory,
                    // Match ResumeSessionPage — same pixel-art mascot on
                    // every list item the extension produces so they all
                    // read consistently in the palette.
                    Icon = new IconInfo("ms-appx:///Assets/StoreLogo.png"),
                    MoreCommands = new IContextItem[]
                    {
                        new CommandContextItem(new OpenInExplorerCommand(shortcut.WorkingDirectory))
                        {
                            Title = "Open in Explorer",
                        },
                        new CommandContextItem(new CopyTextCommand(BuildLaunchCommand(shortcut)))
                        {
                            Title = "Copy launch command",
                        },
                    },
                }).ToArray<IListItem>();
        }
        catch
        {
            return Array.Empty<IListItem>();
        }
    }

    private static string BuildLaunchCommand(Shortcut shortcut)
    {
        // AI summary is launcher-side behavior, so the copied command only
        // includes real copilot CLI flags that can be pasted into a shell.
        var args = new List<string?> { "copilot" };
        if (shortcut.EnableAllowAll) args.Add("--allow-all");
        if (!string.IsNullOrWhiteSpace(shortcut.ResumeTarget)) args.Add($"--resume={shortcut.ResumeTarget}");
        if (!string.IsNullOrWhiteSpace(shortcut.ExtraCopilotArgs))
        {
            args.AddRange(ArgQuoter.Split(shortcut.ExtraCopilotArgs));
        }

        return ArgQuoter.Format(args);
    }
}

