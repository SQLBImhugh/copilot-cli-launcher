using Microsoft.Win32;

namespace CopilotLauncher.Helpers;

/// <summary>
/// Manages the Windows "Start with Windows" registry entry. WinUI app code
/// calls Sync() whenever the SettingsViewModel.StartWithWindows toggle changes,
/// or on app startup to make sure the entry is consistent with current settings.
/// </summary>
public static class AutostartRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "CopilotCLILauncher";

    /// <summary>True if the autostart entry currently exists for the current user.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(EntryName) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Set autostart to <paramref name="enabled"/> for the current user. When enabled,
    /// the registry entry points at <paramref name="exePath"/> (the running app's exe).
    /// When disabled, removes the entry entirely. Best-effort: registry failures
    /// are surfaced via the return value but don't throw.
    /// </summary>
    public static bool Sync(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
                // Quote the path so spaces in install dir don't break the registry value.
                key.SetValue(EntryName, $"\"{exePath}\"");
            }
            else
            {
                if (key.GetValue(EntryName) is not null)
                    key.DeleteValue(EntryName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
