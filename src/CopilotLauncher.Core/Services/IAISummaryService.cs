namespace CopilotLauncher.Services;

/// <summary>
/// Generates an AI-authored briefing summary for a Copilot CLI version bump.
/// Implementations spawn the user's locally-installed `copilot` CLI in
/// non-interactive mode (`copilot -p "..."`). The Core layer ships a no-op
/// default (<see cref="NoopAISummaryService"/>); the real implementation
/// lives alongside the others in Core but is wired from the WinUI app's DI.
///
/// Returns <c>null</c> when the user has the feature disabled, the CLI is
/// unavailable, the call times out, or the response is empty — callers
/// should fall back to the bundled-changelog body in those cases.
/// </summary>
public interface IAISummaryService
{
    /// <summary>True if the feature is enabled in settings (and therefore worth calling).</summary>
    bool IsEnabled { get; }

    Task<string?> GenerateAsync(
        string fromVersion,
        string toVersion,
        string changelogText,
        CancellationToken ct = default);
}

/// <summary>Default no-op (used until the real <c>AISummaryService</c> ships).</summary>
public sealed class NoopAISummaryService : IAISummaryService
{
    public bool IsEnabled => false;
    public Task<string?> GenerateAsync(string fromVersion, string toVersion, string changelogText, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
