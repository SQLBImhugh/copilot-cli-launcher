using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class ProjectMatcherTests
{
    private static ProjectProfile P(string label, string path, bool subdirs = true, bool enabled = true) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Label = label,
        Path = path,
        IncludeSubdirectories = subdirs,
        Enabled = enabled,
    };

    [Fact]
    public void Match_ExactPath()
    {
        var profiles = new[] { P("app", @"C:\repos\app") };
        Assert.Equal("app", ProjectMatcher.Match(profiles, @"C:\repos\app")?.Label);
    }

    [Fact]
    public void Match_IsCaseInsensitive_AndIgnoresTrailingSeparator()
    {
        var profiles = new[] { P("app", @"C:\repos\app\") };
        Assert.NotNull(ProjectMatcher.Match(profiles, @"c:\REPOS\App"));
    }

    [Fact]
    public void Match_LongestPathWins()
    {
        var profiles = new[]
        {
            P("all repos", @"C:\repos"),
            P("app", @"C:\repos\app"),
        };
        Assert.Equal("app", ProjectMatcher.Match(profiles, @"C:\repos\app\src")?.Label);
        Assert.Equal("all repos", ProjectMatcher.Match(profiles, @"C:\repos\other")?.Label);
    }

    [Fact]
    public void Match_RespectsIncludeSubdirectories()
    {
        var profiles = new[] { P("app", @"C:\repos\app", subdirs: false) };
        Assert.NotNull(ProjectMatcher.Match(profiles, @"C:\repos\app"));
        Assert.Null(ProjectMatcher.Match(profiles, @"C:\repos\app\src"));
    }

    [Fact]
    public void Match_DoesNotMatchSiblingWithSharedPrefix()
    {
        // C:\repos\app must NOT swallow C:\repos\app-tools.
        var profiles = new[] { P("app", @"C:\repos\app") };
        Assert.Null(ProjectMatcher.Match(profiles, @"C:\repos\app-tools"));
    }

    [Fact]
    public void Match_SkipsDisabledProfiles()
    {
        var profiles = new[] { P("app", @"C:\repos\app", enabled: false) };
        Assert.Null(ProjectMatcher.Match(profiles, @"C:\repos\app"));
    }

    [Fact]
    public void Match_DriveRootProjectCoversItsSubdirectories()
    {
        // Path.TrimEndingDirectorySeparator leaves "C:\" intact, so a naive
        // root + separator prefix would be the impossible "C:\\".
        var profiles = new[] { P("drive", @"C:\") };
        Assert.Equal("drive", ProjectMatcher.Match(profiles, @"C:\repos\app")?.Label);
        Assert.Equal("drive", ProjectMatcher.Match(profiles, @"C:\")?.Label);
    }

    [Fact]
    public void Match_UncRootProjectCoversItsSubdirectories()
    {
        var profiles = new[] { P("share", @"\\server\share") };
        Assert.Equal("share", ProjectMatcher.Match(profiles, @"\\server\share\proj")?.Label);
    }

    [Fact]
    public void Match_NullOrBlankInputs()
    {
        Assert.Null(ProjectMatcher.Match(null, @"C:\repos"));
        Assert.Null(ProjectMatcher.Match(new[] { P("a", @"C:\repos") }, null));
        Assert.Null(ProjectMatcher.Match(new[] { P("a", @"C:\repos") }, "   "));
    }

    [Fact]
    public void Resolve_NoProject_UsesGlobalDefaultsAndNoCapabilities()
    {
        var settings = new AppSettings();
        settings.SessionsResume.EnableAllowAll = true;
        settings.SessionsResume.ExtraCopilotArgs = "--foo";
        settings.SessionsResume.PreApproveExtensions = true;
        settings.DefaultCapabilities.Agent = "reviewer";

        var resolved = ProjectMatcher.Resolve(null, settings);

        Assert.False(resolved.HasProject);
        Assert.True(resolved.EnableAllowAll);
        Assert.Equal("--foo", resolved.ExtraCopilotArgs);
        Assert.True(resolved.PreApproveExtensions);
        Assert.Null(resolved.TerminalOverride);
        // Global default capabilities must NOT leak into a plain resume.
        Assert.Null(resolved.Capabilities);
    }

    [Fact]
    public void Resolve_NullOverridesInherit()
    {
        var settings = new AppSettings();
        settings.SessionsResume.EnableAllowAll = true;
        settings.SessionsResume.ExtraCopilotArgs = "--global";
        settings.SessionsResume.PreApproveExtensions = true;

        var resolved = ProjectMatcher.Resolve(P("app", @"C:\repos\app"), settings);

        Assert.True(resolved.EnableAllowAll);
        Assert.Equal("--global", resolved.ExtraCopilotArgs);
        Assert.True(resolved.PreApproveExtensions);
    }

    [Fact]
    public void Resolve_ProjectOverridesWin_IncludingTurningFlagsOff()
    {
        var settings = new AppSettings();
        settings.SessionsResume.EnableAllowAll = true;
        settings.SessionsResume.ExtraCopilotArgs = "--global";
        settings.SessionsResume.PreApproveExtensions = true;

        var project = P("app", @"C:\repos\app");
        project.EnableAllowAll = false;
        project.ExtraCopilotArgs = "--project";
        project.PreApproveExtensions = false;
        project.TerminalOverride = "pwsh";

        var resolved = ProjectMatcher.Resolve(project, settings);

        Assert.False(resolved.EnableAllowAll);
        Assert.Equal("--project", resolved.ExtraCopilotArgs);
        Assert.False(resolved.PreApproveExtensions);
        Assert.Equal("pwsh", resolved.TerminalOverride);
    }

    [Fact]
    public void Resolve_ProjectCapabilitiesReplaceGlobalDefaults()
    {
        var settings = new AppSettings();
        settings.DefaultCapabilities.Agent = "global-agent";

        var project = P("app", @"C:\repos\app");
        project.Capabilities = new LaunchCapabilities { Agent = "project-agent" };

        var resolved = ProjectMatcher.Resolve(project, settings);

        Assert.Equal("project-agent", resolved.Capabilities?.Agent);
    }

    [Fact]
    public void Resolve_ProjectWithoutCapabilities_FallsBackToGlobalDefaults()
    {
        var settings = new AppSettings();
        settings.DefaultCapabilities.Agent = "global-agent";

        var resolved = ProjectMatcher.Resolve(P("app", @"C:\repos\app"), settings);

        Assert.Equal("global-agent", resolved.Capabilities?.Agent);
    }

    [Fact]
    public void Resolve_EmptyCapabilitiesBecomeNull()
    {
        // An all-defaults LaunchCapabilities should not cause empty flag emission.
        var resolved = ProjectMatcher.Resolve(P("app", @"C:\repos\app"), new AppSettings());
        Assert.Null(resolved.Capabilities);
    }

    [Fact]
    public void Resolve_BlankTerminalOverrideIsTreatedAsInherit()
    {
        var project = P("app", @"C:\repos\app");
        project.TerminalOverride = "   ";
        Assert.Null(ProjectMatcher.Resolve(project, new AppSettings()).TerminalOverride);
    }
}
