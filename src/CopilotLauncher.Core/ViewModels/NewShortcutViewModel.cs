using System.ComponentModel;
using System.Runtime.CompilerServices;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// ViewModel for the New Shortcut wizard. Holds form state, computes a live
/// command-line preview, and persists the result via <see cref="IShortcutsService"/>
/// (with optional immediate spawn through <see cref="ILaunchService"/>).
/// </summary>
public sealed class NewShortcutViewModel : INotifyPropertyChanged
{
    private readonly IShortcutsService _store;
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;

    public NewShortcutViewModel(
        IShortcutsService store,
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings)
    {
        _store = store;
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
        // Default to current dir + the global default terminal.
        _workingDirectory = Environment.CurrentDirectory;
        _enableAllowAll = settings.Current.CopilotCli.DefaultAllowAll;
        _extraArgs = settings.Current.CopilotCli.DefaultExtraArgs ?? string.Empty;
    }

    private string _label = string.Empty;
    private string _workingDirectory;
    private string _resumeTarget = string.Empty;
    private bool _enableAISummary;
    private bool _enableAllowAll;
    private string _extraArgs;
    private string _terminalOverride = string.Empty;
    private string _statusMessage = string.Empty;

    public string Label
    {
        get => _label;
        set { if (_label != value) { _label = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(CanSave)); RecalcPreview(); } }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { if (_workingDirectory != value) { _workingDirectory = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(CanSave)); RecalcPreview(); } }
    }

    public string ResumeTarget
    {
        get => _resumeTarget;
        set { if (_resumeTarget != value) { _resumeTarget = value ?? string.Empty; OnPropertyChanged(); RecalcPreview(); } }
    }

    public bool EnableAISummary
    {
        get => _enableAISummary;
        set { if (_enableAISummary != value) { _enableAISummary = value; OnPropertyChanged(); RecalcPreview(); } }
    }

    public bool EnableAllowAll
    {
        get => _enableAllowAll;
        set { if (_enableAllowAll != value) { _enableAllowAll = value; OnPropertyChanged(); RecalcPreview(); } }
    }

    public string ExtraArgs
    {
        get => _extraArgs;
        set { if (_extraArgs != value) { _extraArgs = value ?? string.Empty; OnPropertyChanged(); RecalcPreview(); } }
    }

    /// <summary>Terminal id ("auto" / "wt" / "pwsh" / etc.) chosen for THIS launch.</summary>
    public string TerminalOverride
    {
        get => _terminalOverride;
        set { if (_terminalOverride != value) { _terminalOverride = value ?? string.Empty; OnPropertyChanged(); RecalcPreview(); } }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
    }

    private string _commandPreview = string.Empty;
    public string CommandPreview
    {
        get => _commandPreview;
        private set { if (_commandPreview != value) { _commandPreview = value; OnPropertyChanged(); } }
    }

    public bool CanSave => !string.IsNullOrWhiteSpace(_label) && !string.IsNullOrWhiteSpace(_workingDirectory);

    /// <summary>List of {id,displayName} for the Terminal dropdown including 'Auto-detect'.</summary>
    public IReadOnlyList<(string Id, string DisplayName)> TerminalOptions
    {
        get
        {
            var list = new List<(string, string)> { ("auto", "Use default (from Settings)") };
            foreach (var t in _terminals.Discovered)
                list.Add((t.Id, t.DisplayName));
            return list;
        }
    }

    public void RecalcPreview()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_workingDirectory))
            {
                CommandPreview = "(set a working directory to see the command)";
                return;
            }
            var terminal = ResolveTerminal();
            var cmd = _launch.Build(BuildRequest(terminal));
            // Render as "<exe> <args>" — what an .lnk would store.
            CommandPreview = string.IsNullOrEmpty(cmd.ArgumentString)
                ? cmd.FileName
                : $"{cmd.FileName}  {cmd.ArgumentString}";
        }
        catch (Exception ex)
        {
            CommandPreview = $"(cannot preview: {ex.Message})";
        }
    }

    /// <summary>Save the form as a Shortcut entry. Returns the new entry, or null if invalid.</summary>
    public Shortcut? Save()
    {
        if (!CanSave)
        {
            StatusMessage = "Label and working directory are required.";
            return null;
        }
        var entry = new Shortcut
        {
            Id = Guid.NewGuid().ToString(),
            Label = _label.Trim(),
            WorkingDirectory = _workingDirectory.Trim(),
            ResumeTarget = string.IsNullOrWhiteSpace(_resumeTarget) ? null : _resumeTarget.Trim(),
            EnableAISummary = _enableAISummary,
            EnableAllowAll = _enableAllowAll,
            ExtraCopilotArgs = string.IsNullOrWhiteSpace(_extraArgs) ? null : _extraArgs.Trim(),
            TerminalOverride = string.IsNullOrEmpty(_terminalOverride) || _terminalOverride == "auto" ? null : _terminalOverride,
        };
        _store.Add(entry);
        StatusMessage = $"Saved '{entry.Label}'.";
        return entry;
    }

    /// <summary>Save AND spawn. Returns true if both succeeded.</summary>
    public bool SaveAndLaunch()
    {
        var entry = Save();
        if (entry is null) return false;
        try
        {
            _launch.Spawn(BuildRequest(ResolveTerminal()));
            StatusMessage = $"Saved + launched '{entry.Label}'.";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Saved, but launch failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Reset the form to defaults.</summary>
    public void Reset()
    {
        Label = string.Empty;
        ResumeTarget = string.Empty;
        EnableAISummary = false;
        EnableAllowAll = _settings.Current.CopilotCli.DefaultAllowAll;
        ExtraArgs = _settings.Current.CopilotCli.DefaultExtraArgs ?? string.Empty;
        TerminalOverride = string.Empty;
        // Keep WorkingDirectory as-is — user might be queuing several launches in the same dir.
        StatusMessage = "Form reset.";
    }

    /// <summary>Pre-populate the form from an existing CopilotSession (the 'Save as shortcut…' flow).</summary>
    public void PopulateFrom(string suggestedLabel, string workingDir, string? resumeId = null)
    {
        Label = suggestedLabel ?? string.Empty;
        WorkingDirectory = workingDir ?? Environment.CurrentDirectory;
        if (resumeId is not null) ResumeTarget = resumeId;
        StatusMessage = "Pre-populated from session — review and Save.";
    }

    private LaunchRequest BuildRequest(TerminalProfile? terminal) => new()
    {
        WorkingDirectory = _workingDirectory,
        ResumeTarget = string.IsNullOrWhiteSpace(_resumeTarget) ? null : _resumeTarget,
        EnableAISummary = _enableAISummary,
        EnableAllowAll = _enableAllowAll,
        ExtraCopilotArgs = string.IsNullOrWhiteSpace(_extraArgs) ? null : _extraArgs,
        Terminal = terminal,
    };

    private TerminalProfile? ResolveTerminal()
    {
        var discovered = _terminals.Discovered;
        if (discovered.Count == 0) return null;

        var pref = !string.IsNullOrEmpty(_terminalOverride) && _terminalOverride != "auto"
                ? _terminalOverride
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
