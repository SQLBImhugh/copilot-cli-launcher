using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

/// <summary>
/// The single launch path shared by the Sessions tab (Resume / new session here)
/// and the Projects tab's "New session" button.
/// </summary>
public sealed class ProjectLaunchServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly ProjectsService _store;
    private LaunchRequest? _captured;

    public ProjectLaunchServiceTests()
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

    private ProjectLaunchService NewService(FakeSettings? settings = null, FakeAfterLaunch? afterLaunch = null) =>
        new(new FakeLaunch(r => _captured = r),
            new FakeTerminals(),
            settings ?? new FakeSettings(),
            _store,
            afterLaunch: afterLaunch);

    private ProjectProfile AddProject(string path, Action<ProjectProfile> configure)
    {
        var p = new ProjectProfile { Id = Guid.NewGuid().ToString(), Label = "proj", Path = path };
        configure(p);
        _store.Add(p);
        return p;
    }

    [Fact]
    public void Launch_AppliesTheGoverningProjectProfile()
    {
        AddProject(@"C:\repos\app", p =>
        {
            p.EnableAllowAll = true;
            p.ExtraCopilotArgs = "--from-project";
            p.TerminalOverride = "pwsh";
            p.Capabilities = new LaunchCapabilities { Agent = "reviewer" };
        });

        var result = NewService().Launch(@"C:\repos\app\src", resumeTarget: null);

        Assert.True(result.Success);
        Assert.Equal("proj", result.Project?.Label);
        Assert.True(_captured!.EnableAllowAll);
        Assert.Equal("--from-project", _captured.ExtraCopilotArgs);
        Assert.Equal("reviewer", _captured.Capabilities?.Agent);
        Assert.Equal("pwsh", _captured.Terminal?.Id);
        Assert.Null(_captured.ResumeTarget);
    }

    [Fact]
    public void Launch_PassesResumeTargetThrough()
    {
        var result = NewService().Launch(@"C:\anywhere", resumeTarget: "session-123");

        Assert.True(result.Success);
        Assert.Equal("session-123", _captured!.ResumeTarget);
    }

    [Fact]
    public void Launch_UnmatchedDirectory_UsesGlobalDefaults()
    {
        AddProject(@"C:\other", p => p.ExtraCopilotArgs = "--nope");
        var settings = new FakeSettings();
        settings.Current.SessionsResume.ExtraCopilotArgs = "--global";

        var result = NewService(settings).Launch(@"C:\repos\app", null);

        Assert.True(result.Success);
        Assert.Null(result.Project);
        Assert.Equal("--global", _captured!.ExtraCopilotArgs);
        Assert.Null(_captured.Capabilities);
    }

    [Fact]
    public void Launch_AppliesTheAfterLaunchAction()
    {
        var settings = new FakeSettings();
        settings.Current.LauncherBehavior.AfterLaunch = "minimize";
        var after = new FakeAfterLaunch();

        NewService(settings, after).Launch(@"C:\repos\app", null);

        Assert.Equal("minimize", after.Applied);
    }

    [Fact]
    public void Launch_SpawnFailure_IsReportedNotThrown()
    {
        var svc = new ProjectLaunchService(
            new ThrowingLaunch(), new FakeTerminals(), new FakeSettings(), _store);

        var result = svc.Launch(@"C:\repos\app", null);

        Assert.False(result.Success);
        Assert.Contains("copilot not found", result.Error);
        Assert.Contains("copilot not found", result.Describe());
    }

    [Fact]
    public void Describe_MentionsTheProjectAndTerminal()
    {
        AddProject(@"C:\repos\app", p => p.TerminalOverride = "pwsh");

        var text = NewService().Launch(@"C:\repos\app", null).Describe();

        Assert.Contains("PowerShell 7", text);
        Assert.Contains("[proj]", text);
    }

    [Fact]
    public void Resolve_DoesNotLaunch()
    {
        AddProject(@"C:\repos\app", p => p.ExtraCopilotArgs = "--resolved");

        var profile = NewService().Resolve(@"C:\repos\app");

        Assert.Equal("--resolved", profile.ExtraCopilotArgs);
        Assert.Null(_captured);
    }

    // ---- fakes ----

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

    private sealed class ThrowingLaunch : ILaunchService
    {
        public LaunchCommand Build(LaunchRequest request) => throw new InvalidOperationException("copilot not found");
        public System.Diagnostics.Process Spawn(LaunchRequest request) => throw new InvalidOperationException("copilot not found");
    }

    private sealed class FakeTerminals : ITerminalDiscoveryService
    {
        public IReadOnlyList<TerminalProfile> Discovered { get; } = new List<TerminalProfile>
        {
            new() { Id = "wt", DisplayName = "Windows Terminal", ExecutablePath = @"C:\wt.exe", SupportsTabs = true, SupportsWorkingDirectoryFlag = true },
            new() { Id = "pwsh", DisplayName = "PowerShell 7", ExecutablePath = @"C:\pwsh.exe", SupportsTabs = false, SupportsWorkingDirectoryFlag = false },
        };
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

    private sealed class FakeAfterLaunch : IAfterLaunchAction
    {
        public string? Applied { get; private set; }
        public void Apply(string action) => Applied = action;
    }
}
