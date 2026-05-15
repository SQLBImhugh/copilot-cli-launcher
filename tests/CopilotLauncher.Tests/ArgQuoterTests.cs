using CopilotLauncher.Helpers;
using Xunit;

namespace CopilotLauncher.Tests;

public class ArgQuoterTests
{
    [Fact]
    public void Format_PassesThrough_SimpleArgs()
    {
        var result = ArgQuoter.Format(new[] { "-NoExit", "-NoLogo", "--allow-all" });
        Assert.Equal("-NoExit -NoLogo --allow-all", result);
    }

    [Fact]
    public void Format_QuotesArgsWithSpaces()
    {
        var result = ArgQuoter.Format(new[] { "-File", @"C:\path with spaces\Launch.ps1" });
        Assert.Equal(@"-File ""C:\path with spaces\Launch.ps1""", result);
    }

    [Fact]
    public void Format_DoublesEmbeddedQuotes()
    {
        var result = ArgQuoter.Format(new[] { "--prompt", "say \"hi\"" });
        Assert.Equal(@"--prompt ""say """"hi""""""", result);
    }

    [Fact]
    public void Format_SkipsNullAndEmpty()
    {
        var result = ArgQuoter.Format(new string?[] { "a", null, "", "b" });
        Assert.Equal("a b", result);
    }

    [Fact]
    public void Format_ProducesEmptyForNoArgs()
    {
        Assert.Equal(string.Empty, ArgQuoter.Format(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("",                                  new string[] { })]
    [InlineData("--max-autopilot-continues 100",     new[] { "--max-autopilot-continues", "100" })]
    [InlineData("--prompt \"do the thing\"",         new[] { "--prompt", "do the thing" })]
    [InlineData("--prompt \"do the thing\" --flag value",
                                                     new[] { "--prompt", "do the thing", "--flag", "value" })]
    public void Split_TokenizesCommandLine(string input, string[] expected)
    {
        var result = ArgQuoter.Split(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RoundTrip_QuoteThenSplit_PreservesTokens()
    {
        var input = new[] { "--prompt", "do the thing", "--flag", "value" };
        var formatted = ArgQuoter.Format(input);
        var split = ArgQuoter.Split(formatted);
        Assert.Equal(input, split);
    }
}
