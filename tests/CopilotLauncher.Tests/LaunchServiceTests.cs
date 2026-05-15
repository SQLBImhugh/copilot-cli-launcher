using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class LaunchServiceTests
{
    private static Func<string, string?> CmdResolver => name => name switch
    {
        "copilot.cmd" => @"C:\npm\copilot.cmd",
        _ => null,
    };

    [Fact]
    public void Build_DirectMode_ProducesCopilotInvocation()
    {
        var svc = NewSvc(CmdResolver);
        var cmd = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\proj",
            ResumeTarget = "MyApp",
            EnableAllowAll = true,
            Terminal = null,
        });
        Assert.Equal(@"C:\npm\copilot.cmd", cmd.FileName);
        Assert.Equal(@"C:\proj", cmd.WorkingDirectory);
        Assert.Contains("--allow-all", cmd.ArgumentList);
        Assert.Contains("--resume=MyApp", cmd.ArgumentList);
    }

    [Fact]
    public void Build_WindowsTerminal_WrapsCorrectly()
    {
        var svc = NewSvc(CmdResolver);
        var wt = new TerminalProfile
        {
            Id = "wt",
            DisplayName = "Windows Terminal",
            ExecutablePath = @"C:\wt\wt.exe",
            SupportsTabs = true,
            SupportsWorkingDirectoryFlag = true,
        };
        var cmd = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\proj",
            ResumeTarget = "MyApp",
            EnableAllowAll = true,
            Terminal = wt,
        });
        Assert.Equal(@"C:\wt\wt.exe", cmd.FileName);
        Assert.Equal(new[] { "-w", "0", "-d", @"C:\proj", @"C:\npm\copilot.cmd", "--allow-all", "--resume=MyApp" },
                     cmd.ArgumentList);
    }

    [Fact]
    public void Build_PreservesQuotedExtraArgs()
    {
        var svc = NewSvc(CmdResolver);
        var cmd = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\proj",
            ExtraCopilotArgs = "--max-autopilot-continues 100 --prompt \"hello world\"",
            Terminal = null,
        });
        Assert.Contains("--max-autopilot-continues", cmd.ArgumentList);
        Assert.Contains("100", cmd.ArgumentList);
        Assert.Contains("--prompt", cmd.ArgumentList);
        Assert.Contains("hello world", cmd.ArgumentList);
    }

    [Fact]
    public void Build_SkipsResumeFlag_WhenNullOrEmpty()
    {
        var svc = NewSvc(CmdResolver);
        var cmd1 = svc.Build(new LaunchRequest { WorkingDirectory = @"C:\p", ResumeTarget = null });
        var cmd2 = svc.Build(new LaunchRequest { WorkingDirectory = @"C:\p", ResumeTarget = "" });
        Assert.DoesNotContain(cmd1.ArgumentList, a => a.StartsWith("--resume", StringComparison.Ordinal));
        Assert.DoesNotContain(cmd2.ArgumentList, a => a.StartsWith("--resume", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_Throws_WhenWorkingDirectoryEmpty()
    {
        var svc = NewSvc(CmdResolver);
        Assert.Throws<ArgumentException>(() => svc.Build(new LaunchRequest { WorkingDirectory = "" }));
    }

    [Fact]
    public void Build_Throws_WhenCopilotNotFound()
    {
        var svc = NewSvc(_ => null);
        Assert.Throws<InvalidOperationException>(() => svc.Build(new LaunchRequest { WorkingDirectory = @"C:\p" }));
    }

    [Fact]
    public void Build_ArgumentString_IsQuotedCorrectly()
    {
        var svc = NewSvc(CmdResolver);
        var wt = new TerminalProfile { Id = "wt", DisplayName = "WT", ExecutablePath = @"C:\wt\wt.exe", SupportsTabs = true, SupportsWorkingDirectoryFlag = true };
        var cmd = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\path with spaces",
            Terminal = wt,
        });
        // Spaces should trigger quoting on the workdir token.
        Assert.Contains("\"C:\\path with spaces\"", cmd.ArgumentString);
    }

    [Fact]
    public void Build_PowerShellWrapper_UsesStartProcessWithSafeQuoting()
    {
        var svc = NewSvc(CmdResolver);
        var pwsh = new TerminalProfile { Id = "pwsh", DisplayName = "PS7", ExecutablePath = @"C:\pwsh\pwsh.exe" };
        var cmd = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\proj",
            ResumeTarget = "MyApp",
            EnableAllowAll = true,
            Terminal = pwsh,
        });
        Assert.Equal(@"C:\pwsh\pwsh.exe", cmd.FileName);
        // The -Command payload must use Start-Process + single-quoted literals,
        // not & 'path' invocation. Single quotes prevent PowerShell from
        // interpolating $, ", `, ; or treating apostrophes as terminators.
        var commandIdx = cmd.ArgumentList.ToList().FindIndex(a => a == "-Command");
        Assert.True(commandIdx >= 0);
        var payload = cmd.ArgumentList[commandIdx + 1];
        Assert.StartsWith("Start-Process -FilePath '", payload);
        Assert.Contains("'C:\\npm\\copilot.cmd'", payload);   // copilot.cmd is the inner exe, single-quoted
        Assert.Contains("'--allow-all'", payload);
        Assert.Contains("'--resume=MyApp'", payload);
    }

    [Fact]
    public void Build_PowerShellWrapper_EscapesSingleQuotesInArgs()
    {
        var svc = NewSvc(CmdResolver);
        var pwsh = new TerminalProfile { Id = "pwsh", DisplayName = "PS7", ExecutablePath = @"C:\pwsh\pwsh.exe" };
        var cmd = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\proj",
            ExtraCopilotArgs = "--prompt \"don't do that\"",
            Terminal = pwsh,
        });
        var commandIdx = cmd.ArgumentList.ToList().FindIndex(a => a == "-Command");
        var payload = cmd.ArgumentList[commandIdx + 1];
        // PowerShell single-quoted literals escape ' as '' — so don't becomes don''t
        Assert.Contains("'don''t do that'", payload);
    }

    [Fact]
    public void Build_PowerShellWrapper_NeutralizesShellInjectionMetachars()
    {
        // Args containing ;, $, ` would normally be interpreted by PowerShell
        // when assembled into a -Command string. Single-quoted literals make
        // them inert.
        var svc = NewSvc(CmdResolver);
        var pwsh = new TerminalProfile { Id = "pwsh", DisplayName = "PS7", ExecutablePath = @"C:\pwsh\pwsh.exe" };
        var cmd = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\proj",
            ExtraCopilotArgs = "--meta \"a;b$c`d\"",
            Terminal = pwsh,
        });
        var commandIdx = cmd.ArgumentList.ToList().FindIndex(a => a == "-Command");
        var payload = cmd.ArgumentList[commandIdx + 1];
        Assert.Contains("'a;b$c`d'", payload);
    }

    [Fact]
    public void Build_CmdWrapper_UsesSlashKAndQuotedExe()
    {
        var svc = NewSvc(CmdResolver);
        var cmdTerm = new TerminalProfile { Id = "cmd", DisplayName = "cmd", ExecutablePath = @"C:\Windows\cmd.exe" };
        var built = svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\proj",
            EnableAllowAll = true,
            Terminal = cmdTerm,
        });
        Assert.Equal(@"C:\Windows\cmd.exe", built.FileName);
        Assert.Equal("/K", built.ArgumentList[0]);
        Assert.Contains("\"C:\\npm\\copilot.cmd\"", built.ArgumentList[1]);
        Assert.Contains("--allow-all", built.ArgumentList[1]);
    }

    [Fact]
    public void Build_UnknownTerminalId_Throws()
    {
        var svc = NewSvc(CmdResolver);
        var weird = new TerminalProfile { Id = "hyper", DisplayName = "Hyper", ExecutablePath = @"C:\hyper.exe" };
        Assert.Throws<NotSupportedException>(() => svc.Build(new LaunchRequest
        {
            WorkingDirectory = @"C:\p",
            Terminal = weird,
        }));
    }

    private static LaunchService NewSvc(Func<string, string?> resolver)
    {
        var ctor = typeof(LaunchService)
            .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                            null, new[] { typeof(Func<string, string?>) }, null);
        return (LaunchService)ctor!.Invoke(new object[] { resolver });
    }
}
