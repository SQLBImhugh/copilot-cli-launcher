namespace CopilotLauncher.Helpers;

/// <summary>
/// Picks the copilot CLI flag used to re-open an existing session.
/// </summary>
/// <remarks>
/// <para>
/// In interactive (TUI) mode, <c>copilot --resume=&lt;id&gt;</c> initializes <b>two</b>
/// workspaces in one process: a brand-new placeholder session <i>and</i> the session that
/// was actually requested. Extensions and the session host then bind to the placeholder,
/// so the requested session is left locked but never read and the UI sits on "Resuming"
/// indefinitely. The abandoned placeholder shows up as a stray few-KB session afterwards.
/// Verified against copilot 1.0.81-9: <c>--resume=&lt;uuid&gt;</c> logs two
/// "Workspace initialized" lines and creates a new session folder, while
/// <c>--session-id=&lt;uuid&gt;</c> logs one and creates none.
/// </para>
/// <para>
/// <c>--session-id</c> only accepts a full UUID, whereas <c>--resume</c> also accepts a
/// session name or an ID prefix. So the UUID case — every launch started from the Sessions
/// list — uses <c>--session-id</c>, and anything else falls back to <c>--resume</c>.
/// </para>
/// <para>
/// This applies to interactive launches only. Non-interactive <c>-p</c> runs (see
/// <c>AISummaryService</c>) never build a placeholder and resume by name, so they keep
/// using <c>--resume</c>.
/// </para>
/// </remarks>
public static class ResumeFlag
{
    /// <summary>
    /// The single argument that resumes <paramref name="resumeTarget"/>, or null when
    /// there is nothing to resume (a fresh session).
    /// </summary>
    public static string? Format(string? resumeTarget)
    {
        if (string.IsNullOrWhiteSpace(resumeTarget)) return null;

        var target = resumeTarget.Trim();
        return IsSessionId(target) ? $"--session-id={target}" : $"--resume={target}";
    }

    /// <summary>
    /// True when the target is a full canonical UUID — the form session folders use, and
    /// the only form <c>--session-id</c> accepts. Names and ID prefixes return false.
    /// </summary>
    public static bool IsSessionId(string? resumeTarget) =>
        !string.IsNullOrWhiteSpace(resumeTarget)
        && Guid.TryParseExact(resumeTarget.Trim(), "D", out _);
}
