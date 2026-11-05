using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public class SessionsViewModelTests
{
    [Fact]
    public void RecentFilterText_ReflectsConfiguredWindowDays()
    {
        var fakeSettings = new FakeSettings();
        fakeSettings.Current.SessionListing.RecentWindowDays = 35;

        var vm = new SessionsViewModel(new FakeDiscovery(), new FakeTerminals(), new FakeLaunch(_ => { }), fakeSettings);

        Assert.Equal(35, vm.RecentWindowDays);
        Assert.Equal("Recent (35d)", vm.RecentFilterLabel);
        Assert.Equal("Modified in the last 35 days", vm.RecentFilterTooltip);
    }

    [Fact]
    public void ResumeSession_AppliesSessionsResumeDefaults_FromSettings()
    {
        var captured = (LaunchRequest?)null;
        var fakeLaunch = new FakeLaunch(req => captured = req);
        var fakeTerminals = new FakeTerminals();
        var fakeDiscovery = new FakeDiscovery();
        var fakeSettings = new FakeSettings();

        // Configure settings: allow-all on, custom extra args.
        fakeSettings.Current.SessionsResume.EnableAllowAll = true;
        fakeSettings.Current.SessionsResume.ExtraCopilotArgs = "--max-autopilot-continues 100";

        var vm = new SessionsViewModel(fakeDiscovery, fakeTerminals, fakeLaunch, fakeSettings);
        var row = SessionRow.From(new CopilotSession
        {
            Id = "abc12345-0000-0000-0000-000000000000",
            FolderPath = @"C:\fake",
            LastModified = DateTime.UtcNow,
            Cwd = @"C:\some\proj",
        });

        var ok = vm.ResumeSession(row);
        Assert.True(ok);
        Assert.NotNull(captured);
        Assert.True(captured!.EnableAllowAll);
        Assert.Equal("--max-autopilot-continues 100", captured.ExtraCopilotArgs);
        Assert.Equal(@"C:\some\proj", captured.WorkingDirectory);
        Assert.Equal("abc12345-0000-0000-0000-000000000000", captured.ResumeTarget);
    }

    [Fact]
    public void StartNewSessionAt_SpawnsFreshSession_InRowsWorkingDirectory()
    {
        var captured = (LaunchRequest?)null;
        var fakeLaunch = new FakeLaunch(req => captured = req);
        var fakeSettings = new FakeSettings();
        fakeSettings.Current.SessionsResume.EnableAllowAll = true;
        fakeSettings.Current.SessionsResume.ExtraCopilotArgs = "--model test";
        var vm = new SessionsViewModel(new FakeDiscovery(), new FakeTerminals(), fakeLaunch, fakeSettings);

        var ok = vm.StartNewSessionAt(RowWithCwd(@"C:\some\proj"));

        Assert.True(ok);
        Assert.NotNull(captured);
        Assert.Null(captured!.ResumeTarget);
        Assert.Equal(@"C:\some\proj", captured.WorkingDirectory);
        Assert.True(captured.EnableAllowAll);
        Assert.Equal("--model test", captured.ExtraCopilotArgs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(unknown working dir)")]
    public void StartNewSessionAt_FallsBackToUserProfile_WhenWorkingDirectoryUnknown(string cwd)
    {
        var captured = (LaunchRequest?)null;
        var vm = new SessionsViewModel(
            new FakeDiscovery(),
            new FakeTerminals(),
            new FakeLaunch(req => captured = req),
            new FakeSettings());

        var ok = vm.StartNewSessionAt(RowWithCwd(cwd));

        Assert.True(ok);
        Assert.NotNull(captured);
        Assert.Null(captured!.ResumeTarget);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), captured.WorkingDirectory);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SessionRow_From_ShowFullSessionId_ControlsIdDisplay(bool showFullId)
    {
        var session = new CopilotSession
        {
            Id = "7406ade6-1111-2222-3333-444444444444",
            FolderPath = @"C:\fake",
            LastModified = DateTime.UtcNow,
            Cwd = @"C:\proj",
        };

        var row = SessionRow.From(session, showFullId);

        if (showFullId)
        {
            Assert.Contains("7406ade6-1111-2222-3333-444444444444", row.LastOpenedDisplay);
            Assert.DoesNotContain("…", row.LastOpenedDisplay);
        }
        else
        {
            Assert.Contains("7406ade6…", row.LastOpenedDisplay);
            Assert.DoesNotContain("7406ade6-1111", row.LastOpenedDisplay);
        }
    }

    [Fact]
    public async Task RefreshAsync_DoesNotThrow_WhenSessionRootMissing()    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid(), "missing");
        var viewModel = new SessionsViewModel(NewDiscovery(missingRoot), new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings());

        var ex = await Record.ExceptionAsync(() => viewModel.RefreshAsync());

        Assert.Null(ex);
        Assert.Equal(0, viewModel.TotalCount);
        Assert.Empty(viewModel.Visible);
        Assert.Contains("No sessions found", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RefreshAsync_ShowsSessions_BeforeRecursiveSizingCompletes()
    {
        using var discovery = new DeferredSizeDiscovery();
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings());
        var refresh = vm.RefreshAsync();
        SessionRow? initialRow = null;
        var changed = new List<string?>();

        try
        {
            await discovery.SizingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, vm.TotalCount);
            initialRow = Assert.Single(vm.Visible);
            initialRow.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.False(refresh.IsCompleted);
        }
        finally
        {
            discovery.FinishSizing.Set();
            await refresh;
        }

        var finalRow = Assert.Single(vm.Visible);
        Assert.Same(initialRow, finalRow);
        Assert.Contains("4 KB", finalRow.Tags);
        Assert.Contains(nameof(SessionRow.Tags), changed);
    }

    [Fact]
    public async Task RefreshAsync_UpdatesSizeSorting_WhenSizingCompletes()
    {
        using var discovery = new DeferredSizeDiscovery(sessionCount: 2);
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings())
        {
            SortField = SessionSortField.Size,
        };
        var refresh = vm.RefreshAsync();
        try
        {
            await discovery.SizingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, vm.TotalCount);
        }
        finally
        {
            discovery.FinishSizing.Set();
            await refresh;
        }

        Assert.Equal("deferred-size-1", vm.Visible[0].SessionId);
        Assert.Contains("8 KB", vm.Visible[0].Tags);
        Assert.Contains("4 KB", vm.Visible[1].Tags);
    }

    [Fact]
    public async Task RefreshAsync_NewerRefresh_CancelsThePreviousSizeScan()
    {
        using var discovery = new DeferredSizeDiscovery();
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings());
        var first = vm.RefreshAsync();
        Task second = Task.CompletedTask;
        try
        {
            await discovery.SizingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = vm.RefreshAsync();
            await discovery.SecondSizingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await first.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("Updating sizes", vm.StatusMessage);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            discovery.FinishSizing.Set();
            await Task.WhenAll(first, second);
        }

        Assert.Single(vm.Visible);
        Assert.DoesNotContain("canceled", vm.StatusMessage);
    }

    [Fact]
    public async Task RefreshAsync_Cancellation_LeavesMetadataVisible()
    {
        using var discovery = new DeferredSizeDiscovery();
        using var cancellation = new CancellationTokenSource();
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings());
        var refresh = vm.RefreshAsync(cancellation.Token);
        try
        {
            await discovery.SizingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(vm.Visible);
            Assert.Contains("canceled", vm.StatusMessage);
        }
        finally
        {
            discovery.FinishSizing.Set();
            await refresh;
        }
    }

    [Fact]
    public async Task RefreshAsync_DoesNotRestoreASession_DeletedDuringSizing()
    {
        using var discovery = new DeferredSizeDiscovery();
        var vm = new SessionsViewModel(
            discovery, new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings(),
            deletion: new SuccessfulDeletion());
        var refresh = vm.RefreshAsync();
        try
        {
            await discovery.SizingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.DeleteSession(Assert.Single(vm.Visible)));
        }
        finally
        {
            discovery.FinishSizing.Set();
            await refresh;
        }

        Assert.Empty(vm.Visible);
        Assert.Equal(0, vm.TotalCount);
    }

    [Fact]
    public async Task RefreshAsync_SizingFailure_LeavesMetadataVisibleAndReportsTheError()
    {
        using var discovery = new DeferredSizeDiscovery { FailSizing = true };
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings());
        discovery.FinishSizing.Set();

        await vm.RefreshAsync();

        Assert.Single(vm.Visible);
        Assert.Contains("Failed to update session sizes", vm.StatusMessage);
        Assert.Contains("size scan failed", vm.StatusMessage);
    }

    // ---- filtering ----

    private static CopilotSession Session(string id, string? name, string? cwd) => new()
    {
        Id = id,
        FolderPath = @"C:\fake\" + id,
        LastModified = DateTime.UtcNow.AddDays(-400),
        Cwd = cwd,
        Name = name,
        UserNamed = name is not null,
    };

    private static Task<SessionsViewModel> LoadedAsync(params CopilotSession[] sessions) =>
        LoadedAsync(new FakeSettings(), sessions);

    private static async Task<SessionsViewModel> LoadedAsync(FakeSettings settings, params CopilotSession[] sessions)
    {
        var vm = new SessionsViewModel(new SeededDiscovery(sessions), new FakeTerminals(), new FakeLaunch(_ => { }), settings);
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task Search_MatchesSessionName_WhenCwdAndRepoDoNot()
    {
        var vm = await LoadedAsync(
            Session("aaaaaaaa-0000-0000-0000-000000000001", "LogisticsDemo", @"C:\work\alpha"),
            Session("bbbbbbbb-0000-0000-0000-000000000002", "Other", @"C:\work\beta"));
        vm.ShowAll = true;

        vm.SearchText = "logisticsdemo";

        var row = Assert.Single(vm.Visible);
        Assert.StartsWith("aaaaaaaa", row.SessionId);
    }

    [Fact]
    public async Task NoChipsChecked_ShowsAllSessions_EvenWithShowAllOff()
    {
        var vm = await LoadedAsync(
            Session("aaaaaaaa-0000-0000-0000-000000000001", null, @"C:\work\alpha"),
            Session("bbbbbbbb-0000-0000-0000-000000000002", "Named", @"C:\work\beta"));

        vm.ShowAll = false;
        vm.ShowRecent = false;
        vm.ShowNamed = false;
        vm.ShowHeavy = false;

        Assert.Equal(2, vm.Visible.Count);
        Assert.DoesNotContain("No filters", vm.StatusMessage);
    }

    [Fact]
    public async Task CheckedChip_StillFilters_WhenShowAllOff()
    {
        var vm = await LoadedAsync(
            Session("aaaaaaaa-0000-0000-0000-000000000001", null, @"C:\work\alpha"),
            Session("bbbbbbbb-0000-0000-0000-000000000002", "Named", @"C:\work\beta"));

        vm.ShowAll = false;
        vm.ShowRecent = false;
        vm.ShowHeavy = false;
        vm.ShowNamed = true;

        Assert.Single(vm.Visible);
    }

    [Theory]
    [InlineData(@"C:\work\Frontier", @"C:\work\Frontier", true)]
    [InlineData(@"C:\work\Frontier\run\nested", @"C:\work\Frontier", true)]
    [InlineData(@"c:/WORK/frontier/run/", @"C:\work\Frontier\\", true)]
    [InlineData(@"C:\work\Frontier\", "  C:/work/Frontier/  ", true)]
    [InlineData(@"C:\work\Frontier2", @"C:\work\Frontier", false)]
    [InlineData(@"C:\work\Frontier-old\run", @"C:\work\Frontier\", false)]
    [InlineData(@"C:\work", @"C:\work\Frontier", false)]
    [InlineData(@"C:\other\Frontier", @"C:\work\Frontier", false)]
    [InlineData(@"C:\", @"C:\", true)]
    [InlineData(@"c:/work/Frontier", @"C:\", true)]
    [InlineData(@"D:\work\Frontier", @"C:\", false)]
    [InlineData(@"\", @"\", true)]
    [InlineData(@"\Frontier\run", "/", true)]
    [InlineData(@"\\server\share", @"\\SERVER\share\", true)]
    [InlineData("//SERVER/share/Frontier/run/", @"\\server\share\", true)]
    [InlineData(@"\\server\share2\Frontier", @"\\server\share", false)]
    [InlineData(null, @"C:\", false)]
    [InlineData("", @"C:\", false)]
    [InlineData("   ", @"C:\", false)]
    [InlineData(@"C:\work\Frontier", null, false)]
    [InlineData(@"C:\work\Frontier", "", false)]
    [InlineData(@"C:\work\Frontier", " \t ", false)]
    public async Task ExcludedPaths_MatchWindowsFolderBoundaries(string? cwd, string? excludedPath, bool isExcluded)
    {
        var settings = new FakeSettings();
        settings.Current.SessionListing.ExcludedPaths.Add(excludedPath!);
        var vm = await LoadedAsync(settings, Session("boundary-session", null, cwd));
        vm.ShowAll = true;

        Assert.True(vm.IsHidingExcludedPaths);
        Assert.Equal(1, vm.TotalCount);
        Assert.Equal(isExcluded ? 0 : 1, vm.VisibleCount);
    }

    [Fact]
    public async Task ExcludedPaths_IgnoreBlankEntriesAndLegacyGlobs()
    {
        var settings = new FakeSettings();
        settings.Current.SessionListing.ExcludedPaths.AddRange(new[] { "", " \t ", null!, @"C:\work\Frontier" });
        settings.Current.SessionListing.HiddenPathGlobs.Add("**");
        var vm = await LoadedAsync(settings,
            Session("excluded", null, @"C:\work\Frontier\run"),
            Session("included", null, @"C:\work\repo"));
        vm.ShowAll = true;

        Assert.Equal("included", Assert.Single(vm.Visible).SessionId);
        Assert.Equal(2, vm.TotalCount);
    }

    [Fact]
    public async Task ExcludedPaths_ToggleBothDirections_WithoutRescanningOrSaving()
    {
        var settings = new FakeSettings();
        settings.Current.SessionListing.ExcludedPaths.Add(@"C:\work\Frontier");
        var discovery = new SeededDiscovery(new[]
        {
            Session("excluded-parent", null, @"C:\work\Frontier"),
            Session("excluded-child", null, @"C:\work\Frontier\run"),
            Session("included", null, @"C:\work\repo"),
        });
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), settings)
        {
            ShowAll = true,
        };
        await vm.RefreshAsync();
        var scans = discovery.EnumerationCount;
        Assert.Equal("included", Assert.Single(vm.Visible).SessionId);

        vm.IsHidingExcludedPaths = false;
        Assert.Equal(3, vm.VisibleCount);
        vm.IsHidingExcludedPaths = true;
        Assert.Equal("included", Assert.Single(vm.Visible).SessionId);

        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(scans, discovery.EnumerationCount);
        Assert.Equal(0, settings.SaveCount);
        Assert.Equal(new[] { @"C:\work\Frontier" }, settings.Current.SessionListing.ExcludedPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExcludedPaths_ShowAllAndNoChips_StillRespectExclusions(bool showAll)
    {
        var settings = new FakeSettings();
        settings.Current.SessionListing.ExcludedPaths.Add(@"C:\work\Frontier");
        var vm = await LoadedAsync(settings,
            Session("excluded", "Excluded", @"C:\work\Frontier\run"),
            Session("included", "Included", @"C:\work\repo"));
        vm.ShowRecent = showAll;
        vm.ShowNamed = showAll;
        vm.ShowHeavy = showAll;
        vm.ShowAll = showAll;

        Assert.Equal("included", Assert.Single(vm.Visible).SessionId);
        Assert.Contains("1 of 2", vm.StatusMessage);
        vm.IsHidingExcludedPaths = false;
        Assert.Equal(2, vm.VisibleCount);
        vm.IsHidingExcludedPaths = true;
        Assert.Equal("included", Assert.Single(vm.Visible).SessionId);
    }

    [Fact]
    public async Task ExcludedPaths_TemporaryOverride_StillUsesSearchChipsAndSort()
    {
        var settings = new FakeSettings();
        settings.Current.SessionListing.ExcludedPaths.Add(@"C:\work\Frontier");
        var vm = await LoadedAsync(settings,
            Session("excluded", "Match Alpha", @"C:\work\Frontier\run"),
            Session("included", "Match Zebra", @"C:\work\repo"),
            Session("unnamed", null, @"C:\work\match-unnamed"),
            Session("other", "Other", @"C:\work\other"));
        vm.ShowRecent = false;
        vm.ShowNamed = true;
        vm.SortField = SessionSortField.Name;
        vm.SortDescending = false;
        vm.SearchText = "match";
        Assert.Equal("included", Assert.Single(vm.Visible).SessionId);

        vm.IsHidingExcludedPaths = false;
        Assert.Equal(new[] { "excluded", "included" }, vm.Visible.Select(row => row.SessionId));

        vm.ShowAll = true;
        Assert.Equal(new[] { "unnamed", "excluded", "included" }, vm.Visible.Select(row => row.SessionId));
        vm.IsHidingExcludedPaths = true;
        Assert.Equal(new[] { "unnamed", "included" }, vm.Visible.Select(row => row.SessionId));
        vm.SearchText = "Frontier";
        Assert.Empty(vm.Visible);
        vm.IsHidingExcludedPaths = false;
        Assert.Equal("excluded", Assert.Single(vm.Visible).SessionId);
    }

    [Fact]
    public async Task ExcludedPaths_ApplyFilters_ReadsReplacedCurrentSettings()
    {
        var settings = new FakeSettings();
        var discovery = new SeededDiscovery(new[]
        {
            Session("excluded", null, @"C:\work\Frontier\run"),
            Session("included", null, @"C:\work\repo"),
        });
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), settings)
        {
            ShowAll = true,
        };
        await vm.RefreshAsync();
        var scans = discovery.EnumerationCount;
        Assert.Equal(2, vm.VisibleCount);

        settings.Current = new AppSettings
        {
            SessionListing = new SessionListingSettings { ExcludedPaths = new() { @"C:\work\Frontier" } },
        };
        vm.ApplyFilters();
        Assert.Equal("included", Assert.Single(vm.Visible).SessionId);

        settings.Current.SessionListing.ExcludedPaths.Clear();
        vm.ApplyFilters();
        Assert.Equal(2, vm.VisibleCount);
        Assert.Equal(scans, discovery.EnumerationCount);
    }

    [Fact]
    public async Task ExcludedPaths_RefreshRereadsSettings_AndKeepsOverrideForViewLifetime()
    {
        var settings = new FakeSettings();
        settings.Current.SessionListing.ExcludedPaths.Add(@"C:\work\Frontier");
        var sessions = new[]
        {
            Session("frontier", null, @"C:\work\Frontier\run"),
            Session("repo", null, @"C:\work\repo"),
        };
        var vm = await LoadedAsync(settings, sessions);
        vm.ShowAll = true;
        Assert.Equal("repo", Assert.Single(vm.Visible).SessionId);

        settings.Current.SessionListing.ExcludedPaths = new() { @"C:\work\repo" };
        await vm.RefreshAsync();
        Assert.Equal("frontier", Assert.Single(vm.Visible).SessionId);

        vm.IsHidingExcludedPaths = false;
        await vm.RefreshAsync();
        Assert.False(vm.IsHidingExcludedPaths);
        Assert.Equal(2, vm.VisibleCount);

        var nextView = await LoadedAsync(settings, sessions);
        nextView.ShowAll = true;
        Assert.True(nextView.IsHidingExcludedPaths);
        Assert.Equal("frontier", Assert.Single(nextView.Visible).SessionId);
    }

    [Theory]
    [InlineData(SessionSortField.LastModified)]
    [InlineData(SessionSortField.Size)]
    public async Task ExcludedPaths_FilterMetadataBeforeSizing_AndRevealLoadedSizes(SessionSortField sortField)
    {
        var settings = new FakeSettings();
        settings.Current.SessionListing.ExcludedPaths.Add(@"C:\work\Frontier");
        using var discovery = new DeferredSizeDiscovery(sessionCount: 2)
        {
            WorkingDirectory = @"C:\work\Frontier\run",
        };
        var vm = new SessionsViewModel(discovery, new FakeTerminals(), new FakeLaunch(_ => { }), settings)
        {
            ShowAll = true,
            SortField = sortField,
        };
        var refresh = vm.RefreshAsync();
        try
        {
            await discovery.SizingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, vm.TotalCount);
            Assert.Empty(vm.Visible);
            Assert.False(refresh.IsCompleted);

            vm.IsHidingExcludedPaths = false;
            Assert.Equal(2, vm.VisibleCount);
            Assert.All(vm.Visible, row => Assert.Empty(row.Tags));
            vm.IsHidingExcludedPaths = true;
            Assert.Empty(vm.Visible);
        }
        finally
        {
            discovery.FinishSizing.Set();
            await refresh;
        }

        Assert.Empty(vm.Visible);
        Assert.Equal(2, vm.TotalCount);
        vm.IsHidingExcludedPaths = false;
        Assert.Equal(2, vm.VisibleCount);
        Assert.Contains("4 KB", vm.Visible.Single(row => row.SessionId == "deferred-size-0").Tags);
        Assert.Contains("8 KB", vm.Visible.Single(row => row.SessionId == "deferred-size-1").Tags);
        if (sortField == SessionSortField.Size)
            Assert.Equal("deferred-size-1", vm.Visible[0].SessionId);
        vm.IsHidingExcludedPaths = true;
        Assert.Empty(vm.Visible);
    }

    private sealed class SeededDiscovery : ISessionDiscoveryService
    {
        private readonly IReadOnlyList<CopilotSession> _sessions;
        public SeededDiscovery(IReadOnlyList<CopilotSession> sessions) { _sessions = sessions; }
        public string SessionRoot => Path.GetTempPath();
        public int EnumerationCount { get; private set; }
        public IEnumerable<CopilotSession> Enumerate()
        {
            EnumerationCount++;
            return _sessions;
        }
    }

    // ---- project profiles ----

    [Fact]
    public void ResumeSession_AppliesMatchingProjectProfile()
    {
        using var store = new TempProjects();
        var project = store.Add(@"C:\some\proj", p =>
        {
            p.EnableAllowAll = false;
            p.ExtraCopilotArgs = "--project-args";
            p.TerminalOverride = "pwsh";
            p.Capabilities = new LaunchCapabilities { Agent = "reviewer" };
        });

        var captured = (LaunchRequest?)null;
        var settings = new FakeSettings();
        settings.Current.SessionsResume.EnableAllowAll = true;
        settings.Current.SessionsResume.ExtraCopilotArgs = "--global-args";

        var vm = new SessionsViewModel(
            new FakeDiscovery(), new FakeTerminals(), new FakeLaunch(req => captured = req), settings,
            projects: store.Service);

        Assert.True(vm.ResumeSession(RowWithCwd(@"C:\some\proj")));
        Assert.NotNull(captured);
        Assert.False(captured!.EnableAllowAll);
        Assert.Equal("--project-args", captured.ExtraCopilotArgs);
        Assert.Equal("reviewer", captured.Capabilities?.Agent);
        Assert.Equal("pwsh", captured.Terminal?.Id);
        Assert.Contains(project.Label, vm.StatusMessage);
    }

    [Fact]
    public void ResumeSession_AppliesProjectToSubdirectorySessions()
    {
        using var store = new TempProjects();
        store.Add(@"C:\some", p => p.ExtraCopilotArgs = "--from-parent");

        var captured = (LaunchRequest?)null;
        var vm = new SessionsViewModel(
            new FakeDiscovery(), new FakeTerminals(), new FakeLaunch(req => captured = req), new FakeSettings(),
            projects: store.Service);

        Assert.True(vm.ResumeSession(RowWithCwd(@"C:\some\proj\nested")));
        Assert.Equal("--from-parent", captured!.ExtraCopilotArgs);
    }

    [Fact]
    public void ResumeSession_UnmatchedDirectory_KeepsGlobalDefaults()
    {
        using var store = new TempProjects();
        store.Add(@"C:\other\repo", p => p.ExtraCopilotArgs = "--nope");

        var captured = (LaunchRequest?)null;
        var settings = new FakeSettings();
        settings.Current.SessionsResume.ExtraCopilotArgs = "--global-args";
        settings.Current.DefaultCapabilities.Agent = "global-agent";

        var vm = new SessionsViewModel(
            new FakeDiscovery(), new FakeTerminals(), new FakeLaunch(req => captured = req), settings,
            projects: store.Service);

        Assert.True(vm.ResumeSession(RowWithCwd(@"C:\some\proj")));
        Assert.Equal("--global-args", captured!.ExtraCopilotArgs);
        Assert.Equal("wt", captured.Terminal?.Id);
        // Global default capabilities must not start leaking into plain resumes.
        Assert.Null(captured.Capabilities);
    }

    [Fact]
    public void ResumeSession_DisabledProjectIsIgnored()
    {
        using var store = new TempProjects();
        store.Add(@"C:\some\proj", p => { p.Enabled = false; p.ExtraCopilotArgs = "--disabled"; });

        var captured = (LaunchRequest?)null;
        var settings = new FakeSettings();
        settings.Current.SessionsResume.ExtraCopilotArgs = "--global-args";

        var vm = new SessionsViewModel(
            new FakeDiscovery(), new FakeTerminals(), new FakeLaunch(req => captured = req), settings,
            projects: store.Service);

        Assert.True(vm.ResumeSession(RowWithCwd(@"C:\some\proj")));
        Assert.Equal("--global-args", captured!.ExtraCopilotArgs);
    }

    [Fact]
    public void StartNewSessionAt_AppliesTheSameProjectProfileAsResume()
    {
        using var store = new TempProjects();
        store.Add(@"C:\some\proj", p => { p.EnableAllowAll = true; p.TerminalOverride = "pwsh"; });

        var captured = (LaunchRequest?)null;
        var vm = new SessionsViewModel(
            new FakeDiscovery(), new FakeTerminals(), new FakeLaunch(req => captured = req), new FakeSettings(),
            projects: store.Service);

        Assert.True(vm.StartNewSessionAt(RowWithCwd(@"C:\some\proj")));
        Assert.Null(captured!.ResumeTarget);
        Assert.True(captured.EnableAllowAll);
        Assert.Equal("pwsh", captured.Terminal?.Id);
    }

    /// <summary>Real ProjectsService over a throwaway projects.json.</summary>
    private sealed class TempProjects : IDisposable
    {
        private readonly string _dir;
        public ProjectsService Service { get; }

        public TempProjects()
        {
            _dir = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_dir);
            Service = new ProjectsService(Path.Combine(_dir, "projects.json"));
        }

        public ProjectProfile Add(string path, Action<ProjectProfile> configure)
        {
            var p = new ProjectProfile
            {
                Id = Guid.NewGuid().ToString(),
                Label = Path.GetFileName(path.TrimEnd('\\')),
                Path = path,
            };
            configure(p);
            Service.Add(p);
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static SessionDiscoveryService NewDiscovery(string root)    {
        var ctor = typeof(SessionDiscoveryService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string) }, null);
        return (SessionDiscoveryService)ctor!.Invoke(new object[] { root });
    }

    private static SessionRow RowWithCwd(string cwd) => new()
    {
        SessionId = "abc12345-0000-0000-0000-000000000000",
        ShortId = "abc12345",
        Title = string.Empty,
        Cwd = cwd,
        RepoBranch = "(no git repo)",
        LastOpenedDisplay = "just now · id abc12345…",
        Tags = string.Empty,
    };

    private sealed class FakeLaunch : ILaunchService
    {
        private readonly Action<LaunchRequest> _onSpawn;
        public FakeLaunch(Action<LaunchRequest> onSpawn) { _onSpawn = onSpawn; }
        public LaunchCommand Build(LaunchRequest request) =>
            new() { FileName = "fake", ArgumentList = Array.Empty<string>(), WorkingDirectory = request.WorkingDirectory };
        public System.Diagnostics.Process Spawn(LaunchRequest request)
        {
            _onSpawn(request);
            return System.Diagnostics.Process.GetCurrentProcess();
        }
    }
    private sealed class FakeTerminals : ITerminalDiscoveryService
    {
        public IReadOnlyList<TerminalProfile> Discovered { get; } = new List<TerminalProfile>
        {
            new() { Id = "wt", DisplayName = "WT", ExecutablePath = @"C:\wt.exe", SupportsTabs = true, SupportsWorkingDirectoryFlag = true },
            new() { Id = "pwsh", DisplayName = "PowerShell 7", ExecutablePath = @"C:\pwsh.exe", SupportsTabs = false, SupportsWorkingDirectoryFlag = false },
        };
        public void Refresh() { }
    }
    private sealed class FakeDiscovery : ISessionDiscoveryService
    {
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate() => Array.Empty<CopilotSession>();
    }
    private sealed class DeferredSizeDiscovery : ISessionDiscoveryService, IDisposable
    {
        private readonly int _sessionCount;
        private int _sizeScans;

        public DeferredSizeDiscovery(int sessionCount = 1) => _sessionCount = sessionCount;

        public string SessionRoot => Path.GetTempPath();
        public TaskCompletionSource SizingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSizingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim FinishSizing { get; } = new();
        public bool FailSizing { get; init; }
        public string? WorkingDirectory { get; init; }

        public IEnumerable<CopilotSession> Enumerate() => Enumerate(includeSizes: true);

        public IEnumerable<CopilotSession> Enumerate(bool includeSizes, CancellationToken ct = default)
        {
            if (includeSizes)
            {
                SizingStarted.TrySetResult();
                if (Interlocked.Increment(ref _sizeScans) == 2)
                    SecondSizingStarted.TrySetResult();
                FinishSizing.Wait(ct);
                if (FailSizing)
                    throw new IOException("size scan failed");
            }

            for (var i = 0; i < _sessionCount; i++)
            {
                yield return new CopilotSession
                {
                    Id = $"deferred-size-{i}",
                    FolderPath = @$"C:\fake\deferred-size-{i}",
                    LastModified = DateTime.UtcNow,
                    Cwd = WorkingDirectory,
                    SizeBytes = includeSizes ? 4096 * (i + 1) : 0,
                };
            }
        }

        public void Dispose() => FinishSizing.Dispose();
    }
    private sealed class SuccessfulDeletion : ISessionDeletionService
    {
        public SessionDeleteResult Delete(string sessionId) => new() { SessionId = sessionId, Deleted = true };
        public BulkDeleteResult DeleteMany(IEnumerable<string> sessionIds) =>
            new() { Results = sessionIds.Select(Delete).ToArray() };
    }
    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; set; } = new();
        public int SaveCount { get; private set; }
        public void Load() { }
        public void Save() => SaveCount++;
    }
}
