using System;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal.Commands;

/// <summary>Launches a saved <see cref="Shortcut"/>. Wraps
/// <see cref="ILaunchService.Spawn"/> with the shortcut's saved flags +
/// terminal override.</summary>
public sealed partial class LaunchShortcutCommand : InvokableCommand
{
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;
    private readonly Shortcut _shortcut;

    public LaunchShortcutCommand(
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings,
        Shortcut shortcut)
    {
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
        _shortcut = shortcut;
        Name = "Launch";
        // FromRelativePath instead of ms-appx:/// — see ResumeSessionCommand.
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
    }

    public override CommandResult Invoke()
    {
        try
        {
            var terminal = PickTerminal(_shortcut.TerminalOverride);
            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = _shortcut.WorkingDirectory,
                ResumeTarget = _shortcut.ResumeTarget,
                EnableAllowAll = _shortcut.EnableAllowAll,
                ExtraCopilotArgs = _shortcut.ExtraCopilotArgs,
                Terminal = terminal,
            });
            return CommandResult.Hide();
        }
        catch
        {
            return CommandResult.KeepOpen();
        }
    }

    private TerminalProfile? PickTerminal(string? overrideId)
    {
        var discovered = _terminals.Discovered;
        if (discovered.Count == 0) return null;
        var pref = !string.IsNullOrEmpty(overrideId) && overrideId != "auto"
            ? overrideId
            : _settings.Current.Terminal.DefaultTerminal;
        if (!string.IsNullOrEmpty(pref) && pref != "auto")
            foreach (var t in discovered)
                if (t.Id == pref) return t;
        TerminalProfile? best = null; int bestRank = int.MaxValue;
        foreach (var t in discovered)
        {
            var rank = t.Id switch { "wt" => 0, "pwsh" => 1, "powershell" => 2, "cmd" => 3, _ => 4 };
            if (rank < bestRank) { best = t; bestRank = rank; }
        }
        return best;
    }
}

