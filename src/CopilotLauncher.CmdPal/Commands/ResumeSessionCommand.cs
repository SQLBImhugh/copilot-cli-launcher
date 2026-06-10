using System;
using System.Diagnostics;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal.Commands;

/// <summary>
/// Resumes a Copilot CLI session by spawning the user's default terminal +
/// `copilot --resume=&lt;id&gt;` in the session's working directory. Wraps
/// <see cref="ILaunchService.Spawn"/> for one-click invocation from the
/// Command Palette.
/// </summary>
public sealed partial class ResumeSessionCommand : InvokableCommand
{
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;
    private readonly CopilotSession _session;

    public ResumeSessionCommand(
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings,
        CopilotSession session)
    {
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
        _session = session;
        Name = "Resume";
        // Mascot, NOT the Play glyph. Use IconHelpers.FromRelativePath
        // (Toolkit) because ms-appx:/// URIs do not resolve cross-process
        // when the extension is hosted by PT Command Palette — PT falls
        // back to its own default icon (which is what we kept seeing).
        // FromRelativePath resolves against the package install root and
        // loads the bytes for PT to render directly.
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
    }

    public override CommandResult Invoke()
    {
        try
        {
            var terminal = PickTerminal();
            var resumeDefaults = _settings.Current.SessionsResume;
            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = string.IsNullOrEmpty(_session.Cwd)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : _session.Cwd,
                ResumeTarget = _session.Id,
                EnableAllowAll = resumeDefaults.EnableAllowAll,
                ExtraCopilotArgs = resumeDefaults.ExtraCopilotArgs,
                Terminal = terminal,
            });
            // Hide the palette so the user can see their newly-opened terminal.
            return CommandResult.Hide();
        }
        catch
        {
            return CommandResult.KeepOpen();
        }
    }

    private TerminalProfile? PickTerminal()
    {
        var discovered = _terminals.Discovered;
        if (discovered.Count == 0) return null;
        var pref = _settings.Current.Terminal.DefaultTerminal;
        if (!string.IsNullOrEmpty(pref) && pref != "auto")
        {
            foreach (var t in discovered)
                if (t.Id == pref) return t;
        }
        TerminalProfile? best = null; int bestRank = int.MaxValue;
        foreach (var t in discovered)
        {
            var rank = t.Id switch { "wt" => 0, "pwsh" => 1, "powershell" => 2, "cmd" => 3, _ => 4 };
            if (rank < bestRank) { best = t; bestRank = rank; }
        }
        return best;
    }
}

