using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class SessionDeletionServiceTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;
    private readonly SessionDeletionService _svc;

    public SessionDeletionServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        _root = Path.Combine(_tmp, "session-state");
        Directory.CreateDirectory(_root);
        _svc = new SessionDeletionService(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch { /* best effort */ }
    }

    private string MakeSession(string id, long bytes = 100, bool locked = false)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: C:\\x");
        File.WriteAllBytes(Path.Combine(dir, "events.jsonl"), new byte[bytes]);
        if (locked) File.WriteAllText(Path.Combine(dir, "inuse.1234.lock"), "");
        return dir;
    }

    [Fact]
    public void Delete_RemovesTheFolderAndReportsBytes()
    {
        var dir = MakeSession("aaa", bytes: 500);

        var result = _svc.Delete("aaa");

        Assert.True(result.Deleted);
        Assert.Null(result.Error);
        Assert.True(result.BytesFreed >= 500);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Delete_RefusesLockedSessions()
    {
        var dir = MakeSession("locked", locked: true);

        var result = _svc.Delete("locked");

        Assert.False(result.Deleted);
        Assert.Contains("in use", result.Error);
        Assert.True(Directory.Exists(dir));   // untouched
    }

    [Fact]
    public void Delete_MissingSession_IsReportedNotThrown()
    {
        var result = _svc.Delete("does-not-exist");
        Assert.False(result.Deleted);
        Assert.Contains("no longer exists", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Delete_BlankId_IsRefused(string? id)
    {
        var result = _svc.Delete(id!);
        Assert.False(result.Deleted);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData(@"..\..\Windows")]
    [InlineData("sub/nested")]
    [InlineData(@"sub\nested")]
    public void Delete_RefusesAnythingOutsideTheSessionRoot(string id)
    {
        // A sibling folder next to the store must survive a traversal attempt.
        var sibling = Path.Combine(_tmp, "precious");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "important");

        var result = _svc.Delete(id);

        Assert.False(result.Deleted);
        Assert.True(Directory.Exists(sibling));
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void Delete_RefusesAbsolutePathOutsideRoot()
    {
        var outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(outside);

        var result = _svc.Delete(outside);

        Assert.False(result.Deleted);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Delete_WritesAnAuditLogEntry()
    {
        MakeSession("logged");
        _svc.Delete("logged");

        var log = Path.Combine(_tmp, "deleted-sessions.log");
        Assert.True(File.Exists(log));
        Assert.Contains("logged", File.ReadAllText(log));
    }

    [Fact]
    public void DeleteMany_ContinuesPastFailures()
    {
        MakeSession("one");
        MakeSession("two", locked: true);
        MakeSession("three");

        var result = _svc.DeleteMany(new[] { "one", "two", "three", "missing" });

        Assert.Equal(2, result.DeletedCount);
        Assert.Equal(2, result.FailedCount);
        Assert.False(Directory.Exists(Path.Combine(_root, "one")));
        Assert.True(Directory.Exists(Path.Combine(_root, "two")));    // locked, preserved
        Assert.False(Directory.Exists(Path.Combine(_root, "three")));
    }

    [Fact]
    public void DeleteMany_EmptyInput_IsANoOp()
    {
        var result = _svc.DeleteMany(Array.Empty<string>());
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void DeleteMany_SumsReclaimedBytes()
    {
        MakeSession("a", bytes: 1000);
        MakeSession("b", bytes: 2000);

        var result = _svc.DeleteMany(new[] { "a", "b" });

        Assert.Equal(2, result.DeletedCount);
        Assert.True(result.BytesFreed >= 3000);
    }
}
