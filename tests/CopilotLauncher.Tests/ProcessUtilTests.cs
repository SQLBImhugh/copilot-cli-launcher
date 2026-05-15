using CopilotLauncher.Helpers;
using Xunit;

namespace CopilotLauncher.Tests;

public class ProcessUtilTests
{
    [Fact]
    public void Resolve_ReturnsNull_WhenNoShimFound()
    {
        var result = ProcessUtil.Resolve(_ => null);
        Assert.Null(result);
    }

    [Fact]
    public void Resolve_PrefersCmdShim_OverPs1()
    {
        var result = ProcessUtil.Resolve(name => name switch
        {
            "copilot.cmd" => @"C:\npm\copilot.cmd",
            "copilot.ps1" => @"C:\npm\copilot.ps1",
            _ => null,
        });
        Assert.NotNull(result);
        Assert.Equal(@"C:\npm\copilot.cmd", result!.FileName);
        Assert.Empty(result.PrefixArgs);
    }

    [Fact]
    public void Resolve_PrefersExe_WhenNoCmd()
    {
        var result = ProcessUtil.Resolve(name => name switch
        {
            "copilot.cmd" => null,
            "copilot.exe" => @"C:\bin\copilot.exe",
            "copilot.ps1" => @"C:\npm\copilot.ps1",
            _ => null,
        });
        Assert.NotNull(result);
        Assert.Equal(@"C:\bin\copilot.exe", result!.FileName);
    }

    [Fact]
    public void Resolve_FallsBackToPs1_ViaPwsh()
    {
        var result = ProcessUtil.Resolve(name => name switch
        {
            "copilot.ps1" => @"C:\npm\copilot.ps1",
            "pwsh.exe"    => @"C:\pwsh\pwsh.exe",
            _ => null,
        });
        Assert.NotNull(result);
        Assert.Equal(@"C:\pwsh\pwsh.exe", result!.FileName);
        Assert.Equal(new[] { "-NoProfile", "-File", @"C:\npm\copilot.ps1" }, result.PrefixArgs);
    }

    [Fact]
    public void Resolve_FallsBackToPowerShell51_WhenPwshMissing()
    {
        var result = ProcessUtil.Resolve(name => name switch
        {
            "copilot.ps1" => @"C:\npm\copilot.ps1",
            "powershell.exe" => @"C:\Windows\powershell.exe",
            _ => null,
        });
        Assert.NotNull(result);
        Assert.Equal(@"C:\Windows\powershell.exe", result!.FileName);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenPs1FoundButNoShellAvailable()
    {
        var result = ProcessUtil.Resolve(name => name switch
        {
            "copilot.ps1" => @"C:\npm\copilot.ps1",
            _ => null,
        });
        Assert.Null(result);
    }
}
