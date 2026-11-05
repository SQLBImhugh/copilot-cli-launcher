using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class SessionDiscoveryServiceTests : IDisposable
{
    private readonly string _tmpRoot;

    public SessionDiscoveryServiceTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Enumerate_ReturnsEmpty_WhenRootMissing()
    {
        var svc = NewSvc(Path.Combine(_tmpRoot, "missing"));
        Assert.Empty(svc.Enumerate());
    }

    [Fact]
    public void Enumerate_SkipsFolders_WithoutWorkspaceYaml()
    {
        Directory.CreateDirectory(Path.Combine(_tmpRoot, "abc-123"));
        var svc = NewSvc(_tmpRoot);
        Assert.Empty(svc.Enumerate());
    }

    [Fact]
    public void Enumerate_ParsesAllFields_FromWorkspaceYaml()
    {
        var sessionId = "11111111-2222-3333-4444-555555555555";
        var dir = Path.Combine(_tmpRoot, sessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), """
id: 11111111-2222-3333-4444-555555555555
cwd: C:\Users\test\my-project
user_named: true
summary_count: 42
created_at: 2026-05-01T10:00:00.000Z
updated_at: 2026-05-15T14:00:00.000Z
git_root: C:\Users\test\my-project
repository: testorg/my-project
host_type: github
branch: main
""");

        var svc = NewSvc(_tmpRoot);
        var result = svc.Enumerate().Single();

        Assert.Equal(sessionId, result.Id);
        Assert.Equal(@"C:\Users\test\my-project", result.Cwd);
        Assert.Equal("testorg/my-project", result.Repository);
        Assert.Equal("main", result.Branch);
        Assert.True(result.UserNamed);
        Assert.Equal(42, result.SummaryCount);
        Assert.NotNull(result.CreatedAt);
        Assert.False(result.IsLocked);
    }

    [Fact]
    public void Enumerate_DetectsLock_FromInUseFile()
    {
        var dir = Path.Combine(_tmpRoot, "locked-id");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: C:\\test\nuser_named: false\nsummary_count: 0\n");
        File.WriteAllText(Path.Combine(dir, $"inuse.{Environment.ProcessId}.lock"), "");

        var svc = NewSvc(_tmpRoot);
        var result = svc.Enumerate().Single();
        Assert.True(result.IsLocked);
    }

    [Fact]
    public void Enumerate_IgnoresLock_WhenOwnerHasExited()
    {
        var dir = Path.Combine(_tmpRoot, "stale-lock");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: C:\\test\n");
        var marker = Path.Combine(dir, $"inuse.{int.MaxValue}.lock");
        File.WriteAllText(marker, "");

        var result = NewSvc(_tmpRoot).Enumerate().Single();

        Assert.False(result.IsLocked);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Enumerate_Metadata_DoesNotWalkSessionArtifacts()
    {
        var dir = Path.Combine(_tmpRoot, "metadata-only");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: C:\\test\n");
        var svc = NewSvcWithSizer(_tmpRoot,
            (_, _) => throw new InvalidOperationException("Metadata discovery must not calculate sizes."));

        var result = svc.Enumerate(includeSizes: false).Single();

        Assert.Equal("metadata-only", result.Id);
        Assert.Equal(0, result.SizeBytes);
    }

    [Fact]
    public void Enumerate_FullScan_IncludesNestedArtifacts()
    {
        var dir = Path.Combine(_tmpRoot, "nested-artifacts");
        var nested = Path.Combine(dir, "files", "build-context");
        Directory.CreateDirectory(nested);
        var workspace = Path.Combine(dir, "workspace.yaml");
        File.WriteAllText(workspace, "cwd: C:\\test\n");
        File.WriteAllBytes(Path.Combine(nested, "artifact.bin"), new byte[4096]);

        var result = NewSvc(_tmpRoot).Enumerate().Single();

        Assert.Equal(new FileInfo(workspace).Length + 4096, result.SizeBytes);
    }

    [Fact]
    public void Enumerate_Metadata_ReusesSizes_ButRefreshesMetadataAndLocks()
    {
        var dir = Path.Combine(_tmpRoot, "cached-size");
        Directory.CreateDirectory(dir);
        var workspace = Path.Combine(dir, "workspace.yaml");
        File.WriteAllText(workspace, "cwd: C:\\before\n");
        var calls = 0;
        long size = 4096;
        var svc = NewSvcWithSizer(_tmpRoot, (_, _) => { calls++; return size; });
        Assert.Equal(4096, svc.Enumerate().Single().SizeBytes);

        File.WriteAllText(workspace, "cwd: C:\\after\n");
        File.WriteAllText(Path.Combine(dir, $"inuse.{Environment.ProcessId}.lock"), "");
        size = 8192;
        var metadata = svc.Enumerate(includeSizes: false).Single();

        Assert.Equal(1, calls);
        Assert.Equal(4096, metadata.SizeBytes);
        Assert.Equal(@"C:\after", metadata.Cwd);
        Assert.True(metadata.IsLocked);
        Assert.Equal(8192, svc.Enumerate().Single().SizeBytes);
        Assert.Equal(8192, svc.Enumerate(includeSizes: false).Single().SizeBytes);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Enumerate_DoesNotSwallowCancellation_DuringSizing()
    {
        var dir = Path.Combine(_tmpRoot, "canceled-scan");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: C:\\test\n");
        using var cancellation = new CancellationTokenSource();
        var svc = NewSvcWithSizer(_tmpRoot, (_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return 0;
        });

        Assert.Throws<OperationCanceledException>(
            () => svc.Enumerate(includeSizes: true, cancellation.Token).ToList());
    }

    [Fact]
    public void Enumerate_TolaratesMalformedYaml()
    {
        var dir = Path.Combine(_tmpRoot, "broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "this is not: valid: yaml: at all:");
        var svc = NewSvc(_tmpRoot);
        // Should not throw; either returns the entry with empty fields or skips it.
        var _ = svc.Enumerate().ToList();
    }

    private static SessionDiscoveryService NewSvc(string root)
    {
        // Use the internal test ctor.
        var ctor = typeof(SessionDiscoveryService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(string) }, null);
        return (SessionDiscoveryService)ctor!.Invoke(new object[] { root });
    }

    private static SessionDiscoveryService NewSvcWithSizer(
        string root, Func<string, CancellationToken, long> calculateSize)
    {
        var ctor = typeof(SessionDiscoveryService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(string), typeof(Func<string, CancellationToken, long>) }, null);
        return (SessionDiscoveryService)ctor!.Invoke(new object[] { root, calculateSize });
    }
}
