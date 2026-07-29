using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class RepoConfigServiceTests : IDisposable
{
    private readonly string _repo;
    private readonly RepoConfigService _svc = new();

    public RepoConfigServiceTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch { /* best effort */ }
    }

    private string SettingsPath => Path.Combine(_repo, ".github", "copilot", "settings.json");

    private static InstalledPluginInfo Plugin(string name, string marketplace, bool enabled = true) => new()
    {
        Name = name,
        Marketplace = marketplace,
        Directory = @"C:\ignored",
        Enabled = enabled,
    };

    [Fact]
    public void PluginKey_IsNameAtMarketplace()
    {
        Assert.Equal("winui@awesome-copilot", Plugin("winui", "awesome-copilot").Key);
        Assert.Equal("solo", Plugin("solo", "").Key);
    }

    [Fact]
    public void Inspect_EmptyRepo_ReportsNothingPresent()
    {
        var status = _svc.Inspect(_repo);

        Assert.NotEmpty(status.Files);
        Assert.Equal(0, status.PresentCount);
        Assert.Null(status.EnabledPlugins);
        Assert.False(status.ManagesPlugins);
    }

    [Fact]
    public void Inspect_DetectsKnownConfigFilesAndDirectories()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# hi");
        File.WriteAllText(Path.Combine(_repo, ".mcp.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_repo, ".github", "agents"));

        var status = _svc.Inspect(_repo);
        var present = status.Present.Select(f => f.RelativePath).ToList();

        Assert.Contains("AGENTS.md", present);
        Assert.Contains(".mcp.json", present);
        Assert.Contains(".github/agents", present);
        Assert.DoesNotContain("CLAUDE.md", present);
    }

    [Fact]
    public void Inspect_MarksRepoSettingsAsLauncherManaged()
    {
        var managed = _svc.Inspect(_repo).Files.Where(f => f.Kind == RepoConfigKind.Managed).ToList();
        Assert.Single(managed);
        Assert.Equal(".github/copilot/settings.json", managed[0].RelativePath);
    }

    [Fact]
    public void WriteEnabledPlugins_IdenticalContent_DoesNotRewriteOrBackUp()
    {
        // SyncRepoConfigOnLaunch rewrites this file on every launch; identical
        // writes must not churn the user's git working tree.
        var plugins = new[] { Plugin("a", "m") };
        Assert.True(_svc.WriteEnabledPlugins(_repo, plugins, new[] { "a@m" }));
        var firstWrite = File.GetLastWriteTimeUtc(SettingsPath);

        Assert.True(_svc.WriteEnabledPlugins(_repo, plugins, new[] { "a@m" }));

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(SettingsPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "*.bak*"));
    }

    [Fact]
    public void WriteEnabledPlugins_UsesASingleBackupFile()
    {
        // A timestamped backup name would pile up untracked files inside the repo.
        var plugins = new[] { Plugin("a", "m") };
        _svc.WriteEnabledPlugins(_repo, plugins, new[] { "a@m" });
        _svc.WriteEnabledPlugins(_repo, plugins, Array.Empty<string>());
        _svc.WriteEnabledPlugins(_repo, plugins, new[] { "a@m" });

        var backups = Directory.GetFiles(Path.GetDirectoryName(SettingsPath)!, "*.bak*");
        Assert.Single(backups);
        Assert.EndsWith("settings.json.bak", backups[0]);
    }

    [Fact]
    public void WriteEnabledPlugins_IncludesPluginsWithNoMarketplaceOrDirectory()
    {
        // enabledPlugins is an allowlist: a plugin missing from the map is disabled.
        var solo = new InstalledPluginInfo { Name = "solo", Enabled = true };
        Assert.True(_svc.WriteEnabledPlugins(_repo, new[] { Plugin("a", "m"), solo }, new[] { "solo" }));

        var map = RepoConfigService.ReadEnabledPlugins(SettingsPath)!;
        Assert.Equal(2, map.Count);
        Assert.True(map["solo"]);
        Assert.False(map["a@m"]);
    }

    [Fact]
    public void Inspect_BlankDirectory_ReturnsEmpty()
    {
        Assert.Empty(_svc.Inspect(null).Files);
        Assert.Empty(_svc.Inspect("   ").Files);
    }

    [Fact]
    public void WriteEnabledPlugins_WritesEveryPluginExplicitly()
    {
        // The CLI treats enabledPlugins as an allowlist, so a partial map would
        // silently drop every plugin that isn't named.
        var plugins = new[]
        {
            Plugin("winui", "awesome-copilot"),
            Plugin("pbip", "power-bi"),
            Plugin("xlsx", "anthropics", enabled: false),
        };

        Assert.True(_svc.WriteEnabledPlugins(_repo, plugins, new[] { "winui@awesome-copilot" }));

        var map = RepoConfigService.ReadEnabledPlugins(SettingsPath);
        Assert.NotNull(map);
        Assert.Equal(3, map!.Count);
        Assert.True(map["winui@awesome-copilot"]);
        Assert.False(map["pbip@power-bi"]);
        Assert.False(map["xlsx@anthropics"]);
    }

    [Fact]
    public void WriteEnabledPlugins_PreservesOtherKeys()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "mergeStrategy": "rebase", "disableAllHooks": true }""");

        Assert.True(_svc.WriteEnabledPlugins(_repo, new[] { Plugin("winui", "mkt") }, new[] { "winui@mkt" }));

        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("mergeStrategy", json);
        Assert.Contains("rebase", json);
        Assert.Contains("disableAllHooks", json);
        Assert.Contains("winui@mkt", json);
    }

    [Fact]
    public void WriteEnabledPlugins_RefusesToClobberUnparseableFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ not json at all");

        Assert.False(_svc.WriteEnabledPlugins(_repo, new[] { Plugin("winui", "mkt") }, new[] { "winui@mkt" }));
        Assert.Equal("{ not json at all", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void WriteEnabledPlugins_BacksUpBeforeOverwriting()
    {
        Assert.True(_svc.WriteEnabledPlugins(_repo, new[] { Plugin("a", "m") }, new[] { "a@m" }));
        Assert.True(_svc.WriteEnabledPlugins(_repo, new[] { Plugin("a", "m") }, Array.Empty<string>()));

        Assert.True(File.Exists(SettingsPath + ".bak"));
    }

    [Fact]
    public void WriteEnabledPlugins_NoPluginsOrMissingDirectory_IsANoOp()
    {
        Assert.False(_svc.WriteEnabledPlugins(_repo, Array.Empty<InstalledPluginInfo>(), Array.Empty<string>()));
        Assert.False(_svc.WriteEnabledPlugins(Path.Combine(_repo, "nope"), new[] { Plugin("a", "m") }, new[] { "a@m" }));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Inspect_ReadsBackTheWrittenAllowlist()
    {
        _svc.WriteEnabledPlugins(_repo, new[] { Plugin("a", "m"), Plugin("b", "m") }, new[] { "a@m" });

        var status = _svc.Inspect(_repo);

        Assert.True(status.ManagesPlugins);
        Assert.Equal(1, status.EnabledPlugins!.Count(kv => kv.Value));
        Assert.Contains(status.Present, f => f.RelativePath == ".github/copilot/settings.json");
    }

    [Fact]
    public void ClearEnabledPlugins_RemovesOnlyThatKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "mergeStrategy": "merge" }""");
        _svc.WriteEnabledPlugins(_repo, new[] { Plugin("a", "m") }, new[] { "a@m" });

        Assert.True(_svc.ClearEnabledPlugins(_repo));

        Assert.Null(RepoConfigService.ReadEnabledPlugins(SettingsPath));
        Assert.Contains("mergeStrategy", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void ClearEnabledPlugins_NothingToClear()
    {
        Assert.False(_svc.ClearEnabledPlugins(_repo));

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "mergeStrategy": "merge" }""");
        Assert.False(_svc.ClearEnabledPlugins(_repo));
    }
}
