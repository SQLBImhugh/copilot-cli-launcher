using System.Diagnostics;
using System.Text.Json;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

/// <summary>
/// Enumerates the capabilities the copilot CLI can load for a given working
/// directory: MCP servers and skills (via <c>copilot mcp list --json</c> /
/// <c>copilot skill list --json</c>, which include workspace + plugin sources)
/// and custom agents (scanned from the usual agent folders). Used to populate
/// the capability selector UI. Best-effort: never throws; missing/failed
/// sources just come back empty.
/// </summary>
public interface ISessionCapabilityService
{
    Task<CapabilityCatalog> DiscoverAsync(string? workingDirectory, bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>
    /// Installed plugins from <c>~/.copilot/config.json</c>, enabled and disabled. Synchronous
    /// and cheap (a single local file read) — unlike <see cref="DiscoverAsync"/>, which shells
    /// out to the CLI and can take seconds. Safe to call on the UI thread.
    /// </summary>
    IReadOnlyList<InstalledPluginInfo> GetInstalledPlugins();
}

public sealed class SessionCapabilityService : ISessionCapabilityService
{
    /// <summary>Runs the copilot CLI with the given args in <paramref name="workingDir"/> and
    /// returns stdout (or null on failure). Injected so tests can supply canned JSON.</summary>
    internal delegate Task<string?> CliRunner(IReadOnlyList<string> args, string? workingDir, CancellationToken ct);

    private readonly CliRunner _runCli;

    private readonly object _lock = new();
    private string? _cachedKey;
    private CapabilityCatalog? _cached;

    public SessionCapabilityService() : this(RunCopilotAsync) { }

    /// <summary>Test-only ctor.</summary>
    internal SessionCapabilityService(CliRunner runCli) => _runCli = runCli;

    public IReadOnlyList<InstalledPluginInfo> GetInstalledPlugins() =>
        EnumerateInstalledPlugins(DefaultCopilotHome);

    public async Task<CapabilityCatalog> DiscoverAsync(string? workingDirectory, bool forceRefresh = false, CancellationToken ct = default)
    {
        var key = workingDirectory ?? string.Empty;
        if (!forceRefresh)
        {
            lock (_lock)
            {
                if (_cached is not null && string.Equals(_cachedKey, key, StringComparison.OrdinalIgnoreCase))
                    return _cached;
            }
        }

        var mcpJson = await SafeAsync(() => _runCli(new[] { "mcp", "list", "--json" }, workingDirectory, ct)).ConfigureAwait(false);
        var skillJson = await SafeAsync(() => _runCli(new[] { "skill", "list", "--json" }, workingDirectory, ct)).ConfigureAwait(false);

        var catalog = new CapabilityCatalog
        {
            McpServers = ParseMcpServers(mcpJson),
            Skills = ParseSkills(skillJson),
            Agents = ScanAgents(workingDirectory),
            Plugins = GetInstalledPlugins(),
        };

        lock (_lock) { _cached = catalog; _cachedKey = key; }
        return catalog;
    }

    // ----- parsing (internal for tests) -----

