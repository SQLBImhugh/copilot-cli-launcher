using System.Text.Json;
using CopilotLauncher.Models;
using Xunit;

namespace CopilotLauncher.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Normalize_ReplacesNullSubObjects_WithFreshDefaults()
    {
        var s = new AppSettings
        {
            Terminal       = null!,
            CopilotCli     = null!,
            SessionsResume = null!,
            Briefings      = null!,
            Repair         = null!,
            SessionListing = null!,
            LauncherBehavior = null!,
            Storage        = null!,
        };
        s.Normalize();
        Assert.NotNull(s.Terminal);
        Assert.NotNull(s.CopilotCli);
        Assert.NotNull(s.SessionsResume);
        Assert.NotNull(s.Briefings);
        Assert.NotNull(s.Repair);
        Assert.NotNull(s.SessionListing);
        Assert.NotNull(s.LauncherBehavior);
        Assert.NotNull(s.Storage);
    }

    [Fact]
    public void Normalize_ReplacesNullCollections_WithFreshDefaults()
    {
        var s = new AppSettings();
        s.Repair.TrackedGitHubIssues = null!;
        s.SessionListing.HiddenPathGlobs = null!;
        s.Normalize();
        Assert.NotNull(s.Repair.TrackedGitHubIssues);
        Assert.NotNull(s.SessionListing.HiddenPathGlobs);
    }

    [Fact]
    public void Roundtrip_OldSettingsJson_DoesNotNull_NewFields()
    {
        // Simulate an old settings.json that pre-dates SessionsResume + AgentsContextOverride.
        var oldJson = "{\"terminal\":{},\"briefings\":{}}";
        var parsed = JsonSerializer.Deserialize<AppSettings>(oldJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(parsed);
        parsed!.Normalize();
        Assert.NotNull(parsed.SessionsResume);
        Assert.False(parsed.SessionsResume.EnableAISummary);
        Assert.False(parsed.SessionsResume.EnableAllowAll);
        Assert.Null(parsed.SessionsResume.ExtraCopilotArgs);
    }

    [Fact]
    public void Roundtrip_ExplicitNullSubObject_IsHealedByNormalize()
    {
        // settings.json with "briefings": null (null literal, not missing key)
        var json = "{\"briefings\":null,\"sessionsResume\":null}";
        var parsed = JsonSerializer.Deserialize<AppSettings>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(parsed);
        // Before normalize: sub-objects are null
        Assert.Null(parsed!.Briefings);
        Assert.Null(parsed.SessionsResume);
        // After normalize: replaced with defaults
        parsed.Normalize();
        Assert.NotNull(parsed.Briefings);
        Assert.NotNull(parsed.SessionsResume);
    }

    [Fact]
    public void BriefingSettings_Defaults_AISummaryOnStartupUpdate_ToFalse()
    {
        var settings = new AppSettings();
        Assert.False(settings.Briefings.AISummaryOnStartupUpdate);
    }
}
