using CopilotLauncher.Helpers;
using Xunit;

namespace CopilotLauncher.Tests;

public sealed class PathValidatorTests : IDisposable
{
    private readonly string _tmpRoot;

    public PathValidatorTests()
    {
        _tmpRoot = Path.Combine(Path.GetTempPath(), "copilot-launcher-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tmpRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpRoot, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Validate_ReturnsNull_WhenPathIsEmpty()
    {
        Assert.Null(PathValidator.ValidateWorkingDirectory(null));
        Assert.Null(PathValidator.ValidateWorkingDirectory(string.Empty));
        Assert.Null(PathValidator.ValidateWorkingDirectory("   "));
    }

    [Fact]
    public void Validate_ReturnsNull_WhenPathDoesNotExist()
    {
        var missing = Path.Combine(_tmpRoot, "missing");
        Assert.Null(PathValidator.ValidateWorkingDirectory(missing));
    }

    [Fact]
    public void Validate_ReturnsNormalizedPath_WhenValid()
    {
        var dir = Path.Combine(_tmpRoot, "project");
        Directory.CreateDirectory(dir);
        var raw = Path.Combine(_tmpRoot, "project", ".", "..", "project");

        Assert.Equal(Path.GetFullPath(dir), PathValidator.ValidateWorkingDirectory(raw));
    }

    [Fact]
    public void Validate_ReturnsNull_WhenPathIsRelativeAndCwdMissing()
    {
        // Relative working directories stay invalid even if the current process
        // would be able to resolve them.
        Assert.Null(PathValidator.ValidateWorkingDirectory(@"relative\project"));
    }
}
