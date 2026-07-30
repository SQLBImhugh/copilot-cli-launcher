using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>How a session was classified for cleanup purposes.</summary>
public enum SessionCleanupKind
{
    /// <summary>Real work — never auto-selected.</summary>
    Normal = 0,

    /// <summary>Started but never used: no conversation on disk.</summary>
    Empty = 1,

    /// <summary>A smoke test or connectivity probe ("reply with exactly: OK", "hi", "--help").</summary>
    Probe = 2,

    /// <summary>Its first prompt is identical to another session's — a repeated/automated run.</summary>
    Duplicate = 3,

    /// <summary>Ran from a temp / scratch directory.</summary>
    Scratch = 4,
}

/// <summary>One selectable row on the Cleanup page.</summary>
public sealed partial class CleanupRow : ObservableObject
{
    public required string SessionId { get; init; }
    public required string ShortId { get; init; }
    public required string Title { get; init; }
    public required string Cwd { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime LastModified { get; init; }
    public required int Summaries { get; init; }
    public required bool IsLocked { get; init; }
    public required bool UserNamed { get; init; }
    public required SessionCleanupKind Kind { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Locked sessions can never be deleted; the CLI is still using them.</summary>
    public bool CanDelete => !IsLocked;

    public string SizeDisplay => FormatBytes(SizeBytes);
    public string LastUsedDate => RelativeTime.ToLocalDate(LastModified);
    public string LastUsedRelative => RelativeTime.Humanize(LastModified);

    public string KindLabel => Kind switch
    {
        SessionCleanupKind.Empty => "empty",
        SessionCleanupKind.Probe => "test probe",
        SessionCleanupKind.Duplicate => "repeated prompt",
        SessionCleanupKind.Scratch => "scratch dir",
        _ => string.Empty,
    };

    public string Detail
    {
        get
        {
            var bits = new List<string>();
            if (IsLocked) bits.Add("in use — cannot delete");
            if (UserNamed) bits.Add("you named this");
            if (Summaries > 0) bits.Add(Summaries == 1 ? "1 summary" : $"{Summaries} summaries");
            if (!string.IsNullOrEmpty(KindLabel)) bits.Add(KindLabel);
            return string.Join(" · ", bits);
        }
    }

    public double RowOpacity => CanDelete ? 1.0 : 0.5;

    public string AccessibleName => $"{Title}, {Cwd}, {SizeDisplay}, last used {LastUsedDate}, {Detail}";

    /// <summary>Human-readable byte size. Public so hosting pages can format the
    /// selection total in confirmation dialogs.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// ViewModel for the Cleanup page: classify every discovered session, let the
/// user multi-select (with preset filters for the obvious junk), and bulk delete.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly ISessionDeletionService _deletion;
    private Func<Action, Task>? _marshalToUi;

    private List<CleanupRow> _all = new();

    public ObservableCollection<CleanupRow> Visible { get; } = new();

    [ObservableProperty] private string _statusMessage = "Loading sessions…";
    [ObservableProperty] private bool _isBusy;

    // Filter chips.
    [ObservableProperty] private bool _showEmpty = true;
    [ObservableProperty] private bool _showProbes = true;
    [ObservableProperty] private bool _showDuplicates = true;
    [ObservableProperty] private bool _showScratch = true;
    [ObservableProperty] private bool _showNormal;

    /// <summary>Hide sessions you named or that have summaries, even when their kind matches.</summary>
    [ObservableProperty] private bool _protectMyWork = true;

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText != value) { _searchText = value ?? string.Empty; OnPropertyChanged(); ApplyFilters(); } }
    }

    partial void OnShowEmptyChanged(bool value) => ApplyFilters();
    partial void OnShowProbesChanged(bool value) => ApplyFilters();
    partial void OnShowDuplicatesChanged(bool value) => ApplyFilters();
    partial void OnShowScratchChanged(bool value) => ApplyFilters();
    partial void OnShowNormalChanged(bool value) => ApplyFilters();
    partial void OnProtectMyWorkChanged(bool value) => ApplyFilters();

    public int SelectedCount => Visible.Count(r => r.IsSelected && r.CanDelete);

    public long SelectedBytes => Visible.Where(r => r.IsSelected && r.CanDelete).Sum(r => r.SizeBytes);

    public string SelectionSummary => SelectedCount == 0
        ? "Nothing selected."
        : $"{SelectedCount} selected · {CleanupRow.FormatBytes(SelectedBytes)}";

    public bool HasSelection => SelectedCount > 0;

    public CleanupViewModel(
        ISessionDiscoveryService discovery,
        ISessionDeletionService deletion,
        Func<Action, Task>? marshalToUi = null)
    {
        _discovery = discovery;
        _deletion = deletion;
        _marshalToUi = marshalToUi;
    }

    /// <summary>Rescan sessions off the UI thread and reclassify.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var marshal = GetUiMarshaller();
        try
        {
            await marshal(() => { IsBusy = true; StatusMessage = "Scanning sessions…"; }).ConfigureAwait(false);

            var sessions = await Task.Run(() => _discovery.Enumerate().ToList(), ct).ConfigureAwait(false);
            var rows = await Task.Run(() => Classify(sessions), ct).ConfigureAwait(false);

            await marshal(() =>
            {
                foreach (var r in _all) r.PropertyChanged -= OnRowChanged;
                _all = rows;
                foreach (var r in _all) r.PropertyChanged += OnRowChanged;
                ApplyFilters();
                IsBusy = false;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await marshal(() => { IsBusy = false; StatusMessage = "Scan canceled."; }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await marshal(() => { IsBusy = false; StatusMessage = $"Scan failed: {ex.Message}"; }).ConfigureAwait(false);
        }
    }

    /// <summary>Prompts that only ever show up in smoke tests / connectivity probes.</summary>
    private static readonly Regex[] ProbePatterns =
    {
        new(@"^\s*(reply|answer|respond)\s+with\s+(exactly|the\s+single\s+word)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*(hi|hello|hey|yo|sup|test|testing|ping|ok)\s*[.!?]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*--?\w[\w-]*\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*/[a-z-]+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*what\s+is\s+\d+\s*\+\s*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*run\s+your\s+probe\.?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    internal static List<CleanupRow> Classify(IReadOnlyList<CopilotSession> sessions)
    {
        // A first prompt seen more than once means a repeated / automated run.
        var promptCounts = sessions
            .Select(s => Normalize(s.Name))
            .Where(n => n.Length > 0)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var rows = new List<CleanupRow>(sessions.Count);
        foreach (var s in sessions)
        {
            var name = Normalize(s.Name);
            var kind = SessionCleanupKind.Normal;

            if (IsEmptyOnDisk(s)) kind = SessionCleanupKind.Empty;
            else if (name.Length > 0 && ProbePatterns.Any(p => p.IsMatch(name))) kind = SessionCleanupKind.Probe;
            else if (IsScratch(s.Cwd)) kind = SessionCleanupKind.Scratch;
            else if (name.Length > 0 && promptCounts.TryGetValue(name, out var c) && c > 1) kind = SessionCleanupKind.Duplicate;

            var shortId = s.Id.Length >= 8 ? s.Id[..8] : s.Id;
            var title = name.Length > 0
                ? (name.Length > 100 ? name[..97] + "…" : name)
                : "(no name)";

            rows.Add(new CleanupRow
            {
                SessionId = s.Id,
                ShortId = shortId,
                Title = title,
                Cwd = string.IsNullOrWhiteSpace(s.Cwd) ? "(unknown working dir)" : s.Cwd,
                SizeBytes = s.SizeBytes,
                LastModified = s.LastModified,
                Summaries = s.SummaryCount,
                IsLocked = s.IsLocked,
                UserNamed = s.UserNamed,
                Kind = kind,
            });
        }
        return rows;
    }

    /// <summary>True when the session folder holds no real conversation.</summary>
    private static bool IsEmptyOnDisk(CopilotSession s)
    {
        try
        {
            var events = Path.Combine(s.FolderPath, "events.jsonl");
            if (!File.Exists(events)) return true;
            return new FileInfo(events).Length < 2048;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsScratch(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        var c = cwd.Replace('/', '\\');
        return c.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase)
            || c.Contains(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase) && c.EndsWith("Temp", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : Regex.Replace(name, @"\s+", " ").Trim();

    public void ApplyFilters()
    {
        foreach (var r in Visible) { /* keep handlers; rows are shared with _all */ }
        Visible.Clear();

        IEnumerable<CleanupRow> rows = _all.Where(r =>
            r.Kind switch
            {
                SessionCleanupKind.Empty => ShowEmpty,
                SessionCleanupKind.Probe => ShowProbes,
                SessionCleanupKind.Duplicate => ShowDuplicates,
                SessionCleanupKind.Scratch => ShowScratch,
                _ => ShowNormal,
            });

        if (ProtectMyWork)
            rows = rows.Where(r => !r.UserNamed && r.Summaries < 5);

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var q = _searchText.Trim();
            rows = rows.Where(r =>
                r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Cwd.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.SessionId.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var r in rows.OrderByDescending(r => r.LastModified))
            Visible.Add(r);

        RaiseSelectionChanged();
        var totalBytes = Visible.Sum(r => r.SizeBytes);
        StatusMessage = $"{Visible.Count} of {_all.Count} session(s) shown · {CleanupRow.FormatBytes(totalBytes)}";
    }

    public void SetAllSelections(bool selected)
    {
        foreach (var r in Visible)
            if (r.CanDelete) r.IsSelected = selected;
    }

    /// <summary>Delete every selected, deletable row. Returns the aggregate result.</summary>
    public BulkDeleteResult DeleteSelected()
    {
        var chosen = Visible.Where(r => r.IsSelected && r.CanDelete).ToList();
        if (chosen.Count == 0)
        {
            StatusMessage = "Nothing selected.";
            return new BulkDeleteResult { Results = Array.Empty<SessionDeleteResult>() };
        }

        var result = _deletion.DeleteMany(chosen.Select(r => r.SessionId));
        var deletedIds = new HashSet<string>(
            result.Results.Where(r => r.Deleted).Select(r => r.SessionId),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in chosen.Where(r => deletedIds.Contains(r.SessionId)))
        {
            row.PropertyChanged -= OnRowChanged;
            _all.Remove(row);
            Visible.Remove(row);
        }

        StatusMessage = result.FailedCount == 0
            ? $"Deleted {result.DeletedCount} session(s), freed {CleanupRow.FormatBytes(result.BytesFreed)}."
            : $"Deleted {result.DeletedCount}, {result.FailedCount} failed. First error: {result.Failures.First().Error}";

        RaiseSelectionChanged();
        return result;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupRow.IsSelected)) RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(HasSelection));
    }

    private Func<Action, Task> GetUiMarshaller()
    {
        _marshalToUi ??= CreateUiMarshaller(SynchronizationContext.Current);
        return _marshalToUi;
    }

    private static Func<Action, Task> CreateUiMarshaller(SynchronizationContext? ctx)
    {
        if (ctx is null)
        {
            return action =>
            {
                action();
                return Task.CompletedTask;
            };
        }
        return action =>
        {
            var tcs = new TaskCompletionSource();
            ctx.Post(_ =>
            {
                try { action(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }, null);
            return tcs.Task;
        };
    }
}
