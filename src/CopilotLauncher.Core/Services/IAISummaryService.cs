namespace CopilotLauncher.Services;

/// <summary>
/// Generates an AI-authored briefing summary for a Copilot CLI version bump.
/// Implementations spawn the user's locally-installed `copilot` CLI in
/// non-interactive mode (`copilot -p "..."`). The Core layer ships a no-op
/// default (<see cref="NoopAISummaryService"/>); the real implementation
/// lives alongside the others in Core but is wired from the WinUI app's DI.
///
/// Explicit generation is on-demand regardless of the legacy enable setting.
/// Returns <c>null</c> for empty input or the no-op implementation. The real
/// implementation throws on missing CLI, launch errors, nonzero exit, timeout,
/// cancellation, or missing assistant output so callers can display the cause.
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

    /// <summary>
    /// Same as <see cref="GenerateAsync"/> but instead of embedding the
    /// changelog text in the prompt (which is capped at
    /// <c>AISummaryPromptBuilder.ChangelogLimit</c>), it points the spawned
    /// copilot session at the on-disk <c>changelogs.json</c> and lets it read +
    /// filter the entries itself. Because generation runs with
    /// <c>--allow-all-tools</c>, the session can read the file directly, so the
    /// prompt-size limit is no longer a factor for arbitrarily large ranges.
    /// </summary>
    Task<string?> GenerateFromFileAsync(
        string fromVersion,
        string toVersion,
        string changelogFilePath,
        CancellationToken ct = default);
}

/// <summary>Default no-op (used until the real <c>AISummaryService</c> ships).</summary>
public sealed class NoopAISummaryService : IAISummaryService
{
    public bool IsEnabled => false;
    public Task<string?> GenerateAsync(string fromVersion, string toVersion, string changelogText, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    public Task<string?> GenerateFromFileAsync(string fromVersion, string toVersion, string changelogFilePath, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
