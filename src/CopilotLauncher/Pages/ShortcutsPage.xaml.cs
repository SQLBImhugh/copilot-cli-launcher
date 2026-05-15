using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class ShortcutsPage : Page
{
    public ShortcutsViewModel ViewModel { get; }

    public ShortcutsPage()
    {
        ViewModel = new ShortcutsViewModel(
            App.Services.GetRequiredService<IShortcutsService>(),
            App.Services.GetRequiredService<ILaunchService>(),
            App.Services.GetRequiredService<ITerminalDiscoveryService>(),
            App.Services.GetRequiredService<ISettingsService>());
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Reload();
    }

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Shortcut entry)
            ViewModel.LaunchOne(entry);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Shortcut entry)
            ViewModel.Delete(entry);
    }
}

