using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class TerminalDiscoveryServiceTests
{
    [Fact]
    public void Discovered_FindsAllTerminals_WhenAllOnPath()
    {
        var svc = NewSvc(name => name switch
        {
            "wt.exe"         => @"C:\wt\wt.exe",
            "pwsh.exe"       => @"C:\pwsh\pwsh.exe",
            "powershell.exe" => @"C:\Windows\powershell.exe",
            "cmd.exe"        => @"C:\Windows\cmd.exe",
            _ => null,
        });
        Assert.Equal(new[] { "wt", "pwsh", "powershell", "cmd" }, svc.Discovered.Select(t => t.Id));
    }

    [Fact]
    public void Discovered_OmitsMissingTerminals()
    {
        var svc = NewSvc(name => name switch
        {
            "pwsh.exe" => @"C:\pwsh\pwsh.exe",
            "cmd.exe"  => @"C:\Windows\cmd.exe",
            _ => null,
        });
        Assert.Equal(new[] { "pwsh", "cmd" }, svc.Discovered.Select(t => t.Id));
    }

    [Fact]
    public void Discovered_IsEmpty_WhenNothingOnPath()
    {
        var svc = NewSvc(_ => null);
        Assert.Empty(svc.Discovered);
    }

    [Fact]
    public void WtTerminal_FlagsTabsAndWorkdirSupport()
    {
        var svc = NewSvc(name => name == "wt.exe" ? @"C:\wt\wt.exe" : null);
        var wt = svc.Discovered.Single();
        Assert.True(wt.SupportsTabs);
        Assert.True(wt.SupportsWorkingDirectoryFlag);
    }

    [Fact]
    public void PwshTerminal_DoesNotFlagTabSupport()
    {
        var svc = NewSvc(name => name == "pwsh.exe" ? @"C:\pwsh\pwsh.exe" : null);
        var pwsh = svc.Discovered.Single();
        Assert.False(pwsh.SupportsTabs);
    }

    /// <summary>Wraps the internal ctor for test convenience via reflection.</summary>
    private static TerminalDiscoveryService NewSvc(Func<string, string?> resolver)
    {
        var ctor = typeof(TerminalDiscoveryService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(Func<string, string?>) }, null);
        return (TerminalDiscoveryService)ctor!.Invoke(new object[] { resolver });
    }
}
