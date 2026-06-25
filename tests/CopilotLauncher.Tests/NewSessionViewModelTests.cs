using System.Diagnostics;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class NewSessionViewModelTests : IDisposable
{
    private readonly string _tmpRoot;

    public NewSessionViewModelTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); }
        catch { /* best effort */ }
    }

    private static NewSessionViewModel Make(
        ILaunchService? launch = null,
        ITerminalDiscoveryService? terminals = null,
        ISettingsService? settings = null,
        RecordingAfterLaunch? afterLaunch = null,
        RecordingExtPerms? extPerms = null) =>
        new(launch ?? new RecordingLaunch(),
            terminals ?? new FakeTerminals(),
            settings ?? new FakeSettings(),
            afterLaunch ?? new RecordingAfterLaunch(),
            extPerms ?? new RecordingExtPerms());

    [Fact]
    public void CanLaunch_IsFalse_WithoutFolder()
    {
        var vm = Make();
        Assert.False(vm.CanLaunch);
    }

    [Fact]
    public void StartSession_ReturnsFalse_WhenNoFolderChosen()
    {
        var launch = new RecordingLaunch();
        var vm = Make(launch);

        Assert.False(vm.StartSession());
        Assert.Equal("Choose a folder first.", vm.StatusMessage);
        Assert.Null(launch.Last);
    }

    [Fact]
    public void StartSession_ReturnsFalse_WhenFolderDoesNotExist()
    {
        var launch = new RecordingLaunch();
        var vm = Make(launch);
        vm.WorkingDirectory = Path.Combine(_tmpRoot, "missing");

        Assert.False(vm.StartSession());
        Assert.Equal("Folder does not exist.", vm.StatusMessage);
        Assert.Null(launch.Last);
    }

    [Fact]
    public void StartSession_SpawnsFreshSession_WithArgsAllowAllAndAfterLaunch()
    {
        var dir = Path.Combine(_tmpRoot, "project");
        Directory.CreateDirectory(dir);
        var launch = new RecordingLaunch();
        var after = new RecordingAfterLaunch();
        var settings = new FakeSettings();
        settings.Current.LauncherBehavior.AfterLaunch = "minimize";
        var vm = Make(launch, settings: settings, afterLaunch: after);
        vm.WorkingDirectory = dir;
        vm.ExtraArgs = "--model claude-opus-4.8";
        vm.EnableAllowAll = true;

        Assert.True(vm.StartSession());

        Assert.NotNull(launch.Last);
        Assert.Null(launch.Last!.ResumeTarget);                       // fresh session
        Assert.Equal(Path.GetFullPath(dir), launch.Last.WorkingDirectory);
        Assert.True(launch.Last.EnableAllowAll);
        Assert.Equal("--model claude-opus-4.8", launch.Last.ExtraCopilotArgs);
        Assert.Equal("minimize", after.LastBehavior);                 // after-launch applied
        Assert.Contains("Started a new session", vm.StatusMessage);
    }

    [Fact]
    public void StartSession_NormalizesFolder()
    {
        var dir = Path.Combine(_tmpRoot, "project");
        Directory.CreateDirectory(dir);
        var launch = new RecordingLaunch();
        var vm = Make(launch);
        vm.WorkingDirectory = Path.Combine(dir, ".", "..", "project");

        Assert.True(vm.StartSession());
        Assert.Equal(Path.GetFullPath(dir), vm.WorkingDirectory);
        Assert.Equal(Path.GetFullPath(dir), launch.Last!.WorkingDirectory);
    }

    [Fact]
    public void StartSession_PreApprovesExtensions_OnlyWhenSettingEnabled()
    {
        var dir = Path.Combine(_tmpRoot, "project");
        Directory.CreateDirectory(dir);

        // Disabled (default): not called.
        var extOff = new RecordingExtPerms();
        var vmOff = Make(extPerms: extOff);
        vmOff.WorkingDirectory = dir;
        Assert.True(vmOff.StartSession());
        Assert.Null(extOff.LastDir);

        // Enabled: called with the validated dir.
        var settings = new FakeSettings();
        settings.Current.SessionsResume.PreApproveExtensions = true;
        var extOn = new RecordingExtPerms();
        var vmOn = Make(settings: settings, extPerms: extOn);
        vmOn.WorkingDirectory = dir;
        Assert.True(vmOn.StartSession());
        Assert.Equal(Path.GetFullPath(dir), extOn.LastDir);
    }

    [Fact]
    public void StartSession_ReturnsFalse_AndReportsError_WhenSpawnThrows()
    {
        var dir = Path.Combine(_tmpRoot, "project");
        Directory.CreateDirectory(dir);
        var vm = Make(new ThrowingLaunch());
        vm.WorkingDirectory = dir;

        Assert.False(vm.StartSession());
        Assert.StartsWith("Launch failed:", vm.StatusMessage);
    }

    [Fact]
    public void TerminalOptions_StartWithAuto()
    {
        var vm = Make();
        Assert.Equal("auto", vm.TerminalOptions[0].Id);
    }

    // -------------------- fakes --------------------

    private class RecordingLaunch : ILaunchService
    {
        public LaunchRequest? Last { get; private set; }

        public LaunchCommand Build(LaunchRequest request) =>
            new() { FileName = "copilot", ArgumentList = Array.Empty<string>(), WorkingDirectory = request.WorkingDirectory };

        public Process Spawn(LaunchRequest request)
        {
            Last = request;
            return Process.GetCurrentProcess();
        }
    }

    private sealed class ThrowingLaunch : ILaunchService
    {
        public LaunchCommand Build(LaunchRequest request) =>
            new() { FileName = "copilot", ArgumentList = Array.Empty<string>(), WorkingDirectory = request.WorkingDirectory };

        public Process Spawn(LaunchRequest request) => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingAfterLaunch : IAfterLaunchAction
    {
        public string? LastBehavior { get; private set; }
        public void Apply(string behavior) => LastBehavior = behavior;
    }

    private sealed class RecordingExtPerms : IExtensionPermissionService
    {
        public string? LastDir { get; private set; }
        public int EnsureExtensionGrants(string directory) { LastDir = directory; return 0; }
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