    internal static IReadOnlyList<McpServerInfo> ParseMcpServers(string? json)
    {
        var list = new List<McpServerInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) || servers.ValueKind != JsonValueKind.Object)
                return list;
            foreach (var prop in servers.EnumerateObject())
            {
                // Only read name/source/type — never the headers/url (they can hold auth tokens).
                var v = prop.Value;
                var source = v.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : string.Empty;
                var type = v.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;
                list.Add(new McpServerInfo { Name = prop.Name, Source = source, Type = type });
            }
        }
        catch (JsonException) { return new List<McpServerInfo>(); }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static IReadOnlyList<SkillInfo> ParseSkills(string? json)
    {
        var list = new List<SkillInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var source = el.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : string.Empty;
                var desc = el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;
                list.Add(new SkillInfo { Name = name, Source = source, Description = desc });
            }
        }
        catch (JsonException) { return new List<SkillInfo>(); }
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Best-effort scan for custom agent definitions (one *.md per agent) in the
    /// locations the CLI itself loads from: project <c>.github/agents</c> + <c>.claude/agents</c>,
    /// personal <c>~/.copilot/agents</c> + <c>~/.claude/agents</c>, and every enabled plugin
    /// under <c>~/.copilot/installed-plugins</c>. Plugin agents are namespaced
    /// <c>&lt;plugin&gt;:&lt;agent&gt;</c>, matching how <c>--agent</c> resolves them.
    /// The UI also allows free-text entry, so misses are fine.</summary>
    internal static IReadOnlyList<string> ScanAgents(string? workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ScanAgents(workingDirectory, Path.Combine(home, ".copilot"), Path.Combine(home, ".claude"));
    }

    /// <summary><c>~/.copilot</c> — the CLI's user config root.</summary>
    internal static string DefaultCopilotHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    /// <summary>Test-friendly overload with explicit config roots.</summary>
    internal static IReadOnlyList<string> ScanAgents(string? workingDirectory, string copilotHome, string? claudeHome)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            foreach (var rel in new[] { @".github\agents", @".copilot\agents", @".agents\agents", @".claude\agents" })
                dirs.Add(Path.Combine(workingDirectory, rel));
        }
        if (!string.IsNullOrWhiteSpace(copilotHome)) dirs.Add(Path.Combine(copilotHome, "agents"));
        if (!string.IsNullOrWhiteSpace(claudeHome)) dirs.Add(Path.Combine(claudeHome, "agents"));

        foreach (var dir in dirs)
            foreach (var name in AgentNamesIn(dir))
                names.Add(name);

        foreach (var (pluginName, pluginDir) in EnumerateEnabledPlugins(copilotHome))
            foreach (var path in PluginAgentPaths(pluginDir))
                foreach (var name in AgentNamesAt(path))
                    names.Add($"{pluginName}:{name}");

        return names.ToList();
    }

    /// <summary>Agent names for one directory of <c>*.md</c> / <c>*.agent.md</c> files.</summary>
    private static IEnumerable<string> AgentNamesIn(string dir)
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(dir)) return result;
            // Enumerate everything and filter in code: a "*.md" search pattern also
            // matches 8.3 short names on Windows (so "foo.md.disabled" can slip in).
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = AgentNameFromFile(f);
                if (name is not null) result.Add(name);
            }
        }
        catch { /* unreadable dir — skip */ }
        return result;
    }

    /// <summary>Agent names for a manifest entry that may point at a single file or a directory.</summary>
    private static IEnumerable<string> AgentNamesAt(string path)
    {
        try
        {
            if (Directory.Exists(path)) return AgentNamesIn(path);
            var name = AgentNameFromFile(path);
            if (name is not null && File.Exists(path)) return new[] { name };
        }
        catch { /* unreadable path — skip */ }
        return Array.Empty<string>();
    }

    /// <summary>Agent id for a file path, or null when it isn't an agent markdown file.
    /// Mirrors the CLI: strip a trailing <c>.agent.md</c> or <c>.md</c>.</summary>
    internal static string? AgentNameFromFile(string path)
    {
        var file = Path.GetFileName(path);
        if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
        var name = file[..^3];
        if (name.EndsWith(".agent", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Equals("README", StringComparison.OrdinalIgnoreCase)) return null;
        return name;
    }

    /// <summary>Enabled plugins that have a resolvable folder — the set whose agents the CLI
    /// will actually load.</summary>
    internal static IReadOnlyList<(string Name, string Dir)> EnumerateEnabledPlugins(string copilotHome) =>
        EnumerateInstalledPlugins(copilotHome)
            .Where(p => p.Enabled && !string.IsNullOrEmpty(p.Directory))
            .Select(p => (p.Name, p.Directory))
            .ToList();

    /// <summary>
    /// Every plugin listed in <c>~/.copilot/config.json</c>, enabled or not. Enabled state
    /// matters for agent discovery; the disabled ones still matter for the per-repo
    /// <c>enabledPlugins</c> allowlist, which must name every plugin explicitly.
    /// </summary>
    internal static IReadOnlyList<InstalledPluginInfo> EnumerateInstalledPlugins(string copilotHome)
    {
        var results = new List<InstalledPluginInfo>();
        if (string.IsNullOrWhiteSpace(copilotHome)) return results;

        var configPath = Path.Combine(copilotHome, "config.json");
        string json;
        try
        {
            if (!File.Exists(configPath)) return results;
            json = File.ReadAllText(configPath);
        }
        catch { return results; }

        try
        {
            using var doc = JsonDocument.Parse(json, JsoncOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return results;

            // The CLI has written both spellings over time.
            if (!doc.RootElement.TryGetProperty("installedPlugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                if (!doc.RootElement.TryGetProperty("installed_plugins", out plugins) || plugins.ValueKind != JsonValueKind.Array)
                    return results;

            foreach (var el in plugins.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var name = ReadJsonString(el, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var marketplace = ReadJsonString(el, "marketplace");

                // A plugin with no cache_path and no marketplace still has a valid
                // enabledPlugins key, so it must NOT be dropped — omitting it from the
                // allowlist would silently disable it in every repo we write.
                var dir = ReadJsonString(el, "cache_path");
                if (string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(marketplace))
                    dir = Path.Combine(copilotHome, "installed-plugins", marketplace, name);

                // Absent "enabled" means enabled — only an explicit false turns it off.
                var enabled = !el.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;

                results.Add(new InstalledPluginInfo
                {
                    Name = name,
                    Marketplace = marketplace,
                    Directory = dir,
                    Enabled = enabled,
                });
            }
        }
        catch (JsonException) { return new List<InstalledPluginInfo>(); }

        return results
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Marketplace, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly string[] ManifestDirs = { ".plugin", ".", ".github/plugin", ".claude-plugin" };

    /// <summary>The CLI writes its own config as JSONC (leading <c>//</c> banner comments), so
    /// on-disk config + plugin manifests must be parsed leniently.</summary>
    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Agent file/dir paths for a plugin. Honors the optional <c>agents</c> field in
    /// <c>plugin.json</c> (string | string[] | { paths, exclusive }); defaults to <c>&lt;plugin&gt;/agents</c>.
    /// Paths that escape the plugin directory are dropped.</summary>
    internal static IReadOnlyList<string> PluginAgentPaths(string pluginDir)
    {
        var defaultDir = Path.Combine(pluginDir, "agents");
        JsonElement agents;
        try
        {
            if (!TryReadPluginManifest(pluginDir, out var manifest)) return new[] { defaultDir };
            using (manifest)
            {
                if (!manifest.RootElement.TryGetProperty("agents", out var found)) return new[] { defaultDir };
                agents = found.Clone();
            }
        }
        catch { return new[] { defaultDir }; }

        var paths = new List<string>();
        var exclusive = true;
        switch (agents.ValueKind)
        {
            case JsonValueKind.String:
                AddPluginPath(paths, pluginDir, agents.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var el in agents.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String) AddPluginPath(paths, pluginDir, el.GetString());
                break;
            case JsonValueKind.Object:
                if (agents.TryGetProperty("paths", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var el in list.EnumerateArray())
                        if (el.ValueKind == JsonValueKind.String) AddPluginPath(paths, pluginDir, el.GetString());
                exclusive = agents.TryGetProperty("exclusive", out var ex) && ex.ValueKind == JsonValueKind.True;
                break;
            default:
                return new[] { defaultDir };
        }

        if (!exclusive) paths.Insert(0, defaultDir);
        return paths.Count == 0 ? new[] { defaultDir } : paths;
    }

    private static void AddPluginPath(List<string> into, string pluginDir, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return;
        if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];

        string full, root;
        try
        {
            root = Path.GetFullPath(pluginDir);
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch { return; }

        // Never follow a manifest path outside its own plugin folder.
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

        into.Add(full);
    }

    private static bool TryReadPluginManifest(string pluginDir, out JsonDocument manifest)
    {
        manifest = null!;
        foreach (var rel in ManifestDirs)
        {
            var path = Path.Combine(pluginDir, rel.Replace('/', Path.DirectorySeparatorChar), "plugin.json");
            try
            {
                if (!File.Exists(path)) continue;
                manifest = JsonDocument.Parse(File.ReadAllText(path), JsoncOptions);
                return true;
            }
            catch { /* unreadable / malformed manifest — try the next location */ }
        }
        return false;
    }

    private static string ReadJsonString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    // ----- real CLI runner -----

    private static async Task<string?> RunCopilotAsync(IReadOnlyList<string> args, string? workingDir, CancellationToken ct)
    {
        var target = ProcessUtil.Resolve(TerminalDiscoveryService.ResolveOnPath);
        if (target is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = target.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir))
            psi.WorkingDirectory = workingDir;
        foreach (var a in target.PrefixArgs) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return null;
            var readTask = p.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return await readTask.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private static async Task<string?> SafeAsync(Func<Task<string?>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch { return null; }
    }
}
