using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public class ChangelogPageViewModelTests
{
    // ---------- Check now ----------

    [Fact]
    public async Task CheckNow_VersionChange_PersistsChangelogEntry_NeverCallsAI()
    {
        var updates = new FakeUpdate { Next = ChangeResult("1.0.48", "1.0.49") };
        var ai = new FakeAI();
        var vm = NewVm(updates: updates, ai: ai);

        await vm.CheckNowAsync();

        Assert.Single(vm.Changelogs);
        Assert.Equal("1.0.48", vm.Changelogs[0].FromVersion);
        Assert.Equal("1.0.49", vm.Changelogs[0].ToVersion);
        Assert.Equal("copilot-update", vm.Changelogs[0].Source);
        Assert.Equal(0, ai.CallCount);
        Assert.Equal("1.0.49", updates.LastCommitted);
        Assert.Contains("New changelog", vm.ChangelogStatus);
    }

    [Fact]
    public async Task CheckNow_NoVersionChange_PersistsNothing()
    {
        var updates = new FakeUpdate { Next = NoChangeResult("1.0.49") };
        var ai = new FakeAI();
        var vm = NewVm(updates: updates, ai: ai);

        await vm.CheckNowAsync();

        Assert.Empty(vm.Changelogs);
        Assert.Equal(0, ai.CallCount);
        Assert.Contains("no version change", vm.ChangelogStatus);
    }

    [Fact]
    public async Task CheckNow_Bootstrap_PersistsNothing()
    {
        var updates = new FakeUpdate { Next = BootstrapResult("1.0.49") };
        var ai = new FakeAI();
        var vm = NewVm(updates: updates, ai: ai);

        await vm.CheckNowAsync();

        Assert.Empty(vm.Changelogs);
        Assert.Equal(0, ai.CallCount);
        Assert.Contains("Recorded baseline", vm.ChangelogStatus);
    }

    [Fact]
    public async Task CheckNow_UpdaterReturnsNull_LeavesHistoryUntouched()
    {
        var updates = new FakeUpdate { Next = null };
        var vm = NewVm(updates: updates);

        await vm.CheckNowAsync();

        Assert.Empty(vm.Changelogs);
        Assert.Contains("Could not run", vm.ChangelogStatus);
    }

    // ---------- Generate AI Briefing ----------

    [Fact]
    public async Task GenerateAIBriefing_AlreadyBriefedCurrentVersion_DoesNotCallAI_SwitchesToBriefingsView()
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.LastObservedCopilotVersion = "1.0.50";
        var briefingHistory = new FakeBriefingHistory();
        briefingHistory.Add(new BriefingEntry
        {
            Id = "b1", Timestamp = DateTime.UtcNow,
            FromVersion = "1.0.49", ToVersion = "1.0.50",
            Source = "ai-summary", Body = "Old briefing",
        });
        var ai = new FakeAI();
        var notes = new FakeReleaseNotes();

        var vm = NewVm(settings: settings, briefings: briefingHistory, ai: ai, notes: notes);
        vm.Reload();

        await vm.GenerateAIBriefingAsync();

        Assert.Equal(0, ai.CallCount);
        Assert.Equal(0, notes.CallCount);
        Assert.Single(vm.Briefings);  // no new entry added
        Assert.Equal(ChangelogPageSubView.Briefings, vm.SelectedView);
        Assert.Contains("Already briefed", vm.BriefingStatus);
    }

    [Fact]
    public async Task GenerateAIBriefing_NewVersionSinceLastBriefing_CallsAIWithCorrectRange_PersistsBriefing()
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.LastObservedCopilotVersion = "1.0.55";
        var briefingHistory = new FakeBriefingHistory();
        briefingHistory.Add(new BriefingEntry
        {
            Id = "b1", Timestamp = DateTime.UtcNow,
            FromVersion = "1.0.48", ToVersion = "1.0.50",
            Source = "ai-summary", Body = "Old briefing",
        });
        var ai = new FakeAI { CannedSummary = "## Highlights\n- Cool stuff" };
        var notes = new FakeReleaseNotes
        {
            CannedEntries = new List<ReleaseEntry>
            {
                new() { Version = "1.0.55", Date = DateTime.UtcNow, Body = "v1.0.55 notes" },
            },
        };

        var vm = NewVm(settings: settings, briefings: briefingHistory, ai: ai, notes: notes);
        vm.Reload();

        await vm.GenerateAIBriefingAsync();

        Assert.Equal(1, ai.CallCount);
        Assert.Equal("1.0.50", ai.LastFromVersion);
        Assert.Equal("1.0.55", ai.LastToVersion);

        Assert.Equal(2, vm.Briefings.Count);
        Assert.Equal("1.0.50", vm.Briefings[0].FromVersion);
        Assert.Equal("1.0.55", vm.Briefings[0].ToVersion);
        Assert.Equal("ai-summary", vm.Briefings[0].Source);
        Assert.Contains("Highlights", vm.Briefings[0].Body);
        Assert.Equal(ChangelogPageSubView.Briefings, vm.SelectedView);
    }

    [Fact]
    public async Task GenerateAIBriefing_NoPriorBriefingsButHasChangelog_UsesChangelogFromVersionAsStart()
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.LastObservedCopilotVersion = "1.0.55";
        var changelogHistory = new FakeChangelogHistory();
        changelogHistory.Add(new ChangelogEntry
        {
            Id = "c1", Timestamp = DateTime.UtcNow,
            FromVersion = "1.0.52", ToVersion = "1.0.55",
            Source = "copilot-update", Body = "...",
        });
        var ai = new FakeAI { CannedSummary = "summary" };
        var notes = new FakeReleaseNotes
        {
            CannedEntries = new List<ReleaseEntry>
            {
                new() { Version = "1.0.55", Date = DateTime.UtcNow, Body = "..." },
            },
        };

        var vm = NewVm(settings: settings, changelogs: changelogHistory, ai: ai, notes: notes);
        vm.Reload();

        await vm.GenerateAIBriefingAsync();

        Assert.Equal(1, ai.CallCount);
        Assert.Equal("1.0.52", ai.LastFromVersion);
        Assert.Equal("1.0.55", ai.LastToVersion);
        Assert.Single(vm.Briefings);
    }

    [Fact]
    public async Task GenerateAIBriefing_NoChangelogsAndNoBriefings_UsesCurrentVersionAsRange()
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.LastObservedCopilotVersion = "1.0.49";
        var ai = new FakeAI { CannedSummary = "summary" };
        var notes = new FakeReleaseNotes
        {
            CannedEntries = new List<ReleaseEntry>
            {
                new() { Version = "1.0.49", Date = DateTime.UtcNow, Body = "..." },
            },
        };

        var vm = NewVm(settings: settings, ai: ai, notes: notes);
        vm.Reload();

        await vm.GenerateAIBriefingAsync();

        Assert.Equal(1, ai.CallCount);
        Assert.Equal("1.0.49", ai.LastFromVersion);
        Assert.Equal("1.0.49", ai.LastToVersion);
    }

    [Fact]
    public async Task GenerateAIBriefing_NoReleaseNotesInRange_SkipsAICall()
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.LastObservedCopilotVersion = "1.0.49";
        var ai = new FakeAI();
        var notes = new FakeReleaseNotes { CannedEntries = new List<ReleaseEntry>() };

        var vm = NewVm(settings: settings, ai: ai, notes: notes);

        await vm.GenerateAIBriefingAsync();

        Assert.Equal(0, ai.CallCount);
        Assert.Empty(vm.Briefings);
        Assert.Contains("No GitHub release notes available", vm.BriefingStatus);
    }

    [Fact]
    public async Task GenerateAIBriefing_NoObservedVersion_AbortsCleanly()
    {
        var settings = new FakeSettings();
        // settings.Current.Briefings.LastObservedCopilotVersion stays null
        var ai = new FakeAI();

        var vm = NewVm(settings: settings, ai: ai);

        await vm.GenerateAIBriefingAsync();

        Assert.Equal(0, ai.CallCount);
        Assert.Empty(vm.Briefings);
        Assert.Contains("No copilot version recorded", vm.BriefingStatus);
    }

    [Fact]
    public async Task GenerateAIBriefing_AIReturnsEmpty_DoesNotPersistBriefing()
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.LastObservedCopilotVersion = "1.0.49";
        var ai = new FakeAI { CannedSummary = null };
        var notes = new FakeReleaseNotes
        {
            CannedEntries = new List<ReleaseEntry>
            {
                new() { Version = "1.0.49", Date = DateTime.UtcNow, Body = "..." },
            },
        };

        var vm = NewVm(settings: settings, ai: ai, notes: notes);

        await vm.GenerateAIBriefingAsync();

        Assert.Equal(1, ai.CallCount);
        Assert.Empty(vm.Briefings);
        Assert.Contains("AI summary unavailable", vm.BriefingStatus);
    }

    [Fact]
    public void Reload_PopulatesBothCollections()
    {
        var changelogHistory = new FakeChangelogHistory();
        changelogHistory.Add(new ChangelogEntry
        {
            Id = "c1", Timestamp = DateTime.UtcNow,
            FromVersion = "1.0.48", ToVersion = "1.0.49",
            Source = "copilot-update", Body = "...",
        });
        var briefingHistory = new FakeBriefingHistory();
        briefingHistory.Add(new BriefingEntry
        {
            Id = "b1", Timestamp = DateTime.UtcNow,
            FromVersion = "1.0.48", ToVersion = "1.0.49",
            Source = "ai-summary", Body = "...",
        });

        var vm = NewVm(changelogs: changelogHistory, briefings: briefingHistory);
        vm.Reload();

        Assert.Single(vm.Changelogs);
        Assert.Single(vm.Briefings);
    }

    // ---------- Helpers ----------

    private static ChangelogPageViewModel NewVm(
        IChangelogHistoryService? changelogs = null,
        IBriefingHistoryService? briefings = null,
        IUpdateCheckService? updates = null,
        IBriefingService? briefingRender = null,
        ISettingsService? settings = null,
        IReleaseNotesService? notes = null,
        IAISummaryService? ai = null)
        => new(
            changelogs ?? new FakeChangelogHistory(),
            briefings ?? new FakeBriefingHistory(),
            updates ?? new FakeUpdate { Next = NoChangeResult("1.0.49") },
            briefingRender ?? new FakeBriefingRender(),
            settings ?? new FakeSettings(),
            notes ?? new FakeReleaseNotes(),
            ai ?? new FakeAI());

    private static UpdateCheckResult ChangeResult(string from, string to) => new()
    {
        PreviousVersion = from, CurrentVersion = to,
        RawOutput = $"Updated {from} -> {to}", IsBootstrap = false,
    };

    private static UpdateCheckResult NoChangeResult(string v) => new()
    {
        PreviousVersion = v, CurrentVersion = v,
        RawOutput = "No update needed", IsBootstrap = false,
    };

    private static UpdateCheckResult BootstrapResult(string v) => new()
    {
        PreviousVersion = v, CurrentVersion = v,
        RawOutput = $"Bootstrapped at {v}", IsBootstrap = true,
    };

    // ---------- Fakes ----------

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; } = new();
        public void Load() { }
        public void Save() { }
    }

    private sealed class FakeUpdate : IUpdateCheckService
    {
        public UpdateCheckResult? Next { get; set; }
        public string? LastCommitted { get; private set; }

        public Task<UpdateCheckResult?> RunAsync(CancellationToken ct = default)
            => Task.FromResult(Next);

        public void CommitObservedVersion(string version) => LastCommitted = version;
    }

    private sealed class FakeAI : IAISummaryService
    {
        public bool IsEnabled => true;
        public string? CannedSummary { get; set; } = "default summary";
        public int CallCount { get; private set; }
        public string? LastFromVersion { get; private set; }
        public string? LastToVersion { get; private set; }
        public string? LastChangelogText { get; private set; }

        public Task<string?> GenerateAsync(string fromVersion, string toVersion, string changelogText, CancellationToken ct = default)
        {
            CallCount++;
            LastFromVersion = fromVersion;
            LastToVersion = toVersion;
            LastChangelogText = changelogText;
            return Task.FromResult(CannedSummary);
        }
    }

    private sealed class FakeReleaseNotes : IReleaseNotesService
    {
        public IReadOnlyList<ReleaseEntry> CannedEntries { get; set; } = Array.Empty<ReleaseEntry>();
        public int CallCount { get; private set; }
        public string? LastFrom { get; private set; }
        public string? LastTo { get; private set; }

        public Task<IReadOnlyList<ReleaseEntry>> FetchAsync(string fromVersion, string toVersion, CancellationToken ct = default)
        {
            CallCount++;
            LastFrom = fromVersion;
            LastTo = toVersion;
            return Task.FromResult(CannedEntries);
        }
    }

    private sealed class FakeBriefingRender : IBriefingService
    {
        public string Render(string fromVersion, string toVersion, IEnumerable<ReleaseEntry> entries)
            => $"# Updated {fromVersion} → {toVersion}\nrendered body";
    }

    private sealed class FakeChangelogHistory : IChangelogHistoryService
    {
        private readonly List<ChangelogEntry> _items = new();
        public string FilePath => "(in-memory)";
        public IReadOnlyList<ChangelogEntry> All => _items;
        public void Reload() { }
        public void Add(ChangelogEntry entry)
        {
            _items.Add(entry);
            _items.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        }
        public void Clear() => _items.Clear();
    }

    private sealed class FakeBriefingHistory : IBriefingHistoryService
    {
        private readonly List<BriefingEntry> _items = new();
        public string FilePath => "(in-memory)";
        public IReadOnlyList<BriefingEntry> All => _items;
        public void Reload() { }
        public void Add(BriefingEntry entry)
        {
            _items.Add(entry);
            _items.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        }
        public void Clear() => _items.Clear();
    }
}
