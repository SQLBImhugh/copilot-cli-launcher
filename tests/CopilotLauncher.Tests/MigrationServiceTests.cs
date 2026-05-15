using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class MigrationServiceTests : IDisposable
{
    private readonly string _legacyRoot;
    private readonly string _appData;
    private readonly string _origUserProfile;
    private readonly string _origLocalAppData;

    public MigrationServiceTests()
    {
        // Redirect %USERPROFILE% and %LOCALAPPDATA% to a sandbox so the tests
        // don't poke at the developer's real legacy install.
        var sandbox = Path.Combine(Path.GetTempPath(), "copilot-launcher-mig-tests-" + Guid.NewGuid());
        _legacyRoot = Path.Combine(sandbox, "user", "copilot-launcher");
        _appData = Path.Combine(sandbox, "localappdata", "CopilotLauncher");
        Directory.CreateDirectory(_legacyRoot);
        Directory.CreateDirectory(_appData);
        _origUserProfile  = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
        _origLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        Environment.SetEnvironmentVariable("USERPROFILE", Path.Combine(sandbox, "user"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(sandbox, "localappdata"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("USERPROFILE", _origUserProfile);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _origLocalAppData);
        try
        {
            var sandbox = Directory.GetParent(_legacyRoot)!.Parent!.FullName;
            Directory.Delete(sandbox, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Detect_ReturnsNotFound_WhenNoLegacyInstall()
    {
        var settings = new FakeSettings(_appData);
        var svc = new MigrationService(settings, _legacyRoot);
        var result = svc.Detect();
        Assert.False(result.Found);
    }

    [Fact]
    public void Detect_ReturnsFields_WhenConfigPresent()
    {
        File.WriteAllText(Path.Combine(_legacyRoot, "config.json"),
            "{\"projectName\":\"test-proj\",\"stateDir\":\"C:\\\\state\"}");
        File.WriteAllText(Path.Combine(_legacyRoot, "agents.md"), "# my agents");

        var settings = new FakeSettings(_appData);
        var svc = new MigrationService(settings, _legacyRoot);
        var result = svc.Detect();
        Assert.True(result.Found);
        Assert.Equal("test-proj", result.LegacyProjectName);
        Assert.Equal(@"C:\state", result.LegacyStateDir);
        Assert.NotNull(result.LegacyAgentsMdPath);
    }

    [Fact]
    public void Import_CopiesAgentsMd_AndMarksCompleted()
    {
        File.WriteAllText(Path.Combine(_legacyRoot, "config.json"), "{\"projectName\":\"p\"}");
        File.WriteAllText(Path.Combine(_legacyRoot, "agents.md"), "# legacy agents content");

        var settings = new FakeSettings(_appData);
        var svc = new MigrationService(settings, _legacyRoot);
        var result = svc.Detect();
        var status = svc.Import(result);

        Assert.Contains("agents.md", status);
        Assert.True(File.Exists(Path.Combine(_appData, "agents.md")));
        Assert.True(svc.MigrationCompleted);
        Assert.Equal(Path.Combine(_appData, "agents.md"), settings.Current.Briefings.AgentsContextFilePath);
    }

    [Fact]
    public void Import_DoesNotOverwrite_ExistingAgentsMd()
    {
        File.WriteAllText(Path.Combine(_legacyRoot, "config.json"), "{}");
        File.WriteAllText(Path.Combine(_legacyRoot, "agents.md"), "from-legacy");
        File.WriteAllText(Path.Combine(_appData, "agents.md"), "from-2.0");

        var settings = new FakeSettings(_appData);
        var svc = new MigrationService(settings, _legacyRoot);
        svc.Import(svc.Detect());

        // Existing 2.0 agents.md should not have been clobbered.
        Assert.Equal("from-2.0", File.ReadAllText(Path.Combine(_appData, "agents.md")));
    }

    [Fact]
    public void MarkCompleted_PersistsViaSettings()
    {
        var settings = new FakeSettings(_appData);
        var svc = new MigrationService(settings, _legacyRoot);
        Assert.False(svc.MigrationCompleted);
        svc.MarkCompleted();
        Assert.True(svc.MigrationCompleted);
        Assert.True(settings.Current.MigrationCompleted);
        Assert.True(settings.Saved);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory { get; }
        public string SettingsFilePath { get; }
        public AppSettings Current { get; } = new();
        public bool Saved { get; private set; }
        public FakeSettings(string appData)
        {
            AppDataDirectory = appData;
            SettingsFilePath = Path.Combine(appData, "settings.json");
        }
        public void Load() { }
        public void Save() { Saved = true; }
    }
}
