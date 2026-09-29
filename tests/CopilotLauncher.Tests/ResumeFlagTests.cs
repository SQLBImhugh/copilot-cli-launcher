using CopilotLauncher.Helpers;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class ResumeFlagTests
{
    [Fact]
    public void Format_NullOrBlankTargetProducesNoFlag()
    {
        Assert.Null(ResumeFlag.Format(null));
        Assert.Null(ResumeFlag.Format(""));
        Assert.Null(ResumeFlag.Format("   "));
    }

    /// <summary>
    /// The whole point of the helper: a full session UUID must resume via --session-id,
    /// because interactive --resume=&lt;uuid&gt; spins up a second placeholder session and
    /// hangs on "Resuming".
    /// </summary>
    [Fact]
    public void Format_FullSessionIdUsesSessionIdFlag()
    {
        Assert.Equal(
            "--session-id=3d8fdf55-0c73-4b24-8979-7ea46f96abec",
            ResumeFlag.Format("3d8fdf55-0c73-4b24-8979-7ea46f96abec"));
    }

    [Fact]
    public void Format_TrimsSurroundingWhitespace()
    {
        Assert.Equal(
            "--session-id=3d8fdf55-0c73-4b24-8979-7ea46f96abec",
            ResumeFlag.Format("  3d8fdf55-0c73-4b24-8979-7ea46f96abec  "));
    }

    /// <summary>--session-id only accepts a full UUID, so everything else keeps --resume.</summary>
    [Theory]
    [InlineData("my feature")]
    [InlineData("0cb916d")]
    [InlineData("3d8fdf55")]
    [InlineData("copilot-launcher-briefings")]
    [InlineData("{3d8fdf55-0c73-4b24-8979-7ea46f96abec}")]
    [InlineData("3d8fdf550c734b2489797ea46f96abec")]
    public void Format_NonUuidTargetsKeepResumeFlag(string target)
    {
        Assert.Equal($"--resume={target}", ResumeFlag.Format(target));
    }

    [Theory]
    [InlineData("3d8fdf55-0c73-4b24-8979-7ea46f96abec", true)]
    [InlineData("3d8fdf55", false)]
    [InlineData("my feature", false)]
    [InlineData(null, false)]
    public void IsSessionId_RecognizesCanonicalUuidsOnly(string? target, bool expected)
    {
        Assert.Equal(expected, ResumeFlag.IsSessionId(target));
    }
}
