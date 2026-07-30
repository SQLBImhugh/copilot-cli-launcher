using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class ProjectImportPlannerTests
{
    private const string Home = @"C:\Users\tester";

    private static CopilotSession S(string? cwd, string? repository = null, string? gitRoot = null, DateTime? modified = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        FolderPath = @"C:\fake",
        LastModified = modified ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Cwd = cwd,
        Repository = repository,
        GitRoot = gitRoot,
    };

    private static IReadOnlyList<ProjectImportCandidate> Build(
        IEnumerable<CopilotSession> sessions,
        IEnumerable<ProjectProfile>? existing = null,
        Func<string, bool>? exists = null) =>
        ProjectImportPlanner.BuildCandidates(sessions, existing, Home, exists ?? (_ => true));

    [Fact]
    public void SkipsTheUserProfileRoot()
    {
        var candidates = Build(new[] { S(Home), S(@"C:\Users\tester\") , S(@"C:\repos\app") });

        Assert.Single(candidates);
        Assert.Equal(@"C:\repos\app", candidates[0].Path);
    }

    [Fact]
    public void NamesProjectAfterGitRepo_NotTheFolder()
    {
        var candidates = Build(new[] { S(@"C:\code\checkout-dir", repository: "SQLBImhugh/FabricPOCPortal", gitRoot: @"C:\code\checkout-dir") });

        Assert.Equal("FabricPOCPortal", candidates[0].SuggestedName);
        Assert.Equal("SQLBImhugh/FabricPOCPortal", candidates[0].Repository);
        Assert.True(candidates[0].IsGitRepo);
    }

    [Fact]
    public void FallsBackToFolderName_WhenNoRepository()
    {
        var candidates = Build(new[] { S(@"C:\Copilot\MSXInsights") });

        Assert.Equal("MSXInsights", candidates[0].SuggestedName);
        Assert.False(candidates[0].IsGitRepo);
    }

    [Fact]
    public void LocalGitRepoWithoutRemote_IsMarkedAsGit_AndNamedAfterFolder()
    {
        var candidates = Build(new[] { S(@"D:\Copilot\PBIEmbeddedMonitoring", gitRoot: @"D:\Copilot\PBIEmbeddedMonitoring") });

        Assert.True(candidates[0].IsGitRepo);
        Assert.Equal("PBIEmbeddedMonitoring", candidates[0].SuggestedName);
    }

    [Fact]
    public void GroupsSubdirectorySessionsUnderTheirGitRoot()
    {
        var candidates = Build(new[]
        {
            S(@"C:\repos\app\src", repository: "me/app", gitRoot: @"C:\repos\app"),
            S(@"C:\repos\app\tests", repository: "me/app", gitRoot: @"C:\repos\app"),
            S(@"C:\repos\app", repository: "me/app", gitRoot: @"C:\repos\app"),
        });

        Assert.Single(candidates);
        Assert.Equal(@"C:\repos\app", candidates[0].Path);
        Assert.Equal(3, candidates[0].SessionCount);
    }

    [Fact]
    public void DeduplicatesCaseInsensitively_AndCountsSessions()
    {
        var candidates = Build(new[] { S(@"C:\repos\App"), S(@"c:\REPOS\app\"), S(@"C:\repos\app") });

        Assert.Single(candidates);
        Assert.Equal(3, candidates[0].SessionCount);
    }

    [Fact]
    public void TracksMostRecentUse()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var candidates = Build(new[] { S(@"C:\repos\app", modified: older), S(@"C:\repos\app", modified: newer) });

        Assert.Equal(newer, candidates[0].LastUsed);
    }

    [Fact]
    public void MarksFoldersThatAlreadyHaveAProject()
    {
        var existing = new[]
        {
            new ProjectProfile { Id = "1", Label = "app", Path = @"c:\repos\app\" },
        };
        var candidates = Build(new[] { S(@"C:\repos\app"), S(@"C:\repos\other") }, existing);

        var app = candidates.Single(c => c.Path.EndsWith("app", StringComparison.OrdinalIgnoreCase));
        Assert.True(app.AlreadyImported);
        Assert.False(app.RecommendedByDefault);

        var other = candidates.Single(c => c.Path.EndsWith("other", StringComparison.OrdinalIgnoreCase));
        Assert.False(other.AlreadyImported);
        Assert.True(other.RecommendedByDefault);
    }

    [Fact]
    public void ListsButDoesNotRecommendMissingFolders()
    {
        var candidates = Build(
            new[] { S(@"D:\gone") },
            exists: p => !p.Contains("gone", StringComparison.OrdinalIgnoreCase));

        Assert.Single(candidates);
        Assert.False(candidates[0].DirectoryExists);
        Assert.False(candidates[0].RecommendedByDefault);
        Assert.Contains("folder not found", candidates[0].Caption);
    }

    [Theory]
    [InlineData(@"C:\Users\tester\AppData\Local\Programs\GitHub Copilot")]
    [InlineData(@"C:\Users\tester\.copilot")]
    [InlineData(@"C:\Users\tester\.vscode\extensions")]
    public void ListsButDoesNotRecommendToolingFolders(string path)
    {
        var candidates = Build(new[] { S(path) });

        Assert.Single(candidates);
        Assert.False(candidates[0].RecommendedByDefault);
    }

    [Fact]
    public void ListsButDoesNotRecommendMachineInstallFolders()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var path = Path.Combine(programData, "vendor", "app-1.0.0");

        var candidates = Build(new[] { S(path) });

        Assert.Single(candidates);
        Assert.False(candidates[0].RecommendedByDefault);
    }

    [Fact]
    public void RecommendsRealProjectsInsideTheUserFolder()
    {
        // C:\Users\tester\FabricPOCPortal is a project even though it lives under home.
        var candidates = Build(new[] { S(@"C:\Users\tester\FabricPOCPortal", repository: "me/FabricPOCPortal") });

        Assert.True(candidates[0].RecommendedByDefault);
        Assert.Equal("FabricPOCPortal", candidates[0].SuggestedName);
    }

    [Fact]
    public void RecommendedRowsSortFirst_ThenByMostRecentlyUsed()
    {
        var old = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var mid = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        var candidates = Build(new[]
        {
            // Tooling folder, very recent + most sessions: still sorts last.
            S(@"C:\Users\tester\.copilot", modified: recent),
            S(@"C:\Users\tester\.copilot", modified: recent),
            S(@"C:\Users\tester\.copilot", modified: recent),
            // Many sessions but stale.
            S(@"C:\repos\busy-but-old", modified: old),
            S(@"C:\repos\busy-but-old", modified: old),
            // A single recent session wins on recency.
            S(@"C:\repos\fresh", modified: recent),
            S(@"C:\repos\middle", modified: mid),
        });

        Assert.Equal("fresh", candidates[0].SuggestedName);
        Assert.Equal("middle", candidates[1].SuggestedName);
        Assert.Equal("busy-but-old", candidates[2].SuggestedName);
        Assert.Equal(".copilot", candidates[3].SuggestedName);
    }

    [Fact]
    public void SameRepoNameInTwoFolders_IsDisambiguatedByDate()
    {
        // The real case: two MSXInsights checkouts. The recent one must come first
        // and both must expose a date.
        var stale = new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        var fresh = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

        var candidates = Build(new[]
        {
            S(@"C:\Copilot\MSXInsights", repository: "me/MSXInsights", gitRoot: @"C:\Copilot\MSXInsights", modified: stale),
            S(@"C:\Users\tester\msxinsights", repository: "me/MSXInsights", gitRoot: @"C:\Users\tester\msxinsights", modified: fresh),
        });

        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal("MSXInsights", c.SuggestedName));
        Assert.Equal(@"C:\Users\tester\msxinsights", candidates[0].Path);
        Assert.Equal(fresh.ToLocalTime().ToString("yyyy-MM-dd"), candidates[0].LastUsedDate);
        Assert.Equal(stale.ToLocalTime().ToString("yyyy-MM-dd"), candidates[1].LastUsedDate);
    }

    [Fact]
    public void ExposesBothAbsoluteAndRelativeDates()
    {
        var when = DateTime.UtcNow.AddHours(-3);
        var candidates = Build(new[] { S(@"C:\repos\app", modified: when) });

        Assert.Equal(when.ToLocalTime().ToString("yyyy-MM-dd"), candidates[0].LastUsedDate);
        Assert.Equal("3h ago", candidates[0].LastUsedRelative);
    }

    [Fact]
    public void IgnoresSessionsWithNoWorkingDirectory()
    {
        var candidates = Build(new[] { S(null), S("   "), S(@"C:\repos\app") });
        Assert.Single(candidates);
    }

    [Fact]
    public void HandlesNullInputs()
    {
        Assert.Empty(ProjectImportPlanner.BuildCandidates(null, null, Home, _ => true));
    }

    [Theory]
    [InlineData("owner/repo", "repo")]
    [InlineData("owner/repo.git", "repo")]
    [InlineData("repo", "repo")]
    [InlineData("owner/", null)]        // trailing slash → fall back to folder name
    [InlineData("", null)]
    [InlineData(null, null)]
    public void SuggestName_PrefersRepoName(string? repository, string? expected)
    {
        var name = ProjectImportPlanner.SuggestName(repository, @"C:\code\folder-name");
        Assert.Equal(expected ?? "folder-name", name);
    }

    [Fact]
    public void SuggestName_DriveRootFallsBackToThePath()
    {
        Assert.Equal(@"D:\", ProjectImportPlanner.SuggestName(null, @"D:\"));
    }
}
