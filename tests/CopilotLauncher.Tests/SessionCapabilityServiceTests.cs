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
