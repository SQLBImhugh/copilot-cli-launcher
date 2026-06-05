namespace CopilotLauncher.Models;

/// <summary>
/// One entry in the rolling changelog history. Persisted as JSON in
/// <c>%LOCALAPPDATA%\CopilotLauncher\changelogs.json</c> by
/// <see cref="Services.ChangelogHistoryService"/>.
/// </summary>
/// <remarks>
/// Introduced in v0.2.0 as part of the split between changelog (raw release
/// notes + update output, no AI) and briefing (AI-summary-only, on-demand).
/// Prior to v0.2.0 these two concepts lived together in <see cref="BriefingEntry"/>
/// — the migration helper in <see cref="Services.BriefingsSplitMigration"/>
/// extracts the changelog half into instances of this type.
/// </remarks>
public sealed class ChangelogEntry
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }

    /// <summary>From version (the version we previously recorded).</summary>
    public required string FromVersion { get; init; }

    /// <summary>To version (the new version after `copilot update`).</summary>
    public required string ToVersion { get; init; }

    /// <summary>
    /// Where this changelog came from: <c>copilot-update</c> (Check now button),
    /// <c>startup-update</c> (background check on launch), or <c>migrated</c>
    /// (split out of a pre-v0.2.0 briefing entry).
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Rendered markdown body: GitHub release notes + the raw `copilot update`
    /// stdout. Same content that BriefingService.Render historically produced.
    /// </summary>
    public required string Body { get; init; }
}
