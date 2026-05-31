using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class BriefingPage : Page
{
    public BriefingViewModel ViewModel { get; }

    public BriefingPage()
    {
        ViewModel = new BriefingViewModel(
            App.Services.GetRequiredService<IBriefingHistoryService>(),
            App.Services.GetRequiredService<IUpdateCheckService>(),
            App.Services.GetRequiredService<IBriefingService>(),
            App.Services.GetRequiredService<ISettingsService>(),
            App.Services.GetRequiredService<IReleaseNotesService>(),
            App.Services.GetRequiredService<IAISummaryService>());
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Reload();
    }

    private async void OnCheckNowClick(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        CheckSpinner.IsActive = true;
        try
        {
            await ViewModel.ForceCheckAsync();
        }
        finally
        {
            CheckSpinner.IsActive = false;
            CheckNowButton.IsEnabled = true;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => ViewModel.ClearHistory();
}

