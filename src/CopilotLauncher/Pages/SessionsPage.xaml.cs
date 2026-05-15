using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class SessionsPage : Page
{
    public SessionsViewModel ViewModel { get; }

    public SessionsPage()
    {
        ViewModel = new SessionsViewModel(
            App.Services.GetRequiredService<ISessionDiscoveryService>(),
            App.Services.GetRequiredService<ITerminalDiscoveryService>(),
            App.Services.GetRequiredService<ILaunchService>(),
            App.Services.GetRequiredService<ISettingsService>());
        InitializeComponent();
        // Defer Refresh to after first render so the StatusMessage placeholder
        // ("Loading sessions…") is visible while we hit the disk on a 100+ session
        // machine. ListView virtualization handles the actual render cost.
        Loaded += (_, _) => ViewModel.Refresh();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SessionRow row)
        {
            ViewModel.ResumeSession(row);
        }
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            Enum.TryParse<SessionSortField>(tag, out var field))
        {
            ViewModel.SortField = field;
        }
    }

    private void OnToggleSortDirection(object sender, RoutedEventArgs e)
    {
        ViewModel.SortDescending = !ViewModel.SortDescending;
        // Glyph: descending = ScrollChevronDown (E74B), ascending = ScrollChevronUp (E74A)
        SortDirectionGlyph.Glyph = ViewModel.SortDescending ? "\uE74B" : "\uE74A";
    }
}

