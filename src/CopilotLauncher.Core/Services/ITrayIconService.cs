namespace CopilotLauncher.Services;

/// <summary>
/// Owns the system-tray (NotifyIcon) lifecycle. The Core layer can't depend
/// on H.NotifyIcon.WinUI directly, so this interface lets the launcher's
/// behavior helpers (e.g. <see cref="IAfterLaunchAction"/>) know whether
/// hiding to tray is currently safe.
///
/// The WinUI app provides the real implementation; Core uses a no-op so
/// services that depend on this interface remain testable.
/// </summary>
public interface ITrayIconService
{
    /// <summary>True if the tray icon is initialized and visible (so it's safe to hide the main window).</summary>
    bool IsActive { get; }

    /// <summary>True while the app is intentionally exiting via the tray menu.</summary>
    bool IsQuitting { get; }

    /// <summary>Called once at app startup to install the tray icon.</summary>
    void Initialize();

    /// <summary>Called at app shutdown to remove the tray icon.</summary>
    void Shutdown();
}

/// <summary>Default no-op used until the real implementation ships.</summary>
public sealed class NoopTrayIconService : ITrayIconService
{
    public bool IsActive => false;
    public bool IsQuitting => false;
    public void Initialize() { }
    public void Shutdown() { }
}
