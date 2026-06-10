using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using CopilotLauncher.Helpers;
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
        services.AddSingleton<IChangelogHistoryService, ChangelogHistoryService>();
        services.AddSingleton<IBriefingsSplitMigration, BriefingsSplitMigration>();
        services.AddSingleton<IReleaseNotesService, ReleaseNotesService>();
        services.AddSingleton<IShortcutExportService, ShortcutExportService>();
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
                var behavior = settings.Current.LauncherBehavior;
                var (width, height) = WindowSizing.ClampNormalSize(
                    behavior.LastNormalWindowWidth,
                    behavior.LastNormalWindowHeight);
                if (width != behavior.LastNormalWindowWidth || height != behavior.LastNormalWindowHeight)
                {
                    behavior.LastNormalWindowWidth = width;
                    behavior.LastNormalWindowHeight = height;
                    settings.Save();
                }

                nm.ApplyNormalSize(width, height);
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

                // v0.2.0 one-shot migration: split pre-v0.2.0 briefings.json
                // entries (which conflated AI summaries and raw changelogs)
                // into changelogs.json + briefings.json. Idempotent — skips
                // if changelogs.json already exists. Runs in the same
                // best-effort background pass so any failure is non-fatal.
                try { Services.GetRequiredService<IBriefingsSplitMigration>().Migrate(); }
                catch { /* migration is best-effort */ }

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

        // v0.2.0: the background startup update check now writes ONLY a
        // ChangelogEntry on a detected version bump. AI briefings are
        // generated on demand via the Changelog page's "Generate AI Briefing"
        // button — never auto-fired here. Keeps startup latency predictable
        // and avoids burning premium copilot CLI requests behind the user's
        // back. Single-flight + 30-second timeout still apply.
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

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
            var updates = Services.GetRequiredService<IUpdateCheckService>();
            var result = await updates.RunAsync(cts.Token).ConfigureAwait(false);
            File.WriteAllText(stateFile, DateTime.UtcNow.ToString("o"));

            if (result is null) return;
            // Bootstrap was already committed inside RunAsync; nothing to do.
            if (result.IsBootstrap) return;
            if (!result.VersionChanged) return;
            if (!settings.Current.Briefings.ShowVersionBumpBriefing) return;

            var briefings = Services.GetRequiredService<IBriefingService>();
            var changelogHistory = Services.GetRequiredService<IChangelogHistoryService>();
            var releaseNotes = Services.GetRequiredService<IReleaseNotesService>();
            using var fetchCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var entries = await releaseNotes.FetchAsync(result.PreviousVersion, result.CurrentVersion, fetchCts.Token).ConfigureAwait(false);
            var body = briefings.Render(result.PreviousVersion, result.CurrentVersion, entries);

            changelogHistory.Add(new ChangelogEntry
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                FromVersion = result.PreviousVersion,
                ToVersion = result.CurrentVersion,
                Source = "startup-update",
                Body = body + Environment.NewLine + "Raw output:" + Environment.NewLine + result.RawOutput,
            });
            // Two-phase commit: only advance the persisted baseline AFTER the
            // changelog entry is durably written.
            updates.CommitObservedVersion(result.CurrentVersion);
        }
        catch
        {
            // Best-effort. Logging arrives later.
        }
    }
}
