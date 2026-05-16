using System;
using System.Linq;
using CopilotLauncher.CmdPal.Commands;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal.Pages;

/// <summary>
/// Lists every Copilot CLI session under <c>~/.copilot/session-state/</c>.
/// Selecting an item runs <see cref="ResumeSessionCommand"/> which spawns
/// `copilot --resume=&lt;id&gt;` in the user's preferred terminal.
/// </summary>
public sealed partial class ResumeSessionPage : ListPage
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;

    public ResumeSessionPage(
        ISessionDiscoveryService discovery,
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings)
    {
        _discovery = discovery;
        _launch = launch;
        _terminals = terminals;
        _settings = settings;

        Name = "Resume";
        Title = "Resume a Copilot CLI session";
        PlaceholderText = "Filter by name, repo, branch, or path…";
        Icon = new IconInfo("\uE823"); // Segoe Fluent History
    }

    public override IListItem[] GetItems()
    {
        try
        {
            var sessions = _discovery.Enumerate()
                .OrderByDescending(s => s.LastModified)
                .Take(100)
                .ToList();

            return sessions.Select(s =>
            {
                var name = !string.IsNullOrWhiteSpace(s.Name)
                    ? s.Name!
                    : (s.Id.Length > 8 ? s.Id[..8] : s.Id);
                var sub = string.IsNullOrEmpty(s.Cwd) ? s.Id : s.Cwd;

                return new ListItem(new ResumeSessionCommand(_launch, _terminals, _settings, s))
                {
                    Title = name,
                    Subtitle = sub!,
                    Section = string.IsNullOrWhiteSpace(s.Name) ? "Unnamed" : "Named",
                    Icon = new IconInfo("\uE7C3"),
                };
            }).ToArray<IListItem>();
        }
        catch
        {
            return Array.Empty<IListItem>();
        }
    }
}

