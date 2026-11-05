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
            ProjectImport  = null!,
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
        Assert.NotNull(s.ProjectImport);
        Assert.NotNull(s.LauncherBehavior);
        Assert.NotNull(s.Storage);
    }

    [Fact]
    public void Normalize_ReplacesNullCollections_WithFreshDefaults()
    {
        var s = new AppSettings();
        s.Repair.TrackedGitHubIssues = null!;
        s.SessionListing.ExcludedPaths = null!;
        s.SessionListing.HiddenPathGlobs = null!;
        s.ProjectImport.HiddenPaths = null!;
        s.Normalize();
        Assert.NotNull(s.Repair.TrackedGitHubIssues);
        Assert.Empty(s.SessionListing.ExcludedPaths);
        Assert.NotNull(s.SessionListing.HiddenPathGlobs);
        Assert.Empty(s.ProjectImport.HiddenPaths);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sessionListing\":{}}")]
    [InlineData("{\"sessionListing\":null}")]
    [InlineData("{\"sessionListing\":{\"excludedPaths\":null}}")]
    public void SessionListing_ExcludedPaths_MissingOrNullJson_NormalizesToEmpty(string json)
    {
        var parsed = JsonSerializer.Deserialize<AppSettings>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.NotNull(parsed);
        parsed!.Normalize();

        Assert.Empty(parsed.SessionListing.ExcludedPaths);
        Assert.NotNull(parsed.SessionListing.HiddenPathGlobs);
    }

    [Fact]
    public void SessionListing_ExcludedPaths_RoundtripsWithoutChangingLegacyGlobs()
    {
        var settings = new AppSettings();
        var excludedPaths = new List<string> { @"C:\work\Frontier", @"\\server\share\generated" };
        var legacyGlobs = new List<string> { "**/cache/**" };
        settings.SessionListing.ExcludedPaths = excludedPaths;
        settings.SessionListing.HiddenPathGlobs = legacyGlobs;

        settings.Normalize();
        Assert.Same(excludedPaths, settings.SessionListing.ExcludedPaths);
        Assert.Same(legacyGlobs, settings.SessionListing.HiddenPathGlobs);

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(settings, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("sessionListing").GetProperty("excludedPaths").GetArrayLength());

        var parsed = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        parsed.Normalize();
        Assert.Equal(excludedPaths, parsed.SessionListing.ExcludedPaths);
        Assert.Equal(legacyGlobs, parsed.SessionListing.HiddenPathGlobs);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"projectImport\":{}}")]
    [InlineData("{\"projectImport\":null}")]
    [InlineData("{\"projectImport\":{\"hiddenPaths\":null}}")]
    public void ProjectImport_HiddenPaths_MissingOrNullJson_NormalizesToEmpty(string json)
    {
        var parsed = JsonSerializer.Deserialize<AppSettings>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        parsed.Normalize();

        Assert.NotNull(parsed.ProjectImport);
        Assert.Empty(parsed.ProjectImport.HiddenPaths);
        Assert.Empty(parsed.SessionListing.ExcludedPaths);
        Assert.Empty(parsed.SessionListing.HiddenPathGlobs);
    }

    [Fact]
    public void ProjectImport_HiddenPaths_RoundtripsSeparatelyFromSessionFilters()
    {
        var settings = new AppSettings();
        var hiddenPaths = new List<string> { @"C:\work\app", @"\\server\share\generated" };
        settings.ProjectImport.HiddenPaths = hiddenPaths;
        settings.SessionListing.ExcludedPaths.Add(@"C:\session-only");
        settings.SessionListing.HiddenPathGlobs.Add("**/cache/**");

        settings.Normalize();
        Assert.Same(hiddenPaths, settings.ProjectImport.HiddenPaths);

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(settings, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("projectImport").GetProperty("hiddenPaths").GetArrayLength());

        var parsed = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        parsed.Normalize();
        Assert.Equal(hiddenPaths, parsed.ProjectImport.HiddenPaths);
        Assert.Equal(new[] { @"C:\session-only" }, parsed.SessionListing.ExcludedPaths);
        Assert.Equal(new[] { "**/cache/**" }, parsed.SessionListing.HiddenPathGlobs);
    }

    [Fact]
    public void Roundtrip_OldSettingsJson_DoesNotNull_NewFields()
    {
        // Simulate an old settings.json that pre-dates the SessionsResume section.
        var oldJson = "{\"terminal\":{},\"briefings\":{}}";
        var parsed = JsonSerializer.Deserialize<AppSettings>(oldJson,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.NotNull(parsed);
        parsed!.Normalize();
        Assert.NotNull(parsed.SessionsResume);
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
