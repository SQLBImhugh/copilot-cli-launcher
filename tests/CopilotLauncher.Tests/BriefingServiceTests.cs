using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class BriefingServiceTests
{
    [Fact]
    public void Render_IncludesHeaderWithVersionRange()
    {
        var svc = new BriefingService();
        var output = svc.Render("1.0.46", "1.0.48", Array.Empty<ReleaseEntry>());
        Assert.Contains("1.0.46 -> 1.0.48", output);
    }

    [Fact]
    public void Render_FormatsDate_AsYyyyMmDd()
    {
        var svc = new BriefingService();
        var entries = new[]
        {
            new ReleaseEntry
            {
                Version = "1.0.48",
                Date = new DateTime(2026, 5, 14, 13, 53, 45, DateTimeKind.Utc),
                Body = "Bug fixes",
            },
        };
        var output = svc.Render("1.0.46", "1.0.48", entries);
        Assert.Contains("2026-05-14", output);
        Assert.DoesNotContain("13:53", output);   // No time-of-day; just the date.
    }

    [Fact]
    public void Render_HandlesNullDate()
    {
        var svc = new BriefingService();
        var output = svc.Render("1.0.46", "1.0.47", new[]
        {
            new ReleaseEntry { Version = "1.0.47", Date = null, Body = "Bug fixes" },
        });
        // Doesn't crash; just omits the date line.
        Assert.Contains("## v1.0.47", output);
        Assert.Contains("Bug fixes", output);
    }

    [Fact]
    public void Render_HandlesNullBody()
    {
        var svc = new BriefingService();
        var output = svc.Render("1.0.46", "1.0.47", new[]
        {
            new ReleaseEntry { Version = "1.0.47", Date = DateTime.UtcNow, Body = null },
        });
        Assert.Contains("## v1.0.47", output);
    }

    [Fact]
    public void Render_PreservesEntryOrder()
    {
        var svc = new BriefingService();
        var entries = new[]
        {
            new ReleaseEntry { Version = "1.0.47", Body = "A" },
            new ReleaseEntry { Version = "1.0.48", Body = "B" },
        };
        var output = svc.Render("1.0.46", "1.0.48", entries);
        var aIdx = output.IndexOf("v1.0.47", StringComparison.Ordinal);
        var bIdx = output.IndexOf("v1.0.48", StringComparison.Ordinal);
        Assert.True(aIdx < bIdx, "1.0.47 should appear before 1.0.48");
    }
}
