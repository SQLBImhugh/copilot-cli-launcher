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

    public static CapabilityCatalog Empty { get; } = new();
}
