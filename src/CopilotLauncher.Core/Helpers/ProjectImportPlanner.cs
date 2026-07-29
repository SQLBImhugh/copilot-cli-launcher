using CopilotLauncher.Models;

namespace CopilotLauncher.Helpers;

/// <summary>
/// One working directory discovered from past sessions that could become a
/// <see cref="ProjectProfile"/>.
/// </summary>
public sealed class ProjectImportCandidate
{
    /// <summary>The folder the project would cover — the git root when the sessions had one,
    /// otherwise the session working directory.</summary>
    public required string Path { get; init; }

    /// <summary>Project label to use: the git repo name when known, else the folder name.</summary>
    public required string SuggestedName { get; init; }

    /// <summary><c>owner/repo</c> from the session metadata, when the repo has a remote.</summary>
    public string? Repository { get; init; }

    public bool IsGitRepo { get; init; }

    /// <summary>How many past sessions map to this folder.</summary>
    public int SessionCount { get; init; }

    public DateTime LastUsed { get; init; }

    /// <summary>True when a project already covers this exact path.</summary>
    public bool AlreadyImported { get; init; }

    public bool DirectoryExists { get; init; }

    /// <summary>Whether to tick this row by default. Excludes folders that already have a
    /// project, folders that no longer exist, and tooling/install directories that are
    /// technically session cwds but aren't projects.</summary>
    public bool RecommendedByDefault { get; init; }

    public string Caption
    {
        get
        {
            var bits = new List<string> { SessionCount == 1 ? "1 session" : $"{SessionCount} sessions" };
            if (!string.IsNullOrWhiteSpace(Repository)) bits.Add(Repository);
            else if (IsGitRepo) bits.Add("git repo");
            if (AlreadyImported) bits.Add("already a project");
            if (!DirectoryExists) bits.Add("folder not found");
            return string.Join(" · ", bits);
        }
    }
}

/// <summary>
/// Turns past-session history into a de-duplicated list of importable projects.
/// Pure + static so the grouping and naming rules are unit-testable without disk.
/// </summary>
public static class ProjectImportPlanner
{
    /// <summary>
    /// Group sessions into import candidates. Sessions sharing a git root collapse into one
    /// candidate for that root (so a project covers the whole repo, not a subfolder).
    /// Sessions whose working directory is the user-profile root itself are skipped — that's
    /// the "no project" default, not a real project.
    /// </summary>
    /// <param name="directoryExists">Injected for tests; defaults to <c>Directory.Exists</c>.</param>
    public static IReadOnlyList<ProjectImportCandidate> BuildCandidates(
        IEnumerable<CopilotSession>? sessions,
        IEnumerable<ProjectProfile>? existingProjects,
        string? userProfileRoot = null,
        Func<string, bool>? directoryExists = null)
    {
        var exists = directoryExists ?? Directory.Exists;
        var homeRoot = ProjectMatcher.Normalize(
            userProfileRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in existingProjects ?? Enumerable.Empty<ProjectProfile>())
        {
            var normalized = ProjectMatcher.Normalize(p?.Path);
            if (normalized is not null) taken.Add(normalized);
        }

        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sessions ?? Enumerable.Empty<CopilotSession>())
        {
            if (s is null) continue;

            // A git root covers the whole repo; fall back to the session cwd.
            var key = ProjectMatcher.Normalize(s.GitRoot) ?? ProjectMatcher.Normalize(s.Cwd);
            if (key is null) continue;

            // The user-profile root is where sessions land when they have no project.
            if (homeRoot is not null && string.Equals(key, homeRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!groups.TryGetValue(key, out var g))
            {
                g = new Group { Path = key };
                groups[key] = g;
            }

            g.SessionCount++;
            if (s.LastModified > g.LastUsed) g.LastUsed = s.LastModified;
            if (!string.IsNullOrWhiteSpace(s.GitRoot)) g.IsGitRepo = true;
            if (g.Repository is null && !string.IsNullOrWhiteSpace(s.Repository)) g.Repository = s.Repository.Trim();
        }

        return groups.Values
            .Select(g =>
            {
                var already = taken.Contains(g.Path);
                var present = SafeExists(exists, g.Path);
                return new ProjectImportCandidate
                {
                    Path = g.Path,
                    SuggestedName = SuggestName(g.Repository, g.Path),
                    Repository = g.Repository,
                    IsGitRepo = g.IsGitRepo || g.Repository is not null,
                    SessionCount = g.SessionCount,
                    LastUsed = g.LastUsed,
                    AlreadyImported = already,
                    DirectoryExists = present,
                    RecommendedByDefault = !already && present && !LooksLikeToolingDirectory(g.Path, homeRoot),
                };
            })
            .OrderByDescending(c => c.RecommendedByDefault)
            .ThenByDescending(c => c.SessionCount)
            .ThenBy(c => c.SuggestedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Project label: the git repo name when the sessions recorded one (<c>owner/repo</c> →
    /// <c>repo</c>), otherwise the folder name.
    /// </summary>
    internal static string SuggestName(string? repository, string path)
    {
        if (!string.IsNullOrWhiteSpace(repository))
        {
            // Take whatever follows the last '/'. Don't trim trailing slashes first:
            // "owner/" would then be indistinguishable from "owner" and we'd name the
            // project after the GitHub org instead of falling back to the folder.
            var repo = repository.Trim();
            var slash = repo.LastIndexOf('/');
            if (slash >= 0) repo = repo[(slash + 1)..];
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
            if (!string.IsNullOrWhiteSpace(repo)) return repo;
        }

        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        // A drive root ("C:\") has no leaf name.
        return string.IsNullOrWhiteSpace(leaf) ? path : leaf;
    }

    /// <summary>
    /// Install / tooling folders that show up as session cwds but aren't projects. These are
    /// still listed — they're only left un-ticked so a bulk import doesn't create junk.
    /// </summary>
    internal static bool LooksLikeToolingDirectory(string path, string? homeRoot)
    {
        var roots = new List<string>();

        if (homeRoot is not null)
        {
            foreach (var relative in new[] { "AppData", ".copilot", ".vscode", ".nuget", ".dotnet" })
                roots.Add(Path.Combine(homeRoot, relative));
        }

        // Machine-wide install locations (frontier_se and similar ship under ProgramData).
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.Windows,
                 })
        {
            var root = SafeFolderPath(folder);
            if (root is not null) roots.Add(root);
        }

        foreach (var root in roots)
        {
            var normalized = ProjectMatcher.Normalize(root);
            if (normalized is null) continue;
            if (string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string? SafeFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeExists(Func<string, bool> exists, string path)
    {
        try { return exists(path); }
        catch { return false; }
    }

    private sealed class Group
    {
        public required string Path { get; init; }
        public int SessionCount;
        public DateTime LastUsed;
        public bool IsGitRepo;
        public string? Repository;
    }
}
