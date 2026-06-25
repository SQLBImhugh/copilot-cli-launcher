using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// ViewModel for the New Session tab: pick a folder + optional copilot args and
/// start a FRESH Copilot CLI session there (ResumeTarget is always null). Unlike
/// <see cref="NewShortcutViewModel"/> it never persists anything — it's a
/// one-shot launcher. Mirrors the Sessions tab's new-session flags (allow-all,
/// extra args, extension pre-approval, after-launch action) so a fresh session
/// from here behaves the same as one started elsewhere in the app.
/// </summary>
public sealed partial class NewSessionViewModel : ObservableObject
{
    private readonly ILaunchService _launch;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISettingsService _settings;
    private readonly IAfterLaunchAction _afterLaunch;
    private readonly IExtensionPermissionService? _extPerms;

    public NewSessionViewModel(
        ILaunchService launch,
        ITerminalDiscoveryService terminals,
        ISettingsService settings,
        IAfterLaunchAction? afterLaunch = null,
        IExtensionPermissionService? extPerms = null)
    {
        _launch = launch;
        _terminals = terminals;
        _settings = settings;
        _afterLaunch = afterLaunch ?? new NoopAfterLaunchAction();
        _extPerms = extPerms;
        _enableAllowAll = settings.Current.CopilotCli.DefaultAllowAll;
        _extraArgs = settings.Current.CopilotCli.DefaultExtraArgs ?? string.Empty;
        _capabilities = settings.Current.DefaultCapabilities.IsEmpty ? null : settings.Current.DefaultCapabilities.Clone();
    }

    private string _workingDirectory = string.Empty;
    private string _extraArgs;
    private string _terminalOverride = string.Empty;
    private string _statusMessage = string.Empty;
    private string _commandPreview = string.Empty;
    private LaunchCapabilities? _capabilities;

    [ObservableProperty]
    private bool _enableAllowAll;

    partial void OnEnableAllowAllChanged(bool value) => RecalcPreview();

    /// <summary>Folder the fresh session starts in. Required.</summary>
    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { if (_workingDirectory != value) { _workingDirectory = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(CanLaunch)); RecalcPreview(); } }
    }

    /// <summary>Verbatim flags appended to the copilot command (the "launcher args").</summary>
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
        private set => SetProperty(ref _statusMessage, value);
    }

    public string CommandPreview
    {
        get => _commandPreview;
        private set => SetProperty(ref _commandPreview, value);
    }

    public bool CanLaunch => !string.IsNullOrWhiteSpace(_workingDirectory);

    /// <summary>Per-launch capability selection (which MCPs / agent / tools / skills load). Set by the editor.</summary>
    public LaunchCapabilities? Capabilities
    {
        get => _capabilities;
        set { _capabilities = value; OnPropertyChanged(); RecalcPreview(); }
    }

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
                CommandPreview = "(set a folder to see the command)";
                return;
            }
            var cmd = _launch.Build(BuildRequest(ResolveTerminal()));
            CommandPreview = string.IsNullOrEmpty(cmd.ArgumentString)
                ? cmd.FileName
                : $"{cmd.FileName}  {cmd.ArgumentString}";
        }
        catch (Exception ex)
        {
            CommandPreview = $"(cannot preview: {ex.Message})";
        }
    }

    /// <summary>Start a fresh Copilot session in the chosen folder. Returns true on success.</summary>
    public bool StartSession()
    {
        if (!CanLaunch)
        {
            StatusMessage = "Choose a folder first.";
            return false;
        }

        var validated = PathValidator.ValidateWorkingDirectory(_workingDirectory);
        if (validated is null)
        {
            StatusMessage = "Folder does not exist.";
            return false;
        }
        if (!string.Equals(_workingDirectory, validated, StringComparison.Ordinal))
            WorkingDirectory = validated;

        try
        {
            // Honor the same extension pre-approval the Sessions tab applies to
            // its resume / new-session launches so a fresh session here doesn't
            // re-prompt for extensions the user already trusts in this repo.
            if (_settings.Current.SessionsResume.PreApproveExtensions)
                _extPerms?.EnsureExtensionGrants(validated);

            var terminal = ResolveTerminal();
            _launch.Spawn(BuildRequest(terminal));
            StatusMessage = terminal is not null
                ? $"Started a new session in {validated} ({terminal.DisplayName})."
                : $"Started a new session in {validated}.";
            _afterLaunch.Apply(_settings.Current.LauncherBehavior.AfterLaunch);
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Launch failed: {ex.Message}";
            return false;
        }
    }

    private LaunchRequest BuildRequest(TerminalProfile? terminal) => new()
    {
        WorkingDirectory = _workingDirectory,
        ResumeTarget = null, // always a fresh session
        EnableAllowAll = this.EnableAllowAll,
        ExtraCopilotArgs = string.IsNullOrWhiteSpace(_extraArgs) ? null : _extraArgs,
        Capabilities = _capabilities,
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
            "wt" => 0,
            "pwsh" => 1,
            "powershell" => 2,
            "cmd" => 3,
            _ => 4
        }).First();
    }
}
