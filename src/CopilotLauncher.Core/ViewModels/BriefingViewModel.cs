using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// Backs the Briefing page. Lists past version-bump briefings (newest first)
/// and exposes a "Force check now" action that runs UpdateCheckService +
/// BriefingService + adds a new BriefingEntry on a version change.
/// </summary>
public sealed partial class BriefingViewModel : ObservableObject
{
    private readonly IBriefingHistoryService _history;
    private readonly IUpdateCheckService _updates;
    private readonly IBriefingService _briefings;
    private readonly ISettingsService _settings;
    private readonly IAISummaryService _ai;
    private readonly IReleaseNotesService _releaseNotes;
    private int _checkInFlight;  // single-flight guard

    public ObservableCollection<BriefingEntry> Items { get; } = new();

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set => SetProperty(ref _isChecking, value);
    }

    public BriefingViewModel(
        IBriefingHistoryService history,
        IUpdateCheckService updates,
        IBriefingService briefings,
        ISettingsService settings,
        IReleaseNotesService releaseNotes,
        IAISummaryService? ai = null)
    {
        _history = history;
        _updates = updates;
        _briefings = briefings;
        _settings = settings;
        _releaseNotes = releaseNotes;
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
            if (result.IsBootstrap)
            {
                // First-ever check — RunAsync already persisted the baseline.
                // No briefing yet; subsequent checks will catch real updates.
                StatusMessage = $"Recorded baseline {result.CurrentVersion}. Future updates from here will be briefed.";
                return;
            }
            if (!result.VersionChanged)
            {
                StatusMessage = $"Checked — no version change (still {result.CurrentVersion}).";
                return;
            }

            var entries = await _releaseNotes.FetchAsync(result.PreviousVersion, result.CurrentVersion, ct).ConfigureAwait(true);
            var body = _briefings.Render(result.PreviousVersion, result.CurrentVersion, entries);
            // Build the changelog text fed to the AI. Only feed it real
            // GitHub release notes — when the GitHub fetch returned nothing
            // (offline + no cache, or the range matched zero entries), SKIP
            // the AI call entirely instead of falling back to the raw
            // `copilot update` stdout. The raw output is just "No update
            // needed, current version is X.Y.Z" which gives the model
            // nothing to anchor on and historically led to it hallucinating
            // from session memory (referencing legacy PowerShell helpers
            // that don't exist in this product). "No AI summary" is the
            // honest output when we have no source data.
            string? aiChangelog = entries.Count > 0
                ? ReleaseNotesService.BuildChangelogText(entries)
                : null;
            var aiUnavailable = false;

            if (_ai.IsEnabled && !string.IsNullOrWhiteSpace(aiChangelog))
            {
                StatusMessage = $"Updated {result.PreviousVersion} → {result.CurrentVersion}. Generating AI summary…";
                var summary = await _ai.GenerateAsync(result.PreviousVersion, result.CurrentVersion, aiChangelog, ct).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    body = "## AI Summary\n\n" + summary.Trim() + "\n\n---\n\n" + body;
                }
                else
                {
                    aiUnavailable = true;
                    StatusMessage = $"Updated {result.PreviousVersion} → {result.CurrentVersion}. AI summary unavailable; using bundled changelog.";
                }
            }
            else if (_ai.IsEnabled)
            {
                // AI is enabled but we have nothing to summarize. Surface this
                // so the user knows why no summary appears (offline, rate-
                // limited GitHub API with no cache, or no releases in range).
                aiUnavailable = true;
                StatusMessage = $"Updated {result.PreviousVersion} → {result.CurrentVersion}. Skipped AI summary — no GitHub release notes available for this range.";
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
            // Two-phase commit: advance the persisted baseline only AFTER
            // the briefing entry has been durably written. If history.Add
            // throws, the prior baseline stays in settings and the next
            // check will re-detect the same transition (better than losing
            // it silently). See UpdateCheckService for full rationale.
            _updates.CommitObservedVersion(result.CurrentVersion);
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
}
