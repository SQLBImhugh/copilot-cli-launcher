using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace CopilotLauncher.Helpers;

internal static class SessionLock
{
    public static bool IsInUse(string sessionFolder) =>
        IsInUse(sessionFolder, GetProcessStartTimeUtc);

    internal static bool IsInUse(string sessionFolder, Func<int, DateTime?> getProcessStartTimeUtc)
    {
        try
        {
            foreach (var marker in new DirectoryInfo(sessionFolder).EnumerateFiles("inuse.*.lock"))
            {
                var owner = marker.Name.Length < "inuse..lock".Length
                    ? ReadOnlySpan<char>.Empty
                    : marker.Name.AsSpan("inuse.".Length, marker.Name.Length - "inuse..lock".Length);
                if (!int.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                {
                    Trace.TraceWarning("Unrecognized session lock {0}; keeping the session protected.", marker.FullName);
                    return true;
                }

                var started = getProcessStartTimeUtc(pid);
                if (started is null)
                    continue;

                marker.Refresh();
                if (!marker.Exists)
                    continue;

                // A newer process with the same PID does not own an older marker.
                var markerTime = marker.CreationTimeUtc > marker.LastWriteTimeUtc
                    ? marker.CreationTimeUtc
                    : marker.LastWriteTimeUtc;
                if (started.Value <= markerTime)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            Trace.TraceWarning("Could not verify session locks in {0}; keeping the session protected: {1}",
                sessionFolder, ex.Message);
            return true;
        }
    }

    private static DateTime? GetProcessStartTimeUtc(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
