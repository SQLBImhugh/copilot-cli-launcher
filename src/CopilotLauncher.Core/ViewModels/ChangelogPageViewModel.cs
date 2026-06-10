using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// Logical sub-view inside the Changelog page. Bound to a SelectorBar at the
/// top of the page.
/// </summary>
public enum ChangelogPageSubView
{
    Changelog = 0,
    Briefings = 1,
}

/// <summary>
/// Backs the (renamed-from-Briefing) Changelog page. Owns two parallel histories
/// — raw <see cref="ChangelogEntry"/> records (release notes + `copilot update`
/// stdout, written automatically on version change) and AI-generated
/// <see cref="BriefingEntry"/> summaries (on-demand via the Generate AI Briefing
/// button) — and exposes the two commands that produce them.
/// </summary>
/// <remarks>
/// Introduced in v0.2.0 as a rewrite of the old BriefingViewModel. The previous
/// design conflated three concerns (run <c>copilot update</c>, render the
/// changelog, generate an AI summary) behind a single "Check now" action, all
/// gated on detecting a version change. That made it impossible to view past
/// changelogs or generate a fresh AI summary when the CLI was already
/// up-to-date. The v0.2.0 split:
/// <list type="bullet">
///   <item><b>Check now</b> always runs the updater + persists a changelog entry. NO AI.</item>
///   <item><b>Generate AI Briefing</b> calls the model with the diff between the last briefed version and the current version. If they match, it just re-surfaces the existing briefing (zero AI spend).</item>
/// </list>
/// </remarks>
public sealed partial class ChangelogPageViewModel : ObservableObject
{
    private readonly IChangelogHistoryService _changelogHistory;
    private readonly IBriefingHistoryService _briefingHistory;
    private readonly IUpdateCheckService _updates;
    private readonly IBriefingService _briefings;
    private readonly ISettingsService _settings;
    private readonly IReleaseNotesService _releaseNotes;
    private readonly IAISummaryService _ai;

    private int _checkInFlight;       // single-flight: Check now
    private int _briefingInFlight;    // single-flight: Generate AI Briefing

    public ObservableCollection<ChangelogEntry> Changelogs { get; } = new();
    public ObservableCollection<BriefingEntry> Briefings { get; } = new();

    private ChangelogPageSubView _selectedView = ChangelogPageSubView.Changelog;
    public ChangelogPageSubView SelectedView
    {
        get => _selectedView;
        set => SetProperty(ref _selectedView, value);
    }

    private string _changelogStatus = string.Empty;
    public string ChangelogStatus
    {
        get => _changelogStatus;
        private set => SetProperty(ref _changelogStatus, value);
    }

