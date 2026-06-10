using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// ViewModel for SessionsPage. Discovers sessions, exposes filter chips +
/// search, and surfaces commands to Resume / Reveal / Repair. Lives in Core
/// so its filtering logic is unit-testable.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ILaunchService _launch;
    private readonly ISettingsService _settings;
    private readonly IAfterLaunchAction _afterLaunch;
    private Func<Action, Task>? _marshalToUi;

    public ObservableCollection<SessionRow> Visible { get; } = new();

    private int _totalCount;
    public int TotalCount { get => _totalCount; private set => _totalCount = value; }

    public int VisibleCount => Visible.Count;

    public int RecentWindowDays => Math.Max(1, _settings.Current.SessionListing.RecentWindowDays);

    public string RecentFilterLabel => $"Recent ({RecentWindowDays}d)";

    public string RecentFilterTooltip => $"Modified in the last {RecentWindowDays} days";

    [ObservableProperty]
    private bool _showRecent = true;

    [ObservableProperty]
    private bool _showNamed = true;

    [ObservableProperty]
    private bool _showHeavy = true;

    [ObservableProperty]
    private bool _showAll;

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText != value) { _searchText = value ?? string.Empty; OnPropertyChanged(); ApplyFilters(); } }
    }

    [ObservableProperty]
    private string _statusMessage = "Loading sessions…";

    [ObservableProperty]
    private SessionSortField _sortField = SessionSortField.LastModified;

    [ObservableProperty]
    private bool _sortDescending = true;

    private List<CopilotSession> _all = new();

    public SessionsViewModel(
        ISessionDiscoveryService discovery,
        ITerminalDiscoveryService terminals,
        ILaunchService launch,
        ISettingsService settings,
        IAfterLaunchAction? afterLaunch = null,
        Func<Action, Task>? marshalToUi = null)
    {
        _discovery = discovery;
        _terminals = terminals;
        _launch = launch;
        _settings = settings;
        _afterLaunch = afterLaunch ?? new NoopAfterLaunchAction();
        _marshalToUi = marshalToUi ?? (SynchronizationContext.Current is not null ? CreateUiMarshaller(SynchronizationContext.Current) : null);
    }

    partial void OnShowRecentChanged(bool value) => ApplyFilters();

    partial void OnShowNamedChanged(bool value) => ApplyFilters();

    partial void OnShowHeavyChanged(bool value) => ApplyFilters();

    partial void OnShowAllChanged(bool value) => ApplyFilters();

    partial void OnSortFieldChanged(SessionSortField value) => ApplyFilters();

    partial void OnSortDescendingChanged(bool value) => ApplyFilters();

    /// <summary>Re-scan disk + apply current filters. Safe to call repeatedly.</summary>
    [Obsolete("Use RefreshAsync instead. This method now schedules a background refresh and returns immediately.")]
    public void Refresh() => _ = RefreshAsync();

    /// <summary>Re-scan disk off the UI thread and then apply the current filters.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var marshalToUi = GetUiMarshaller();

        try
        {
            await marshalToUi(() => StatusMessage = "Loading sessions…").ConfigureAwait(false);
            var discovered = await Task.Run(() => _discovery.Enumerate().ToList(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            await marshalToUi(() =>
            {
                _all = discovered;
                TotalCount = _all.Count;
                ApplyFilters();
                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(RecentWindowDays));
                OnPropertyChanged(nameof(RecentFilterLabel));
                OnPropertyChanged(nameof(RecentFilterTooltip));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await marshalToUi(() => StatusMessage = "Session refresh canceled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await marshalToUi(() => StatusMessage = $"Failed to scan sessions: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Spawn a terminal session for this row. Picks the default terminal from
    /// settings (or auto-pick from discovered terminals). Also applies the
    /// user's "Sessions Resume defaults" (AI summary, --allow-all, extra args)
    /// from <see cref="ISettingsService"/> so a one-click resume from the
    /// Sessions tab uses the same flags every time.
    /// Returns true on success; false (and updates StatusMessage) on failure.
    /// </summary>
    public bool ResumeSession(SessionRow row)
    {
        try
        {
            var terminal = ResolveDefaultTerminal();
            var resumeDefaults = _settings.Current.SessionsResume;
            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = string.IsNullOrEmpty(row.Cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : row.Cwd,
                ResumeTarget = row.SessionId,
                EnableAllowAll = resumeDefaults.EnableAllowAll,
                ExtraCopilotArgs = resumeDefaults.ExtraCopilotArgs,
                Terminal = terminal,
            });
            StatusMessage = $"Resumed {row.ShortId}… in {terminal?.DisplayName ?? "direct"}.";
            _afterLaunch.Apply(_settings.Current.LauncherBehavior.AfterLaunch);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Resume failed: {ex.Message}";
            return false;
        }
    }

    public bool StartNewSessionAt(SessionRow row)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(row.Cwd) || row.Cwd == "(unknown working dir)"
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : row.Cwd;
            var terminal = ResolveDefaultTerminal();
            // Mirror the ▶ Resume button's flags (the adjacent Sessions-tab
            // launch action) so a fresh session opens with the same allow-all /
            // extra-args behavior. Without --allow-all, copilot re-prompts for
            // every extension's elevated-permission request on a new session.
            var resumeDefaults = _settings.Current.SessionsResume;
            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = dir,
                ResumeTarget = null,
                EnableAllowAll = resumeDefaults.EnableAllowAll,
                ExtraCopilotArgs = resumeDefaults.ExtraCopilotArgs,
                Terminal = terminal,
            });
            StatusMessage = $"Started new session in {dir}…";
            _afterLaunch.Apply(_settings.Current.LauncherBehavior.AfterLaunch);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"New session failed: {ex.Message}";
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
        if (ShowAll)
        {
            rows = _all;
        }
        else if (!ShowRecent && !ShowNamed && !ShowHeavy)
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

        rows = ApplySort(rows);

        var showFullId = _settings.Current.SessionListing.ShowFullSessionId;
        foreach (var s in rows)
            Visible.Add(SessionRow.From(s, showFullId));

        StatusMessage = $"{Visible.Count} of {_all.Count} session(s) visible.";
        OnPropertyChanged(nameof(VisibleCount));
    }

    private IEnumerable<CopilotSession> ApplySort(IEnumerable<CopilotSession> rows)
    {
        // Use a single sort key extractor based on the chosen field; flipping
        // direction is just OrderBy vs OrderByDescending.
        return SortField switch
        {
            SessionSortField.LastModified => SortDescending ? rows.OrderByDescending(s => s.LastModified) : rows.OrderBy(s => s.LastModified),
            SessionSortField.Name => SortDescending ? rows.OrderByDescending(s => s.Name ?? s.Cwd ?? s.Id, StringComparer.OrdinalIgnoreCase)
                                                    : rows.OrderBy(s => s.Name ?? s.Cwd ?? s.Id, StringComparer.OrdinalIgnoreCase),
            SessionSortField.Cwd => SortDescending ? rows.OrderByDescending(s => s.Cwd ?? "", StringComparer.OrdinalIgnoreCase)
                                                   : rows.OrderBy(s => s.Cwd ?? "", StringComparer.OrdinalIgnoreCase),
            SessionSortField.Repository => SortDescending ? rows.OrderByDescending(s => s.Repository ?? "", StringComparer.OrdinalIgnoreCase)
                                                          : rows.OrderBy(s => s.Repository ?? "", StringComparer.OrdinalIgnoreCase),
            SessionSortField.SummaryCount => SortDescending ? rows.OrderByDescending(s => s.SummaryCount)
                                                            : rows.OrderBy(s => s.SummaryCount),
            SessionSortField.Size => SortDescending ? rows.OrderByDescending(s => s.SizeBytes)
                                                    : rows.OrderBy(s => s.SizeBytes),
            _ => rows.OrderByDescending(s => s.LastModified),
        };
    }

    private bool MatchesAllCheckedChips(CopilotSession s)
    {
        var settings = _settings.Current.SessionListing;
        var recentCutoff = DateTime.UtcNow.AddDays(-Math.Max(1, settings.RecentWindowDays));
        if (ShowRecent && s.LastModified.ToUniversalTime() < recentCutoff) return false;
        if (ShowNamed && !s.UserNamed) return false;
        if (ShowHeavy && s.SummaryCount < settings.HeavyUseSummaryThreshold) return false;
        return true;
    }

    private Func<Action, Task> GetUiMarshaller()
    {
        _marshalToUi ??= CreateUiMarshaller(SynchronizationContext.Current);
        return _marshalToUi;
    }

    private static Func<Action, Task> CreateUiMarshaller(SynchronizationContext? syncContext)
    {
        if (syncContext is null)
        {
            return action =>
            {
                action();
                return Task.CompletedTask;
            };
        }

        return action =>
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            syncContext.Post(_ =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);
            return tcs.Task;
        };
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
}

