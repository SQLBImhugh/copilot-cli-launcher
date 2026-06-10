using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class ReleaseNotesServiceTests
{
    [Fact]
    public void ParseReleases_ExtractsTagBodyAndDate()
    {
        var json = """
        [
          { "tag_name": "v1.0.56", "body": "Fixed widget crash.", "published_at": "2026-05-29T15:30:00Z" },
          { "tag_name": "v1.0.55", "body": "Added foo.", "published_at": "2026-05-22T10:00:00Z" }
        ]
        """;
        var entries = ReleaseNotesService.ParseReleases(json);
        Assert.Equal(2, entries.Count);
        Assert.Equal("1.0.56", entries[0].Version);
        Assert.Equal("Fixed widget crash.", entries[0].Body);
        Assert.Equal(new DateTime(2026, 5, 29, 15, 30, 0, DateTimeKind.Utc), entries[0].Date!.Value.ToUniversalTime());
        Assert.Equal("1.0.55", entries[1].Version);
    }

    [Fact]
    public void ParseReleases_SkipsMalformedEntries()
    {
        var json = """
        [
          { "tag_name": "v1.0.56", "body": "ok", "published_at": "2026-05-29T15:30:00Z" },
          { "no_tag": "missing" },
          { "tag_name": "" },
          "string-instead-of-object",
          { "tag_name": "v1.0.55" }
        ]
        """;
        var entries = ReleaseNotesService.ParseReleases(json);
        // Entries without tag_name are skipped; entries without body/date are kept.
        Assert.Equal(2, entries.Count);
        Assert.Equal("1.0.56", entries[0].Version);
        Assert.Equal("1.0.55", entries[1].Version);
        Assert.Null(entries[1].Body);
        Assert.Null(entries[1].Date);
    }

    [Fact]
    public void ParseReleases_KeepsPreReleaseTagsWithFullTagName()
    {
        // v0.1.12: copilot CLI publishes daily pre-releases like "v1.0.57-3"
        // between stable cuts. When a user jumps from 1.0.56 (stable) to a
        // 1.0.57-N pre-release (no stable v1.0.57 yet), skipping pre-releases
        // produced an empty changelog and forced the AI summary to hallucinate
        // from session memory. v0.1.12 keeps them — preserving the full tag in
        // the Version field so the briefing displays e.g. "v1.0.57-3" rather
        // than masquerading as the stable cut.
        var json = """
        [
          { "tag_name": "v1.0.57-3", "body": "pre" },
          { "tag_name": "v1.0.57-0", "body": "pre" },
          { "tag_name": "v1.0.56", "body": "stable" },
          { "tag_name": "v1.0.56-2", "body": "pre" },
          { "tag_name": "v1.0.55", "body": "stable" }
        ]
        """;
        var entries = ReleaseNotesService.ParseReleases(json);
        Assert.Equal(5, entries.Count);
        Assert.Contains(entries, e => e.Version == "1.0.57-3");
        Assert.Contains(entries, e => e.Version == "1.0.56-2");
        Assert.Contains(entries, e => e.Version == "1.0.56");
    }

    [Fact]
    public void FilterRange_IncludesPreReleasesInRange_AndSortsBeforeStableOfSameBase()
    {
        // (1.0.56, 1.0.57] should include 1.0.57-0, -1, -2, -3 even when no
        // stable v1.0.57 exists yet. Ordering: stable beats all pre-releases
        // of the same base version (1.0.56 stable > all 1.0.56-N).
        var input = new List<ReleaseEntry>
        {
            new() { Version = "1.0.57-3" },
            new() { Version = "1.0.57-0" },
            new() { Version = "1.0.57-2" },
            new() { Version = "1.0.57-1" },
            new() { Version = "1.0.56" },     // boundary — excluded
            new() { Version = "1.0.56-2" },   // boundary pre — excluded
        };
        var filtered = ReleaseNotesService.FilterRange(input, "1.0.56", "1.0.57");
        Assert.Equal(4, filtered.Count);
        Assert.Equal("1.0.57-0", filtered[0].Version);
        Assert.Equal("1.0.57-1", filtered[1].Version);
        Assert.Equal("1.0.57-2", filtered[2].Version);
        Assert.Equal("1.0.57-3", filtered[3].Version);
    }

    [Fact]
    public void FilterRange_StableBeatsAllPreReleasesOfSameBase()
    {
        // Range (1.0.55, 1.0.56] picks 1.0.56-0, -1, -2, then stable 1.0.56.
        var input = new List<ReleaseEntry>
        {
            new() { Version = "1.0.56" },
            new() { Version = "1.0.56-1" },
            new() { Version = "1.0.56-0" },
            new() { Version = "1.0.56-2" },
        };
        var filtered = ReleaseNotesService.FilterRange(input, "1.0.55", "1.0.56");
        Assert.Equal(4, filtered.Count);
        Assert.Equal("1.0.56-0", filtered[0].Version);
        Assert.Equal("1.0.56-1", filtered[1].Version);
        Assert.Equal("1.0.56-2", filtered[2].Version);
        Assert.Equal("1.0.56", filtered[3].Version);
    }

    [Fact]
    public void TryParseSemVer_OrdersStableAfterItsPreReleases()
    {
        var preZero = ReleaseNotesService.TryParseSemVer("1.0.56-0")!.Value;
        var preTwo = ReleaseNotesService.TryParseSemVer("1.0.56-2")!.Value;
        var stable = ReleaseNotesService.TryParseSemVer("1.0.56")!.Value;
        var nextPre = ReleaseNotesService.TryParseSemVer("1.0.57-0")!.Value;

        Assert.True(preZero.CompareTo(preTwo) < 0);
        Assert.True(preTwo.CompareTo(stable) < 0);
        Assert.True(stable.CompareTo(nextPre) < 0);
        // v-prefix tolerant.
        Assert.Equal(stable, ReleaseNotesService.TryParseSemVer("v1.0.56")!.Value);
    }

    [Fact]
    public void FilterRange_AppliesHalfOpenSemVerWindow_AndSortsOldestFirst()
    {
        var input = new List<ReleaseEntry>
        {
            new() { Version = "1.0.56", Date = new DateTime(2026, 5, 29) },
            new() { Version = "1.0.50", Date = new DateTime(2026, 4, 1) },
            new() { Version = "1.0.49", Date = new DateTime(2026, 3, 25) },  // boundary — excluded
            new() { Version = "1.0.55", Date = new DateTime(2026, 5, 22) },
            new() { Version = "1.0.48", Date = new DateTime(2026, 3, 18) },  // pre-window
            new() { Version = "1.0.57", Date = new DateTime(2026, 6, 1) },   // post-window
        };
        var filtered = ReleaseNotesService.FilterRange(input, "1.0.49", "1.0.56");
        // (1.0.49, 1.0.56] -> 1.0.50, 1.0.55, 1.0.56 in chronological order.
        Assert.Equal(3, filtered.Count);
        Assert.Equal("1.0.50", filtered[0].Version);
        Assert.Equal("1.0.55", filtered[1].Version);
        Assert.Equal("1.0.56", filtered[2].Version);
    }

    [Fact]
    public void FilterRange_TolerantOfInvalidSemVerEntries()
    {
        var input = new List<ReleaseEntry>
        {
            new() { Version = "1.0.50" },
            new() { Version = "not-a-version" },
            new() { Version = "" },
            new() { Version = "1.0.56" },
        };
        var filtered = ReleaseNotesService.FilterRange(input, "1.0.49", "1.0.56");
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void BuildChangelogText_RendersOnePerVersion_WithDate()
    {
        var entries = new List<ReleaseEntry>
        {
            new() { Version = "1.0.50", Date = new DateTime(2026, 4, 1), Body = "- Added foo\n- Fixed bar" },
            new() { Version = "1.0.56", Date = new DateTime(2026, 5, 29), Body = "- Critical fix" },
        };
        var text = ReleaseNotesService.BuildChangelogText(entries);
        Assert.Contains("## v1.0.50", text);
        Assert.Contains("2026-04-01", text);
        Assert.Contains("- Added foo", text);
        Assert.Contains("## v1.0.56", text);
        Assert.Contains("2026-05-29", text);
        Assert.Contains("- Critical fix", text);
    }

    [Fact]
    public void BuildChangelogText_EmptyInput_ReturnsEmptyString()
    {
        var text = ReleaseNotesService.BuildChangelogText(Array.Empty<ReleaseEntry>());
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public async Task FetchAsync_UsesFetchedJsonAndFiltersRange()
    {
        var json = """
        [
          { "tag_name": "v1.0.56", "body": "Fixed widget crash.", "published_at": "2026-05-29T15:30:00Z" },
          { "tag_name": "v1.0.55", "body": "Added foo.", "published_at": "2026-05-22T10:00:00Z" },
          { "tag_name": "v1.0.49", "body": "Older.", "published_at": "2026-03-25T10:00:00Z" }
        ]
        """;
        var settings = new FakeSettings();
        // Point cache dir at a fresh per-test temp folder so we don't read a stale cache.
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settings.AppDataDirectory);
        var svc = new ReleaseNotesService(settings, _ => Task.FromResult<string?>(json));
        var entries = await svc.FetchAsync("1.0.49", "1.0.56");
        Assert.Equal(2, entries.Count);
        Assert.Equal("1.0.55", entries[0].Version);
        Assert.Equal("1.0.56", entries[1].Version);
        // Side-effect: cache file written for subsequent calls.
        Assert.True(File.Exists(Path.Combine(settings.AppDataDirectory, "state", "releases-cache.json")));
    }

    [Fact]
    public async Task FetchAsync_ReturnsEmpty_OnFetchFailure()
    {
        var settings = new FakeSettings();
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settings.AppDataDirectory);
        var svc = new ReleaseNotesService(settings, _ => Task.FromResult<string?>(null));
        var entries = await svc.FetchAsync("1.0.49", "1.0.56");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_DoesNotThrow_OnMalformedJson()
    {
        var settings = new FakeSettings();
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settings.AppDataDirectory);
        var svc = new ReleaseNotesService(settings, _ => Task.FromResult<string?>("not json at all"));
        var entries = await svc.FetchAsync("1.0.49", "1.0.56");
        Assert.Empty(entries);
    }

    [Fact]
    public async Task FetchAsync_UsesCache_WhenFresh()
    {
        var settings = new FakeSettings();
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(settings.AppDataDirectory, "state"));
        var cacheFile = Path.Combine(settings.AppDataDirectory, "state", "releases-cache.json");
        var cachedJson = """[{ "tag_name": "v1.0.56", "body": "from cache" }]""";
        await File.WriteAllTextAsync(cacheFile, cachedJson);

        var fetchCalled = false;
        var svc = new ReleaseNotesService(settings, _ =>
        {
            fetchCalled = true;
            return Task.FromResult<string?>("""[{ "tag_name": "v1.0.57", "body": "from network" }]""");
        });
        var entries = await svc.FetchAsync("1.0.50", "1.0.56");
        Assert.False(fetchCalled);  // cache was fresh, no network hit
        Assert.Single(entries);
        Assert.Equal("1.0.56", entries[0].Version);
        Assert.Equal("from cache", entries[0].Body);
    }

    [Fact]
    public async Task FetchAsync_FreshCacheContainingToVersion_DoesNotForceRefresh()
    {
        var settings = new FakeSettings();
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(settings.AppDataDirectory, "state"));
        var cacheFile = Path.Combine(settings.AppDataDirectory, "state", "releases-cache.json");
        await File.WriteAllTextAsync(cacheFile, """[{ "tag_name": "v1.0.56", "body": "from cache" }]""");

        var fetchCount = 0;
        var svc = new ReleaseNotesService(settings, _ =>
        {
            fetchCount++;
            return Task.FromResult<string?>("""[{ "tag_name": "v1.0.57", "body": "from network" }]""");
        });

        var entries = await svc.FetchAsync("1.0.55", "1.0.56");

        Assert.Equal(0, fetchCount);
        Assert.Single(entries);
        Assert.Equal("1.0.56", entries[0].Version);
        Assert.Equal("from cache", entries[0].Body);
    }

    [Fact]
    public async Task FetchAsync_FreshCacheMissingToVersion_ForcesOneRefreshAndUsesFreshData()
    {
        var settings = new FakeSettings();
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(settings.AppDataDirectory, "state"));
        var cacheFile = Path.Combine(settings.AppDataDirectory, "state", "releases-cache.json");
        await File.WriteAllTextAsync(cacheFile, """[{ "tag_name": "v1.0.55", "body": "old cache" }]""");

        var fetchCount = 0;
        var svc = new ReleaseNotesService(settings, _ =>
        {
            fetchCount++;
            return Task.FromResult<string?>("""
            [
              { "tag_name": "v1.0.56", "body": "fresh notes" },
              { "tag_name": "v1.0.55", "body": "old cache" }
            ]
            """);
        });

        var entries = await svc.FetchAsync("1.0.55", "1.0.56");

        Assert.Equal(1, fetchCount);
        Assert.Single(entries);
        Assert.Equal("1.0.56", entries[0].Version);
        Assert.Equal("fresh notes", entries[0].Body);
    }

    [Fact]
    public async Task FetchAsync_FreshCacheMissingToVersion_WhenForcedFetchReturnsNull_FallsBackToCache()
    {
        var settings = new FakeSettings();
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(settings.AppDataDirectory, "state"));
        var cacheFile = Path.Combine(settings.AppDataDirectory, "state", "releases-cache.json");
        await File.WriteAllTextAsync(cacheFile, """[{ "tag_name": "v1.0.55", "body": "old cache" }]""");

        var fetchCount = 0;
        var svc = new ReleaseNotesService(settings, _ =>
        {
            fetchCount++;
            return Task.FromResult<string?>(null);
        });

        var entries = await svc.FetchAsync("1.0.54", "1.0.56");

        Assert.Equal(1, fetchCount);
        Assert.Single(entries);
        Assert.Equal("1.0.55", entries[0].Version);
        Assert.Equal("old cache", entries[0].Body);
    }

    [Fact]
    public async Task FetchAsync_FallsBackToStaleCache_WhenFreshFetchFails()
    {
        // v0.1.12: when the cache is past TTL and the fresh fetch returns
        // null (network outage / rate limit / DNS hiccup), return the stale
        // cache rather than empty. Stale release notes are infinitely better
        // than zero — empty changelog forces the AI summary to hallucinate
        // from session memory.
        var settings = new FakeSettings();
        settings.AppDataDirectory = Path.Combine(Path.GetTempPath(), "ccl-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(settings.AppDataDirectory, "state"));
        var cacheFile = Path.Combine(settings.AppDataDirectory, "state", "releases-cache.json");
        var staleJson = """[{ "tag_name": "v1.0.56", "body": "from stale cache" }]""";
        await File.WriteAllTextAsync(cacheFile, staleJson);
        // Backdate so the cache appears past the 1-hour TTL.
        File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow.AddHours(-25));

        var svc = new ReleaseNotesService(settings, _ => Task.FromResult<string?>(null));
        var entries = await svc.FetchAsync("1.0.55", "1.0.99");
        // Stale cache content was returned and parsed despite TTL expiry.
        Assert.Single(entries);
        Assert.Equal("1.0.56", entries[0].Version);
        Assert.Equal("from stale cache", entries[0].Body);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory { get; set; } = Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");
        public AppSettings Current { get; set; } = new();
        public void Load() { }
        public void Save() { }
    }
}
