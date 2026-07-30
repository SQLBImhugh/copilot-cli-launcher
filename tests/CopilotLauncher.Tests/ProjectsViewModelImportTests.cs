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

    private ProjectsViewModel NewVm(params CopilotSession[] sessions) => new(
        _store,
        new FakeRepoConfig(),
        new FakeCapabilities(),
        new FakeSettings(),
        new FakeTerminals(),
        new FakeSessions(sessions))
    {
        // Test folders live under %LOCALAPPDATA%\Temp, which production correctly treats as
        // a tooling path. Point the heuristic at the test root instead.
        ImportHomeRootOverride = _dir,
    };

    private static CopilotSession S(string cwd, string? repository = null, string? gitRoot = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        FolderPath = @"C:\fake",
        LastModified = DateTime.UtcNow,
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
        // The candidate list is a stale snapshot; a second click must not re-add.
        Assert.Equal(0, vm.ImportSelected());

        Assert.Single(vm.Items);
        Assert.Single(_store.All);
    }

    [Fact]
    public async Task AlreadyImportedFoldersAreListedButNotSelectable()
    {
        var app = MakeFolder("app");
        _store.Add(new ProjectProfile { Id = "existing", Label = "app", Path = app });

        var vm = NewVm(S(app));
        await vm.LoadImportCandidatesAsync();

        var row = Assert.Single(vm.ImportCandidates);
        Assert.False(row.CanImport);
        Assert.False(row.IsSelected);
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
    public async Task ScanFailure_IsSurfacedNotThrown()
    {
        var vm = new ProjectsViewModel(
            _store, new FakeRepoConfig(), new FakeCapabilities(), new FakeSettings(),
            new FakeTerminals(), new ThrowingSessions());

        var ex = await Record.ExceptionAsync(() => vm.LoadImportCandidatesAsync());

        Assert.Null(ex);
        Assert.False(vm.IsScanningSessions);
        Assert.Contains("Could not scan sessions", vm.ImportMessage);
    }

    [Fact]
    public async Task NoSessionDiscoveryService_DegradesGracefully()
    {
        var vm = new ProjectsViewModel(
            _store, new FakeRepoConfig(), new FakeCapabilities(), new FakeSettings(), new FakeTerminals());

        await vm.LoadImportCandidatesAsync();

        Assert.Empty(vm.ImportCandidates);
        Assert.Contains("unavailable", vm.ImportMessage);
    }

    // ---- fakes ----

    private sealed class FakeSessions : ISessionDiscoveryService
    {
        private readonly CopilotSession[] _sessions;
        public FakeSessions(CopilotSession[] sessions) { _sessions = sessions; }
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate() => _sessions;
    }

    private sealed class ThrowingSessions : ISessionDiscoveryService
    {
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate() => throw new IOException("disk on fire");
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; } = new();
        public void Load() { }
        public void Save() { }
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
    }
}
