namespace CopilotLauncher.Models;

/// <summary>
/// A per-directory launch profile, stored in <c>projects.json</c>. Whenever a
/// session is resumed (or a fresh session is started) whose working directory
/// falls under <see cref="Path"/>, these overrides replace the global
/// "Sessions Resume defaults" so the project always starts the same way.
///
/// Every override is nullable: <c>null</c> means "inherit the global default".
/// That keeps a project profile a sparse diff rather than a full snapshot, so
/// changing a global default still flows through to projects that didn't
/// deliberately opt out of it.
/// </summary>
public sealed class ProjectProfile
{
    public required string Id { get; init; }              // GUID

    public required string Label { get; set; }

    /// <summary>The working directory this profile applies to. Match key.</summary>
    public required string Path { get; set; }

    /// <summary>Also apply to sessions whose cwd is nested under <see cref="Path"/>.
    /// When several profiles match, the longest (most specific) path wins.</summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>Temporarily disable without deleting.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Override for <c>--allow-all</c>. Null = inherit.</summary>
    public bool? EnableAllowAll { get; set; }

    /// <summary>Override for extra copilot args. Null = inherit; empty string = deliberately none.</summary>
    public string? ExtraCopilotArgs { get; set; }

    /// <summary>Terminal id override (wt / pwsh / powershell / cmd). Null or blank = inherit.</summary>
    public string? TerminalOverride { get; set; }

    /// <summary>Override for the extension pre-approval pass. Null = inherit.</summary>
    public bool? PreApproveExtensions { get; set; }

    /// <summary>Capability selection (MCP servers / agent / tools / skills) for this project.
    /// Null = inherit. A non-null value REPLACES the global default wholesale rather than
    /// merging field-by-field — partial capability merges are ambiguous (is an empty tool
    /// list "inherit" or "clear"?) and would be impossible to reason about in the preview.</summary>
    public LaunchCapabilities? Capabilities { get; set; }

    /// <summary>
    /// Plugin keys (<c>name@marketplace</c>) to keep enabled for this project, mirrored into
    /// <c>.github/copilot/settings.json</c> so the CLI reads them from the repo instead of
    /// needing startup flags. Null = don't manage the repo file at all.
    /// See <see cref="Services.IRepoConfigService"/>.
    /// </summary>
    public List<string>? RepoEnabledPlugins { get; set; }

    /// <summary>When true, the launcher writes <see cref="RepoEnabledPlugins"/> into
    /// <c>.github/copilot/settings.json</c> before each launch of this project.</summary>
    public bool SyncRepoConfigOnLaunch { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
