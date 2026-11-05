using System.Text.Json;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

/// <summary>Covers the Projects tab's bulk-import flow end to end over a real
/// <see cref="ProjectsService"/> backed by a throwaway projects.json.</summary>
public sealed class ProjectsViewModelImportTests : IDisposable
{
    private readonly string _dir;
    private readonly ProjectsService _store;

    public ProjectsViewModelImportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _store = new ProjectsService(Path.Combine(_dir, "projects.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>Real, existing directories so candidates are recommended by default.</summary>
    private string MakeFolder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private ProjectsViewModel NewVm(params CopilotSession[] sessions) =>
        NewVm(new FakeSettings(_dir), new FakeSessions(sessions));

    private ProjectsViewModel NewVm(ISettingsService settings, params CopilotSession[] sessions) =>
        NewVm(settings, new FakeSessions(sessions));

    private ProjectsViewModel NewVm(ISettingsService settings, ISessionDiscoveryService sessions) => new(
        _store,
        new FakeRepoConfig(),
        new FakeCapabilities(),
        settings,
        new FakeTerminals(),
        sessions)
    {
        // Test folders live under %LOCALAPPDATA%\Temp, which production correctly treats as
        // a tooling path. Point the heuristic at the test root instead.
        ImportHomeRootOverride = _dir,
    };

    private static CopilotSession S(string cwd, string? repository = null, string? gitRoot = null, DateTime? modified = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        FolderPath = @"C:\fake",
        LastModified = modified ?? DateTime.UtcNow,
        Cwd = cwd,
        Repository = repository,
        GitRoot = gitRoot,
    };

    [Fact]
    public async Task LoadImportCandidatesAsync_PopulatesAndPreTicks()
    {
        var app = MakeFolder("app");
        var vm = NewVm(S(app, repository: "me/my-repo", gitRoot: app));

        await vm.LoadImportCandidatesAsync();

        var row = Assert.Single(vm.ImportCandidates);
        Assert.Equal("my-repo", row.SuggestedName);
        Assert.True(row.IsSelected);
        Assert.True(row.CanImport);
        Assert.False(vm.IsScanningSessions);
        Assert.True(vm.CanRunImport);
        Assert.Contains("1 folder(s) available, 1 selected", vm.ImportMessage);
    }

    [Fact]
    public async Task ImportSelected_CreatesProjectsNamedAfterTheGitRepo()
    {
        var app = MakeFolder("checkout");
        var plain = MakeFolder("plain-folder");
        var vm = NewVm(S(app, repository: "owner/FancyRepo", gitRoot: app), S(plain));

        await vm.LoadImportCandidatesAsync();
        var created = vm.ImportSelected();

        Assert.Equal(2, created);
        Assert.Equal(2, vm.Items.Count);
        Assert.Contains(vm.Items, p => p.Label == "FancyRepo" && p.Path == app);
        Assert.Contains(vm.Items, p => p.Label == "plain-folder" && p.Path == plain);

        // Persisted, not just in memory.
        var reloaded = new ProjectsService(Path.Combine(_dir, "projects.json"));
        Assert.Equal(2, reloaded.All.Count);
    }

    [Fact]
    public async Task ImportedProjectsInheritEverything()
    {
        var app = MakeFolder("app");
        var vm = NewVm(S(app));

        await vm.LoadImportCandidatesAsync();
        vm.ImportSelected();

        var project = Assert.Single(vm.Items);
        Assert.Null(project.EnableAllowAll);
        Assert.Null(project.PreApproveExtensions);
        Assert.Null(project.ExtraCopilotArgs);
        Assert.Null(project.TerminalOverride);
        Assert.Null(project.Capabilities);
        Assert.True(project.Enabled);
        Assert.True(project.IncludeSubdirectories);
    }

    [Fact]
    public async Task ImportSelected_IsIdempotent_NoDuplicatesOnSecondClick()
    {
        var app = MakeFolder("app");
        var vm = NewVm(S(app));

        await vm.LoadImportCandidatesAsync();
        Assert.Equal(1, vm.ImportSelected());
        Assert.Empty(vm.ImportCandidates);
        vm.ShowHiddenImportPaths = true;
        Assert.Empty(vm.ImportCandidates);
        // A second click must not re-add, even after revealing manually hidden paths.
        Assert.Equal(0, vm.ImportSelected());

        Assert.Single(vm.Items);
        Assert.Single(_store.All);
    }

    [Fact]
    public async Task AlreadyImportedFoldersAreHiddenFromImportList()
    {
        var app = MakeFolder("app");
        _store.Add(new ProjectProfile { Id = "existing", Label = "app", Path = app });

        var vm = NewVm(S(app));
        await vm.LoadImportCandidatesAsync();

        Assert.Empty(vm.ImportCandidates);
        Assert.Equal(0, vm.ImportSelected());
    }

    [Fact]
    public async Task SetAllImportSelections_TogglesOnlyImportableRows()
    {
        var a = MakeFolder("a");
        var b = MakeFolder("b");
        _store.Add(new ProjectProfile { Id = "existing", Label = "b", Path = b });

        var vm = NewVm(S(a), S(b));
        await vm.LoadImportCandidatesAsync();

        vm.SetAllImportSelections(true);
        Assert.Equal(1, vm.SelectedImportCount);

        vm.SetAllImportSelections(false);
        Assert.Equal(0, vm.SelectedImportCount);
        Assert.Equal(0, vm.ImportSelected());
    }

    [Fact]
    public async Task ReloadingCandidatesTwice_DoesNotDoubleCount()
    {
        var app = MakeFolder("app");
        var vm = NewVm(S(app));

        await vm.LoadImportCandidatesAsync();
        await vm.LoadImportCandidatesAsync();

        Assert.Single(vm.ImportCandidates);
        Assert.Equal(1, vm.SelectedImportCount);   // stale handlers must not inflate the count
    }

    [Fact]
    public async Task HiddenRoot_PersistsAcrossJsonReloadAndFutureSessionScans()
    {
        var hidden = MakeFolder("hidden");
        var visible = MakeFolder("visible");
        var settings = new FakeSettings(_dir);
        settings.Current.SessionListing.ExcludedPaths.Add(@"C:\session-only");
        settings.Current.SessionListing.HiddenPathGlobs.Add("**/cache/**");
        var vm = NewVm(settings, S(hidden), S(visible));
        await vm.LoadImportCandidatesAsync();
        vm.SetAllImportSelections(false);
        vm.ImportCandidates.Single(r => r.Path == hidden).IsSelected = true;

        Assert.Equal(1, vm.ToggleSelectedImportPaths());

        Assert.Equal(1, settings.SaveCount);
        Assert.Equal(new[] { hidden }, settings.Current.ProjectImport.HiddenPaths);
        Assert.Equal(visible, Assert.Single(vm.ImportCandidates).Path);
        Assert.Equal(0, vm.SelectedImportCount);
        Assert.False(vm.CanToggleImportPathVisibility);

        var futureChild = MakeFolder(Path.Combine("hidden", "future"));
        var reloaded = new FakeSettings(_dir);
        var next = NewVm(reloaded, S(hidden), S(futureChild), S(visible));
        await next.LoadImportCandidatesAsync();

        Assert.False(next.ShowHiddenImportPaths);
        Assert.Equal(visible, Assert.Single(next.ImportCandidates).Path);
        Assert.Contains("2 hidden path(s)", next.ImportMessage);
        Assert.Equal(0, reloaded.SaveCount);
        Assert.Equal(new[] { @"C:\session-only" }, reloaded.Current.SessionListing.ExcludedPaths);
        Assert.Equal(new[] { "**/cache/**" }, reloaded.Current.SessionListing.HiddenPathGlobs);
        Assert.Empty(_store.All);
    }

    [Fact]
    public async Task ShowHiddenPaths_CanRestoreWithoutRescanningOrAutomaticallySelecting()
    {
        var hidden = MakeFolder("hidden");
        var visible = MakeFolder("visible");
        var settings = new FakeSettings(_dir);
        settings.Current.ProjectImport.HiddenPaths.Add(hidden);
        settings.Save();
        var sessions = new FakeSessions(new[] { S(hidden), S(visible) });
        var vm = NewVm(settings, sessions);
        await vm.LoadImportCandidatesAsync();
        var visibleRow = Assert.Single(vm.ImportCandidates);
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.ShowHiddenImportPaths = true;

        var hiddenRow = vm.ImportCandidates.Single(r => r.Path == hidden);
        Assert.True(hiddenRow.IsHidden);
        Assert.True(hiddenRow.CanSelect);
        Assert.False(hiddenRow.CanImport);
        Assert.False(hiddenRow.IsSelected);
        Assert.Contains("hidden", hiddenRow.Caption);
        Assert.Contains("hidden", hiddenRow.DetailText);
        Assert.Contains("hidden", hiddenRow.AccessibleName);
        Assert.True(visibleRow.IsSelected);
        hiddenRow.IsSelected = true;

        vm.ShowHiddenImportPaths = false;
        Assert.Same(visibleRow, Assert.Single(vm.ImportCandidates));
        Assert.True(visibleRow.IsSelected);
        Assert.False(hiddenRow.IsSelected);
        vm.ShowHiddenImportPaths = true;
        Assert.False(hiddenRow.IsSelected);
        Assert.Equal(1, settings.SaveCount);
        Assert.Equal(1, sessions.EnumerationCount);

        vm.SetAllImportSelections(false);
        hiddenRow.IsSelected = true;
        Assert.Equal(0, vm.SelectedImportCount);
        Assert.True(vm.CanToggleImportPathVisibility);
        Assert.Equal(0, vm.ImportSelected());
        Assert.Empty(_store.All);
        Assert.Contains("Unhide", vm.ImportMessage);
        Assert.True(hiddenRow.IsSelected);
        var rowProperties = new List<string?>();
        hiddenRow.PropertyChanged += (_, e) => rowProperties.Add(e.PropertyName);

        Assert.Equal(1, vm.ToggleSelectedImportPaths());

        Assert.False(hiddenRow.IsHidden);
        Assert.True(hiddenRow.CanImport);
        Assert.False(hiddenRow.IsSelected);
        Assert.False(vm.CanToggleImportPathVisibility);
        Assert.Equal(0, vm.SelectedImportCount);
        Assert.Empty(settings.Current.ProjectImport.HiddenPaths);
        Assert.Empty(new FakeSettings(_dir).Current.ProjectImport.HiddenPaths);
        Assert.Equal(2, settings.SaveCount);
        Assert.Equal(1, sessions.EnumerationCount);
        Assert.Contains(nameof(ProjectsViewModel.SelectedImportCount), changedProperties);
        Assert.Contains(nameof(ProjectsViewModel.CanToggleImportPathVisibility), changedProperties);
        Assert.Contains(nameof(ProjectImportRow.IsHidden), rowProperties);
        Assert.Contains(nameof(ProjectImportRow.CanImport), rowProperties);
        Assert.Contains(nameof(ProjectImportRow.Caption), rowProperties);
        Assert.Contains(nameof(ProjectImportRow.DetailText), rowProperties);
        Assert.Contains(nameof(ProjectImportRow.AccessibleName), rowProperties);
        Assert.Contains(nameof(ProjectImportRow.RowOpacity), rowProperties);
    }

    [Theory]
    [InlineData(@"C:\work\App\", @"c:\WORK\APP", true)]
    [InlineData(@"C:/work/App/", @"c:\work\app\src", true)]
    [InlineData(@"C:\work\App", @"C:\work\Application", false)]
    [InlineData(@"C:\", @"c:\work\app", true)]
    [InlineData(@"C:\", @"D:\work\app", false)]
    [InlineData(@"\\server\share\", @"\\SERVER\share\src", true)]
    [InlineData(@"\\server\share", @"\\server\share", true)]
    [InlineData(@"\\server\share", @"\\server\share-two\src", false)]
    [InlineData(null, @"C:\work\app", false)]
    [InlineData("\0", @"C:\work\app", false)]
    [InlineData(@"C:\work", null, false)]
    [InlineData(@"C:\work", "   ", false)]
    public void HiddenPathMatching_RespectsRootsDescendantsAndSeparatorBoundaries(
        string? root, string? target, bool isHidden)
    {
        // Pure normalization/matching: no access to the synthetic drive or UNC paths.
        Assert.Equal(isHidden, ProjectsViewModel.IsImportPathHidden(target, new[] { root!, "", " ", "\0", null! }));
    }

    [Fact]
    public async Task UnhideInheritedPath_ClearsCoveringRootsButPreservesIndependentChoices()
    {
        var root = MakeFolder("root");
        var child = MakeFolder(Path.Combine("root", "child"));
        var otherChild = MakeFolder(Path.Combine("root", "other-child"));
        var independentlyHidden = MakeFolder(Path.Combine("root", "independent"));
        var outside = MakeFolder("outside");
        var settings = new FakeSettings(_dir);
        settings.Current.ProjectImport.HiddenPaths.AddRange(new[]
        {
            root.ToUpperInvariant().Replace('\\', '/') + "/",
            child.ToUpperInvariant() + Path.DirectorySeparatorChar,
            independentlyHidden,
            outside,
        });
        var vm = NewVm(settings, S(child), S(otherChild), S(independentlyHidden), S(outside));
        await vm.LoadImportCandidatesAsync();
        Assert.Empty(vm.ImportCandidates);
        vm.ShowHiddenImportPaths = true;
        vm.ImportCandidates.Single(r => r.Path == child).IsSelected = true;

        Assert.Equal(1, vm.ToggleSelectedImportPaths());

        Assert.Equal(new[] { independentlyHidden, outside }, settings.Current.ProjectImport.HiddenPaths);
        Assert.False(vm.ImportCandidates.Single(r => r.Path == child).IsHidden);
        Assert.False(vm.ImportCandidates.Single(r => r.Path == otherChild).IsHidden);
        Assert.All(vm.ImportCandidates, r => Assert.False(r.IsSelected));
        Assert.True(vm.ImportCandidates.Single(r => r.Path == independentlyHidden).IsHidden);
        Assert.True(vm.ImportCandidates.Single(r => r.Path == outside).IsHidden);
        var next = NewVm(new FakeSettings(_dir), S(child), S(otherChild), S(independentlyHidden), S(outside));
        await next.LoadImportCandidatesAsync();
        Assert.Equal(new[] { child, otherChild }.OrderBy(p => p), next.ImportCandidates.Select(r => r.Path).OrderBy(p => p));
    }

    [Fact]
    public async Task MixedSelections_HideAndUnhideOriginalStatesAndKeepIndependentRoots()
    {
        var toHide = MakeFolder("to-hide");
        var toUnhide = MakeFolder("to-unhide");
        var independent = MakeFolder("independent");
        var unrelated = MakeFolder("unrelated");
        var settings = new FakeSettings(_dir);
        settings.Current.ProjectImport.HiddenPaths.AddRange(new[] { toUnhide, independent });
        var vm = NewVm(settings, S(toHide), S(toUnhide), S(independent), S(unrelated));
        await vm.LoadImportCandidatesAsync();
        vm.ShowHiddenImportPaths = true;
        vm.SetAllImportSelections(false);
        vm.ImportCandidates.Single(r => r.Path == toHide).IsSelected = true;
        vm.ImportCandidates.Single(r => r.Path == toUnhide).IsSelected = true;

        Assert.Equal(2, vm.ToggleSelectedImportPaths());

        Assert.Equal(new[] { independent, toHide }, settings.Current.ProjectImport.HiddenPaths);
        Assert.True(vm.ImportCandidates.Single(r => r.Path == toHide).IsHidden);
        Assert.False(vm.ImportCandidates.Single(r => r.Path == toUnhide).IsHidden);
        Assert.True(vm.ImportCandidates.Single(r => r.Path == independent).IsHidden);
        Assert.False(vm.ImportCandidates.Single(r => r.Path == unrelated).IsHidden);
        Assert.All(vm.ImportCandidates, r => Assert.False(r.IsSelected));
        Assert.Equal(1, settings.SaveCount);

        vm.ShowHiddenImportPaths = false;
        Assert.Equal(2, vm.ImportCandidates.Count);
        Assert.Equal(new[] { false, true }, vm.ImportCandidates.Select(r => r.IsAlternate));
        vm.ShowHiddenImportPaths = true;
        Assert.Equal(new[] { false, true, false, true }, vm.ImportCandidates.Select(r => r.IsAlternate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedAncestorSelections_UseOriginalRootsRegardlessOfDisplayOrder(bool isParentFirst)
    {
        var parent = MakeFolder("parent");
        var child = MakeFolder(Path.Combine("parent", "child"));
        var settings = new FakeSettings(_dir);
        settings.Current.ProjectImport.HiddenPaths.Add(child);
        var recent = DateTime.UtcNow;
        var older = recent.AddDays(-1);
        var vm = NewVm(settings,
            S(parent, modified: isParentFirst ? recent : older),
            S(child, modified: isParentFirst ? older : recent));
        await vm.LoadImportCandidatesAsync();
        vm.ShowHiddenImportPaths = true;
        Assert.Equal(isParentFirst ? parent : child, vm.ImportCandidates[0].Path);
        vm.SetAllImportSelections(true);

        Assert.Equal(1, vm.ToggleSelectedImportPaths());

        // The child's original root is removed, not the parent newly hidden by this action.
        // With literal exclusion roots (no exceptions), that child remains hidden by its parent.
        Assert.Equal(new[] { parent }, settings.Current.ProjectImport.HiddenPaths);
        Assert.All(vm.ImportCandidates, r => Assert.True(r.IsHidden));
        Assert.All(vm.ImportCandidates, r => Assert.False(r.IsSelected));
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public async Task SelectAll_UsesVisibleSelectablePaths_ButNeverImportsHiddenSelections()
    {
        var a = MakeFolder("a");
        var b = MakeFolder("b");
        var hidden = MakeFolder("hidden");
        var settings = new FakeSettings(_dir);
        settings.Current.ProjectImport.HiddenPaths.Add(hidden);
        var vm = NewVm(settings, S(a), S(b), S(hidden));
        await vm.LoadImportCandidatesAsync();
        vm.ShowHiddenImportPaths = true;
        var hiddenRow = vm.ImportCandidates.Single(r => r.IsHidden);
        vm.SetAllImportSelections(true);
        Assert.All(vm.ImportCandidates, r => Assert.True(r.IsSelected));
        Assert.Equal(2, vm.SelectedImportCount);

        vm.ShowHiddenImportPaths = false;
        Assert.False(hiddenRow.IsSelected);
        Assert.All(vm.ImportCandidates, r => Assert.True(r.IsSelected));
        vm.SetAllImportSelections(false);
        vm.ShowHiddenImportPaths = true;
        Assert.All(vm.ImportCandidates, r => Assert.False(r.IsSelected));
        vm.SetAllImportSelections(true);

        Assert.Equal(2, vm.ImportSelected());

        Assert.Equal(2, _store.All.Count);
        Assert.DoesNotContain(_store.All, p => p.Path == hidden);
        Assert.Same(hiddenRow, Assert.Single(vm.ImportCandidates));
        Assert.True(hiddenRow.IsSelected);
        Assert.Equal(0, vm.SelectedImportCount);
        Assert.Equal(0, vm.ImportSelected());
        Assert.Equal(2, _store.All.Count);
        vm.SetAllImportSelections(false);
        Assert.False(hiddenRow.IsSelected);
        Assert.False(vm.CanToggleImportPathVisibility);
        Assert.Equal(0, vm.ToggleSelectedImportPaths());
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task ExistingPaths_AreExactNormalizedDisabledMatches_AndRecheckedBeforeImport()
    {
        var root = MakeFolder("root");
        var child = MakeFolder(Path.Combine("root", "child"));
        _store.Add(new ProjectProfile
        {
            Id = "disabled-root", Label = "Root", Enabled = false, IncludeSubdirectories = true,
            Path = root.ToUpperInvariant().Replace('\\', '/') + "/",
        });
        var vm = NewVm(S(root), S(child));
        await vm.LoadImportCandidatesAsync();
        vm.ShowHiddenImportPaths = true;

        Assert.Equal(child, Assert.Single(vm.ImportCandidates).Path);
        Assert.True(vm.ImportCandidates[0].CanImport); // A saved parent does not suppress subfolder projects.
        _store.Add(new ProjectProfile
        {
            Id = "added-after-scan", Label = "Child", Enabled = false,
            Path = child.ToUpperInvariant() + Path.DirectorySeparatorChar,
        });

        Assert.Equal(0, vm.ImportSelected());

        Assert.Empty(vm.ImportCandidates);
        Assert.Equal(2, _store.All.Count);
        Assert.Empty(vm.Items);
        vm.ShowHiddenImportPaths = false;
        vm.ShowHiddenImportPaths = true;
        Assert.Empty(vm.ImportCandidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisibilitySaveFailure_RollsBackListRowsSelectionsAndJson(bool isInitiallyHidden)
    {
        var target = MakeFolder("target");
        var independent = MakeFolder("independent");
        var settings = new FakeSettings(_dir);
        settings.Current.ProjectImport.HiddenPaths.Add(independent);
        if (isInitiallyHidden) settings.Current.ProjectImport.HiddenPaths.Add(target);
        settings.Save();
        var originalList = settings.Current.ProjectImport.HiddenPaths;
        var originalJson = File.ReadAllText(settings.SettingsFilePath);
        var vm = NewVm(settings, S(target));
        await vm.LoadImportCandidatesAsync();
        vm.ShowHiddenImportPaths = true;
        var row = Assert.Single(vm.ImportCandidates);
        row.IsSelected = true;
        settings.IsSaveFailing = true;

        Assert.Equal(0, vm.ToggleSelectedImportPaths());

        Assert.Same(originalList, settings.Current.ProjectImport.HiddenPaths);
        Assert.Equal(originalJson, File.ReadAllText(settings.SettingsFilePath));
        Assert.Same(row, Assert.Single(vm.ImportCandidates));
        Assert.Equal(isInitiallyHidden, row.IsHidden);
        Assert.True(row.IsSelected);
        Assert.True(vm.CanToggleImportPathVisibility);
        Assert.Contains("Could not save hidden import paths", vm.ImportMessage);
        Assert.Contains("No visibility changes were saved", vm.ImportMessage);
        settings.IsSaveFailing = false;

        Assert.Equal(1, vm.ToggleSelectedImportPaths());
        Assert.Equal(!isInitiallyHidden, row.IsHidden);
        Assert.False(row.IsSelected);
        Assert.Contains(independent, new FakeSettings(_dir).Current.ProjectImport.HiddenPaths);
    }

    [Fact]
    public async Task ScanGuards_PreventDirectSelectionVisibilityImportAndConcurrentScans()
    {
        var app = MakeFolder("app");
        var settings = new FakeSettings(_dir);
        var sessions = new BlockingSessions(new[] { S(app) });
        var vm = NewVm(settings, sessions);
        var scan = vm.LoadImportCandidatesAsync();
        Task? secondScan = null;
        try
        {
            await sessions.EnumerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var row = new ProjectImportRow
            {
                Candidate = new() { Path = app, SuggestedName = "app", DirectoryExists = true },
                IsSelected = true,
            };
            vm.ImportCandidates.Add(row);
            Assert.False(vm.CanRunImport);
            Assert.False(vm.CanToggleImportPathVisibility);

            vm.SetAllImportSelections(false);
            Assert.True(row.IsSelected);
            Assert.Equal(0, vm.ToggleSelectedImportPaths());
            Assert.Equal(0, vm.ImportSelected());
            Assert.Empty(_store.All);
            Assert.Equal(0, settings.SaveCount);
            Assert.Equal("Scanning past sessions…", vm.ImportMessage);
            secondScan = vm.LoadImportCandidatesAsync();
            Assert.True(secondScan.IsCompleted);
            Assert.Equal(1, sessions.EnumerationCount);
        }
        finally
        {
            sessions.ContinueEnumeration.TrySetResult(true);
            await scan;
            if (secondScan is not null) await secondScan;
        }
        Assert.Single(vm.ImportCandidates);
        Assert.False(vm.IsScanningSessions);
    }

    [Fact]
    public async Task CanceledScan_MessageSurvivesShowHiddenFilterChanges()
    {
        var vm = NewVm(S(MakeFolder("app")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await vm.LoadImportCandidatesAsync(cts.Token);
        vm.ShowHiddenImportPaths = true;
        vm.SetAllImportSelections(true);
        Assert.Equal(0, vm.ImportSelected());

        Assert.Empty(vm.ImportCandidates);
        Assert.False(vm.IsScanningSessions);
        Assert.Equal("Scan canceled.", vm.ImportMessage);
    }

    [Fact]
    public async Task ScanFailure_IsSurfacedNotThrown()
    {
        var vm = new ProjectsViewModel(
            _store, new FakeRepoConfig(), new FakeCapabilities(), new FakeSettings(_dir),
            new FakeTerminals(), new ThrowingSessions());

        var ex = await Record.ExceptionAsync(() => vm.LoadImportCandidatesAsync());

        Assert.Null(ex);
        Assert.False(vm.IsScanningSessions);
        Assert.Contains("Could not scan sessions", vm.ImportMessage);
        var message = vm.ImportMessage;
        vm.ShowHiddenImportPaths = true;
        Assert.Equal(0, vm.ImportSelected());
        Assert.Equal(message, vm.ImportMessage);
    }

    [Fact]
    public async Task NoSessionDiscoveryService_DegradesGracefully()
    {
        var vm = new ProjectsViewModel(
            _store, new FakeRepoConfig(), new FakeCapabilities(), new FakeSettings(_dir), new FakeTerminals());

        await vm.LoadImportCandidatesAsync();

        Assert.Empty(vm.ImportCandidates);
        Assert.Contains("unavailable", vm.ImportMessage);
    }

    // ---- fakes ----

    private sealed class FakeSessions : ISessionDiscoveryService
    {
        private readonly CopilotSession[] _sessions;
        public FakeSessions(CopilotSession[] sessions) { _sessions = sessions; }
        public int EnumerationCount { get; private set; }
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate()
        {
            EnumerationCount++;
            return _sessions;
        }
    }

    private sealed class BlockingSessions : ISessionDiscoveryService
    {
        private readonly CopilotSession[] _sessions;
        public BlockingSessions(CopilotSession[] sessions) { _sessions = sessions; }
        public TaskCompletionSource<bool> EnumerationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ContinueEnumeration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EnumerationCount { get; private set; }
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate()
        {
            EnumerationCount++;
            EnumerationStarted.TrySetResult(true);
            ContinueEnumeration.Task.GetAwaiter().GetResult();
            return _sessions;
        }
    }

    private sealed class ThrowingSessions : ISessionDiscoveryService
    {
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate() => throw new IOException("disk on fire");
    }

    private sealed class FakeSettings : ISettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public FakeSettings(string directory)
        {
            AppDataDirectory = directory;
            Load();
        }
        public string AppDataDirectory { get; }
        public string SettingsFilePath => Path.Combine(AppDataDirectory, "fake-settings.json");
        public AppSettings Current { get; private set; } = new();
        public int SaveCount { get; private set; }
        public bool IsSaveFailing { get; set; }
        public void Load()
        {
            Current = File.Exists(SettingsFilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath), JsonOptions)!
                : new();
            Current.Normalize();
        }
        public void Save()
        {
            SaveCount++;
            if (IsSaveFailing) throw new IOException("settings file is locked");
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
    }

    private sealed class FakeTerminals : ITerminalDiscoveryService
    {
        public IReadOnlyList<TerminalProfile> Discovered { get; } = Array.Empty<TerminalProfile>();
        public void Refresh() { }
    }

    private sealed class FakeCapabilities : ISessionCapabilityService
    {
        public Task<CapabilityCatalog> DiscoverAsync(string? workingDirectory, bool forceRefresh = false, CancellationToken ct = default)
            => Task.FromResult(CapabilityCatalog.Empty);
        public IReadOnlyList<InstalledPluginInfo> GetInstalledPlugins() => Array.Empty<InstalledPluginInfo>();
    }

    private sealed class FakeRepoConfig : IRepoConfigService
    {
        public RepoConfigStatus Inspect(string? directory) => new()
        {
            Directory = directory ?? string.Empty,
            Files = Array.Empty<RepoConfigFile>(),
        };
        public bool WriteEnabledPlugins(string directory, IEnumerable<InstalledPluginInfo> allPlugins, IEnumerable<string> enabledKeys) => false;
        public bool ClearEnabledPlugins(string directory) => false;
        public bool MergeDisabledMcpServers(string directory, IEnumerable<string> serverNames) => false;
    }
}
