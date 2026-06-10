using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// ViewModel for the Shortcuts page. Wraps <see cref="IShortcutsService"/>
/// + <see cref="ILaunchService"/> behind an ObservableCollection so the UI updates
/// when entries are added / removed.
/// </summary>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    private readonly IShortcutsService _store;
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;
    private readonly IAfterLaunchAction _afterLaunch;

    public ObservableCollection<Shortcut> Items { get; } = new();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ShortcutsViewModel(
        IShortcutsService store,
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings,
        IAfterLaunchAction? afterLaunch = null)
    {
        _store = store;
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
        _afterLaunch = afterLaunch ?? new NoopAfterLaunchAction();
    }

    public void Reload()
    {
        try
        {
            _store.Reload();
            Items.Clear();
            foreach (var l in _store.All) Items.Add(l);
            StatusMessage = Items.Count switch
            {
                0 => "No saved shortcuts yet. Use the New Shortcut tab (or 'Save as shortcut…' on a session card) to add one.",
                1 => "1 saved shortcut.",
                _ => $"{Items.Count} saved shortcuts.",
            };
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load shortcuts.json: {ex.Message}";
        }
    }

    public bool LaunchOne(Shortcut entry)
    {
        try
        {
            var terminal = ResolveTerminal(entry.TerminalOverride);
            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = entry.WorkingDirectory,
                ResumeTarget = entry.ResumeTarget,
                EnableAllowAll = entry.EnableAllowAll,
                ExtraCopilotArgs = entry.ExtraCopilotArgs,
                Terminal = terminal,
            });
            StatusMessage = $"Launched '{entry.Label}' in {terminal?.DisplayName ?? "direct"}.";
            _afterLaunch.Apply(_settings.Current.LauncherBehavior.AfterLaunch);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Launch failed: {ex.Message}";
            return false;
        }
    }

    public void Delete(Shortcut entry)
    {
        _store.Remove(entry.Id);
        Items.Remove(entry);
        StatusMessage = $"Deleted '{entry.Label}'.";
    }

    private TerminalProfile? ResolveTerminal(string? overrideId)
    {
        var discovered = _terminals.Discovered;
        if (discovered.Count == 0) return null;

        var pref = !string.IsNullOrEmpty(overrideId) ? overrideId
                  : _settings.Current.Terminal.DefaultTerminal;

        if (!string.IsNullOrEmpty(pref) && pref != "auto")
        {
            var match = discovered.FirstOrDefault(t => t.Id == pref);
            if (match is not null) return match;
        }
        return discovered.OrderBy(t => t.Id switch
        {
            "wt" => 0,
            "pwsh" => 1,
            "powershell" => 2,
            "cmd" => 3,
            _ => 4
        }).First();
    }
}
