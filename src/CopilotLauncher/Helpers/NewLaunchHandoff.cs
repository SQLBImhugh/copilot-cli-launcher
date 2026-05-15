namespace CopilotLauncher.Helpers;

/// <summary>
/// Static one-shot payload for the "Save as launch from session" flow. The
/// Sessions page sets <see cref="Pending"/> + asks the main window to switch
/// to the New Launch tab; New Launch page reads + clears it on Loaded.
///
/// Static-shared state is acceptable here because there's exactly one main
/// window and one of each page; a real navigation service is overkill until
/// we add multi-window or deep-link support.
/// </summary>
internal static class NewLaunchHandoff
{
    public static NewLaunchPayload? Pending { get; set; }
}

internal sealed record NewLaunchPayload(string SuggestedLabel, string WorkingDirectory, string? ResumeId);
