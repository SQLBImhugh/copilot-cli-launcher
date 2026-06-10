namespace CopilotLauncher.Models;

/// <summary>
/// User-defined launch shortcut stored in shortcuts.json.
/// Each entry is one row in the Shortcuts tab.
/// </summary>
public sealed class Shortcut
{
    public required string Id { get; init; }              // GUID
    public required string Label { get; set; }
    public required string WorkingDirectory { get; set; }

    /// <summary>Optional. Empty = fresh session each launch.</summary>
    public string? ResumeTarget { get; set; }

    public bool EnableAllowAll { get; set; }
    public string? ExtraCopilotArgs { get; set; }

    /// <summary>
    /// Optional terminal override. Null = use the global default from <see cref="AppSettings"/>.
    /// </summary>
    public string? TerminalOverride { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
