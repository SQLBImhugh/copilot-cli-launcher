namespace CopilotLauncher.Models;

/// <summary>
/// One Copilot CLI session as discovered from ~/.copilot/session-state/&lt;uuid&gt;/workspace.yaml.
/// Populated by <see cref="Services.SessionDiscoveryService"/>.
/// </summary>
public sealed class CopilotSession
{
    public required string Id { get; init; }
    public required DateTime LastModified { get; init; }

    /// <summary>User-given name from <c>copilot --name &lt;...&gt;</c>. Null if unnamed.</summary>
    public string? Name { get; init; }

    /// <summary>Optional short summary stored alongside the name. May be auto-generated.</summary>
    public string? Summary { get; init; }

    public string? Cwd { get; init; }
    public string? Repository { get; init; }
    public string? Branch { get; init; }
    public string? GitRoot { get; init; }
    public string? HostType { get; init; }
    public bool UserNamed { get; init; }
    public int SummaryCount { get; init; }
    public DateTime? CreatedAt { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Path to the session folder on disk.</summary>
    public required string FolderPath { get; init; }

    /// <summary>True if a sibling inuse.*.lock file is present (active session).</summary>
    public bool IsLocked { get; init; }
}

