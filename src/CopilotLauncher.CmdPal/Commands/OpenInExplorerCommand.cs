using System;
using System.Diagnostics;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace CopilotLauncher.CmdPal.Commands;

/// <summary>
/// Opens a working directory in Windows Explorer. Used by the list-item
/// context menus so users can jump straight to a session or shortcut folder.
/// </summary>
public sealed partial class OpenInExplorerCommand : InvokableCommand
{
    private readonly string? _targetDirectory;

    public OpenInExplorerCommand(string? targetDirectory)
    {
        _targetDirectory = targetDirectory;
        Name = "Open in Explorer";
        Icon = new IconInfo("\uE838");
    }

    public override CommandResult Invoke()
    {
        if (string.IsNullOrWhiteSpace(_targetDirectory) || !Directory.Exists(_targetDirectory))
        {
            return CommandResult.ShowToast("Folder does not exist.");
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _targetDirectory)
            {
                UseShellExecute = true,
            });
            return CommandResult.Hide();
        }
        catch
        {
            return CommandResult.ShowToast("Failed to open Explorer.");
        }
    }
}
