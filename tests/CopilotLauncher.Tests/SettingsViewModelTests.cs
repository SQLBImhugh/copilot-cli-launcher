using System.Text.Json;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;
using Xunit;

namespace CopilotLauncher.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void ExcludedPathsText_InitiallyListsOneFolderPerLine()
    {
        var settings = new MemorySettings();
        settings.Current.SessionListing.ExcludedPaths = new() { @"C:\work\Frontier", @"D:\generated" };
        using var vm = new SettingsViewModel(settings, new EmptyTerminals());

        Assert.Equal(@"C:\work\Frontier" + Environment.NewLine + @"D:\generated", vm.ExcludedPathsText);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void ExcludedPathsText_ConvertsLinesToTrimmedNonblankFolders(string newline)
    {
        var settings = new MemorySettings();
        using var vm = new SettingsViewModel(settings, new EmptyTerminals());
        var text = $"  C:\\work\\Frontier  {newline}{newline} \t{newline}D:\\generated{newline}";

        vm.ExcludedPathsText = text;

        Assert.Equal(new[] { @"C:\work\Frontier", @"D:\generated" }, settings.Current.SessionListing.ExcludedPaths);
        Assert.Equal(text, vm.ExcludedPathsText);
    }

    [Fact]
    public void ExcludedPathsText_PreservesTrailingNewlineWhileAddingAnotherFolder()
    {
        var settings = new MemorySettings();
        using var vm = new SettingsViewModel(settings, new EmptyTerminals());

        vm.ExcludedPathsText = @"C:\work\Frontier" + "\r\n";
        Assert.EndsWith("\r\n", vm.ExcludedPathsText);
        Assert.Equal(new[] { @"C:\work\Frontier" }, settings.Current.SessionListing.ExcludedPaths);

        vm.ExcludedPathsText += @"D:\generated";
        Assert.Equal(new[] { @"C:\work\Frontier", @"D:\generated" }, settings.Current.SessionListing.ExcludedPaths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t\n ")]
    public void ExcludedPathsText_ClearingEditor_RemovesExcludedFolders(string? text)
    {
        var settings = new MemorySettings();
        settings.Current.SessionListing.ExcludedPaths.Add(@"C:\work\Frontier");
        using var vm = new SettingsViewModel(settings, new EmptyTerminals());

        vm.ExcludedPathsText = text!;

        Assert.Empty(settings.Current.SessionListing.ExcludedPaths);
        Assert.Equal(text ?? string.Empty, vm.ExcludedPathsText);
    }

    [Fact]
    public async Task ExcludedPathsText_Flush_PersistsListUsingExistingSettingsSave()
    {
        var settings = new MemorySettings();
        settings.Current.SessionListing.HiddenPathGlobs.Add("**/legacy/**");
        using var vm = new SettingsViewModel(settings, new EmptyTerminals());
        vm.ExcludedPathsText = " C:\\work\\Frontier \r\n\r\n D:\\generated ";

        vm.Flush();
        var json = await settings.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var parsed = JsonSerializer.Deserialize<AppSettings>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        Assert.Equal(new[] { @"C:\work\Frontier", @"D:\generated" }, parsed.SessionListing.ExcludedPaths);
        Assert.Equal(new[] { "**/legacy/**" }, parsed.SessionListing.HiddenPathGlobs);
    }

    private sealed class MemorySettings : ISettingsService
    {
        public string AppDataDirectory => @"C:\unused";
        public string SettingsFilePath => @"C:\unused\settings.json";
        public AppSettings Current { get; } = new();
        public TaskCompletionSource<string> Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Load() { }
        public void Save() => Saved.TrySetResult(JsonSerializer.Serialize(Current,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class EmptyTerminals : ITerminalDiscoveryService
    {
        public IReadOnlyList<TerminalProfile> Discovered { get; } = Array.Empty<TerminalProfile>();
        public void Refresh() { }
    }
}
