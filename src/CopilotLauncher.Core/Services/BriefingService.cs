using System.Text;

namespace CopilotLauncher.Services;

/// <summary>
/// One release entry, normalized from either the bundled changelog format or
/// the GitHub Releases API response.
/// </summary>
public sealed class ReleaseEntry
{
    public required string Version { get; init; }
    public DateTime? Date { get; init; }
    public string? Body { get; init; }
}

public interface IBriefingService
{
    /// <summary>
    /// Render a markdown briefing for the supplied entries. Caller is
    /// responsible for filtering entries to the appropriate version range
    /// (semantic version comparison happens at the change-discovery layer
    /// in <c>UpdateCheckService</c> + bundled-changelog/GH-Releases readers,
    /// not here).
    /// </summary>
    string Render(string fromVersion, string toVersion, IEnumerable<ReleaseEntry> entries);
}

public sealed class BriefingService : IBriefingService
{
    public string Render(string fromVersion, string toVersion, IEnumerable<ReleaseEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Copilot CLI updated: {fromVersion} -> {toVersion}");
        sb.AppendLine();

        foreach (var e in entries)
        {
            sb.AppendLine($"## v{e.Version}");
            if (e.Date is DateTime d)
            {
                // DateTime fix carried over from the legacy launcher: Invoke-RestMethod
                // auto-deserializes ISO timestamps into DateTime values, so .Substring
                // would crash. Always format via ToString.
                sb.AppendLine(d.ToString("yyyy-MM-dd"));
            }
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(e.Body))
            {
                sb.AppendLine(e.Body.TrimEnd());
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
