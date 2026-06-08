using CopilotLauncher.Helpers;
using Xunit;

namespace CopilotLauncher.Tests;

public class WindowSizingTests
{
    [Theory]
    [InlineData(899, 961)]
    [InlineData(1110, 699)]
    [InlineData(100, 100)]
    public void ClampNormalSize_BelowMinimum_ReturnsDefaults(int width, int height)
    {
        var result = WindowSizing.ClampNormalSize(width, height);

        Assert.Equal((1180, 1040), result);
    }

    [Fact]
    public void ClampNormalSize_NormalValues_PassThrough()
    {
        var result = WindowSizing.ClampNormalSize(1110, 961);

        Assert.Equal((1110, 961), result);
    }

    [Theory]
    [InlineData(96u, 1.0)]
    [InlineData(144u, 1.5)]
    [InlineData(0u, 1.0)]
    public void ScaleFromDpi_ReturnsExpectedScale(uint dpi, double expected)
    {
        Assert.Equal(expected, WindowSizing.ScaleFromDpi(dpi));
    }
}
