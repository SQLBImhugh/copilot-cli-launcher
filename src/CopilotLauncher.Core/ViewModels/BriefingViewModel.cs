using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// Backs the Briefing page. Lists past version-bump briefings (newest first)
/// and exposes a "Force check now" action that runs UpdateCheckService +
/// BriefingService + adds a new BriefingEntry on a version change.
/// </summary>
public sealed class BriefingViewModel : INotifyPropertyChanged
{
    private readonly IBriefingHistoryService _history;
    private readonly IUpdateCheckService _updates;
    private readonly IBriefingService _briefings;
    private readonly ISettingsService _settings;
    private readonly IAISummaryService _ai;
    private int _checkInFlight;  // single-flight guard

    public ObservableCollection<BriefingEntry> Items { get; } = new();

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set { if (_isChecking != value) { _isChecking = value; OnPropertyChanged(); } }
    }

    public BriefingViewModel(
        IBriefingHistoryService history,
        IUpdateCheckService updates,
        IBriefingService briefings,
        ISettingsService settings,
        IAISummaryService? ai = null)
    {
        _history = history;
        _updates = updates;
        _briefings = briefings;
        _settings = settings;
        _ai = ai ?? new NoopAISummaryService();
    }

    public void Reload()
    {
        try
        {
            _history.Reload();
            Items.Clear();
            foreach (var e in _history.All) Items.Add(e);
            StatusMessage = Items.Count switch
            {
                0 => "No briefings yet. They appear automatically when Copilot CLI updates, or click Check now.",
                1 => "1 briefing.",
                _ => $"{Items.Count} briefings (newest first).",
            };
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load briefings.json: {ex.Message}";
        }
    }

    /// <summary>Manually run `copilot update` and, if the version changed, render
    /// + persist a new briefing entry. Single-flight guarded.</summary>
    public async Task ForceCheckAsync(CancellationToken ct = default)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _checkInFlight, 1, 0) == 1)
        {
            StatusMessage = "A check is already in progress.";
            return;
        }
        IsChecking = true;
        try
        {
            StatusMessage = "Running `copilot update`…";
            var result = await _updates.RunAsync(ct).ConfigureAwait(true);
            if (result is null)
            {
                StatusMessage = "Could not run `copilot update` (CLI not on PATH or update failed).";
                return;
            }
            if (!result.VersionChanged)
            {
                StatusMessage = $"Checked — no version change (still {result.CurrentVersion}).";
                return;
            }

            var body = _briefings.Render(result.PreviousVersion, result.CurrentVersion, Array.Empty<ReleaseEntry>());
            var aiUnavailable = false;

            if (_ai.IsEnabled)
            {
                StatusMessage = "Generating AI summary...";
                var summary = await _ai.GenerateAsync(result.PreviousVersion, result.CurrentVersion, result.RawOutput, ct).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    body = "## AI Summary\n\n" + summary.Trim() + "\n\n---\n\n" + body;
                }
                else
                {
                    aiUnavailable = true;
                    StatusMessage = "AI summary unavailable; using bundled changelog.";
                }
            }

            var entry = new BriefingEntry
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                FromVersion = result.PreviousVersion,
                ToVersion = result.CurrentVersion,
                Source = "copilot-update",
                Body = body + Environment.NewLine + "Raw output:" + Environment.NewLine + result.RawOutput,
            };
            _history.Add(entry);
            Items.Insert(0, entry);
            if (!aiUnavailable)
                StatusMessage = $"New briefing: {result.PreviousVersion} → {result.CurrentVersion}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
            System.Threading.Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    public void ClearHistory()
    {
        _history.Clear();
        Items.Clear();
        StatusMessage = "Briefing history cleared.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
