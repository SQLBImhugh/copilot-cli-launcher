namespace CopilotLauncher.Models;

/// <summary>One MCP server the CLI knows about (from <c>copilot mcp list --json</c>).</summary>
public sealed class McpServerInfo
{
    public required string Name { get; init; }

    /// <summary>"user" | "workspace" | "plugin" | "builtin".</summary>
    public required string Source { get; init; }

    /// <summary>"http" | "local" | "sse" etc. May be empty if unknown.</summary>
    public string Type { get; init; } = string.Empty;
}

/// <summary>One skill the CLI knows about (from <c>copilot skill list --json</c>). Display-only —
/// the CLI has no per-session skill selection flag, so this just informs the "disable all" toggle.</summary>
public sealed class SkillInfo
{
    public required string Name { get; init; }

    /// <summary>"personal-copilot" | "project" | "plugin" | "custom" etc.</summary>
    public string Source { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// One plugin from <c>~/.copilot/config.json</c>'s <c>installedPlugins</c> array. Plugins are
/// the delivery vehicle for most MCP servers, agents, and skills, and they can be toggled
/// per-repository via <c>.github/copilot/settings.json</c> — which is why the launcher needs
/// their CLI identity rather than just their folder name.
/// </summary>
public sealed class InstalledPluginInfo
{
    public required string Name { get; init; }

    /// <summary>Marketplace/collection the plugin was installed from. May be empty.</summary>
    public string Marketplace { get; init; } = string.Empty;

    /// <summary>Whether the plugin is enabled at the user level.</summary>
    public bool Enabled { get; init; }

    /// <summary>Absolute path to the plugin's folder on disk. May be empty when the config
    /// entry has neither a <c>cache_path</c> nor a marketplace to derive one from; only agent
    /// scanning needs it, so such a plugin is still listed for allowlist purposes.</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>The identifier the CLI uses as an <c>enabledPlugins</c> key: <c>name@marketplace</c>
    /// (falling back to bare name when the plugin has no marketplace).</summary>
    public string Key => string.IsNullOrWhiteSpace(Marketplace) ? Name : $"{Name}@{Marketplace}";

    /// <summary>"winui (awesome-copilot)" — for display in pickers.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Marketplace) ? Name : $"{Name} ({Marketplace})";
}

/// <summary>
/// The set of capabilities discoverable for a given working directory, used to
/// populate the capability selector UI. Built by
/// <see cref="Services.ISessionCapabilityService"/>.
/// </summary>
public sealed class CapabilityCatalog
{
    public IReadOnlyList<McpServerInfo> McpServers { get; init; } = Array.Empty<McpServerInfo>();
    public IReadOnlyList<SkillInfo> Skills { get; init; } = Array.Empty<SkillInfo>();

    /// <summary>Custom agent names discovered under the working dir + user config.</summary>
    public IReadOnlyList<string> Agents { get; init; } = Array.Empty<string>();

    /// <summary>Every installed plugin (enabled and disabled), for per-repo plugin toggling.</summary>
    public IReadOnlyList<InstalledPluginInfo> Plugins { get; init; } = Array.Empty<InstalledPluginInfo>();

    public static CapabilityCatalog Empty { get; } = new();
}
