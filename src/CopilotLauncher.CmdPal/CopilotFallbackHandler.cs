using System;
using System.Linq;
using CopilotLauncher.CmdPal.Commands;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal;

/// <summary>
/// Provider-level fallback result that lets users type <c>copilot …</c> and
/// jump straight into the best matching session without opening the Resume page
/// first.
/// </summary>
public sealed partial class CopilotFallbackHandler : FallbackCommandItem
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;
    private string _displayTitle = string.Empty;

    public CopilotFallbackHandler(
        ISessionDiscoveryService discovery,
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings)
        : base(string.Empty, "resume-session-fallback")
    {
        _discovery = discovery;
        _launch = launch;
        _terminals = terminals;
        _settings = settings;

        // App's pixel-art mascot — distinctive vs the generic Document glyph
        // (\uE7C3) we used to use which made fallback results look like
        // plain file shortcuts.
        Icon = new IconInfo("ms-appx:///Assets/StoreLogo.png");
        Title = string.Empty;
        Subtitle = string.Empty;
    }

    public override string DisplayTitle => _displayTitle;

    public override void UpdateQuery(string query)
    {
        try
        {
            var match = FindBestMatch(query);
            if (match is null)
            {
                Command = null;
                Title = string.Empty;
                Subtitle = string.Empty;
                SetDisplayTitle(string.Empty);
                return;
            }

            var title = GetSessionTitle(match);
            Command = new ResumeSessionCommand(_launch, _terminals, _settings, match);
            Title = title;
            Subtitle = string.IsNullOrWhiteSpace(match.Cwd) ? match.Id : match.Cwd!;
            SetDisplayTitle($"Resume {title}");
        }
        catch
        {
            Command = null;
            Title = string.Empty;
            Subtitle = string.Empty;
            SetDisplayTitle(string.Empty);
        }
    }

    private CopilotSession? FindBestMatch(string query)
    {
        var normalized = NormalizeQuery(query);
        return _discovery.Enumerate()
            .Select(session => new { Session = session, Rank = GetMatchRank(session, normalized) })
            .Where(match => match.Rank is not null)
            .OrderBy(match => match.Rank)
            .ThenByDescending(match => match.Session.LastModified)
            .Select(match => match.Session)
            .FirstOrDefault();
    }

    private void SetDisplayTitle(string value)
    {
        if (string.Equals(_displayTitle, value, StringComparison.Ordinal))
        {
            return;
        }

        _displayTitle = value;
        OnPropertyChanged(nameof(DisplayTitle));
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

    private static string NormalizeQuery(string query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith("copilot", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var remainder = trimmed["copilot".Length..];
        return remainder.Length == 0 || char.IsWhiteSpace(remainder[0])
            ? remainder.Trim()
            : trimmed;
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string GetSessionTitle(CopilotSession session) =>
        !string.IsNullOrWhiteSpace(session.Name)
            ? session.Name!
            : (session.Id.Length > 8 ? session.Id[..8] : session.Id);
}
