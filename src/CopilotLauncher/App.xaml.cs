using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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
                if (!settings.Current.Repair.AutoRepairDanglingToolUse) return;
                var repair = Services.GetRequiredService<ISessionRepairService>();
                repair.RepairAll();
            }
            catch
            {
                // Logging service lands in Phase 5; for now swallow silently.
            }
        });
    }
}


