using Xunit;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Tests;

public sealed class CopilotArgsEditorViewModelTests
{
    [Fact]
    public void LoadFromSeedsRowsAndUnrecognizedText()
    {
        var vm = new CopilotArgsEditorViewModel();

        vm.LoadFrom("--model gpt-5.5 --autopilot --unknown value");

        Assert.True(Row(vm, "--model").IsEnabled);
        Assert.Equal("gpt-5.5", Row(vm, "--model").Value);
        Assert.True(Row(vm, "--autopilot").IsEnabled);
        Assert.Equal("--unknown value", vm.UnrecognizedText);
    }

    [Fact]
    public void ToArgStringFormatsEnabledRowsAndPassthroughText()
    {
        var vm = new CopilotArgsEditorViewModel();
        Row(vm, "--model").IsEnabled = true;
        Row(vm, "--model").Value = "gpt-5.4";
        Row(vm, "--effort").IsEnabled = true;
        Row(vm, "--effort").Value = "medium";
        Row(vm, "--no-color").IsEnabled = true;
        vm.UnrecognizedText = "--future x";

        Assert.Equal("--model gpt-5.4 --effort medium --no-color --future x", vm.ToArgString());
    }

    [Fact]
    public void TogglingRowOffRemovesOnlyThatFlag()
    {
        var vm = new CopilotArgsEditorViewModel();
        vm.LoadFrom("--model auto --effort high --future x");

        Row(vm, "--effort").IsEnabled = false;

        Assert.Equal("--model auto --future x", vm.ToArgString());
    }

    [Fact]
    public void RepeatableRowsUseOneValuePerLine()
    {
        var vm = new CopilotArgsEditorViewModel();
        vm.LoadFrom("--allow-tool shell(git:*) --allow-tool shell(gh:*)");

        Assert.Equal($"shell(git:*){Environment.NewLine}shell(gh:*)", Row(vm, "--allow-tool").Value);
        Assert.Equal("--allow-tool shell(git:*) --allow-tool shell(gh:*)", vm.ToArgString());
    }

    [Fact]
    public void ChangedFiresWhenRowsAndPassthroughChange()
    {
        var vm = new CopilotArgsEditorViewModel();
        var count = 0;
        vm.Changed += (_, _) => count++;

        Row(vm, "--banner").IsEnabled = true;
        vm.UnrecognizedText = "--future";

        Assert.True(count >= 2);
    }

    [Fact]
    public void GroupsExposeRowsByCategory()
    {
        var vm = new CopilotArgsEditorViewModel();

        Assert.Contains(vm.Groups, g => g.Category == "Model & reasoning" && g.Rows.Any(r => r.Flag == "--model"));
        Assert.Contains(vm.Groups, g => g.Category == "Advanced" && g.Rows.Any(r => r.Flag == "--plugin-dir"));
    }

    private static CopilotArgRowViewModel Row(CopilotArgsEditorViewModel vm, string flag) =>
        vm.Rows.Single(r => string.Equals(r.Flag, flag, StringComparison.OrdinalIgnoreCase));
}


