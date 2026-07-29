using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

/// <summary>How much control the launcher has over one in-repo config file.</summary>
public enum RepoConfigKind
{
    /// <summary>The launcher can read AND write this file.</summary>
    Managed = 0,

    /// <summary>The CLI reads it from the directory, but the launcher only reports its presence.</summary>
    Detected = 1,
}

/// <summary>One copilot CLI config file the working directory can supply.</summary>
public sealed class RepoConfigFile
{
    public required string RelativePath { get; init; }
    public required string AbsolutePath { get; init; }
    public required string Description { get; init; }
    public required bool Exists { get; init; }
    public RepoConfigKind Kind { get; init; } = RepoConfigKind.Detected;

    /// <summary>True when the path is a directory convention rather than a single file.</summary>
    public bool IsDirectory { get; init; }
}

/// <summary>What a given working directory already configures for the copilot CLI.</summary>
public sealed class RepoConfigStatus
{
    public required string Directory { get; init; }
    public required IReadOnlyList<RepoConfigFile> Files { get; init; }

    /// <summary>Parsed <c>enabledPlugins</c> from <c>.github/copilot/settings.json</c>, or null
    /// when the file is missing or doesn't set the key.</summary>
    public IReadOnlyDictionary<string, bool>? EnabledPlugins { get; init; }

    public IEnumerable<RepoConfigFile> Present => Files.Where(f => f.Exists);

    public int PresentCount => Files.Count(f => f.Exists);

    /// <summary>True when the repo pins its plugin set (so the launcher doesn't need flags for it).</summary>
    public bool ManagesPlugins => EnabledPlugins is { Count: > 0 };
}

/// <summary>
/// Reads and writes the copilot CLI config that lives <em>inside</em> a project
/// directory, so per-project behavior can be pinned in the repo rather than
/// re-supplied as startup flags on every launch.
/// </summary>
/// <remarks>
/// What the CLI honors from a working directory (verified against the
/// <c>@github/copilot</c> bundle, v1.0.x):
/// <list type="bullet">
/// <item><c>.github/copilot/settings.json</c> — <c>enabledPlugins</c>, <c>hooks</c>,
/// <c>disableAllHooks</c>, <c>mergeStrategy</c>, <c>extraKnownMarketplaces</c>. Merged over the
/// user config at session start. The launcher WRITES <c>enabledPlugins</c> here.</item>
/// <item><c>.mcp.json</c> / <c>.github/mcp.json</c> — workspace MCP servers (adds servers; it
/// cannot switch off a user- or plugin-provided one).</item>
/// <item><c>.github/copilot-instructions.md</c>, <c>AGENTS.md</c>, <c>CLAUDE.md</c> — instructions.</item>
/// <item><c>.github/agents/</c>, <c>.github/skills/</c> — repo-scoped agents and skills.</item>
/// <item><c>.github/lsp.json</c> — repo language servers.</item>
/// </list>
/// There is NO in-repo equivalent for <c>--agent</c>, <c>--available-tools</c>,
/// <c>--excluded-tools</c>, <c>--allow-all</c>, or <c>--disable-mcp-server</c>; those stay
/// startup flags supplied by the launcher.
/// </remarks>
public interface IRepoConfigService
{
    /// <summary>Report which CLI config files <paramref name="directory"/> already provides.
    /// Best-effort — never throws.</summary>
    RepoConfigStatus Inspect(string? directory);

    /// <summary>
    /// Pin the plugin set for <paramref name="directory"/> in
    /// <c>.github/copilot/settings.json</c>. Every plugin in
    /// <paramref name="allPlugins"/> is written explicitly (true/false) because the CLI treats
    /// <c>enabledPlugins</c> as an allowlist — anything not present as <c>true</c> is dropped.
    /// Returns true when the file was written.
    /// </summary>
    bool WriteEnabledPlugins(string directory, IEnumerable<InstalledPluginInfo> allPlugins, IEnumerable<string> enabledKeys);

    /// <summary>Remove the <c>enabledPlugins</c> key, handing plugin selection back to the user
    /// config. Other keys in the file are preserved. Returns true when something changed.</summary>
    bool ClearEnabledPlugins(string directory);
}

public sealed class RepoConfigService : IRepoConfigService
{
    /// <summary>The repo settings file the CLI merges over the user config.</summary>
    internal static readonly string SettingsRelativePath = Path.Combine(".github", "copilot", "settings.json");

    private const string EnabledPluginsKey = "enabledPlugins";

