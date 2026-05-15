namespace CopilotLauncher.Models;

/// <summary>
/// Persisted app settings. Serialized to settings.json under the AppDataDirectory.
/// All seven settings groups from the architecture plan land here as nested objects
/// in subsequent phases; Phase 0 only fills in storage + about basics.
/// </summary>
public sealed class AppSettings
{
    public TerminalSettings Terminal { get; set; } = new();
    public CopilotCliSettings CopilotCli { get; set; } = new();
    public BriefingSettings Briefings { get; set; } = new();
    public RepairSettings Repair { get; set; } = new();
    public SessionListingSettings SessionListing { get; set; } = new();
    public LauncherBehaviorSettings LauncherBehavior { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    public bool MigrationCompleted { get; set; }
}

public sealed class TerminalSettings
{
    public string DefaultTerminal { get; set; } = "auto";   // auto | wt | pwsh | powershell | cmd | custom
    public string? CustomTerminalPath { get; set; }
    public string? CustomTerminalArgTemplate { get; set; }
    public string WindowsTerminalWindowMode { get; set; } = "tabInWindow0";
    public string? WindowsTerminalProfileName { get; set; }
    public string PowerShellHostPreference { get; set; } = "pwsh";
    public string InnerPwshArgs { get; set; } = "-NoExit -NoLogo -ExecutionPolicy Bypass";
}

public sealed class CopilotCliSettings
{
    public string? CopilotPath { get; set; }
    public bool AutoUpdateBeforeLaunch { get; set; } = true;
    public string AutoUpdateFrequency { get; set; } = "everyLaunch";
    public bool DefaultAllowAll { get; set; }
    public string? DefaultExtraArgs { get; set; }
    public string DefaultResumeTarget { get; set; } = "none";
}

public sealed class BriefingSettings
{
    public bool ShowVersionBumpBriefing { get; set; } = true;
    public bool AISummaryOnBump { get; set; }
    public string? AgentsContextFilePath { get; set; }
    public bool AppendToHistoryLog { get; set; } = true;
    public string FallbackSource { get; set; } = "both";
}

public sealed class RepairSettings
{
    public bool AutoRepairDanglingToolUse { get; set; } = true;
    public bool ApplyWin32KeepAliveWorkaround { get; set; } = true;
    public List<int> TrackedGitHubIssues { get; set; } = new() { 3298 };
}

public sealed class SessionListingSettings
{
    public int RecentWindowDays { get; set; } = 15;
    public int HeavyUseSummaryThreshold { get; set; } = 10;
    public bool GroupByProject { get; set; }
    public bool ShowFullSessionId { get; set; }
    public List<string> HiddenPathGlobs { get; set; } = new();
}

public sealed class LauncherBehaviorSettings
{
    public string AfterLaunch { get; set; } = "stayOpen";   // stayOpen | minimize | hideToTray | close
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool SystemTrayIconEnabled { get; set; } = true;
    public string Theme { get; set; } = "system";
    public string WindowTitlePattern { get; set; } = "Copilot Launcher";
}

public sealed class StorageSettings
{
    public string? SavedLaunchesFile { get; set; }
    public string? BriefingHistoryFile { get; set; }
    public string? StateDirectory { get; set; }
}
