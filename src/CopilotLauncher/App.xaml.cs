using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using CopilotLauncher.Services;

namespace CopilotLauncher;

public partial class App : Application
{
    private Window? _mainWindow;

    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // All singletons — these services hold cached state (settings, discovered
        // sessions, terminal list) that should survive page navigation.
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISessionDiscoveryService, SessionDiscoveryService>();
        services.AddSingleton<ITerminalDiscoveryService, TerminalDiscoveryService>();
        services.AddSingleton<ILaunchService, LaunchService>();
        services.AddSingleton<ISavedLaunchesService, SavedLaunchesService>();
        services.AddSingleton<ISessionRepairService, SessionRepairService>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();
        services.AddSingleton<IBriefingService, BriefingService>();

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}

