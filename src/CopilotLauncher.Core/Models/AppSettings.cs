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
    public SessionsResumeSettings SessionsResume { get; set; } = new();
    public BriefingSettings Briefings { get; set; } = new();
    public RepairSettings Repair { get; set; } = new();
    public SessionListingSettings SessionListing { get; set; } = new();
    public LauncherBehaviorSettings LauncherBehavior { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();

    public bool MigrationCompleted { get; set; }

    /// <summary>
    /// Replace any null sub-objects with fresh defaults. Run after JSON
    /// deserialization so an old or hand-edited settings.json that contains
    /// "briefings": null doesn't NPE the entire app on launch. Property
    /// initializers handle the missing-key case automatically; this handles
    /// the explicit-null case.
    /// </summary>
    public void Normalize()
    {
        Terminal         ??= new();
        CopilotCli       ??= new();
        SessionsResume   ??= new();
        Briefings        ??= new();
        Repair           ??= new();
        SessionListing   ??= new();
        LauncherBehavior ??= new();
        Storage          ??= new();

        Repair.TrackedGitHubIssues   ??= new() { 3298 };
        SessionListing.HiddenPathGlobs ??= new();
    }
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

/// <summary>
/// Defaults applied when the user clicks the ▶ Resume button on a session
/// card in the Sessions tab. Distinct from per-shortcut config (which is
/// stored on each <see cref="Shortcut"/> and used by the Shortcuts page
/// Launch button). These let the Sessions-tab one-click resume use the
/// same flags every time without having to save a Shortcut for every
/// session you might want to revisit.
/// </summary>
public sealed class SessionsResumeSettings
{
    public bool EnableAISummary { get; set; }
    public bool EnableAllowAll { get; set; }
    public string? ExtraCopilotArgs { get; set; }
}

public sealed class BriefingSettings
{
    public bool ShowVersionBumpBriefing { get; set; } = true;
    public bool AISummaryOnBump { get; set; }
    /// <summary>True to also generate AI summaries when the startup update
    /// check (gated by AutoUpdateFrequency=daily|weekly) detects a version
    /// bump. Distinct from <see cref="AISummaryOnBump"/> which controls the
    /// manual Briefing tab "Check now" button. Default false to avoid
    /// burning premium Copilot CLI requests on every launch.</summary>
    public bool AISummaryOnStartupUpdate { get; set; }
    public string? AgentsContextFilePath { get; set; }
    public bool AppendToHistoryLog { get; set; } = true;
    public string FallbackSource { get; set; } = "both";

    /// <summary>Name of the Copilot CLI session to reuse for AI briefings so
    /// historical context accumulates across version bumps. When set, the
    /// launcher passes <c>--resume=&lt;name&gt;</c> if a session with this
    /// name already exists (per workspace.yaml), or <c>--name &lt;name&gt;</c>
    /// to create a new one on first use. Set to empty/null for stateless
    /// one-shot summaries. Default matches the convention many users adopted
    /// manually before the launcher gained this feature.</summary>
    public string? BriefingSessionName { get; set; } = "CopilotCLI-Update-Briefings";
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
    public string Theme { get; set; } = "copilotCli";   // system | light | dark | copilotCli (default)
    public string WindowTitlePattern { get; set; } = "Copilot Launcher";
    /// <summary>True if the user toggled into the compact-mini view.</summary>
    public bool CompactMode { get; set; }
    /// <summary>Window width in effective pixels last time the app was in
    /// non-compact mode. Used to restore when leaving compact, and applied
    /// on startup so resizes survive across launches. Default is the
    /// "Sessions overview at portrait scale" sizing the user picked.</summary>
    public int LastNormalWindowWidth { get; set; } = 1180;
    public int LastNormalWindowHeight { get; set; } = 1040;
}

public sealed class StorageSettings
{
    public string? SavedLaunchesFile { get; set; }
    public string? BriefingHistoryFile { get; set; }
    public string? StateDirectory { get; set; }
}
