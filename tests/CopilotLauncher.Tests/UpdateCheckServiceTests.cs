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

    [Fact]
    public void Fallback_PrefersPostUpdateQuery_OverParsedOutput()
    {
        // Regression test for "first Check Now sees OLD->OLD even though the
        // update succeeded" bug. Documents the precedence rule used inside
        // RunAsync:
        //   post != "unknown"  ->  post
        //   else parsed?.CurrentVersion ?? prev
        // This protects against older copilot CLIs that emit unrecognized
        // update text but DID install a new version (visible via --version).
        string prev = "1.0.40";
        string post = "1.0.49";

        // Unrecognized update output -> parsed is null. Post wins.
        var parsed = IUpdateCheckService.ParseOutput("blah blah", prev);
        Assert.Null(parsed);
        var current = post != "unknown" ? post : (parsed?.CurrentVersion ?? prev);
        Assert.Equal("1.0.49", current);

        // Post unknown, parsed recognized -> parsed wins.
        post = "unknown";
        parsed = IUpdateCheckService.ParseOutput("Copilot CLI version 1.0.48 installed.", prev);
        current = post != "unknown" ? post : (parsed?.CurrentVersion ?? prev);
        Assert.Equal("1.0.48", current);

        // Post unknown, parsed null -> fall back to prev.
        parsed = IUpdateCheckService.ParseOutput("noise", prev);
        current = post != "unknown" ? post : (parsed?.CurrentVersion ?? prev);
        Assert.Equal("1.0.40", current);
    }

    [Fact]
    public void BuildVersionArguments_IncludesNoAutoUpdateFlag()
    {
        // Regression test for v0.1.10 fix. copilot CLI silently
        // auto-updates itself on every invocation that goes through the
        // app gate, INCLUDING `--version`. If our version probe omits
        // `--no-auto-update`, the "prev" query inside RunAsync ends up
        // capturing the post-auto-update version — the launcher then
        // sees prev == post and reports "no version change" even though
        // copilot actually just updated under the hood. Symptom: user
        // clicks Check Now when a new release drops, gets "still X.Y.Z",
        // and no briefing entry is created.
        var args = UpdateCheckService.BuildVersionArguments(Array.Empty<string>());
        Assert.Contains("--no-auto-update", args);
        Assert.Contains("--version", args);
        var list = args.ToList();
        Assert.True(list.IndexOf("--no-auto-update") < list.IndexOf("--version"));
    }

    [Fact]
    public void BuildVersionArguments_PreservesPrefixArgs()
    {
        // If ProcessUtil.Resolve hands us a wrapped invocation (e.g.
        // ["node", "path/to/copilot.js"]), the prefix args must survive
        // ahead of our --no-auto-update/--version flags so the wrapper
        // still works.
        var prefix = new[] { "node", @"C:\bin\copilot.js" };
        var args = UpdateCheckService.BuildVersionArguments(prefix);
        Assert.Equal(4, args.Count);
        Assert.Equal("node", args[0]);
        Assert.Equal(@"C:\bin\copilot.js", args[1]);
        Assert.Equal("--no-auto-update", args[2]);
        Assert.Equal("--version", args[3]);
    }
}
