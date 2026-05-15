using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class BriefingHistoryServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _filePath;

    public BriefingHistoryServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "copilot-launcher-briefings-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _filePath = Path.Combine(_tmpDir, "briefings.json");
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
        // Newest 50 retained → versions ToVersion 11..60
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
        Assert.Empty(NewSvc().All);  // confirm cleared on disk too
    }

    [Fact]
    public void Reload_RecoversFromCorruptJson_WithBackup()
    {
        File.WriteAllText(_filePath, "this is not valid json");
        var svc = NewSvc();
        Assert.Empty(svc.All);
        Assert.NotEmpty(Directory.GetFiles(_tmpDir, "briefings.json.corrupt-*"));
    }

    private BriefingHistoryService NewSvc()
    {
        var ctor = typeof(BriefingHistoryService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string) }, null);
        return (BriefingHistoryService)ctor!.Invoke(new object[] { _filePath });
    }

    private static BriefingEntry MakeEntry(string from, string to, DateTime? when = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Timestamp = when ?? DateTime.UtcNow,
        FromVersion = from,
        ToVersion = to,
        Source = "test",
        Body = $"# Updated {from} → {to}",
    };
}
