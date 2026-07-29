using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class SessionCapabilityServiceTests : IDisposable
{
    private readonly string _tmpRoot;

    public SessionCapabilityServiceTests()
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
    public void ParseMcpServers_ReadsNameSourceType_IgnoringSecrets()
    {
        const string json = """
        {
          "mcpServers": {
            "azure": { "type": "http", "url": "http://x", "source": "plugin" },
            "pocportal": { "type": "http", "url": "http://y", "headers": { "Authorization": "Bearer SECRET" }, "source": "user" }
          }
        }
        """;
        var servers = SessionCapabilityService.ParseMcpServers(json);
        Assert.Equal(2, servers.Count);
        var azure = servers.Single(s => s.Name == "azure");
        Assert.Equal("plugin", azure.Source);
        Assert.Equal("http", azure.Type);
        // Sorted by name.
        Assert.Equal("azure", servers[0].Name);
        Assert.Equal("pocportal", servers[1].Name);
    }

    [Fact]
    public void ParseMcpServers_BadInput_ReturnsEmpty()
    {
        Assert.Empty(SessionCapabilityService.ParseMcpServers(null));
        Assert.Empty(SessionCapabilityService.ParseMcpServers(""));
        Assert.Empty(SessionCapabilityService.ParseMcpServers("not json"));
        Assert.Empty(SessionCapabilityService.ParseMcpServers("{}"));
    }

    [Fact]
    public void ParseSkills_ReadsArray()
    {
        const string json = """
        [
          { "name": "pptx", "description": "decks", "source": "personal-copilot", "path": "C:\\x" },
          { "name": "docx", "description": "docs", "source": "personal-copilot" }
        ]
        """;
        var skills = SessionCapabilityService.ParseSkills(json);
        Assert.Equal(2, skills.Count);
        Assert.Equal("docx", skills[0].Name);   // sorted
        Assert.Equal("decks", skills.Single(s => s.Name == "pptx").Description);
    }

    [Fact]
    public void ScanAgents_FindsMarkdownInWorkspaceAndUser()
    {
        var agentsDir = Path.Combine(_tmpRoot, ".github", "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "researcher.md"), "# researcher");
        File.WriteAllText(Path.Combine(agentsDir, "reviewer.md"), "# reviewer");
        File.WriteAllText(Path.Combine(agentsDir, "README.md"), "ignore me");

        var agents = SessionCapabilityService.ScanAgents(_tmpRoot);

        Assert.Contains("researcher", agents);
        Assert.Contains("reviewer", agents);
        Assert.DoesNotContain("README", agents);
    }

    [Fact]
    public void ScanAgents_StripsAgentMdSuffix_AndSkipsDisabledFiles()
    {
        var agentsDir = Path.Combine(_tmpRoot, ".github", "agents");
        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(Path.Combine(agentsDir, "winui-dev.agent.md"), "# winui");
        File.WriteAllText(Path.Combine(agentsDir, "retired.md.disabled"), "# off");

        var agents = SessionCapabilityService.ScanAgents(_tmpRoot, copilotHome: NewDir("copilot"), claudeHome: null);

        Assert.Contains("winui-dev", agents);
        Assert.DoesNotContain("retired", agents);
        Assert.DoesNotContain("winui-dev.agent", agents);
    }

    [Fact]
    public void ScanAgents_FindsPluginAgents_NamespacedByPlugin()
    {
        var copilotHome = NewDir("copilot");
        WritePlugin(copilotHome, "market", "winui", agentFiles: new[] { "winui-dev.agent.md" });
        WritePlugin(copilotHome, "market", "pbip", agentFiles: new[] { "pbip-validator.md" });
        WritePlugin(copilotHome, "market", "off-plugin", agentFiles: new[] { "nope.md" }, enabled: false);
        WriteConfig(copilotHome);

        var agents = SessionCapabilityService.ScanAgents(null, copilotHome, claudeHome: null);

        Assert.Contains("winui:winui-dev", agents);
        Assert.Contains("pbip:pbip-validator", agents);
        Assert.DoesNotContain("off-plugin:nope", agents);
    }

    [Fact]
    public void ScanAgents_HonorsPluginManifestAgentsList()
    {
        var copilotHome = NewDir("copilot");
        var dir = WritePlugin(copilotHome, "market", "msbuild", agentFiles: new[] { "msbuild.agent.md", "extra.md" });
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            """{ "name": "msbuild", "agents": ["./agents/msbuild.agent.md"] }""");
        WriteConfig(copilotHome);

        var agents = SessionCapabilityService.ScanAgents(null, copilotHome, claudeHome: null);

        Assert.Contains("msbuild:msbuild", agents);
        Assert.DoesNotContain("msbuild:extra", agents);
    }

    [Fact]
    public void PluginAgentPaths_RejectsPathsEscapingPluginDir()
    {
        var copilotHome = NewDir("copilot");
        var dir = WritePlugin(copilotHome, "market", "evil", agentFiles: Array.Empty<string>());
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            """{ "name": "evil", "agents": { "paths": ["../../elsewhere"], "exclusive": true } }""");

        var paths = SessionCapabilityService.PluginAgentPaths(dir);

        Assert.Equal(new[] { Path.Combine(dir, "agents") }, paths);
    }

    [Fact]
    public void EnumerateInstalledPlugins_KeepsEntriesWithNoMarketplaceOrCachePath()
    {
        // Directory is only needed for agent scanning. Dropping such an entry would
        // omit it from the per-repo enabledPlugins allowlist and silently disable it.
        var copilotHome = NewDir("copilot");
        File.WriteAllText(Path.Combine(copilotHome, "config.json"), """
        {
          "installedPlugins": [
            { "name": "solo" },
            { "name": "winui", "marketplace": "market", "enabled": false }
          ]
        }
        """);

        var plugins = SessionCapabilityService.EnumerateInstalledPlugins(copilotHome);

        Assert.Equal(2, plugins.Count);
        var solo = plugins.Single(p => p.Name == "solo");
        Assert.Equal("solo", solo.Key);
        Assert.Equal(string.Empty, solo.Directory);
        Assert.True(solo.Enabled);          // absent "enabled" means enabled

        var winui = plugins.Single(p => p.Name == "winui");
        Assert.Equal("winui@market", winui.Key);
        Assert.False(winui.Enabled);
    }

    [Fact]
    public void ScanAgents_ParsesJsoncConfig_WithCommentsAndTrailingCommas()
    {
        var copilotHome = NewDir("copilot");
        WritePlugin(copilotHome, "market", "winui", agentFiles: new[] { "winui-dev.agent.md" });
        // The CLI writes config.json with a leading // banner comment.
        File.WriteAllText(Path.Combine(copilotHome, "config.json"), """
        // User configuration for the Copilot CLI.
        {
          "installedPlugins": [
            { "name": "winui", "marketplace": "market", "enabled": true },
          ],
        }
        """);

        var agents = SessionCapabilityService.ScanAgents(null, copilotHome, claudeHome: null);

        Assert.Contains("winui:winui-dev", agents);
    }

    private string NewDir(string name)
    {
        var dir = Path.Combine(_tmpRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Creates &lt;copilotHome&gt;/installed-plugins/&lt;market&gt;/&lt;plugin&gt;/agents/* and records it for WriteConfig.</summary>
    private string WritePlugin(string copilotHome, string marketplace, string plugin, string[] agentFiles, bool enabled = true)
    {
        var dir = Path.Combine(copilotHome, "installed-plugins", marketplace, plugin);
        var agentsDir = Path.Combine(dir, "agents");
        Directory.CreateDirectory(agentsDir);
        foreach (var f in agentFiles) File.WriteAllText(Path.Combine(agentsDir, f), "# agent");
        _plugins.Add((plugin, marketplace, enabled));
        return dir;
    }

    private void WriteConfig(string copilotHome)
    {
        var entries = string.Join(",", _plugins.Select(p =>
            $$"""{ "name": "{{p.Name}}", "marketplace": "{{p.Marketplace}}", "enabled": {{(p.Enabled ? "true" : "false")}} }"""));
        File.WriteAllText(Path.Combine(copilotHome, "config.json"), $$"""{ "installedPlugins": [{{entries}}] }""");
    }

    private readonly List<(string Name, string Marketplace, bool Enabled)> _plugins = new();

    [Fact]
    public async Task DiscoverAsync_ComposesCatalog_AndCaches()
    {
        var calls = 0;
        SessionCapabilityService.CliRunner runner = (args, wd, ct) =>
        {
            calls++;
            if (args.Contains("mcp"))
                return Task.FromResult<string?>("""{ "mcpServers": { "azure": { "type": "http", "source": "plugin" } } }""");
            if (args.Contains("skill"))
                return Task.FromResult<string?>("""[ { "name": "pptx", "source": "personal-copilot" } ]""");
            return Task.FromResult<string?>(null);
        };
        var svc = new SessionCapabilityService(runner);

        var catalog = await svc.DiscoverAsync(_tmpRoot);
        Assert.Single(catalog.McpServers);
        Assert.Equal("azure", catalog.McpServers[0].Name);
        Assert.Single(catalog.Skills);
        Assert.Equal(2, calls); // mcp + skill

        // Second call with same dir is served from cache (no extra CLI calls).
        await svc.DiscoverAsync(_tmpRoot);
        Assert.Equal(2, calls);

        // forceRefresh re-runs.
        await svc.DiscoverAsync(_tmpRoot, forceRefresh: true);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task DiscoverAsync_FailedCli_ReturnsEmptyCatalog()
    {
        SessionCapabilityService.CliRunner runner = (args, wd, ct) => Task.FromResult<string?>(null);
        var svc = new SessionCapabilityService(runner);
        var catalog = await svc.DiscoverAsync(null);
        Assert.Empty(catalog.McpServers);
        Assert.Empty(catalog.Skills);
    }
}
