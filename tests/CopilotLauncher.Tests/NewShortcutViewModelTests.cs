using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class NewShortcutViewModelTests : IDisposable
{
    private readonly string _tmpRoot;

    public NewShortcutViewModelTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Save_ReturnsNull_WhenWorkingDirectoryDoesNotExist()
    {
        var store = new FakeShortcuts();
        var vm = new NewShortcutViewModel(store, new FakeLaunch(), new FakeTerminals(), new FakeSettings())
        {
            Label = "My Shortcut",
            WorkingDirectory = Path.Combine(_tmpRoot, "missing")
        };

        var saved = vm.Save();

        Assert.Null(saved);
        Assert.Equal("Working directory does not exist.", vm.StatusMessage);
        Assert.Empty(store.All);
    }

    [Fact]
    public void Save_NormalizesWorkingDirectory_WhenValid()
    {
        var store = new FakeShortcuts();
        var workingDirectory = Path.Combine(_tmpRoot, "project");
        Directory.CreateDirectory(workingDirectory);
        var rawPath = Path.Combine(workingDirectory, ".", "..", "project");
        var vm = new NewShortcutViewModel(store, new FakeLaunch(), new FakeTerminals(), new FakeSettings())
        {
            Label = "My Shortcut",
            WorkingDirectory = rawPath
        };

        var saved = vm.Save();

        Assert.NotNull(saved);
        Assert.Equal(Path.GetFullPath(workingDirectory), saved!.WorkingDirectory);
        Assert.Equal(saved.WorkingDirectory, vm.WorkingDirectory);
        Assert.Single(store.All);
    }

    private sealed class FakeShortcuts : IShortcutsService
    {
        private readonly List<Shortcut> _items = new();
        public string FilePath => Path.Combine(Path.GetTempPath(), "fake-shortcuts.json");
        public IReadOnlyList<Shortcut> All => _items;
        public void Reload() { }
        public void Add(Shortcut launch) => _items.Add(launch);
        public void Update(Shortcut launch) => throw new NotSupportedException();
        public void Remove(string id) => throw new NotSupportedException();
        public Shortcut? GetById(string id) => _items.FirstOrDefault(x => x.Id == id);
    }

    private sealed class FakeLaunch : ILaunchService
    {
        public LaunchCommand Build(LaunchRequest request) =>
            new() { FileName = "copilot", ArgumentList = Array.Empty<string>(), WorkingDirectory = request.WorkingDirectory };

        public System.Diagnostics.Process Spawn(LaunchRequest request) => System.Diagnostics.Process.GetCurrentProcess();
    }

    private sealed class FakeTerminals : ITerminalDiscoveryService
    {
        public IReadOnlyList<TerminalProfile> Discovered { get; } = Array.Empty<TerminalProfile>();
        public void Refresh() { }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; } = new();
        public void Load() { }
        public void Save() { }
    }
}
