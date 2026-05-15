using CopilotLauncher.Services;
using Xunit;

namespace CopilotLauncher.Tests;

public class UpdateCheckServiceTests
{
    [Fact]
    public void ParseOutput_HandlesNoUpdateMessage()
    {
        var output = "No update needed, current version is 1.0.48, last checked 1 minute ago.";
        var result = IUpdateCheckService.ParseOutput(output, "1.0.46");
        Assert.NotNull(result);
        Assert.Equal("1.0.46", result!.PreviousVersion);
        Assert.Equal("1.0.48", result.CurrentVersion);
        Assert.True(result.VersionChanged);
    }

    [Fact]
    public void ParseOutput_HandlesInstalledMessage()
    {
        var output = "Copilot CLI version 1.0.49 installed.";
        var result = IUpdateCheckService.ParseOutput(output, "1.0.48");
        Assert.NotNull(result);
        Assert.Equal("1.0.49", result!.CurrentVersion);
        Assert.True(result.VersionChanged);
    }

    [Fact]
    public void ParseOutput_DetectsNoChange()
    {
        var output = "No update needed, current version is 1.0.48, last checked 2 hours ago.";
        var result = IUpdateCheckService.ParseOutput(output, "1.0.48");
        Assert.NotNull(result);
        Assert.False(result!.VersionChanged);
    }

    [Fact]
    public void ParseOutput_ReturnsNull_OnUnknownFormat()
    {
        var result = IUpdateCheckService.ParseOutput("Some unexpected output we don't recognize", "1.0.48");
        Assert.Null(result);
    }

    [Fact]
    public void ParseOutput_ReturnsNull_OnEmptyInput()
    {
        Assert.Null(IUpdateCheckService.ParseOutput("", "1.0.48"));
        Assert.Null(IUpdateCheckService.ParseOutput("   \n\n  ", "1.0.48"));
    }

    [Fact]
    public void ParseOutput_PreservesRawOutput()
    {
        var raw = "No update needed, current version is 1.0.48, blah blah";
        var result = IUpdateCheckService.ParseOutput(raw, "1.0.46");
        Assert.Equal(raw, result!.RawOutput);
    }
}
