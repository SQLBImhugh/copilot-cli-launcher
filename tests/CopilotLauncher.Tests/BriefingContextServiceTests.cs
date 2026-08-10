using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class BriefingContextServiceTests : IDisposable
{
    private readonly string _appData;
    private readonly FakeSettings _settings;

    public BriefingContextServiceTests()
    {
        _appData = Path.Combine(Path.GetTempPath(), "copilot-launcher-briefctx-" + Guid.NewGuid());
        Directory.CreateDirectory(_appData);
        _settings = new FakeSettings(_appData);
    }

    public void Dispose()
    {
        try { Directory.Delete(_appData, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void ResolvePath_UsesConfiguredPath_WhenSet()
    {
        var custom = Path.Combine(_appData, "my-context.md");
        _settings.Current.Briefings.AgentsContextFilePath = custom;

        Assert.Equal(custom, new BriefingContextService(_settings).ResolvePath());
    }

    [Fact]
    public void ResolvePath_FallsBackToAppDataAgentsMd_WhenUnset()
    {
        _settings.Current.Briefings.AgentsContextFilePath = null;

        var resolved = new BriefingContextService(_settings).ResolvePath();

        Assert.Equal(Path.Combine(_appData, "AGENTS.md"), resolved);
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_WhenFileMissing()
    {
        _settings.Current.Briefings.AgentsContextFilePath = Path.Combine(_appData, "nope.md");

        Assert.Equal(string.Empty, await new BriefingContextService(_settings).ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_ReturnsExistingContent()
    {
        var path = Path.Combine(_appData, "ctx.md");
        await File.WriteAllTextAsync(path, "# FabricPOCPortal\nProject context here.");
        _settings.Current.Briefings.AgentsContextFilePath = path;

        var read = await new BriefingContextService(_settings).ReadAsync();

        Assert.Contains("FabricPOCPortal", read);
    }

    [Fact]
    public async Task WriteAsync_CreatesFile_AndPersistsPath_WhenUnset()
    {
        _settings.Current.Briefings.AgentsContextFilePath = null;
        var svc = new BriefingContextService(_settings);

        await svc.WriteAsync("# My project\nDetails.");

        var expected = Path.Combine(_appData, "AGENTS.md");
        Assert.True(File.Exists(expected));
        Assert.Contains("My project", await File.ReadAllTextAsync(expected));
        // Path must be persisted or AISummaryService would never read it back.
        Assert.Equal(expected, _settings.Current.Briefings.AgentsContextFilePath);
        Assert.True(_settings.SaveCount > 0);
    }

    [Fact]
    public async Task WriteAsync_BacksUpPriorContents()
    {
        var path = Path.Combine(_appData, "ctx.md");
        await File.WriteAllTextAsync(path, "ORIGINAL");
        _settings.Current.Briefings.AgentsContextFilePath = path;

        await new BriefingContextService(_settings).WriteAsync("REPLACED");

        Assert.Equal("REPLACED", await File.ReadAllTextAsync(path));
        var backups = Directory.GetFiles(_appData, "ctx.md.bak-*");
        Assert.Single(backups);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(backups[0]));
    }

    [Fact]
    public async Task WriteAsync_RoundTripsThroughReadAsync()
    {
        _settings.Current.Briefings.AgentsContextFilePath = Path.Combine(_appData, "rt.md");
        var svc = new BriefingContextService(_settings);
        var content = "# Heading\n\n- bullet\n- another\n";

        await svc.WriteAsync(content);

        Assert.Equal(content, await svc.ReadAsync());
    }

    [Fact]
    public async Task WriteAsync_DoesNotOverwriteConfiguredPath_WhenAlreadySet()
    {
        var path = Path.Combine(_appData, "explicit.md");
        _settings.Current.Briefings.AgentsContextFilePath = path;

        await new BriefingContextService(_settings).WriteAsync("x");

        Assert.Equal(path, _settings.Current.Briefings.AgentsContextFilePath);
    }

    // ─── Shrink guard (regression: first-line truncation wiped a 5 KB file) ──

    [Fact]
    public void IsSuspiciousShrink_FlagsFirstLineTruncationOfRealFile()
    {
        // Exactly the incident: a 5 KB hand-authored context file reduced to its
        // first heading line by a TextBox that wasn't in multi-line mode.
        var original = "# FabricPOCPortal-Briefings — Persistent Instructions\n"
                     + new string('x', 5000);
        var truncated = "# FabricPOCPortal-Briefings — Persistent Instructions";

        Assert.True(BriefingContextService.IsSuspiciousShrink(original, truncated));
    }

    [Fact]
    public void IsSuspiciousShrink_FlagsClearingASubstantialFile()
    {
        var original = new string('x', 1000);

        Assert.True(BriefingContextService.IsSuspiciousShrink(original, string.Empty));
        Assert.True(BriefingContextService.IsSuspiciousShrink(original, null));
    }

    [Fact]
    public void IsSuspiciousShrink_AllowsNormalEditsAndGrowth()
    {
        var original = new string('x', 1000);

        Assert.False(BriefingContextService.IsSuspiciousShrink(original, new string('x', 900)));   // trim
        Assert.False(BriefingContextService.IsSuspiciousShrink(original, new string('x', 501)));   // just over half
        Assert.False(BriefingContextService.IsSuspiciousShrink(original, new string('x', 4000)));  // growth
        Assert.False(BriefingContextService.IsSuspiciousShrink(original, original));               // unchanged
    }

    [Fact]
    public void IsSuspiciousShrink_IgnoresSmallOriginals()
    {
        // Starting from empty/near-empty is normal authoring, not data loss.
        Assert.False(BriefingContextService.IsSuspiciousShrink(null, "hello"));
        Assert.False(BriefingContextService.IsSuspiciousShrink(string.Empty, string.Empty));
        Assert.False(BriefingContextService.IsSuspiciousShrink(new string('x', 199), string.Empty));
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(string appData) => AppDataDirectory = appData;
        public string AppDataDirectory { get; }
        public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");
        public AppSettings Current { get; } = new();
        public int SaveCount { get; private set; }
        public void Load() { }
        public void Save() => SaveCount++;
    }
}
