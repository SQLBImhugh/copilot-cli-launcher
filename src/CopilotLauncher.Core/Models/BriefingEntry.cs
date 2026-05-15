namespace CopilotLauncher.Models;

/// <summary>
/// One entry in the rolling briefing history. Persisted as JSON in
/// <c>%LOCALAPPDATA%\CopilotLauncher\briefings.json</c> by
/// <see cref="Services.BriefingHistoryService"/>.
/// </summary>
public sealed class BriefingEntry
{
    public required string Id { get; init; }
    public required DateTime Timestamp { get; init; }

    /// <summary>From version (the version we previously recorded).</summary>
    public required string FromVersion { get; init; }

    /// <summary>To version (the new version after `copilot update`).</summary>
    public required string ToVersion { get; init; }

    /// <summary>Where this briefing came from: "bundled" / "github" / "ai-summary".</summary>
    public required string Source { get; init; }

    /// <summary>Rendered markdown body. Display as plain text or render in a future markdown control.</summary>
    public required string Body { get; init; }
}
