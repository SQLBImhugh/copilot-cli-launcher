using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class ChangelogHistoryServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _filePath;

    public ChangelogHistoryServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "copilot-launcher-changelogs-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _filePath = Path.Combine(_tmpDir, "changelogs.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void All_IsEmpty_OnFreshInstance() => Assert.Empty(NewSvc().All);

    [Fact]
    public void Add_PersistsAndCanReload()
    {
        var s1 = NewSvc();
        s1.Add(MakeEntry("1.0.0", "1.0.1"));
        Assert.Single(s1.All);

        var s2 = NewSvc();
        Assert.Single(s2.All);
        Assert.Equal("1.0.1", s2.All[0].ToVersion);
        Assert.Equal("copilot-update", s2.All[0].Source);
    }

    [Fact]
    public void Add_SortsNewestFirst()
    {
        var svc = NewSvc();
        svc.Add(MakeEntry("1.0.0", "1.0.1", DateTime.UtcNow.AddHours(-2)));
        svc.Add(MakeEntry("1.0.1", "1.0.2", DateTime.UtcNow.AddHours(-1)));
        svc.Add(MakeEntry("1.0.2", "1.0.3", DateTime.UtcNow));
        Assert.Equal(new[] { "1.0.3", "1.0.2", "1.0.1" }, svc.All.Select(e => e.ToVersion));
    }

    [Fact]
    public void Add_CapsAt50_RotatingOldest()
    {
        var svc = NewSvc();
        var baseTime = DateTime.UtcNow;
        for (int i = 0; i < 60; i++)
            svc.Add(MakeEntry($"1.0.{i}", $"1.0.{i + 1}", baseTime.AddSeconds(i)));
        Assert.Equal(50, svc.All.Count);
        Assert.Equal("1.0.60", svc.All[0].ToVersion);
        Assert.Equal("1.0.11", svc.All[^1].ToVersion);
    }

    [Fact]
    public void Clear_EmptiesAndPersists()
    {
        var svc = NewSvc();
        svc.Add(MakeEntry("1.0.0", "1.0.1"));
        svc.Clear();
        Assert.Empty(svc.All);
        Assert.Empty(NewSvc().All);
    }

    [Fact]
    public void Replace_UpdatesExistingEntryAndPersists()
    {
        var svc = NewSvc();
        var entry = MakeEntry("1.0.0", "1.0.1");
        svc.Add(entry);

        svc.Replace(new ChangelogEntry
        {
            Id = entry.Id,
            Timestamp = entry.Timestamp,
            FromVersion = entry.FromVersion,
            ToVersion = entry.ToVersion,
            Source = entry.Source,
            Body = "updated body",
        });

        Assert.Equal("updated body", svc.All[0].Body);
        Assert.Equal("updated body", NewSvc().All[0].Body);
    }

    [Fact]
    public void Reload_RecoversFromCorruptJson_WithBackup()
    {
        File.WriteAllText(_filePath, "this is not valid json");
        var svc = NewSvc();
        Assert.Empty(svc.All);
        Assert.NotEmpty(Directory.GetFiles(_tmpDir, "changelogs.json.corrupt-*"));
    }

    [Fact]
    public void Add_WritesAtomically_LeavingNoTmpFileOnSuccess()
    {
        var svc = NewSvc();
        svc.Add(MakeEntry("1.0.0", "1.0.1"));
        Assert.False(File.Exists(_filePath + ".tmp"),
            "Atomic-write tmp file must not linger after a successful Add.");
    }

    private ChangelogHistoryService NewSvc()
    {
        var ctor = typeof(ChangelogHistoryService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string) }, null);
        return (ChangelogHistoryService)ctor!.Invoke(new object[] { _filePath });
    }

    private static ChangelogEntry MakeEntry(string from, string to, DateTime? when = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Timestamp = when ?? DateTime.UtcNow,
        FromVersion = from,
        ToVersion = to,
        Source = "copilot-update",
        Body = $"# Updated {from} → {to}",
    };
}
