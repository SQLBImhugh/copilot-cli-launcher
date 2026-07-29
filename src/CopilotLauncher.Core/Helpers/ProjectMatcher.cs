using CopilotLauncher.Models;

namespace CopilotLauncher.Helpers;

/// <summary>
/// The effective launch settings for one directory: the global "Sessions
/// Resume defaults" with the matching <see cref="ProjectProfile"/>'s overrides
/// applied on top.
/// </summary>
public sealed class ResolvedLaunchProfile
{
    /// <summary>The profile that matched, or null when the directory isn't under any project.</summary>
    public ProjectProfile? Project { get; init; }

    public bool EnableAllowAll { get; init; }
    public string? ExtraCopilotArgs { get; init; }

    /// <summary>Terminal id override, or null to use the global default terminal.</summary>
    public string? TerminalOverride { get; init; }

    public bool PreApproveExtensions { get; init; }

    /// <summary>Capability selection to launch with, or null for none.</summary>
    public LaunchCapabilities? Capabilities { get; init; }

    public bool HasProject => Project is not null;
}

/// <summary>
/// Maps a working directory to its <see cref="ProjectProfile"/> and merges that
/// profile over the global defaults. Pure + static so the precedence rules are
/// unit-testable without touching disk or the UI.
/// </summary>
public static class ProjectMatcher
{
    /// <summary>
    /// Find the profile that governs <paramref name="directory"/>. An exact path match always
    /// wins; otherwise the longest (most specific) ancestor path with
    /// <see cref="ProjectProfile.IncludeSubdirectories"/> wins. Disabled profiles are skipped.
    /// Returns null when nothing matches.
    /// </summary>
    public static ProjectProfile? Match(IEnumerable<ProjectProfile>? profiles, string? directory)
    {
        if (profiles is null) return null;
        var target = Normalize(directory);
        if (target is null) return null;

        ProjectProfile? best = null;
        var bestLength = -1;

        foreach (var p in profiles)
        {
            if (p is null || !p.Enabled) continue;
            var root = Normalize(p.Path);
            if (root is null) continue;

            bool isMatch;
            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
            {
                isMatch = true;
            }
            else if (p.IncludeSubdirectories)
            {
                // Path.TrimEndingDirectorySeparator deliberately leaves the separator on a
                // root ("C:\" stays "C:\"), so appending one unconditionally would build the
                // impossible prefix "C:\\" and a drive-root project would match nothing.
                var prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
                    ? root
                    : root + System.IO.Path.DirectorySeparatorChar;
                isMatch = target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                isMatch = false;
            }

            // Longest root wins so C:\repos\app beats C:\repos. Ties keep the
            // first profile in list order, which is stable across reloads.
            if (isMatch && root.Length > bestLength)
            {
                best = p;
                bestLength = root.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Merge <paramref name="project"/> over the global defaults. Null overrides inherit;
    /// non-null overrides win. <see cref="ProjectProfile.Capabilities"/> replaces the global
    /// default capabilities wholesale rather than merging field-by-field.
    /// </summary>
    public static ResolvedLaunchProfile Resolve(ProjectProfile? project, AppSettings settings)
    {
        var defaults = settings.SessionsResume;

        if (project is null)
        {
            // No project: exactly the pre-Projects behavior. Capabilities stay
            // null so a plain resume doesn't suddenly start emitting the global
            // default capability flags it never emitted before.
            return new ResolvedLaunchProfile
            {
                Project = null,
                EnableAllowAll = defaults.EnableAllowAll,
                ExtraCopilotArgs = defaults.ExtraCopilotArgs,
                TerminalOverride = null,
                PreApproveExtensions = defaults.PreApproveExtensions,
                Capabilities = null,
            };
        }

        var caps = project.Capabilities ?? settings.DefaultCapabilities;
        if (caps is not null && caps.IsEmpty) caps = null;

        return new ResolvedLaunchProfile
        {
            Project = project,
            EnableAllowAll = project.EnableAllowAll ?? defaults.EnableAllowAll,
            ExtraCopilotArgs = project.ExtraCopilotArgs ?? defaults.ExtraCopilotArgs,
            TerminalOverride = string.IsNullOrWhiteSpace(project.TerminalOverride) ? null : project.TerminalOverride,
            PreApproveExtensions = project.PreApproveExtensions ?? defaults.PreApproveExtensions,
            Capabilities = caps,
        };
    }

    /// <summary>Trimmed, separator-normalized absolute path, or null when unusable.</summary>
    internal static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return null;
        }
    }
}
