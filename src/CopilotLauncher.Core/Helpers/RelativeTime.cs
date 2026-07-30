namespace CopilotLauncher.Helpers;

/// <summary>
/// Shared "how long ago" formatting so the Sessions list and the project import
/// picker describe the same timestamp identically.
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// "just now" / "5m ago" / "3h ago" / "2d ago", falling back to an absolute
    /// <c>yyyy-MM-dd</c> local date once the timestamp is a week old — at that
    /// distance a precise date is more useful than a fuzzy interval.
    /// </summary>
    public static string Humanize(DateTime when, DateTime? nowUtc = null)
    {
        var span = (nowUtc ?? DateTime.UtcNow) - when.ToUniversalTime();
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    /// <summary>Absolute local calendar date, always <c>yyyy-MM-dd</c>.</summary>
    public static string ToLocalDate(DateTime when) => when.ToLocalTime().ToString("yyyy-MM-dd");
}
