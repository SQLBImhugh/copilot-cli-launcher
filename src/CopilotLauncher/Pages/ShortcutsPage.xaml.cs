using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class ShortcutsPage : Page
{
    public ShortcutsViewModel ViewModel { get; }
    private readonly IShortcutExportService _exporter;

    public ShortcutsPage()
    {
        ViewModel = new ShortcutsViewModel(
            App.Services.GetRequiredService<IShortcutsService>(),
            App.Services.GetRequiredService<ILaunchService>(),
            App.Services.GetRequiredService<ITerminalDiscoveryService>(),
            App.Services.GetRequiredService<ISettingsService>(),
            App.Services.GetRequiredService<IAfterLaunchAction>());
        _exporter = App.Services.GetRequiredService<IShortcutExportService>();
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

    private async void OnExportLnkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Shortcut entry) return;
        try
        {
            var picker = new FileSavePicker();
            picker.SuggestedFileName = entry.Label + " Copilot";
            picker.FileTypeChoices.Add("Windows shortcut", new[] { ".lnk" }.ToList());
            picker.SuggestedStartLocation = PickerLocationId.Desktop;
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindowOrNull
                ?? throw new InvalidOperationException("No main window."));
            InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var plan = _exporter.BuildPlan(entry);
            LnkWriter.Write(plan, file.Path);
        }
        catch (Exception ex)
        {
            // Failure shows up in the page status; ViewModel doesn't expose
            // a setter for arbitrary text yet so we use the launch path's
            // status field instead.
            ViewModel.GetType().GetProperty("StatusMessage")
                ?.SetValue(ViewModel, $"Export failed: {ex.Message}");
        }
    }
}


