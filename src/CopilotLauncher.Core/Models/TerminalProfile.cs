namespace CopilotLauncher.Models;

/// <summary>
/// One installed terminal that the launcher can target. Discovered by
/// <see cref="Services.TerminalDiscoveryService"/> at startup; surfaced in
/// the Settings → Terminal drop-down.
/// </summary>
public sealed class TerminalProfile
{
    public required string Id { get; init; }            // wt | pwsh | powershell | cmd | custom-<n>
    public required string DisplayName { get; init; }
    public required string ExecutablePath { get; init; }

    /// <summary>True if this terminal supports tab/window placement (wt.exe).</summary>
    public bool SupportsTabs { get; init; }

    /// <summary>True if this terminal natively understands -d &lt;workdir&gt;.</summary>
    public bool SupportsWorkingDirectoryFlag { get; init; }
}
