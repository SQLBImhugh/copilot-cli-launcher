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
        var service = new AISummaryService(settings);

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
        var service = new AISummaryService(settings);
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

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; } = new();
        public void Load() { }
        public void Save() { }
    }
}
