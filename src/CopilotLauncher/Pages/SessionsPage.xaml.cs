using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Helpers;
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

    private void OnSaveAsLaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SessionRow row) return;

        // Suggested label: a user-given name is the best signal; otherwise
        // the cwd leaf (e.g. "FabricPOCPortal"). Auto-generated names from
        // Copilot are often 90-char prompt prefixes — not great launch
        // labels, so we skip those in favor of the workdir leaf.
        string label;
        if (row.HasUserName && !string.IsNullOrWhiteSpace(row.Title))
        {
            label = row.Title;
        }
        else
        {
            try { label = System.IO.Path.GetFileName(row.Cwd.TrimEnd('\\', '/')); }
            catch { label = string.Empty; }
            if (string.IsNullOrWhiteSpace(label)) label = row.ShortId;
        }

        NewLaunchHandoff.Pending = new NewLaunchPayload(
            SuggestedLabel: label,
            WorkingDirectory: row.Cwd,
            ResumeId: row.SessionId);

        // Switch to the New Launch tab; its Loaded handler picks up the
        // pending payload and pre-populates the form.
        if (Application.Current is App app && app.MainWindowOrNull is MainWindow mw)
        {
            mw.NavigateToTab("new");
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

