using System.ComponentModel;
using CopilotLauncher.Helpers;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class SessionLockTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());

    public SessionLockTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void IsInUse_WithoutMarkers_DoesNotInspectProcesses()
    {
        Assert.False(SessionLock.IsInUse(_folder, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void IsInUse_ProtectsTheCurrentProcess()
    {
        WriteMarker(Environment.ProcessId);

        Assert.True(SessionLock.IsInUse(_folder));
    }

    [Fact]
    public void IsInUse_IgnoresExitedOwners_WithoutRemovingMarkers()
    {
        var marker = WriteMarker(int.MaxValue);

        Assert.False(SessionLock.IsInUse(_folder));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void IsInUse_IgnoresReusedPids()
    {
        var marker = WriteMarker(42);
        var newerProcess = DateTime.UtcNow.AddMinutes(1);

        Assert.False(SessionLock.IsInUse(_folder, _ => newerProcess));
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void IsInUse_ProtectsAnOldButStillRunningOwner()
    {
        WriteMarker(42);

        Assert.True(SessionLock.IsInUse(_folder, _ => DateTime.UnixEpoch));
    }

    [Fact]
    public void IsInUse_ProtectsAnyLiveOwner_AmongStaleMarkers()
    {
        WriteMarker(42);
        WriteMarker(43);

        Assert.True(SessionLock.IsInUse(_folder, pid => pid == 43 ? DateTime.UnixEpoch : null));
    }

    [Theory]
    [InlineData("inuse.0.lock")]
    [InlineData("inuse.-1.lock")]
    [InlineData("inuse.unknown.lock")]
    [InlineData("inuse.9999999999999999.lock")]
    public void IsInUse_ProtectsUnrecognizedOwners(string name)
    {
        File.WriteAllText(Path.Combine(_folder, name), "");

        Assert.True(SessionLock.IsInUse(_folder, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void IsInUse_ProtectsOwners_WhenProcessInspectionIsDenied()
    {
        WriteMarker(42);

        Assert.True(SessionLock.IsInUse(_folder, _ => throw new Win32Exception(5)));
    }

    [Fact]
    public void IsInUse_ProtectsOwners_WhenProcessInspectionRacesWithExit()
    {
        WriteMarker(42);

        Assert.True(SessionLock.IsInUse(_folder, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public void IsInUse_RechecksOwners_OnEveryCall()
    {
        WriteMarker(42);
        DateTime? started = DateTime.UnixEpoch;
        Assert.True(SessionLock.IsInUse(_folder, _ => started));

        started = null;

        Assert.False(SessionLock.IsInUse(_folder, _ => started));
    }

    private string WriteMarker(int pid)
    {
        var marker = Path.Combine(_folder, $"inuse.{pid}.lock");
        File.WriteAllText(marker, "");
        return marker;
    }
}