    public RepoConfigStatus Inspect(string? directory)
    {
        var root = TryFullPath(directory);
        if (root is null)
        {
            return new RepoConfigStatus
            {
                Directory = directory ?? string.Empty,
                Files = Array.Empty<RepoConfigFile>(),
            };
        }

        var files = new List<RepoConfigFile>
        {
            Describe(root, SettingsRelativePath, "Repo settings — plugin allowlist, hooks, merge strategy", RepoConfigKind.Managed),
            Describe(root, Path.Combine(".github", "copilot", "settings.local.json"), "Personal (git-ignored) overrides of the repo settings"),
            Describe(root, ".mcp.json", "Workspace MCP servers"),
            Describe(root, Path.Combine(".github", "mcp.json"), "Workspace MCP servers (alternate location)"),
            Describe(root, Path.Combine(".github", "copilot-instructions.md"), "Repo instructions injected into the system prompt"),
            Describe(root, "AGENTS.md", "Repo instructions (AGENTS.md convention)"),
            Describe(root, "CLAUDE.md", "Repo instructions (CLAUDE.md convention)"),
            Describe(root, Path.Combine(".github", "agents"), "Repo-scoped custom agents", isDirectory: true),
            Describe(root, Path.Combine(".github", "skills"), "Repo-scoped skills", isDirectory: true),
            Describe(root, Path.Combine(".github", "lsp.json"), "Repo language-server configuration"),
        };

        return new RepoConfigStatus
        {
            Directory = root,
            Files = files,
            EnabledPlugins = ReadEnabledPlugins(Path.Combine(root, SettingsRelativePath)),
        };
    }

    public bool WriteEnabledPlugins(string directory, IEnumerable<InstalledPluginInfo> allPlugins, IEnumerable<string> enabledKeys)
    {
        var root = TryFullPath(directory);
        if (root is null || !Directory.Exists(root)) return false;

        var plugins = (allPlugins ?? Enumerable.Empty<InstalledPluginInfo>()).ToList();
        if (plugins.Count == 0) return false;

        var enabled = new HashSet<string>(enabledKeys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var path = Path.Combine(root, SettingsRelativePath);
        var root_ = LoadOrCreate(path);
        if (root_ is null) return false;   // present but unparseable — never clobber

        var map = new JsonObject();
        foreach (var p in plugins)
            map[p.Key] = enabled.Contains(p.Key);
        root_[EnabledPluginsKey] = map;

        return WriteAtomic(path, root_);
    }

    public bool ClearEnabledPlugins(string directory)
    {
        var root = TryFullPath(directory);
        if (root is null) return false;

        var path = Path.Combine(root, SettingsRelativePath);
        if (!File.Exists(path)) return false;

        var obj = LoadOrCreate(path);
        if (obj is null || !obj.ContainsKey(EnabledPluginsKey)) return false;

        obj.Remove(EnabledPluginsKey);
        return WriteAtomic(path, obj);
    }

    // ----- helpers -----

    internal static IReadOnlyDictionary<string, bool>? ReadEnabledPlugins(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return null;
            var node = JsonNode.Parse(File.ReadAllText(settingsPath));
            if (node is not JsonObject obj) return null;
            if (obj[EnabledPluginsKey] is not JsonObject map) return null;

            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in map)
            {
                if (kvp.Value is JsonValue v && v.TryGetValue<bool>(out var b))
                    result[kvp.Key] = b;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static RepoConfigFile Describe(
        string root,
        string relative,
        string description,
        RepoConfigKind kind = RepoConfigKind.Detected,
        bool isDirectory = false)
    {
        var abs = Path.Combine(root, relative);
        var exists = isDirectory ? Directory.Exists(abs) : File.Exists(abs);
        return new RepoConfigFile
        {
            RelativePath = relative.Replace('\\', '/'),
            AbsolutePath = abs,
            Description = description,
            Exists = exists,
            Kind = kind,
            IsDirectory = isDirectory,
        };
    }

    /// <summary>Existing object, a fresh one when the file is absent, or null when the file
    /// exists but can't be parsed (in which case we refuse to touch it).</summary>
    private static JsonObject? LoadOrCreate(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool WriteAtomic(string path, JsonObject root)
    {
        try
        {
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            // This file usually lives in the user's git repo and SyncRepoConfigOnLaunch
            // rewrites it on every launch. Skip identical writes so a project launch
            // doesn't churn the working tree (or the backup) for no reason.
            if (File.Exists(path))
            {
                try
                {
                    if (string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
                        return true;
                }
                catch (IOException) { /* fall through and attempt the write */ }
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Single fixed backup name: a timestamped one would accumulate an unbounded
            // pile of untracked files inside the repo.
            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", overwrite: true); }
                catch { /* best effort */ }
            }

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryFullPath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim())); }
        catch { return null; }
    }
}
