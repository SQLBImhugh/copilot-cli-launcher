using System;
using System.Linq;
using CopilotLauncher.CmdPal.Commands;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal.Pages;

/// <summary>
/// Lists every Copilot CLI session under <c>~/.copilot/session-state/</c>.
/// Selecting an item runs <see cref="ResumeSessionCommand"/> which spawns
/// `copilot --resume=&lt;id&gt;` in the user's preferred terminal.
/// </summary>
public sealed partial class ResumeSessionPage : DynamicListPage
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

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!string.Equals(oldSearch, newSearch, StringComparison.Ordinal))
        {
            RaiseItemsChanged();
        }
    }

    public override IListItem[] GetItems()
    {
        try
        {
            return FilterSessions(SearchText)
                .Select(BuildItem)
                .ToArray<IListItem>();
        }
        catch
        {
            return Array.Empty<IListItem>();
        }
    }

    private IEnumerable<CopilotSession> FilterSessions(string? searchText)
    {
        var query = searchText?.Trim() ?? string.Empty;
        return _discovery.Enumerate()
            .Select(session => new { Session = session, Rank = GetMatchRank(session, query) })
            .Where(match => match.Rank is not null)
            .OrderBy(match => match.Rank)
            .ThenByDescending(match => match.Session.LastModified)
            .Take(50)
            .Select(match => match.Session);
    }

    private ListItem BuildItem(CopilotSession session)
    {
        return new ListItem(new ResumeSessionCommand(_launch, _terminals, _settings, session))
        {
            Title = GetSessionTitle(session),
            Subtitle = string.IsNullOrWhiteSpace(session.Cwd) ? session.Id : session.Cwd!,
            Section = string.IsNullOrWhiteSpace(session.Name) ? "Unnamed" : "Named",
            // App's pixel-art mascot — distinctive vs the generic Segoe Fluent
            // Document glyph (\uE7C3) we used to use, which looked like a
            // plain "file shortcut" and confused users about what these
            // entries represent.
            Icon = new IconInfo("ms-appx:///Assets/StoreLogo.png"),
            MoreCommands = new IContextItem[]
            {
                new CommandContextItem(new OpenInExplorerCommand(session.Cwd))
                {
                    Title = "Open in Explorer",
                },
                new CommandContextItem(new CopyTextCommand(session.Id))
                {
                    Title = "Copy session id",
                },
                new CommandContextItem(new CopyTextCommand($"copilot --resume={session.Id}"))
                {
                    Title = "Copy resume command",
                },
            },
        };
    }

    private static int? GetMatchRank(CopilotSession session, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        if (Contains(session.Name, query)) return 0;
        if (Contains(session.Cwd, query)) return 1;
        if (Contains(session.Repository, query)) return 2;
        if (Contains(session.Branch, query)) return 3;
        if (Contains(session.Id, query)) return 4;
        return null;
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string GetSessionTitle(CopilotSession session) =>
        !string.IsNullOrWhiteSpace(session.Name)
            ? session.Name!
            : (session.Id.Length > 8 ? session.Id[..8] : session.Id);
}

