using CopilotLauncher.Helpers;
using CopilotLauncher.Models;

namespace CopilotLauncher.Services;

/// <summary>
/// What a Windows .lnk shortcut will contain when exported. Pure data — the
/// actual COM call to write the .lnk lives in the WinUI app project so this
/// stays unit-testable in plain .NET 8.
/// </summary>
public sealed class ShortcutExportPlan
{
    public required string TargetPath { get; init; }
    public required string Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Description { get; init; }
    public string? IconLocation { get; init; }
}

public interface IShortcutExportService
{
    /// <summary>
    /// Given a saved Shortcut, build the descriptor a .lnk writer needs:
    /// target exe + quoted arguments + working dir + description.
    /// Throws if the shortcut's working directory or terminal can't be
    /// resolved into a runnable command.
    /// </summary>
    ShortcutExportPlan BuildPlan(Shortcut shortcut);
}

public sealed class ShortcutExportService : IShortcutExportService
{
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;

    public ShortcutExportService(
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings)
    {
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
    }

    public ShortcutExportPlan BuildPlan(Shortcut shortcut)
    {
        var validatedWorkingDirectory = PathValidator.ValidateWorkingDirectory(shortcut.WorkingDirectory);
        if (validatedWorkingDirectory is null)
            throw new InvalidOperationException($"Working directory does not exist or is invalid: {shortcut.WorkingDirectory}");

        var terminal = ResolveTerminal(shortcut.TerminalOverride);
        var cmd = _launch.Build(new LaunchRequest
        {
            WorkingDirectory = validatedWorkingDirectory,
            ResumeTarget = shortcut.ResumeTarget,
            EnableAISummary = shortcut.EnableAISummary,
            EnableAllowAll = shortcut.EnableAllowAll,
            ExtraCopilotArgs = shortcut.ExtraCopilotArgs,
            Terminal = terminal,
        });

        return new ShortcutExportPlan
        {
            TargetPath = cmd.FileName,
            Arguments = cmd.ArgumentString,
            WorkingDirectory = cmd.WorkingDirectory,
            Description = $"Launch GitHub Copilot CLI for {shortcut.Label} (created by Copilot CLI Launcher)",
            IconLocation = cmd.FileName,  // use the target exe's own icon
        };
    }

    private TerminalProfile? ResolveTerminal(string? overrideId)
    {
        var discovered = _terminals.Discovered;
        if (discovered.Count == 0) return null;

        var pref = !string.IsNullOrEmpty(overrideId) && overrideId != "auto"
            ? overrideId
            : _settings.Current.Terminal.DefaultTerminal;

        if (!string.IsNullOrEmpty(pref) && pref != "auto")
        {
            var match = discovered.FirstOrDefault(t => t.Id == pref);
            if (match is not null) return match;
        }
        return discovered.OrderBy(t => t.Id switch
        {
            "wt" => 0, "pwsh" => 1, "powershell" => 2, "cmd" => 3, _ => 4
        }).First();
    }
}
