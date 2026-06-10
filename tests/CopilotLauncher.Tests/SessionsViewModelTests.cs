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
    public async Task RefreshAsync_DoesNotThrow_WhenSessionRootMissing()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid(), "missing");
        var viewModel = new SessionsViewModel(NewDiscovery(missingRoot), new FakeTerminals(), new FakeLaunch(_ => { }), new FakeSettings());

        var ex = await Record.ExceptionAsync(() => viewModel.RefreshAsync());

        Assert.Null(ex);
        Assert.Equal(0, viewModel.TotalCount);
        Assert.Empty(viewModel.Visible);
        Assert.Contains("No sessions found", viewModel.StatusMessage);
    }

    private static SessionDiscoveryService NewDiscovery(string root)
    {
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
        public IReadOnlyList<TerminalProfile> Discovered { get; } =
            new List<TerminalProfile> { new() { Id = "wt", DisplayName = "WT", ExecutablePath = @"C:\wt.exe", SupportsTabs = true, SupportsWorkingDirectoryFlag = true } };
        public void Refresh() { }
    }
    private sealed class FakeDiscovery : ISessionDiscoveryService
    {
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate() => Array.Empty<CopilotSession>();
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
