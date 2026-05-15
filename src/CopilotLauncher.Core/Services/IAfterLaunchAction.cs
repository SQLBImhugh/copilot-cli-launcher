namespace CopilotLauncher.Services;

/// <summary>
/// Strategy for what the launcher window should do AFTER spawning a Copilot
/// session. The Core layer can't reference WinUI / Win32 window APIs directly,
/// so it expresses intent through this interface and the WinUI layer wires it
/// up. Tests can substitute a no-op or recorder.
///
/// Behavior strings come from <see cref="Models.LauncherBehaviorSettings.AfterLaunch"/>:
///   "stayOpen" (default), "minimize", "hideToTray", "close".
/// Implementations should treat unknown values as "stayOpen" (do nothing).
/// </summary>
public interface IAfterLaunchAction
{
    void Apply(string behavior);
}

/// <summary>No-op default used when no UI host is wired up (e.g., unit tests).</summary>
public sealed class NoopAfterLaunchAction : IAfterLaunchAction
{
    public void Apply(string behavior) { }
}
