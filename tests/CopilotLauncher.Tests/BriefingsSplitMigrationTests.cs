using System.Text.Json;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class BriefingsSplitMigrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _briefingsPath;
    private readonly string _changelogsPath;

    public BriefingsSplitMigrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "copilot-launcher-split-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _briefingsPath = Path.Combine(_tmpDir, "briefings.json");
        _changelogsPath = Path.Combine(_tmpDir, "changelogs.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void SplitBody_ParsesRealV01Body_ExtractingBothHalves()
    {
        // This is the exact format BriefingViewModel.ForceCheckAsync used in
        // v0.1.x: "## AI Summary\n\n{summary}\n\n---\n\n{changelog}".
        var body = "## AI Summary\n\nHighlights for project X:\n- Cool fix\n- Faster startup\n\n---\n\n# v1.0.49\n\nFixed bug.\nRaw output:\nUpdated 1.0.48 -> 1.0.49";

        var (ai, cl) = BriefingsSplitMigration.SplitBody(body);

        Assert.NotNull(ai);
        Assert.StartsWith("Highlights for project X", ai);
        Assert.Contains("Faster startup", ai);
        Assert.DoesNotContain("Raw output", ai);

        Assert.NotNull(cl);
        Assert.StartsWith("# v1.0.49", cl);
        Assert.Contains("Raw output", cl);
    }

    [Fact]
    public void SplitBody_ReturnsChangelogOnly_WhenNoAISummaryHeader()
    {
        // Pre-AI v0.1.x entries (or AISummaryOnBump=false) had no header.
        var body = "# v1.0.5\n\nFixed bugs.\nRaw output:\nUpdated 1.0.4 -> 1.0.5";

        var (ai, cl) = BriefingsSplitMigration.SplitBody(body);

        Assert.Null(ai);
        Assert.NotNull(cl);
        Assert.StartsWith("# v1.0.5", cl);
    }

    [Fact]
    public void SplitBody_SplitsOnFirstSeparator_PreservingFollowingDividers()
    {
        // Real changelogs sometimes contain markdown "---" rules inside the
        // bundled body. Only the first separator should split AI from changelog.
        var body = "## AI Summary\n\nbrief\n\n---\n\n# v2\n\nfix1\n\n---\n\nmore stuff";

        var (ai, cl) = BriefingsSplitMigration.SplitBody(body);

        Assert.Equal("brief", ai);
        Assert.Contains("# v2", cl);
        Assert.Contains("---", cl);
        Assert.Contains("more stuff", cl);
    }

    [Fact]
    public void SplitBody_HandlesEmptyBody()
    {
        var (ai, cl) = BriefingsSplitMigration.SplitBody("");
        Assert.Null(ai);
        Assert.Null(cl);
    }

    [Fact]
    public void SplitBody_HandlesMalformedHeader_NoSeparator()
    {
        // "## AI Summary" present but no separator — treat the rest as AI.
        var body = "## AI Summary\n\nLooks like nothing else here.";

        var (ai, cl) = BriefingsSplitMigration.SplitBody(body);

        Assert.Equal("Looks like nothing else here.", ai);
        Assert.Null(cl);
    }

    [Fact]
    public void Migrate_SplitsLegacyBriefings_IntoTwoFiles()
    {
        WriteLegacyBriefings(new[]
        {
            MakeLegacy("1.0.0", "1.0.1", "## AI Summary\n\nA fix here\n\n---\n\n# 1.0.1\n\nBundled notes"),
            MakeLegacy("1.0.1", "1.0.2", "# 1.0.2\n\nNo AI for this one"),  // no header
        });

        var mig = NewMigration();
        var status = mig.Migrate();

        Assert.Contains("Migrated 2 legacy briefing", status);

        // changelogs.json: both entries should appear (1 with AI + changelog, 1 changelog-only)
        var changelogs = ReadChangelogs();
        Assert.Equal(2, changelogs.Count);
        Assert.All(changelogs, e => Assert.Equal("migrated", e.Source));

        // briefings.json: only the one with an AI summary survives
        var briefings = ReadBriefings();
        Assert.Single(briefings);
        Assert.Equal("ai-summary", briefings[0].Source);
        Assert.Equal("A fix here", briefings[0].Body);

        // Backup of original briefings.json should exist
        Assert.True(File.Exists(_briefingsPath + ".v01-backup"));
    }

    [Fact]
    public void Migrate_IsIdempotent_WhenChangelogsAlreadyExists()
    {
        // Pre-existing changelogs.json means we already migrated.
        File.WriteAllText(_changelogsPath, "[]");
        WriteLegacyBriefings(new[]
        {
            MakeLegacy("1.0.0", "1.0.1", "## AI Summary\n\nA\n\n---\n\nB"),
        });

        var status = NewMigration().Migrate();

        Assert.StartsWith("Skipped: changelogs.json already exists", status);
        // Briefings file untouched
        var briefings = ReadBriefings();
        Assert.Single(briefings);
        Assert.False(File.Exists(_briefingsPath + ".v01-backup"));
    }

    [Fact]
    public void Migrate_NoOp_WhenNoBriefingsFile()
    {
        var status = NewMigration().Migrate();
        Assert.StartsWith("Skipped: no briefings.json to migrate", status);
        Assert.False(File.Exists(_changelogsPath));
    }

    [Fact]
    public void Migrate_NoOp_WhenBriefingsFileIsEmpty()
    {
        File.WriteAllText(_briefingsPath, "[]");
        var status = NewMigration().Migrate();
        Assert.StartsWith("Skipped: briefings.json is empty", status);
        Assert.False(File.Exists(_changelogsPath));
    }

    private BriefingsSplitMigration NewMigration()
    {
        var ctor = typeof(BriefingsSplitMigration)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string), typeof(string) }, null);
        return (BriefingsSplitMigration)ctor!.Invoke(new object[] { _briefingsPath, _changelogsPath });
    }

    private void WriteLegacyBriefings(IEnumerable<BriefingEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(_briefingsPath, json);
    }

    private List<ChangelogEntry> ReadChangelogs()
    {
        var json = File.ReadAllText(_changelogsPath);
        return JsonSerializer.Deserialize<List<ChangelogEntry>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) ?? new();
    }

    private List<BriefingEntry> ReadBriefings()
    {
        var json = File.ReadAllText(_briefingsPath);
        return JsonSerializer.Deserialize<List<BriefingEntry>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) ?? new();
    }

    private static BriefingEntry MakeLegacy(string from, string to, string body) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow,
        FromVersion = from,
        ToVersion = to,
        Source = "copilot-update",
        Body = body,
    };
}
