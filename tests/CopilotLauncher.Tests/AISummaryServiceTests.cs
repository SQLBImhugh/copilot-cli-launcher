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
    public void Build_TruncatesLongRepoContextWithMarker()
    {
        var repoContext = new string('y', AISummaryPromptBuilder.RepositoryContextLimit + 500);

        var prompt = AISummaryPromptBuilder.Build("1.0.0", "1.1.0", "Fixed bugs", repoContext);

        Assert.Contains("Repository context:", prompt);
        Assert.Contains("[truncated]", prompt);
        // Truncated content must be present but not the full original length.
        Assert.True(prompt.Length < repoContext.Length, "prompt should be shorter than oversize repo-context input");
    }

    [Fact]
    public async Task TryReadRepositoryContextAsync_ReturnsContent_EvenForOversizeFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid() + ".md");
        var oversize = new string('z', AISummaryPromptBuilder.RepositoryContextLimit + 1000);
        await File.WriteAllTextAsync(path, oversize);

        try
        {
            // Pre-fix behavior: silently returned null for files > limit.
            // Post-fix: returns the raw text; Build() handles truncation.
            var read = await AISummaryService.TryReadRepositoryContextAsync(path, CancellationToken.None);
            Assert.NotNull(read);
            Assert.Equal(oversize.Length, read!.Length);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
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
        // Non-interactive mode requirements for copilot 1.0.49+.
        Assert.Contains("--allow-all-tools", psi.ArgumentList);
        var fmtIdx = psi.ArgumentList.IndexOf("--output-format");
        Assert.True(fmtIdx >= 0, "--output-format missing");
        Assert.Equal("json", psi.ArgumentList[fmtIdx + 1]);
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

    [Fact]
    public void ExtractAssistantText_PullsContentFromFinalAssistantMessage()
    {
        // Two assistant.message events: the first is a tool-call turn with
        // empty content, the second contains the real summary text. The
        // extractor must concatenate only non-empty content fields.
        var jsonl = string.Join('\n', new[]
        {
            "{\"type\":\"session.info\",\"data\":{\"sessionId\":\"abc\"}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"\",\"toolRequests\":[{\"name\":\"web\"}]}}",
            "{\"type\":\"tool.execution_complete\",\"data\":{\"toolCallId\":\"x\"}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"Here are the changes:\\n- bullet one\\n- bullet two\",\"toolRequests\":[]}}",
            "{\"type\":\"result\",\"data\":{\"exitCode\":0}}",
        });

        var text = AISummaryService.ExtractAssistantTextFromJsonl(jsonl);

        Assert.Equal("Here are the changes:\n- bullet one\n- bullet two", text);
    }

    [Fact]
    public void ExtractAssistantText_ConcatenatesMultipleAssistantTextTurns()
    {
        var jsonl = string.Join('\n', new[]
        {
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"First half.\",\"toolRequests\":[]}}",
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"Second half.\",\"toolRequests\":[]}}",
        });

        var text = AISummaryService.ExtractAssistantTextFromJsonl(jsonl);

        Assert.Equal($"First half.{Environment.NewLine}Second half.", text);
    }

    [Fact]
    public void ExtractAssistantText_ReturnsEmpty_OnEmptyOrToolOnlyStreams()
    {
        Assert.Equal(string.Empty, AISummaryService.ExtractAssistantTextFromJsonl(""));
        Assert.Equal(string.Empty, AISummaryService.ExtractAssistantTextFromJsonl("   \n\n  "));

        // Only tool-call assistant messages (content empty) -> nothing extractable.
        var toolOnly = "{\"type\":\"assistant.message\",\"data\":{\"content\":\"\",\"toolRequests\":[{\"name\":\"x\"}]}}";
        Assert.Equal(string.Empty, AISummaryService.ExtractAssistantTextFromJsonl(toolOnly));
    }

    [Fact]
    public void ExtractAssistantText_IgnoresMalformedAndNonJsonLines()
    {
        var jsonl = string.Join('\n', new[]
        {
            "● Extension loaded",
            "not json at all",
            "{\"type\":\"assistant.reasoning\",\"data\":{\"content\":\"thinking...\"}}",  // wrong event type
            "{\"type\":\"assistant.message\",\"data\":{\"content\":\"Real reply.\"}}",
            "{broken json",
        });

        var text = AISummaryService.ExtractAssistantTextFromJsonl(jsonl);

        Assert.Equal("Real reply.", text);
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
