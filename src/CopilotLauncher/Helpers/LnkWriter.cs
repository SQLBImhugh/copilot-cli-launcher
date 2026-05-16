using System.Runtime.InteropServices;
using CopilotLauncher.Services;

namespace CopilotLauncher.Helpers;

/// <summary>
/// Writes a Windows .lnk file from a <see cref="ShortcutExportPlan"/>.
/// Lives in the WinUI app project (not Core) because it depends on the
/// <c>WScript.Shell</c> COM ProgID, which is Windows-only.
/// Tests cover the plan-builder side; this writer is one tiny COM call.
/// </summary>
public static class LnkWriter
{
    /// <summary>
    /// Writes the .lnk. If <paramref name="iconOverride"/> is supplied it
    /// wins over <see cref="ShortcutExportPlan.IconLocation"/>; that lets
    /// callers brand all exported shortcuts with the launcher's own icon
    /// instead of the terminal exe's icon (which is the plan default).
    /// </summary>
    public static void Write(ShortcutExportPlan plan, string outputPath, string? iconOverride = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell COM type not registered (Windows only).");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic sc = shell.CreateShortcut(outputPath);
            try
            {
                sc.TargetPath = plan.TargetPath;
                sc.Arguments = plan.Arguments;
                sc.WorkingDirectory = plan.WorkingDirectory;
                sc.Description = plan.Description;
                var icon = !string.IsNullOrEmpty(iconOverride) ? iconOverride : plan.IconLocation;
                if (!string.IsNullOrEmpty(icon))
                    sc.IconLocation = icon;
                sc.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(sc);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
