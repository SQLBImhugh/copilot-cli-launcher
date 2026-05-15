using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public class SessionsViewModelTests
{
    [Fact]
    public void ResumeSession_AppliesSessionsResumeDefaults_FromSettings()
    {
        var captured = (LaunchRequest?)null;
        var fakeLaunch = new FakeLaunch(req => captured = req);
        var fakeTerminals = new FakeTerminals();
        var fakeDiscovery = new FakeDiscovery();
        var fakeSettings = new FakeSettings();

        // Configure settings: AI summary on, allow-all on, custom extra args.
        fakeSettings.Current.SessionsResume.EnableAISummary = true;
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
        Assert.True(captured!.EnableAISummary);
        Assert.True(captured.EnableAllowAll);
        Assert.Equal("--max-autopilot-continues 100", captured.ExtraCopilotArgs);
        Assert.Equal(@"C:\some\proj", captured.WorkingDirectory);
        Assert.Equal("abc12345-0000-0000-0000-000000000000", captured.ResumeTarget);
    }

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
