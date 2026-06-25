namespace CopilotLauncher.Models;

/// <summary>How the per-launch tool list (if any) is interpreted by copilot.</summary>
public enum ToolFilterMode
{
    /// <summary>No tool restriction (all tools available).</summary>
    None = 0,

    /// <summary>Only the listed tools are available — maps to <c>--available-tools</c>.</summary>
    OnlyThese = 1,

    /// <summary>The listed tools are hidden from the model — maps to <c>--excluded-tools</c>.</summary>
    ExcludeThese = 2,
}

/// <summary>
/// Per-launch selection of which copilot capabilities load. Persisted on
/// <see cref="Shortcut"/> and in <see cref="AppSettings"/> (as the defaults),
/// and carried on <see cref="Services.LaunchRequest"/>. Translated to copilot
/// CLI flags by <see cref="Services.LaunchService"/>.
///
/// Capability → flag mapping (copilot 1.0.x):
///   DisabledMcpServers   → --disable-mcp-server &lt;name&gt; (repeatable)
///   DisableBuiltinMcps   → --disable-builtin-mcps (the built-in GitHub MCP)
///   Agent                → --agent &lt;name&gt; (single custom agent)
///   ToolMode + Tools     → --available-tools / --excluded-tools (variadic)
///   DisableAllSkills     → excludes the `skill` tool (the CLI has no
///                          per-session skill subset flag, so this is
///                          all-or-nothing).
/// </summary>
public sealed class LaunchCapabilities
{
    /// <summary>MCP server names to turn OFF for this launch (everything not listed stays on).</summary>
    public List<string> DisabledMcpServers { get; set; } = new();

    /// <summary>Disable the built-in GitHub MCP server (<c>--disable-builtin-mcps</c>).</summary>
    public bool DisableBuiltinMcps { get; set; }

    /// <summary>Custom agent name to launch with (<c>--agent</c>). Null/blank = none.</summary>
    public string? Agent { get; set; }

    /// <summary>Whether <see cref="Tools"/> is an allowlist, denylist, or unused.</summary>
    public ToolFilterMode ToolMode { get; set; } = ToolFilterMode.None;

    /// <summary>Tool names for the allow/exclude list (e.g. <c>write</c>, <c>shell(git push)</c>).</summary>
    public List<string> Tools { get; set; } = new();

    /// <summary>Disable ALL skills by excluding the `skill` tool.</summary>
    public bool DisableAllSkills { get; set; }

    /// <summary>True when nothing is selected — lets the launcher skip emitting any flags.</summary>
    public bool IsEmpty =>
        DisabledMcpServers.Count == 0
        && !DisableBuiltinMcps
        && string.IsNullOrWhiteSpace(Agent)
        && (ToolMode == ToolFilterMode.None || Tools.Count == 0)
        && !DisableAllSkills;

    /// <summary>Deep copy — used when seeding a form from the saved defaults so edits don't mutate them.</summary>
    public LaunchCapabilities Clone() => new()
    {
        DisabledMcpServers = new List<string>(DisabledMcpServers),
        DisableBuiltinMcps = DisableBuiltinMcps,
        Agent = Agent,
        ToolMode = ToolMode,
        Tools = new List<string>(Tools),
        DisableAllSkills = DisableAllSkills,
    };
}
