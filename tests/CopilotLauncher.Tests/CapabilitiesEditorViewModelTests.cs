using CopilotLauncher.Models;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class CapabilitiesEditorViewModelTests
{
    private static CapabilityCatalog SampleCatalog() => new()
    {
        McpServers = new[]
        {
            new McpServerInfo { Name = "azure", Source = "plugin", Type = "http" },
            new McpServerInfo { Name = "ms-learn", Source = "user", Type = "http" },
        },
        Skills = new[] { new SkillInfo { Name = "pptx", Source = "personal-copilot" } },
        Agents = new[] { "researcher", "reviewer" },
    };

    [Fact]
    public void LoadCatalog_PopulatesCollections_AndSeedsDisabledServers()
    {
        var vm = new CapabilitiesEditorViewModel();
        var existing = new LaunchCapabilities { DisabledMcpServers = new() { "azure" } };

        vm.LoadCatalog(SampleCatalog(), existing);

        Assert.Equal(2, vm.McpServers.Count);
        Assert.False(vm.McpServers.Single(m => m.Name == "azure").IsEnabled);
        Assert.True(vm.McpServers.Single(m => m.Name == "ms-learn").IsEnabled);
        Assert.Equal(new[] { "researcher", "reviewer" }, vm.AgentOptions);
        Assert.Equal(1, vm.SkillCount);
    }

    [Fact]
    public void ToCapabilities_ReturnsNull_WhenNothingSelected()
    {
        var vm = new CapabilitiesEditorViewModel();
        vm.LoadCatalog(SampleCatalog(), existing: null);
        Assert.Null(vm.ToCapabilities());
    }

    [Fact]
    public void ToCapabilities_ComposesSelection()
    {
        var vm = new CapabilitiesEditorViewModel();
        vm.LoadCatalog(SampleCatalog(), existing: null);

        vm.McpServers.Single(m => m.Name == "ms-learn").IsEnabled = false;
        vm.DisableBuiltinGitHubMcp = true;
        vm.SelectedAgent = "researcher";
        vm.ToolModeExclude = true;            // RadioButton group would clear the others
        vm.ToolModeNone = false;
        vm.ToolsText = "write\nshell(git push)";
        vm.DisableAllSkills = true;

        var caps = vm.ToCapabilities();
        Assert.NotNull(caps);
        Assert.Equal(new[] { "ms-learn" }, caps!.DisabledMcpServers);
        Assert.True(caps.DisableBuiltinMcps);
        Assert.Equal("researcher", caps.Agent);
        Assert.Equal(ToolFilterMode.ExcludeThese, caps.ToolMode);
        Assert.Equal(new[] { "write", "shell(git push)" }, caps.Tools);
        Assert.True(caps.DisableAllSkills);
    }

    [Fact]
    public void ParseTools_SplitsOnLinesAndCommas_PreservingSpacesInSpecs()
    {
        var tools = CapabilitiesEditorViewModel.ParseTools("write\nshell(git push)\r\n view , read");
        Assert.Equal(new[] { "write", "shell(git push)", "view", "read" }, tools);
    }

    [Fact]
    public void RoundTrip_PreservesSelection()
    {
        var original = new LaunchCapabilities
        {
            DisabledMcpServers = new() { "azure" },
            DisableBuiltinMcps = true,
            Agent = "reviewer",
            ToolMode = ToolFilterMode.OnlyThese,
            Tools = new() { "view", "write" },
            DisableAllSkills = false,
        };

        var vm = new CapabilitiesEditorViewModel();
        vm.LoadCatalog(SampleCatalog(), original);
        var round = vm.ToCapabilities();

        Assert.NotNull(round);
        Assert.Equal(original.DisabledMcpServers, round!.DisabledMcpServers);
        Assert.Equal(original.DisableBuiltinMcps, round.DisableBuiltinMcps);
        Assert.Equal(original.Agent, round.Agent);
        Assert.Equal(original.ToolMode, round.ToolMode);
        Assert.Equal(original.Tools, round.Tools);
    }

    [Fact]
    public void Changed_FiresOnSelectionChange()
    {
        var vm = new CapabilitiesEditorViewModel();
        vm.LoadCatalog(SampleCatalog(), existing: null);
        var fired = 0;
        vm.Changed += (_, _) => fired++;

        vm.DisableAllSkills = true;
        vm.McpServers[0].IsEnabled = false;

        Assert.True(fired >= 2);
    }
}
