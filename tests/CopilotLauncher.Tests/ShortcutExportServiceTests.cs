using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class ShortcutExportServiceTests
{
    [Fact]
    public void BuildPlan_ProducesQuotedArguments_AndMatchingFields()
    {
        var (svc, _) = MakeServices();
        var shortcut = new Shortcut
        {
            Id = Guid.NewGuid().ToString(),
            Label = "MyApp",
            WorkingDirectory = @"C:\code\myapp",
            ResumeTarget = "MyApp-Main",
            EnableAllowAll = true,
            ExtraCopilotArgs = "--max-autopilot-continues 100",
        };

        var plan = svc.BuildPlan(shortcut);

        Assert.Equal(@"C:\npm\copilot.cmd", plan.TargetPath);
        Assert.Equal(@"C:\code\myapp", plan.WorkingDirectory);
        Assert.Contains("--allow-all", plan.Arguments);
        Assert.Contains("--resume=MyApp-Main", plan.Arguments);
        Assert.Contains("--max-autopilot-continues", plan.Arguments);
        Assert.Contains("100", plan.Arguments);
        Assert.Contains("MyApp", plan.Description);
    }

    [Fact]
    public void BuildPlan_Throws_WhenWorkingDirectoryEmpty()
    {
        var (svc, _) = MakeServices();
        var shortcut = new Shortcut { Id = "x", Label = "x", WorkingDirectory = "" };
        Assert.Throws<ArgumentException>(() => svc.BuildPlan(shortcut));
    }

    private static (ShortcutExportService svc, FakeSettings settings) MakeServices()
    {
        Func<string, string?> resolveOnPath = name => name switch
        {
            "copilot.cmd" => @"C:\npm\copilot.cmd",
            _ => null,
        };
        var launchCtor = typeof(LaunchService).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new[] { typeof(Func<string, string?>) }, null)!;
        var launch = (LaunchService)launchCtor.Invoke(new object[] { resolveOnPath });

        var termCtor = typeof(TerminalDiscoveryService).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new[] { typeof(Func<string, string?>) }, null)!;
        var terminals = (TerminalDiscoveryService)termCtor.Invoke(new object[] { resolveOnPath });

        var settings = new FakeSettings();
        return (new ShortcutExportService(launch, terminals, settings), settings);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public string AppDataDirectory => Path.GetTempPath();
        public string SettingsFilePath => Path.Combine(Path.GetTempPath(), "fake-settings.json");
        public AppSettings Current { get; } = new();
        public void Load() { }
        public void Save() { }
    }
}
