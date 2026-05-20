using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

/// <summary>
/// Backs the Settings page. Two-way bindings flow into the underlying
/// <see cref="AppSettings"/>; saves to disk are debounced (500ms) and
/// run off the UI thread to avoid jank from rapid slider/text changes.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const int SaveDebounceMs = 500;

    private readonly ISettingsService _settings;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly System.Threading.Timer _saveTimer;
    private int _saveScheduled;  // 0 = idle, 1 = debounce in flight; single-flight guard

    public SettingsViewModel(ISettingsService settings, ITerminalDiscoveryService terminals)
    {
        _settings = settings;
        _terminals = terminals;
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    /// <summary>Underlying mutable settings; bind sub-objects via x:Bind paths like
    /// <c>{x:Bind ViewModel.Settings.Briefings.AISummaryOnBump, Mode=TwoWay}</c>.
    /// Each setter on a sub-object should call <see cref="ScheduleSave"/>.</summary>
    public AppSettings Settings => _settings.Current;

    public IReadOnlyList<TerminalProfile> Terminals => _terminals.Discovered;

    /// <summary>Schedule a save 500ms from now. Repeated calls within the window
    /// reset the timer (debounce), so dragging a slider doesn't disk-spam.</summary>
    public void ScheduleSave()
    {
        // single-flight guard: if a save is already pending, just reset the timer
        System.Threading.Interlocked.Exchange(ref _saveScheduled, 1);
        _saveTimer.Change(SaveDebounceMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>Force-save immediately (e.g., on app exit).</summary>
    public void Flush()
    {
        if (System.Threading.Interlocked.Exchange(ref _saveScheduled, 0) == 0) return;
        try
        {
            _settings.Save();
        }
        catch
        {
            // Persist errors are non-fatal; the user can re-edit. Log when logging exists.
        }
    }

    // --- Bindable properties (one wrapper each so x:Bind setters can call ScheduleSave) ---
    // Terminal
    public string DefaultTerminal { get => Settings.Terminal.DefaultTerminal; set { Settings.Terminal.DefaultTerminal = value; OnPropertyChanged(); ScheduleSave(); } }
    public string PowerShellHostPreference { get => Settings.Terminal.PowerShellHostPreference; set { Settings.Terminal.PowerShellHostPreference = value; OnPropertyChanged(); ScheduleSave(); } }
    public string InnerPwshArgs { get => Settings.Terminal.InnerPwshArgs; set { Settings.Terminal.InnerPwshArgs = value; OnPropertyChanged(); ScheduleSave(); } }

    // Sessions Resume defaults
    public bool ResumeAISummary { get => Settings.SessionsResume.EnableAISummary; set { Settings.SessionsResume.EnableAISummary = value; OnPropertyChanged(); ScheduleSave(); } }
    public bool ResumeAllowAll { get => Settings.SessionsResume.EnableAllowAll; set { Settings.SessionsResume.EnableAllowAll = value; OnPropertyChanged(); ScheduleSave(); } }
    public string ResumeExtraArgs { get => Settings.SessionsResume.ExtraCopilotArgs ?? string.Empty; set { Settings.SessionsResume.ExtraCopilotArgs = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); ScheduleSave(); } }

    // Copilot CLI defaults (used by New Shortcut form pre-fill)
    public bool ShortcutDefaultAllowAll { get => Settings.CopilotCli.DefaultAllowAll; set { Settings.CopilotCli.DefaultAllowAll = value; OnPropertyChanged(); ScheduleSave(); } }
    public string ShortcutDefaultExtraArgs { get => Settings.CopilotCli.DefaultExtraArgs ?? string.Empty; set { Settings.CopilotCli.DefaultExtraArgs = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); ScheduleSave(); } }
    public bool AutoUpdateBeforeLaunch { get => Settings.CopilotCli.AutoUpdateBeforeLaunch; set { Settings.CopilotCli.AutoUpdateBeforeLaunch = value; OnPropertyChanged(); ScheduleSave(); } }
    public string AutoUpdateFrequency { get => Settings.CopilotCli.AutoUpdateFrequency; set { Settings.CopilotCli.AutoUpdateFrequency = value; OnPropertyChanged(); ScheduleSave(); } }

    // Briefings + AI summary
    public bool ShowVersionBumpBriefing { get => Settings.Briefings.ShowVersionBumpBriefing; set { Settings.Briefings.ShowVersionBumpBriefing = value; OnPropertyChanged(); ScheduleSave(); } }
    public bool AISummaryOnBump { get => Settings.Briefings.AISummaryOnBump; set { Settings.Briefings.AISummaryOnBump = value; OnPropertyChanged(); ScheduleSave(); } }
    public string AgentsContextFilePath { get => Settings.Briefings.AgentsContextFilePath ?? string.Empty; set { Settings.Briefings.AgentsContextFilePath = string.IsNullOrWhiteSpace(value) ? null : value; OnPropertyChanged(); ScheduleSave(); } }
    public string BriefingSessionName { get => Settings.Briefings.BriefingSessionName ?? string.Empty; set { Settings.Briefings.BriefingSessionName = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); OnPropertyChanged(); ScheduleSave(); } }
    public bool AppendToHistoryLog { get => Settings.Briefings.AppendToHistoryLog; set { Settings.Briefings.AppendToHistoryLog = value; OnPropertyChanged(); ScheduleSave(); } }

    // Session listing
    public int RecentWindowDays { get => Settings.SessionListing.RecentWindowDays; set { Settings.SessionListing.RecentWindowDays = value; OnPropertyChanged(); ScheduleSave(); } }
    public int HeavyUseSummaryThreshold { get => Settings.SessionListing.HeavyUseSummaryThreshold; set { Settings.SessionListing.HeavyUseSummaryThreshold = value; OnPropertyChanged(); ScheduleSave(); } }
    public bool GroupByProject { get => Settings.SessionListing.GroupByProject; set { Settings.SessionListing.GroupByProject = value; OnPropertyChanged(); ScheduleSave(); } }
    public bool ShowFullSessionId { get => Settings.SessionListing.ShowFullSessionId; set { Settings.SessionListing.ShowFullSessionId = value; OnPropertyChanged(); ScheduleSave(); } }

    // Repair
    public bool AutoRepairDanglingToolUse { get => Settings.Repair.AutoRepairDanglingToolUse; set { Settings.Repair.AutoRepairDanglingToolUse = value; OnPropertyChanged(); ScheduleSave(); } }

    // Launcher behavior
    public string AfterLaunch { get => Settings.LauncherBehavior.AfterLaunch; set { Settings.LauncherBehavior.AfterLaunch = value; OnPropertyChanged(); ScheduleSave(); } }
    public bool StartWithWindows { get => Settings.LauncherBehavior.StartWithWindows; set { Settings.LauncherBehavior.StartWithWindows = value; OnPropertyChanged(); ScheduleSave(); StartWithWindowsChanged?.Invoke(this, value); } }
    public string Theme { get => Settings.LauncherBehavior.Theme; set { if (Settings.LauncherBehavior.Theme != value) { Settings.LauncherBehavior.Theme = value; OnPropertyChanged(); ScheduleSave(); ThemeChanged?.Invoke(this, value); } } }

    /// <summary>Raised when StartWithWindows toggles. Consumed by SettingsPage to call
    /// the platform-specific registry sync (which lives in the WinUI app project).</summary>
    public event EventHandler<bool>? StartWithWindowsChanged;

    /// <summary>Raised when the user picks a different launcher theme. WinUI
    /// side hooks this to call ThemeManager.Apply on the main window.</summary>
    public event EventHandler<string>? ThemeChanged;

    public void Dispose()
    {
        Flush();
        _saveTimer.Dispose();
    }
}
