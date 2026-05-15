using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// ViewModel for SessionsPage. Discovers sessions, exposes filter chips +
/// search, and surfaces commands to Resume / Reveal / Repair. Lives in Core
/// so its filtering logic is unit-testable.
/// </summary>
public sealed class SessionsViewModel : INotifyPropertyChanged
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ILaunchService _launch;
    private readonly ISettingsService _settings;

    public ObservableCollection<SessionRow> Visible { get; } = new();

    public int TotalCount { get; private set; }
    public int VisibleCount => Visible.Count;

    private bool _showRecent = true;
    private bool _showNamed = true;
    private bool _showHeavy = true;
    private bool _showAll = false;
    private string _searchText = string.Empty;
    private string _statusMessage = "Loading sessions…";

    public bool ShowRecent
    {
        get => _showRecent;
        set { if (_showRecent != value) { _showRecent = value; OnPropertyChanged(); ApplyFilters(); } }
    }

    public bool ShowNamed
    {
        get => _showNamed;
        set { if (_showNamed != value) { _showNamed = value; OnPropertyChanged(); ApplyFilters(); } }
    }

    public bool ShowHeavy
    {
        get => _showHeavy;
        set { if (_showHeavy != value) { _showHeavy = value; OnPropertyChanged(); ApplyFilters(); } }
    }

    public bool ShowAll
    {
        get => _showAll;
        set { if (_showAll != value) { _showAll = value; OnPropertyChanged(); ApplyFilters(); } }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (_searchText != value) { _searchText = value ?? string.Empty; OnPropertyChanged(); ApplyFilters(); } }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    private List<CopilotSession> _all = new();

    public SessionsViewModel(
        ISessionDiscoveryService discovery,
        ITerminalDiscoveryService terminals,
        ILaunchService launch,
        ISettingsService settings)
    {
        _discovery = discovery;
        _terminals = terminals;
        _launch = launch;
        _settings = settings;
    }

    /// <summary>Re-scan disk + apply current filters. Safe to call repeatedly.</summary>
    public void Refresh()
    {
        try
        {
            _all = _discovery.Enumerate().ToList();
            TotalCount = _all.Count;
            ApplyFilters();
            OnPropertyChanged(nameof(TotalCount));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to scan sessions: {ex.Message}";
        }
    }

    /// <summary>
    /// Spawn a terminal session for this row. Picks the default terminal from
    /// settings (or auto-pick from discovered terminals).
    /// Returns true on success; false (and updates StatusMessage) on failure.
    /// </summary>
    public bool ResumeSession(SessionRow row)
    {
        try
        {
            var terminal = ResolveDefaultTerminal();
            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = string.IsNullOrEmpty(row.Cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : row.Cwd,
                ResumeTarget = row.SessionId,
                Terminal = terminal,
            });
            StatusMessage = $"Resumed {row.ShortId}… in {terminal?.DisplayName ?? "direct"}.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Resume failed: {ex.Message}";
            return false;
        }
    }

    private TerminalProfile? ResolveDefaultTerminal()
    {
        var discovered = _terminals.Discovered;
        if (discovered.Count == 0) return null;

        var pref = _settings.Current.Terminal.DefaultTerminal;
        if (!string.IsNullOrEmpty(pref) && pref != "auto")
        {
            var match = discovered.FirstOrDefault(t => t.Id == pref);
            if (match is not null) return match;
        }
        // Auto-pick: prefer wt > pwsh > powershell > cmd.
        return discovered.OrderBy(t => t.Id switch
        {
            "wt" => 0,
            "pwsh" => 1,
            "powershell" => 2,
            "cmd" => 3,
            _ => 4
        }).First();
    }

    /// <summary>Applies the current filter chips + search to the full list.
    /// Filter logic is AND across checked chips: a session must satisfy every
    /// chip the user has checked (Recent + Named + Heavy intersect, not union).
    /// "Show all" is a single override that bypasses the other chips entirely.
    /// </summary>
    public void ApplyFilters()
    {
        Visible.Clear();
        if (_all.Count == 0)
        {
            StatusMessage = "No sessions found in ~/.copilot/session-state. Start a Copilot CLI session to populate this list.";
            OnPropertyChanged(nameof(VisibleCount));
            return;
        }

        IEnumerable<CopilotSession> rows;
        if (_showAll)
        {
            rows = _all;
        }
        else if (!_showRecent && !_showNamed && !_showHeavy)
        {
            // No chips checked + Show all off: user explicitly hid everything.
            rows = Array.Empty<CopilotSession>();
            StatusMessage = "No filters checked. Toggle a chip above (Recent / Named / Heavily used) or check Show all.";
            OnPropertyChanged(nameof(VisibleCount));
            return;
        }
        else
        {
            rows = _all.Where(MatchesAllCheckedChips);
        }

        if (!string.IsNullOrWhiteSpace(_searchText))
            rows = rows.Where(s => MatchesSearch(s, _searchText));

        rows = rows.OrderByDescending(s => s.LastModified);

        foreach (var s in rows)
            Visible.Add(SessionRow.From(s));

        StatusMessage = $"{Visible.Count} of {_all.Count} session(s) visible.";
        OnPropertyChanged(nameof(VisibleCount));
    }

    private bool MatchesAllCheckedChips(CopilotSession s)
    {
        var settings = _settings.Current.SessionListing;
        var recentCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, settings.RecentWindowDays));
        if (_showRecent && s.LastModified.ToUniversalTime() < recentCutoff) return false;
        if (_showNamed  && !s.UserNamed) return false;
        if (_showHeavy  && s.SummaryCount < settings.HeavyUseSummaryThreshold) return false;
        return true;
    }

    private static bool MatchesSearch(CopilotSession s, string q)
    {
        return Contains(s.Cwd, q)
            || Contains(s.Repository, q)
            || Contains(s.Branch, q)
            || Contains(s.Id, q);
        static bool Contains(string? haystack, string needle) =>
            haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Display projection of a CopilotSession for the Sessions page card.
/// Pre-computes display strings so XAML bindings stay simple.
/// </summary>
public sealed class SessionRow
{
    public required string SessionId { get; init; }
    public required string ShortId { get; init; }

    /// <summary>Bold title at the top of the card. Falls back gracefully when the
    /// session has no user-given name: cwd-leaf or short id.</summary>
    public required string Title { get; init; }

    /// <summary>Full working directory path; subtitle under the title.</summary>
    public required string Cwd { get; init; }

    public required string RepoBranch { get; init; }
    public required string LastOpenedDisplay { get; init; }
    public required string Tags { get; init; }
    public bool UserNamed { get; init; }
    public bool HeavyUse { get; init; }

    public static SessionRow From(CopilotSession s)
    {
        var ago = HumanizeRelative(s.LastModified);
        var tags = new List<string>();
        if (s.SummaryCount >= 20) tags.Add($"heavy: {s.SummaryCount} summaries");
        else if (s.SummaryCount > 0) tags.Add($"{s.SummaryCount} summaries");
        if (s.SizeBytes > 0) tags.Add(FormatBytes(s.SizeBytes));

        // Title preference: user-given name > cwd leaf > short id.
        string title;
        if (s.UserNamed && !string.IsNullOrWhiteSpace(s.Name))
        {
            title = s.Name!;
        }
        else if (!string.IsNullOrWhiteSpace(s.Cwd))
        {
            try { title = Path.GetFileName(s.Cwd!.TrimEnd('\\', '/')) ?? s.Cwd!; }
            catch { title = s.Cwd!; }
            if (string.IsNullOrWhiteSpace(title)) title = s.Cwd!;
        }
        else
        {
            title = s.Id.Length >= 8 ? s.Id[..8] + "…" : s.Id;
        }

        return new SessionRow
        {
            SessionId = s.Id,
            ShortId = s.Id.Length >= 8 ? s.Id[..8] : s.Id,
            Title = title,
            Cwd = string.IsNullOrEmpty(s.Cwd) ? "(unknown working dir)" : s.Cwd,
            RepoBranch = string.IsNullOrEmpty(s.Repository)
                ? "(no git repo)"
                : (string.IsNullOrEmpty(s.Branch) ? s.Repository : $"{s.Repository} · {s.Branch}"),
            LastOpenedDisplay = $"{ago} · id {(s.Id.Length >= 8 ? s.Id[..8] : s.Id)}…",
            Tags = string.Join(" · ", tags),
            UserNamed = s.UserNamed,
            HeavyUse = s.SummaryCount >= 20,
        };
    }

    private static string HumanizeRelative(DateTime when)
    {
        var span = DateTime.UtcNow - when.ToUniversalTime();
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7)   return $"{(int)span.TotalDays}d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }
}
