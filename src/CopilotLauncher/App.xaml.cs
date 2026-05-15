using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>The single main window. Null until OnLaunched fires.
    /// Used by file/folder pickers etc. that need the parent HWND.</summary>
    public Window? MainWindowOrNull { get; private set; }

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
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

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowOrNull = new MainWindow();
        MainWindowOrNull.Activate();

        // Phase 4: silently repair any sessions with dangling tool_use events
        // in the background so --resume works on next launch. Skips active
        // (locked) sessions; backs up before mutating. Best-effort — failures
        // don't bubble up to the user.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var settings = Services.GetRequiredService<ISettingsService>();
                if (settings.Current.Repair.AutoRepairDanglingToolUse)
                    Services.GetRequiredService<ISessionRepairService>().RepairAll();

                // Apply known-bug workarounds (e.g. issue #3298 win32 keep-alive).
                // Reads its own toggle internally; safe to call unconditionally.
                Services.GetRequiredService<IKnownBugWorkaroundService>().ApplyAll();

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