/// <summary>Sort field for the Sessions tab.</summary>
public enum SessionSortField
{
    LastModified,
    Name,
    Cwd,
    Repository,
    SummaryCount,
    Size,
}

/// <summary>
/// Display projection of a CopilotSession for the Sessions page card.
/// Pre-computes display strings so XAML bindings stay simple.
/// </summary>
public sealed class SessionRow
{
    public required string SessionId { get; init; }
    public required string ShortId { get; init; }

    /// <summary>
    /// Display title:
    /// - User-given name (when <see cref="UserNamed"/> = true), shown bold
    /// - Copilot's auto-generated name from the first prompt, shown regular weight
    /// - Empty when no name field at all (very rare; brand-new session)
    /// Long auto names are truncated for display.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>True when <see cref="Title"/> is the user's deliberate --name / /rename. Bold.</summary>
    public bool HasUserName { get; init; }

    /// <summary>True when <see cref="Title"/> is Copilot's auto-summary (not user-named). Regular weight + caption.</summary>
    public bool HasAutoName { get; init; }

    /// <summary>Caption line under an auto-name explaining where it came from.</summary>
    public string AutoNameDescription { get; init; } = "Auto-summary by Copilot from the first prompt — not an official name";

    /// <summary>Full working directory path; subtitle under the title.</summary>
    public required string Cwd { get; init; }

