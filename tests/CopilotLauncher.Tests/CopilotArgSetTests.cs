using Xunit;
using CopilotLauncher.Helpers;

namespace CopilotLauncher.Tests;

public sealed class CopilotArgSetTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void ParseEmptyFormatsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, CopilotArgSet.Parse(input).Format());
    }

    [Fact]
    public void FormatsFlagEqualsAndFlagSpaceConsistently()
    {
        var equals = CopilotArgSet.Parse("--model=gpt-5.5 --effort high");
        var spaced = CopilotArgSet.Parse("--model gpt-5.5 --effort high");

        Assert.Equal("--model gpt-5.5 --effort high", equals.Format());
        Assert.Equal(equals.Format(), spaced.Format());
    }

    [Fact]
    public void PreservesQuotedValuesWithSpaces()
    {
        var set = CopilotArgSet.Parse("--name \"deep work session\" --log-dir \"C:\\Logs With Spaces\"");

        Assert.Equal("--name \"deep work session\" --log-dir \"C:\\Logs With Spaces\"", set.Format());
    }

    [Fact]
    public void PreservesRepeatableFlags()
    {
        var set = CopilotArgSet.Parse("--allow-tool shell(git:*) --allow-tool=shell(gh:*) --add-dir \"C:\\Repo One\" --add-dir=C:\\RepoTwo");

        Assert.Equal("--allow-tool shell(git:*) --allow-tool shell(gh:*) --add-dir \"C:\\Repo One\" --add-dir C:\\RepoTwo", set.Format());
        Assert.Equal(new[] { "shell(git:*)", "shell(gh:*)" }, set.GetValues("--allow-tool"));
        Assert.Equal(new[] { "C:\\Repo One", "C:\\RepoTwo" }, set.GetValues("--add-dir"));
    }

    [Fact]
    public void PreservesUnrecognizedTokens()
    {
        var set = CopilotArgSet.Parse("--model auto --future-flag \"some value\" positional --weird=value");

        Assert.Equal("--future-flag \"some value\" positional --weird=value", set.UnrecognizedText);
        Assert.Equal("--model auto --future-flag \"some value\" positional --weird=value", set.Format());
    }

    [Fact]
    public void HandlesSwitchFlagsWithNoValue()
    {
        var set = CopilotArgSet.Parse("--autopilot --allow-all-tools --no-color");

        Assert.True(set.IsEnabled("--autopilot"));
        Assert.True(set.IsEnabled("--allow-all-tools"));
        Assert.True(set.IsEnabled("--no-color"));
        Assert.Equal("--autopilot --allow-all-tools --no-color", set.Format());
    }

    [Fact]
    public void NormalizesReasoningEffortAliasToEffort()
    {
        var set = CopilotArgSet.Parse("--reasoning-effort high");

        Assert.True(set.IsEnabled("--effort"));
        Assert.Equal("--effort high", set.Format());
    }
}



