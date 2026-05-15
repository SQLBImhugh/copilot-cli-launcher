namespace CopilotLauncher.Models;

/// <summary>
/// User-defined launch shortcut stored in launches.json.
/// Each entry is one row in the Saved Launches tab.
/// </summary>
public sealed class SavedLaunch
{
    public required string Id { get; init; }              // GUID
    public required string Label { get; set; }
    public required string WorkingDirectory { get; set; }

    /// <summary>Optional. Empty = fresh session each launch.</summary>
    public string? ResumeTarget { get; set; }

    public bool EnableAISummary { get; set; }
    public bool EnableAllowAll { get; set; }
    public string? ExtraCopilotArgs { get; set; }

    /// <summary>
    /// Optional terminal override. Null = use the global default from <see cref="AppSettings"/>.
    /// </summary>
    public string? TerminalOverride { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