    private string _briefingStatus = string.Empty;
    public string BriefingStatus
    {
        get => _briefingStatus;
        private set => SetProperty(ref _briefingStatus, value);
    }

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set => SetProperty(ref _isChecking, value);
    }

    private bool _isGeneratingBriefing;
    public bool IsGeneratingBriefing
    {
        get => _isGeneratingBriefing;
        private set => SetProperty(ref _isGeneratingBriefing, value);
    }

    public ChangelogPageViewModel(
        IChangelogHistoryService changelogHistory,
        IBriefingHistoryService briefingHistory,
        IUpdateCheckService updates,
        IBriefingService briefings,
        ISettingsService settings,
        IReleaseNotesService releaseNotes,
        IAISummaryService? ai = null)
    {
        _changelogHistory = changelogHistory;
        _briefingHistory = briefingHistory;
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
            _changelogHistory.Reload();
            Changelogs.Clear();
            foreach (var e in _changelogHistory.All) Changelogs.Add(e);
            ChangelogStatus = Changelogs.Count switch
            {
                0 => "No changelogs yet. They appear here automatically when Copilot CLI updates, or click Check now.",
                1 => "1 changelog.",
                _ => $"{Changelogs.Count} changelogs (newest first).",
            };
        }
        catch (Exception ex)
        {
            ChangelogStatus = $"Failed to load changelogs.json: {ex.Message}";
        }

        try
        {
            _briefingHistory.Reload();
            Briefings.Clear();
            foreach (var e in _briefingHistory.All) Briefings.Add(e);
            BriefingStatus = Briefings.Count switch
            {
                0 => "No AI briefings yet. Click Generate AI Briefing to summarize the changes since the last briefed version.",
                1 => "1 AI briefing.",
                _ => $"{Briefings.Count} AI briefings (newest first).",
            };
        }
        catch (Exception ex)
        {
            BriefingStatus = $"Failed to load briefings.json: {ex.Message}";
        }
    }

    /// <summary>
    /// Manually runs <c>copilot update</c> + fetches release notes + persists
    /// a new <see cref="ChangelogEntry"/> on a version change. Never invokes
    /// the AI — that lives behind the Generate AI Briefing button.
    /// Single-flight guarded.
    /// </summary>
    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _checkInFlight, 1, 0) == 1)
        {
            ChangelogStatus = "A check is already in progress.";
            return;
        }
        IsChecking = true;
        try
        {
            ChangelogStatus = "Running `copilot update`…";
            var result = await _updates.RunAsync(ct).ConfigureAwait(true);
            if (result is null)
            {
                ChangelogStatus = "Could not run `copilot update` (CLI not on PATH or update failed).";
                return;
            }
            if (result.IsBootstrap)
            {
                ChangelogStatus = $"Recorded baseline {result.CurrentVersion}. Future updates from here will be tracked.";
                return;
            }
            if (!result.VersionChanged)
            {
                ChangelogStatus = $"Checked — no version change (still {result.CurrentVersion}).";
                return;
            }

            var entries = await _releaseNotes.FetchAsync(result.PreviousVersion, result.CurrentVersion, ct).ConfigureAwait(true);
            var body = _briefings.Render(result.PreviousVersion, result.CurrentVersion, entries);

            var changelog = new ChangelogEntry
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                FromVersion = result.PreviousVersion,
                ToVersion = result.CurrentVersion,
                Source = "copilot-update",
                Body = body + Environment.NewLine + "Raw output:" + Environment.NewLine + result.RawOutput,
            };
            _changelogHistory.Add(changelog);
            // Two-phase commit: advance the persisted baseline ONLY after the
            // changelog entry is durably written. Mirrors the v0.1.x flow but
            // for the new ChangelogEntry path. If history.Add throws, the
            // prior baseline stays in settings so the next check re-detects
            // the same transition (better than losing it silently).
            _updates.CommitObservedVersion(result.CurrentVersion);
            Changelogs.Insert(0, changelog);
            ChangelogStatus = $"New changelog: {result.PreviousVersion} → {result.CurrentVersion}.";
        }
        catch (Exception ex)
        {
            ChangelogStatus = $"Check failed: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
            System.Threading.Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    /// <summary>
    /// On-demand AI briefing. Smart behavior:
    /// <list type="bullet">
    ///   <item>If the newest briefing's <c>ToVersion</c> already matches the
    ///         current observed copilot version → no AI spend; just surface
    ///         the existing entry and report it.</item>
    ///   <item>If there is at least one prior briefing → summarize the range
    ///         <c>(lastBriefed.ToVersion, currentVersion]</c>.</item>
    ///   <item>If no prior briefing exists → use the newest changelog entry's
    ///         <c>FromVersion → ToVersion</c> as the range. If there's no
    ///         changelog history either, summarize the current version alone.</item>
    /// </list>
    /// Single-flight guarded.
    /// </summary>
    public async Task GenerateAIBriefingAsync(CancellationToken ct = default)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _briefingInFlight, 1, 0) == 1)
        {
            BriefingStatus = "A briefing generation is already in progress.";
            return;
        }
        IsGeneratingBriefing = true;
        try
        {
            var currentVersion = _settings.Current.Briefings.LastObservedCopilotVersion;
            if (string.IsNullOrWhiteSpace(currentVersion))
            {
                BriefingStatus = "No copilot version recorded yet. Click Check now first to establish a baseline.";
                return;
            }

            var latestBriefing = Briefings.FirstOrDefault();
            if (latestBriefing is not null
                && string.Equals(latestBriefing.ToVersion, currentVersion, StringComparison.Ordinal))
            {
                // Smart re-display: latest briefing already covers the current
                // version. Surface that fact and switch to the Briefings sub-
                // view (the existing entry is already at the top of the list,
                // newest-first). No AI call.
                SelectedView = ChangelogPageSubView.Briefings;
                BriefingStatus = $"Already briefed at {currentVersion}. Re-displaying last briefing — no new AI call.";
                return;
            }

            // Determine the range to summarize.
            string fromVersion;
            if (latestBriefing is not null)
            {
                fromVersion = latestBriefing.ToVersion;
            }
            else
            {
                var latestChangelog = Changelogs.FirstOrDefault();
                fromVersion = latestChangelog?.FromVersion ?? currentVersion;
            }

            BriefingStatus = string.Equals(fromVersion, currentVersion, StringComparison.Ordinal)
                ? $"Fetching release notes for {currentVersion}…"
                : $"Fetching release notes for {fromVersion} → {currentVersion}…";

            var entries = await _releaseNotes.FetchAsync(fromVersion, currentVersion, ct).ConfigureAwait(true);
            if (entries.Count == 0)
            {
                BriefingStatus = $"No GitHub release notes available for {fromVersion} → {currentVersion}. Skipped AI summary.";
                return;
            }

            BriefingStatus = $"Generating AI summary for {fromVersion} → {currentVersion}…";
            var changelogText = ReleaseNotesService.BuildChangelogText(entries);
            var summary = await _ai.GenerateAsync(fromVersion, currentVersion, changelogText, ct).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(summary))
            {
                BriefingStatus = $"AI summary unavailable for {fromVersion} → {currentVersion} (copilot CLI not installed or returned empty).";
                return;
            }

            var briefing = new BriefingEntry
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                FromVersion = fromVersion,
                ToVersion = currentVersion,
                Source = "ai-summary",
                Body = summary.Trim(),
            };
            _briefingHistory.Add(briefing);
            Briefings.Insert(0, briefing);
            SelectedView = ChangelogPageSubView.Briefings;
            BriefingStatus = $"New AI briefing: {fromVersion} → {currentVersion}.";
        }
        catch (Exception ex)
        {
            BriefingStatus = $"Generate AI Briefing failed: {ex.Message}";
        }
        finally
        {
            IsGeneratingBriefing = false;
            System.Threading.Interlocked.Exchange(ref _briefingInFlight, 0);
        }
    }

    public async Task BackfillMissingReleaseNotesAsync(CancellationToken ct = default)
    {
        var backfilled = 0;
        for (var i = 0; i < Changelogs.Count; i++)
        {
            var entry = Changelogs[i];
            if (!NeedsReleaseNotesBackfill(entry))
                continue;

            try
            {
                var entries = await _releaseNotes.FetchAsync(entry.FromVersion, entry.ToVersion, ct).ConfigureAwait(true);
                if (entries.Count == 0)
                    continue;

                var renderedNotes = _briefings.Render(entry.FromVersion, entry.ToVersion, entries);
                var rawMarkerIndex = entry.Body.IndexOf("Raw output:", StringComparison.Ordinal);
                var newBody = rawMarkerIndex >= 0
                    ? renderedNotes + Environment.NewLine + entry.Body[rawMarkerIndex..]
                    : renderedNotes;

                var updated = new ChangelogEntry
                {
                    Id = entry.Id,
                    Timestamp = entry.Timestamp,
                    FromVersion = entry.FromVersion,
                    ToVersion = entry.ToVersion,
                    Source = entry.Source,
                    Body = newBody,
                };
                _changelogHistory.Replace(updated);
                Changelogs[i] = updated;
                backfilled++;
            }
            catch
            {
                // Best-effort: one bad historical entry must not abort the page load.
            }
        }

        if (backfilled > 0)
            ChangelogStatus = backfilled == 1
                ? "Backfilled release notes for 1 changelog."
                : $"Backfilled release notes for {backfilled} changelog(s).";
    }

    private static bool NeedsReleaseNotesBackfill(ChangelogEntry entry)
        => !string.IsNullOrWhiteSpace(entry.FromVersion)
           && !string.IsNullOrWhiteSpace(entry.ToVersion)
           && !string.Equals(entry.FromVersion, entry.ToVersion, StringComparison.Ordinal)
           && !entry.Body.Contains("## v", StringComparison.Ordinal);

    public void ClearChangelogs()
    {
        _changelogHistory.Clear();
        Changelogs.Clear();
        ChangelogStatus = "Changelog history cleared.";
    }

    public void ClearBriefings()
    {
        _briefingHistory.Clear();
        Briefings.Clear();
        BriefingStatus = "AI briefing history cleared.";
    }
}
