using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using Windows.UI.ViewManagement;

namespace CopilotLauncher;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private readonly AccessibilitySettings? _accessibilitySettings;

    /// <summary>The single main window. Null until OnLaunched fires.
    /// Used by file/folder pickers etc. that need the parent HWND.</summary>
    public Window? MainWindowOrNull { get; private set; }

    public App()
    {
        InitializeComponent();
        Application.Current.HighContrastAdjustment = Microsoft.UI.Xaml.ApplicationHighContrastAdjustment.None;
        Services = ConfigureServices();

        try
        {
            _accessibilitySettings = new AccessibilitySettings();
            _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
        }
        catch
        {
            // High Contrast change notifications are best-effort.
        }
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args)
    {
        var currentWindow = MainWindowOrNull;
        if (currentWindow is null) return;

        try
        {
            _ = currentWindow.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var settings = Services.GetRequiredService<ISettingsService>();
                    Helpers.ThemeManager.Apply(
                        settings.Current.LauncherBehavior.Theme,
                        currentWindow,
                        settings.Current.LauncherBehavior.CompactMode);
                }
                catch
                {
                    // High Contrast theme sync is non-critical.
                }
            });
        }
        catch
        {
            // High Contrast theme sync is non-critical.
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // All singletons — services hold cached state (settings, discovered
        // sessions, terminal list) that should survive page navigation.
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISessionDiscoveryService, SessionDiscoveryService>();
        services.AddSingleton<ITerminalDiscoveryService, TerminalDiscoveryService>();
        services.AddSingleton<ILaunchService, LaunchService>();
        services.AddSingleton<IShortcutsService, ShortcutsService>();
        services.AddSingleton<ISessionRepairService, SessionRepairService>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton<IBriefingService, BriefingService>();
        services.AddSingleton<IBriefingHistoryService, BriefingHistoryService>();
        services.AddSingleton<IShortcutExportService, ShortcutExportService>();
        services.AddSingleton<IKnownBugWorkaroundService, KnownBugWorkaroundService>();
        services.AddSingleton<IMigrationService, MigrationService>();
        services.AddSingleton<IAfterLaunchAction, Helpers.WinUIAfterLaunchAction>();
        services.AddSingleton<IAISummaryService, AISummaryService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowOrNull = new MainWindow();

        // Apply the user's chosen theme BEFORE Activate so the window paints
        // in the right palette on first frame instead of flashing default.
        // ThemeManager.Apply takes the compact-mode flag too so font sizes /
        // padding are scaled in one combined pass (no overwrite race between
        // theme + compact applies).
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            var compact = settings.Current.LauncherBehavior.CompactMode;
            Helpers.ThemeManager.Apply(settings.Current.LauncherBehavior.Theme, MainWindowOrNull, compact);
            // Also kick the MainWindow into compact layout (resize + hide nav)
            // before first paint if the saved state was compact.
            if (compact && MainWindowOrNull is MainWindow mw)
            {
                mw.ApplyCompactMode(true, persist: false);
            }
            else if (MainWindowOrNull is MainWindow nm)
            {
                // Apply the user's last non-compact window size so resizes
                // survive across launches. Done BEFORE Activate so there's
                // no visible resize from the WinUI default 800x600.
                nm.ApplyNormalSize(
                    settings.Current.LauncherBehavior.LastNormalWindowWidth,
                    settings.Current.LauncherBehavior.LastNormalWindowHeight);
            }
        }
        catch
        {
            // Theme / size is non-critical — fall back to system default.
        }

        MainWindowOrNull.Activate();

        var mainWindow = MainWindowOrNull;
        if (mainWindow is not null)
        {
            var tray = Services.GetRequiredService<ITrayIconService>();

            mainWindow.AppWindow.Closing += (_, closingArgs) =>
            {
                try
                {
                    var settings = Services.GetRequiredService<ISettingsService>();
                    var shouldHideToTray = string.Equals(
                        settings.Current.LauncherBehavior.AfterLaunch,
                        "hideToTray",
                        StringComparison.OrdinalIgnoreCase);

                    if (tray.IsQuitting || !shouldHideToTray || !tray.IsActive)
                        return;

                    closingArgs.Cancel = true;
                    mainWindow.AppWindow.Hide();
                }
                catch
                {
                    // Best-effort. If interception fails, allow normal close.
                }
            };

            mainWindow.Closed += (_, _) =>
            {
                try
                {
                    tray.Shutdown();
                }
                catch
                {
                    // Best-effort cleanup on shutdown.
                }
            };

            try
            {
                tray.Initialize();
            }
            catch
            {
                // Tray icon is optional; continue without it.
            }
        }

        // Phase 4: silently repair any sessions with dangling tool_use events
        // in the background so --resume works on next launch. Skips active
        // (locked) sessions; backs up before mutating. Best-effort — failures
        // don't bubble up to the user.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var settings = Services.GetRequiredService<ISettingsService>();
                if (settings.Current.Repair.AutoRepairDanglingToolUse)
                    Services.GetRequiredService<ISessionRepairService>().RepairAll();

                // Apply known-bug workarounds (e.g. issue #3298 win32 keep-alive).
                // Reads its own toggle internally; safe to call unconditionally.
                // Async because the version probe spawns `copilot --version`
                // with a 5s timeout — we don't want to block the startup task.
                await Services.GetRequiredService<IKnownBugWorkaroundService>().ApplyAllAsync().ConfigureAwait(false);

                // Sync the Start with Windows registry entry to whatever is
                // currently saved in settings. Idempotent.
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    Helpers.AutostartRegistry.Sync(settings.Current.LauncherBehavior.StartWithWindows, exePath);
            }
            catch
            {
                // Logging service lands in Phase 5; for now swallow silently.
            }
        });

        // Phase 3: check for Copilot CLI updates in the background, gated by
        // the user's AutoUpdateFrequency policy. If a new version is detected,
        // append a briefing entry to the rolling history. UI sees it via
        // BriefingHistoryService.Reload() the next time the Briefing tab is
        // opened. Single-flight + 30-second timeout so a hanging `copilot
        // update` can't lock up the launcher.
        _ = System.Threading.Tasks.Task.Run(StartupUpdateCheckAsync);
    }

    private static async System.Threading.Tasks.Task StartupUpdateCheckAsync()
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            var cli = settings.Current.CopilotCli;
            if (!cli.AutoUpdateBeforeLaunch) return;

            // Frequency gate using a small last-run state file.
            var stateDir = Path.Combine(settings.AppDataDirectory, "state");
            Directory.CreateDirectory(stateDir);
            var stateFile = Path.Combine(stateDir, "last-update-check.txt");
            if (File.Exists(stateFile) && DateTime.TryParse(File.ReadAllText(stateFile), out var last))
            {
                var window = cli.AutoUpdateFrequency switch
                {
                    "weekly"  => TimeSpan.FromDays(7),
                    "daily"   => TimeSpan.FromDays(1),
                    "manual"  => TimeSpan.MaxValue,
                    _         => TimeSpan.Zero,           // "everyLaunch"
                };
                if (window == TimeSpan.MaxValue) return;
                if (DateTime.UtcNow - last.ToUniversalTime() < window) return;
            }

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var updates = Services.GetRequiredService<IUpdateCheckService>();
            var result = await updates.RunAsync(cts.Token).ConfigureAwait(false);
            File.WriteAllText(stateFile, DateTime.UtcNow.ToString("o"));

            if (result is null || !result.VersionChanged) return;
            if (!settings.Current.Briefings.ShowVersionBumpBriefing) return;

            var briefings = Services.GetRequiredService<IBriefingService>();
            var history = Services.GetRequiredService<IBriefingHistoryService>();
            var body = briefings.Render(result.PreviousVersion, result.CurrentVersion, Array.Empty<ReleaseEntry>());
            var generateStartupAiSummary = settings.Current.Briefings.AISummaryOnStartupUpdate
                && settings.Current.Briefings.AISummaryOnBump
                && (string.Equals(cli.AutoUpdateFrequency, "daily", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(cli.AutoUpdateFrequency, "weekly", StringComparison.OrdinalIgnoreCase));

            if (generateStartupAiSummary)
            {
                var ai = Services.GetRequiredService<IAISummaryService>();
                var summary = await ai.GenerateAsync(result.PreviousVersion, result.CurrentVersion, result.RawOutput, cts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    body = "## AI Summary\n\n" + summary.Trim() + "\n\n---\n\n" + body;
                }
            }

            history.Add(new BriefingEntry
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                FromVersion = result.PreviousVersion,
                ToVersion = result.CurrentVersion,
                Source = "startup-update",
                Body = body + Environment.NewLine + "Raw output:" + Environment.NewLine + result.RawOutput,
            });
        }
        catch
        {
            // Best-effort. Logging arrives later.
        }
    }
}


