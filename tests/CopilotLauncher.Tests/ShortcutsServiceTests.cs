using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class ShortcutsServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _filePath;

    public ShortcutsServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "copilot-launcher-saved-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpDir);
        _filePath = Path.Combine(_tmpDir, "shortcuts.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void All_IsEmpty_OnFreshInstance()
    {
        var svc = NewSvc();
        Assert.Empty(svc.All);
    }

    [Fact]
    public void Add_PersistsToDisk()
    {
        var svc = NewSvc();
        svc.Add(NewLaunch("alpha"));
        Assert.True(File.Exists(_filePath));
        Assert.Single(svc.All);
        Assert.Equal("alpha", svc.All[0].Label);
    }

    [Fact]
    public void Reload_PicksUp_ExternallyWrittenFile()
    {
        var svc = NewSvc();
        svc.Add(NewLaunch("first"));

        // Simulate another instance writing.
        File.WriteAllText(_filePath, """[{"id":"x","label":"second","workingDirectory":"C:\\b","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"}]""");
        svc.Reload();
        Assert.Single(svc.All);
        Assert.Equal("second", svc.All[0].Label);
    }

    [Fact]
    public void Update_ReplacesExistingById()
    {
        var svc = NewSvc();
        var launch = NewLaunch("alpha");
        svc.Add(launch);
        launch.Label = "alpha-renamed";
        svc.Update(launch);
        Assert.Equal("alpha-renamed", svc.All[0].Label);
    }

    [Fact]
    public void Update_ThrowsOnUnknownId()
    {
        var svc = NewSvc();
        Assert.Throws<KeyNotFoundException>(() => svc.Update(NewLaunch("unknown")));
    }

    [Fact]
    public void Remove_DeletesById()
    {
        var svc = NewSvc();
        var a = NewLaunch("a");
        var b = NewLaunch("b");
        svc.Add(a);
        svc.Add(b);
        svc.Remove(a.Id);
        Assert.Single(svc.All);
        Assert.Equal("b", svc.All[0].Label);
    }

    [Fact]
    public void GetById_ReturnsMatch()
    {
        var svc = NewSvc();
        var a = NewLaunch("a");
        svc.Add(a);
        Assert.Equal(a, svc.GetById(a.Id));
        Assert.Null(svc.GetById("nope"));
    }

    [Fact]
    public void Reload_RecoversFromCorruptJson()
    {
        File.WriteAllText(_filePath, "this is not valid json");
        var svc = NewSvc();
        Assert.Empty(svc.All);
        // A backup file should exist.
        Assert.NotEmpty(Directory.GetFiles(_tmpDir, "shortcuts.json.corrupt-*"));
    }

    [Fact]
    public void Reload_PreservesData_OnIoException()
    {
        // Setup: create a service with valid data loaded.
        var svc = NewSvc();
        svc.Add(new Shortcut { Id = "x", Label = "important", WorkingDirectory = @"C:\proj" });
        Assert.Single(svc.All);

        // Now corrupt-file path: open the underlying file with FileShare.None
        // to simulate the data file being locked by another app. Reload should
        // throw rather than silently wipe in-memory state and corrupt-backup.
        using (var lockHandle = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => svc.Reload());
        }
        // After IO error: in-memory data should still be intact AND no
        // bogus .corrupt-* backup should have been created (that backup is
        // only for actually-corrupt JSON).
        Assert.Single(svc.All);
        Assert.Equal("important", svc.All[0].Label);
        Assert.Empty(Directory.GetFiles(_tmpDir, "shortcuts.json.corrupt-*"));
    }

    private ShortcutsService NewSvc()
    {
        var ctor = typeof(ShortcutsService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string) }, null);
        return (ShortcutsService)ctor!.Invoke(new object[] { _filePath });
    }

    private static Shortcut NewLaunch(string label) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Label = label,
        WorkingDirectory = @"C:\anywhere",
    };
}
