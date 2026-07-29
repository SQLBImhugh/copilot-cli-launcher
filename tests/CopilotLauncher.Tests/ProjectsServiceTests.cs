using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class ProjectsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public ProjectsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "projects.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private static ProjectProfile New(string label, string path) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Label = label,
        Path = path,
    };

    [Fact]
    public void MissingFile_StartsEmpty()
    {
        var svc = new ProjectsService(_file);
        Assert.Empty(svc.All);
    }

    [Fact]
    public void Add_Update_Remove_RoundTripsThroughDisk()
    {
        var svc = new ProjectsService(_file);
        var project = New("app", @"C:\repos\app");
        project.ExtraCopilotArgs = "--foo";
        svc.Add(project);

        var reloaded = new ProjectsService(_file);
        Assert.Single(reloaded.All);
        Assert.Equal("--foo", reloaded.All[0].ExtraCopilotArgs);

        project.Label = "renamed";
        svc.Update(project);
        Assert.Equal("renamed", new ProjectsService(_file).All[0].Label);

        svc.Remove(project.Id);
        Assert.Empty(new ProjectsService(_file).All);
    }

    [Fact]
    public void Update_UnknownId_Throws()
    {
        var svc = new ProjectsService(_file);
        Assert.Throws<KeyNotFoundException>(() => svc.Update(New("ghost", @"C:\nope")));
    }

    [Fact]
    public void PersistsNullableOverridesFaithfully()
    {
        var svc = new ProjectsService(_file);
        var project = New("app", @"C:\repos\app");
        project.EnableAllowAll = false;          // deliberately off, not "inherit"
        project.PreApproveExtensions = null;     // inherit
        project.Capabilities = new LaunchCapabilities { Agent = "reviewer", DisableAllSkills = true };
        project.RepoEnabledPlugins = new List<string> { "winui@awesome-copilot" };
        project.SyncRepoConfigOnLaunch = true;
        svc.Add(project);

        var loaded = new ProjectsService(_file).All[0];

        Assert.False(loaded.EnableAllowAll);
        Assert.Null(loaded.PreApproveExtensions);
        Assert.Equal("reviewer", loaded.Capabilities?.Agent);
        Assert.True(loaded.Capabilities?.DisableAllSkills);
        Assert.Equal(new[] { "winui@awesome-copilot" }, loaded.RepoEnabledPlugins);
        Assert.True(loaded.SyncRepoConfigOnLaunch);
    }

    [Fact]
    public void CorruptFile_IsBackedUpAndReset()
    {
        File.WriteAllText(_file, "{ this is not json");

        var svc = new ProjectsService(_file);

        Assert.Empty(svc.All);
        Assert.NotEmpty(Directory.GetFiles(_dir, "projects.json.corrupt-*"));
    }

    [Fact]
    public void Match_And_Resolve_DelegateToTheMatcher()
    {
        var svc = new ProjectsService(_file);
        var project = New("app", @"C:\repos\app");
        project.ExtraCopilotArgs = "--project";
        svc.Add(project);

        Assert.Equal("app", svc.Match(@"C:\repos\app\src")?.Label);
        Assert.Null(svc.Match(@"C:\elsewhere"));

        var settings = new AppSettings();
        settings.SessionsResume.ExtraCopilotArgs = "--global";

        Assert.Equal("--project", svc.Resolve(@"C:\repos\app", settings).ExtraCopilotArgs);
        Assert.Equal("--global", svc.Resolve(@"C:\elsewhere", settings).ExtraCopilotArgs);
    }
}
