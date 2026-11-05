using System.Collections.Specialized;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class ProjectsViewModelSortingTests : IDisposable
{
    private readonly string _dir;
    private readonly ProjectsService _store;
    private readonly FakeSettings _settings = new();
    private readonly FakeProjectLaunch _launch = new();

    public ProjectsViewModelSortingTests()
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

    private ProjectsViewModel NewVm() => new(
        _store, new FakeRepoConfig(), new FakeCapabilities(), _settings, new FakeTerminals(),
        projectLaunch: _launch);

    private void Add(string id, string label, string path) =>
        _store.Add(new ProjectProfile { Id = id, Label = label, Path = path });

    private string MakeFolder(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertIds(ProjectsViewModel vm, params string[] expected) =>
        Assert.Equal(expected, vm.Items.Select(p => p.Id));

    private static string[] InDirection(string[] ascending, bool isDescending) =>
        isDescending ? ascending.AsEnumerable().Reverse().ToArray() : ascending;

    [Fact]
    public void Reload_DefaultsToNameAscending()
    {
        Add("z", "Zulu", @"C:\work\alpha");
        Add("a", "alpha", @"C:\work\zulu");
        Add("m", "Middle", @"C:\work\middle");
        var vm = NewVm();

        Assert.Equal(ProjectSortField.Name, vm.SortField);
        Assert.False(vm.IsSortDescending);
        vm.Reload();

        AssertIds(vm, "a", "m", "z");
    }

    [Theory]
    [InlineData(ProjectSortField.Name, false)]
    [InlineData(ProjectSortField.Name, true)]
    [InlineData(ProjectSortField.WorkingDirectory, false)]
    [InlineData(ProjectSortField.WorkingDirectory, true)]
    public void Sorting_OrdersBothFieldsAndDirections_AndSurvivesReload(ProjectSortField field, bool isDescending)
    {
        Add("c", "charlie", @"C:\work\bravo");
        Add("a", "alpha", @"C:\work\zulu");
        Add("d", "Delta", @"C:\work\delta");
        Add("b", "Bravo", @"C:\work\alpha");
        var vm = NewVm();
        vm.Reload();

        vm.SortField = field;
        vm.IsSortDescending = isDescending;

        var expected = InDirection(field == ProjectSortField.Name
            ? new[] { "a", "b", "c", "d" }
            : new[] { "b", "c", "d", "a" }, isDescending);
        AssertIds(vm, expected);

        vm.Reload();

        Assert.Equal(field, vm.SortField);
        Assert.Equal(isDescending, vm.IsSortDescending);
        AssertIds(vm, expected);
    }

    [Theory]
    [InlineData(ProjectSortField.Name)]
    [InlineData(ProjectSortField.WorkingDirectory)]
    public void Sorting_UsesCaseInsensitiveSecondaryFieldAndDeterministicIdTies(ProjectSortField field)
    {
        Add("z", "alpha", @"C:\work\zulu");
        Add("b", "ALPHA", @"c:\WORK\SAME");
        Add("c", "beta", @"C:\work\same");
        Add("a", "Alpha", @"C:\work\Same");
        var vm = NewVm();
        vm.Reload();

        vm.SortField = field;

        var ascending = field == ProjectSortField.Name
            ? new[] { "a", "b", "z", "c" }
            : new[] { "a", "b", "c", "z" };
        AssertIds(vm, ascending);

        vm.IsSortDescending = true;
        AssertIds(vm, InDirection(ascending, true));

        // Change stored order without changing any sort keys.
        var first = _store.GetById("z")!;
        _store.Remove(first.Id);
        _store.Add(first);
        vm.Reload();

        AssertIds(vm, InDirection(ascending, true));
    }

    [Fact]
    public void Sorting_DoesNotReorderStoreOrWriteProjectOrSettingsFiles()
    {
        Add("z", "Zulu", @"C:\work\alpha");
        Add("a", "Alpha", @"C:\work\zulu");
        var vm = NewVm();
        vm.Reload();
        var stored = _store.All.ToArray();
        var content = File.ReadAllBytes(_store.FilePath);
        var lastWrite = File.GetLastWriteTimeUtc(_store.FilePath);

        vm.SortField = ProjectSortField.WorkingDirectory;
        vm.IsSortDescending = true;
        vm.SortField = ProjectSortField.Name;
        vm.IsSortDescending = false;

        Assert.Equal(stored, _store.All.ToArray());
        Assert.Equal(content, File.ReadAllBytes(_store.FilePath));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(_store.FilePath));
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void Sorting_MovesExistingRowsWithoutChangingSelectionOrUnsavedEditor()
    {
        Add("a", "Alpha", @"C:\work\zulu");
        Add("b", "Beta", @"C:\work\alpha");
        Add("z", "Zulu", @"C:\work\middle");
        var vm = NewVm();
        vm.Reload();
        var selected = vm.Items[0];
        vm.Edit(selected);
        vm.EditLabel = "Unsaved name";
        vm.EditPath = @"Z:\unsaved\folder";
        vm.EditExtraArgs = "--model unsaved-model";
        vm.EditTerminal = "unsaved-terminal";
        vm.EditEnabled = false;
        vm.EditOverrideAllowAll = true;
        var plugin = new RepoPluginToggle { Key = "plugin", DisplayName = "Unsaved plugin", IsEnabled = false };
        var config = new RepoConfigRow { RelativePath = "config", Description = "Loaded config", Exists = true, Managed = true };
        vm.RepoPlugins.Add(plugin);
        vm.RepoConfigFiles.Add(config);
        vm.RepoConfigMessage = "Existing editor data";
        var instances = vm.Items.ToArray();
        var actions = new List<NotifyCollectionChangedAction>();
        var selectionChanges = 0;
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectsViewModel.Selected)) selectionChanges++;
        };

        vm.SortField = ProjectSortField.WorkingDirectory;
        vm.IsSortDescending = true;
        vm.SortField = ProjectSortField.Name;
        vm.IsSortDescending = false;

        Assert.NotEmpty(actions);
        Assert.All(actions, action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
        Assert.All(instances, item => Assert.Contains(item, vm.Items));
        Assert.Same(selected, vm.Selected);
        Assert.Equal(0, selectionChanges);
        Assert.True(vm.IsEditing);
        Assert.Equal("Unsaved name", vm.EditLabel);
        Assert.Equal(@"Z:\unsaved\folder", vm.EditPath);
        Assert.Equal("--model unsaved-model", vm.EditExtraArgs);
        Assert.Equal("unsaved-terminal", vm.EditTerminal);
        Assert.False(vm.EditEnabled);
        Assert.True(vm.EditOverrideAllowAll);
        Assert.Same(plugin, Assert.Single(vm.RepoPlugins));
        Assert.Same(config, Assert.Single(vm.RepoConfigFiles));
        Assert.Equal("Existing editor data", vm.RepoConfigMessage);
        Assert.Equal("Alpha", selected.Label);
    }

    [Fact]
    public void Sorting_KeepsAnUnsavedNewProjectUnselected()
    {
        Add("z", "Zulu", @"C:\work\alpha");
        Add("a", "Alpha", @"C:\work\zulu");
        var vm = NewVm();
        vm.Reload();
        vm.StartNew(@"C:\draft");
        vm.EditLabel = "Unsaved new project";
        vm.EditExtraArgs = "--model draft-model";

        vm.SortField = ProjectSortField.WorkingDirectory;
        vm.IsSortDescending = true;

        Assert.Null(vm.Selected);
        Assert.True(vm.IsEditing);
        Assert.Equal("Unsaved new project", vm.EditLabel);
        Assert.Equal(@"C:\draft", vm.EditPath);
        Assert.Equal("--model draft-model", vm.EditExtraArgs);
        Assert.Equal(2, vm.Items.Count);
    }

    [Theory]
    [InlineData(ProjectSortField.Name, false)]
    [InlineData(ProjectSortField.Name, true)]
    [InlineData(ProjectSortField.WorkingDirectory, false)]
    [InlineData(ProjectSortField.WorkingDirectory, true)]
    public void SaveNew_UsesCurrentSortWithoutReorderingStore(ProjectSortField field, bool isDescending)
    {
        Add("z", "Zulu", MakeFolder("alpha"));
        Add("a", "Alpha", MakeFolder("zulu"));
        var vm = NewVm();
        vm.Reload();
        vm.SortField = field;
        vm.IsSortDescending = isDescending;
        vm.StartNew(MakeFolder("middle"));
        vm.EditLabel = "Middle";

        var saved = vm.Save(null);

        Assert.NotNull(saved);
        AssertIds(vm, InDirection(field == ProjectSortField.Name
            ? new[] { "a", saved.Id, "z" }
            : new[] { "z", saved.Id, "a" }, isDescending));
        Assert.Equal(new[] { "z", "a", saved.Id }, _store.All.Select(p => p.Id));
        Assert.Same(saved, vm.Selected);
        Assert.False(vm.IsEditing);
    }

    [Theory]
    [InlineData(ProjectSortField.Name, false)]
    [InlineData(ProjectSortField.Name, true)]
    [InlineData(ProjectSortField.WorkingDirectory, false)]
    [InlineData(ProjectSortField.WorkingDirectory, true)]
    public void SaveEdit_RepositionsRenamedOrMovedProjectUsingCurrentSort(ProjectSortField field, bool isDescending)
    {
        Add("edited", "Zulu", MakeFolder("zulu"));
        Add("a", "Alpha", MakeFolder("alpha"));
        Add("m", "Middle", MakeFolder("middle"));
        var vm = NewVm();
        vm.Reload();
        vm.SortField = field;
        vm.IsSortDescending = isDescending;
        var selected = vm.Items.Single(p => p.Id == "edited");
        vm.Edit(selected);
        vm.EditLabel = "Aardvark";
        vm.EditPath = MakeFolder("aardvark");

        var saved = vm.Save(null);

        Assert.Same(selected, saved);
        Assert.Same(saved, vm.Selected);
        Assert.False(vm.IsEditing);
        AssertIds(vm, InDirection(new[] { "edited", "a", "m" }, isDescending));
        Assert.Equal(new[] { "edited", "a", "m" }, _store.All.Select(p => p.Id));
        Assert.Equal("Aardvark", _store.GetById("edited")!.Label);
        Assert.Equal(field, vm.SortField);
        Assert.Equal(isDescending, vm.IsSortDescending);
    }

    [Theory]
    [InlineData(ProjectSortField.Name, false)]
    [InlineData(ProjectSortField.Name, true)]
    [InlineData(ProjectSortField.WorkingDirectory, false)]
    [InlineData(ProjectSortField.WorkingDirectory, true)]
    public void Delete_KeepsCurrentSortAndNormalSelectionSemantics(ProjectSortField field, bool isDescending)
    {
        Add("z", "Zulu", @"C:\work\alpha");
        Add("a", "Alpha", @"C:\work\zulu");
        Add("m", "Middle", @"C:\work\middle");
        var vm = NewVm();
        vm.Reload();
        vm.SortField = field;
        vm.IsSortDescending = isDescending;
        var selected = vm.Items.Single(p => p.Id == "a");
        vm.Edit(selected);
        vm.EditLabel = "Unsaved name";

        vm.Delete(vm.Items.Single(p => p.Id == "m"));

        AssertIds(vm, InDirection(field == ProjectSortField.Name
            ? new[] { "a", "z" }
            : new[] { "z", "a" }, isDescending));
        Assert.Same(selected, vm.Selected);
        Assert.Equal("Unsaved name", vm.EditLabel);
        Assert.True(vm.IsEditing);

        vm.Delete(selected);

        AssertIds(vm, "z");
        Assert.Null(vm.Selected);
        Assert.False(vm.IsEditing);
        Assert.Equal(field, vm.SortField);
        Assert.Equal(isDescending, vm.IsSortDescending);
    }

    [Theory]
    [InlineData(ProjectSortField.Name, false)]
    [InlineData(ProjectSortField.Name, true)]
    [InlineData(ProjectSortField.WorkingDirectory, false)]
    [InlineData(ProjectSortField.WorkingDirectory, true)]
    public void ImportSelected_UsesCurrentSortAndKeepsUnsavedEditor(ProjectSortField field, bool isDescending)
    {
        Add("m", "Middle", MakeFolder("middle"));
        var vm = NewVm();
        vm.Reload();
        vm.SortField = field;
        vm.IsSortDescending = isDescending;
        var selected = Assert.Single(vm.Items);
        vm.Edit(selected);
        vm.EditLabel = "Unsaved name";
        vm.EditExtraArgs = "--model unsaved-model";
        vm.ImportCandidates.Add(new ProjectImportRow
        {
            Candidate = new ProjectImportCandidate { Path = MakeFolder("alpha"), SuggestedName = "Zulu", DirectoryExists = true },
            IsSelected = true,
        });
        vm.ImportCandidates.Add(new ProjectImportRow
        {
            Candidate = new ProjectImportCandidate { Path = MakeFolder("zulu"), SuggestedName = "Alpha", DirectoryExists = true },
            IsSelected = true,
        });

        Assert.Equal(2, vm.ImportSelected());

        var alpha = vm.Items.Single(p => p.Label == "Alpha");
        var zulu = vm.Items.Single(p => p.Label == "Zulu");
        var expected = InDirection(field == ProjectSortField.Name
            ? new[] { alpha.Id, "m", zulu.Id }
            : new[] { zulu.Id, "m", alpha.Id }, isDescending);
        AssertIds(vm, expected);
        Assert.Equal(new[] { "m", zulu.Id, alpha.Id }, _store.All.Select(p => p.Id));
        Assert.Same(selected, vm.Selected);
        Assert.True(vm.IsEditing);
        Assert.Equal("Unsaved name", vm.EditLabel);
        Assert.Equal("--model unsaved-model", vm.EditExtraArgs);
        Assert.Equal(0, vm.ImportSelected());
        AssertIds(vm, expected);
    }

    [Fact]
    public void StartSession_AfterSortingStillLaunchesTheClickedProject()
    {
        Add("z", "Zulu", MakeFolder("alpha"));
        Add("a", "Alpha", MakeFolder("zulu"));
        var vm = NewVm();
        vm.Reload();
        var selected = vm.Items.Single(p => p.Id == "a");
        var clicked = vm.Items.Single(p => p.Id == "z");
        vm.Edit(selected);
        vm.EditLabel = "Unsaved name";
        vm.SortField = ProjectSortField.WorkingDirectory;
        vm.IsSortDescending = true;

        Assert.True(vm.StartSession(clicked));

        Assert.Equal(clicked.Path, _launch.LastDirectory);
        Assert.Null(_launch.LastResumeTarget);
        Assert.Same(selected, vm.Selected);
        Assert.Equal("Unsaved name", vm.EditLabel);
    }

    [Fact]
    public void Sorting_EmptyListAndDirectionNotificationsAreSafe()
    {
        var vm = NewVm();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Equal("\uE74A", vm.SortDirectionGlyph);
        Assert.StartsWith("Sorted ascending", vm.SortDirectionDescription);

        vm.SortField = ProjectSortField.WorkingDirectory;
        vm.IsSortDescending = true;
        vm.Reload();

        Assert.Empty(vm.Items);
        Assert.True(vm.HasNoItems);
        Assert.Equal("\uE74B", vm.SortDirectionGlyph);
        Assert.StartsWith("Sorted descending", vm.SortDirectionDescription);
        Assert.Contains(nameof(ProjectsViewModel.SortDirectionGlyph), changed);
        Assert.Contains(nameof(ProjectsViewModel.SortDirectionDescription), changed);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(AppDataDirectory, "fake-sorting-settings.json");
        public AppSettings Current { get; } = new();
        public int SaveCount { get; private set; }
        public void Load() { }
        public void Save() => SaveCount++;
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

    private sealed class FakeProjectLaunch : IProjectLaunchService
    {
        public string? LastDirectory { get; private set; }
        public string? LastResumeTarget { get; private set; }
        public ResolvedLaunchProfile Resolve(string? directory) => throw new NotSupportedException();
        public ProjectLaunchResult Launch(string directory, string? resumeTarget)
        {
            LastDirectory = directory;
            LastResumeTarget = resumeTarget;
            return new ProjectLaunchResult { Success = true };
        }
    }
}
