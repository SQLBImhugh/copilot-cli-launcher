using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// ViewModel for the Saved Launches page. Wraps <see cref="ISavedLaunchesService"/>
/// + <see cref="ILaunchService"/> behind an ObservableCollection so the UI updates
/// when entries are added / removed.
/// </summary>
public sealed class SavedLaunchesViewModel : INotifyPropertyChanged
{
    private readonly ISavedLaunchesService _store;
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;

    public ObservableCollection<SavedLaunch> Items { get; } = new();

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    public SavedLaunchesViewModel(
        ISavedLaunchesService store,
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings)
    {
        _store = store;
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
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
                0 => "No saved launches yet. Use the New Launch tab (or 'Save as launch…' on a session card) to add one.",
                1 => "1 saved launch.",
                _ => $"{Items.Count} saved launches.",
            };
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load launches.json: {ex.Message}";
        }
    }

    public bool LaunchOne(SavedLaunch entry)
    {
        try
        {
            var terminal = ResolveTerminal(entry.TerminalOverride);
            _launch.Spawn(new LaunchRequest
            {
                WorkingDirectory = entry.WorkingDirectory,
                ResumeTarget = entry.ResumeTarget,
                EnableAISummary = entry.EnableAISummary,
                EnableAllowAll = entry.EnableAllowAll,
                ExtraCopilotArgs = entry.ExtraCopilotArgs,
                Terminal = terminal,
            });
            StatusMessage = $"Launched '{entry.Label}' in {terminal?.DisplayName ?? "direct"}.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Launch failed: {ex.Message}";
            return false;
        }
    }

    public void Delete(SavedLaunch entry)
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
            "wt" => 0, "pwsh" => 1, "powershell" => 2, "cmd" => 3, _ => 4
        }).First();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
