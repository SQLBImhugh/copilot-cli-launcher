using System.Diagnostics;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class AISummaryServiceTests
{
    [Fact]
    public void Build_ProducesPromptContainingVersionsAndChangelog()
    {
        var prompt = AISummaryPromptBuilder.Build("1.0.0", "1.1.0", "Fixed bugs\nAdded features", null);

        Assert.Contains("1.0.0", prompt);
        Assert.Contains("1.1.0", prompt);
        Assert.Contains("Changelog:", prompt);
        Assert.Contains("Fixed bugs", prompt);
        Assert.Contains("Added features", prompt);
    }

    [Fact]
    public void Build_TruncatesLongChangelog()
    {
        var changelog = new string('x', AISummaryPromptBuilder.ChangelogLimit + 250);

        var prompt = AISummaryPromptBuilder.Build("1.0.0", "1.1.0", changelog, null);

        Assert.Contains("[truncated]", prompt);
    }

    [Fact]
    public void Build_AppendsRepoContextWhenProvided()
    {
        var prompt = AISummaryPromptBuilder.Build("1.0.0", "1.1.0", "Fixed bugs", "Launcher repo context");

        Assert.Contains("Repository context:", prompt);
        Assert.Contains("Launcher repo context", prompt);
    }

    [Fact]
    public void IsEnabled_ReflectsSettingsToggle()
    {
        var settings = new FakeSettings();
        var service = new AISummaryService(settings, new StubSessionDiscovery());

        Assert.False(service.IsEnabled);

        settings.Current.Briefings.AISummaryOnBump = true;
        Assert.True(service.IsEnabled);

        settings.Current.Briefings.AISummaryOnBump = false;
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public async Task TryReadRepositoryContextAsync_RejectsUnsupportedExtensions_FromEnabledBriefingContext()
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.AISummaryOnBump = true;
        var service = new AISummaryService(settings, new StubSessionDiscovery());
        var contextPath = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid() + ".exe");
        await File.WriteAllTextAsync(contextPath, "Launcher repo context");

        try
        {
            settings.Current.Briefings.AgentsContextFilePath = contextPath;

            Assert.True(service.IsEnabled);

            var repoContext = await AISummaryService.TryReadRepositoryContextAsync(settings.Current.Briefings.AgentsContextFilePath, CancellationToken.None);
            var prompt = AISummaryPromptBuilder.Build("1.0.0", "1.1.0", "Fixed bugs", repoContext);

            Assert.Null(repoContext);
            Assert.DoesNotContain("Repository context:", prompt);
        }
        finally
        {
            try { File.Delete(contextPath); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task GenerateAsync_OmitsSessionFlags_WhenBriefingSessionNameEmpty()
    {
        var (settings, psi) = await CaptureSpawnAsync(name => false, sessionName: null);

        Assert.NotNull(psi);
        Assert.DoesNotContain(psi!.ArgumentList, a => a == "--name" || a.StartsWith("--resume", StringComparison.Ordinal));
        // Original required args still present.
        Assert.Contains("-p", psi.ArgumentList);
        Assert.Contains("--no-color", psi.ArgumentList);
    }

    [Fact]
    public async Task GenerateAsync_UsesNameFlag_WhenSessionDoesNotExistYet()
    {
        var (settings, psi) = await CaptureSpawnAsync(
            sessionExists: _ => false,
            sessionName: "CopilotCLI-Update-Briefings");

        Assert.NotNull(psi);
        // --name + value as two separate args (matches `-n, --name <name>` form in copilot --help).
        var nameIdx = psi!.ArgumentList.IndexOf("--name");
        Assert.True(nameIdx >= 0, "--name flag missing");
        Assert.Equal("CopilotCLI-Update-Briefings", psi.ArgumentList[nameIdx + 1]);
        Assert.DoesNotContain(psi.ArgumentList, a => a.StartsWith("--resume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateAsync_UsesResumeEqualsFlag_WhenSessionAlreadyExists()
    {
        var (settings, psi) = await CaptureSpawnAsync(
            sessionExists: name => string.Equals(name, "CopilotCLI-Update-Briefings", StringComparison.Ordinal),
            sessionName: "CopilotCLI-Update-Briefings");

        Assert.NotNull(psi);
        // --resume uses equals form (matches `--resume[=value]` shape).
        Assert.Contains("--resume=CopilotCLI-Update-Briefings", psi!.ArgumentList);
        Assert.DoesNotContain("--name", psi.ArgumentList);
    }

    [Fact]
    public async Task GenerateAsync_TreatsWhitespaceSessionNameAsEmpty()
    {
        var (_, psi) = await CaptureSpawnAsync(_ => true, sessionName: "   ");

        Assert.NotNull(psi);
        Assert.DoesNotContain(psi!.ArgumentList, a => a == "--name" || a.StartsWith("--resume", StringComparison.Ordinal));
    }

    private static async Task<(FakeSettings settings, ProcessStartInfo? captured)> CaptureSpawnAsync(
        Func<string, bool> sessionExists,
        string? sessionName)
    {
        var settings = new FakeSettings();
        settings.Current.Briefings.AISummaryOnBump = true;
        settings.Current.Briefings.BriefingSessionName = sessionName;

        ProcessStartInfo? captured = null;
        // Inject a fake copilot resolver so the test passes regardless of
        // whether the host machine has copilot on PATH (CI runners don't).
        // Capture the PSI then return null — GenerateAsync's null-process
        // path returns null cleanly without actually spawning anything.
        var fakeTarget = new CopilotLauncher.Helpers.ProcessUtil.CopilotProcessTarget { FileName = "copilot-test.cmd" };
        var service = new AISummaryService(
            settings,
            psi => { captured = psi; return null; },
            sessionExists,
            _ => fakeTarget);
        await service.GenerateAsync("1.0.0", "1.1.0", "Fixed bugs\nAdded features");
        return (settings, captured);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; } = new();
        public void Load() { }
        public void Save() { }
    }

    private sealed class StubSessionDiscovery : ISessionDiscoveryService
    {
        public string SessionRoot => Path.GetTempPath();
        public IEnumerable<CopilotSession> Enumerate() => Array.Empty<CopilotSession>();
    }
}