    /// <summary>Opacity for the Cwd TextBlock — full when no name shows above it,
    /// dimmed when something occupies the title slot.</summary>
    public double CwdOpacity => (HasUserName || HasAutoName) ? 0.7 : 1.0;

    public required string RepoBranch { get; init; }
    public required string LastOpenedDisplay { get; init; }
    public required string Tags { get; init; }
    public bool UserNamed { get; init; }
    public bool HeavyUse { get; init; }

    public static SessionRow From(CopilotSession s, bool showFullId = false)
    {
        var ago = HumanizeRelative(s.LastModified);
        var tags = new List<string>();
        if (s.SummaryCount >= 10) tags.Add($"heavy: {s.SummaryCount} summaries");
        else if (s.SummaryCount > 0) tags.Add($"{s.SummaryCount} summaries");
        if (s.SizeBytes > 0) tags.Add(FormatBytes(s.SizeBytes));

        // Title resolution:
        //   user_named=true,  name set  -> bold (HasUserName)
        //   user_named=false, name set  -> regular weight (HasAutoName) — Copilot
        //                                  auto-generates the name field after the
        //                                  first user prompt; treat as soft hint.
        //   no name at all              -> empty title; cwd takes visual prominence.
        var rawName = s.Name;
        var hasAnyName = !string.IsNullOrWhiteSpace(rawName);
        var hasUserName = s.UserNamed && hasAnyName;
        var hasAutoName = !s.UserNamed && hasAnyName;
        var title = hasAnyName ? CleanForDisplay(rawName!, maxLen: hasAutoName ? 90 : 120) : string.Empty;

        var shortId = s.Id.Length >= 8 ? s.Id[..8] : s.Id;
        // When the "Show full session id" setting is on, surface the entire id
        // (no ellipsis) so it can be read / copied in full; otherwise keep the
        // compact 8-char prefix.
        var idDisplay = showFullId ? s.Id : $"{shortId}…";

        return new SessionRow
        {
            SessionId = s.Id,
            ShortId = shortId,
            Title = title,
            HasUserName = hasUserName,
            HasAutoName = hasAutoName,
            Cwd = string.IsNullOrEmpty(s.Cwd) ? "(unknown working dir)" : s.Cwd,
            RepoBranch = string.IsNullOrEmpty(s.Repository)
                ? "(no git repo)"
                : (string.IsNullOrEmpty(s.Branch) ? s.Repository : $"{s.Repository} · {s.Branch}"),
            LastOpenedDisplay = $"{ago} · id {idDisplay}",
            Tags = string.Join(" · ", tags),
            UserNamed = s.UserNamed,
            HeavyUse = s.SummaryCount >= 10,
        };
    }

    private static string HumanizeRelative(DateTime when)
    {
        var span = DateTime.UtcNow - when.ToUniversalTime();
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Sanitize a title for single-line display: collapse newlines/tabs to
    /// spaces, trim trailing whitespace, truncate to <paramref name="maxLen"/>
    /// with an ellipsis. Auto-generated names from Copilot can be entire
    /// prompt prefixes hundreds of characters long.
    /// </summary>
    private static string CleanForDisplay(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var cleaned = s
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("\t", " ")
            .Trim();
        // Collapse runs of spaces.
        while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
        if (cleaned.Length > maxLen)
            cleaned = cleaned[..maxLen].TrimEnd() + "…";
        return cleaned;
    }
}
