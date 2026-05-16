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
    private readonly IMigrationService _migration;
    private LegacyDetectionResult? _legacyDetection;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public SessionsPage()
    {
        // Provide a UI marshaller so the off-thread Refresh can mutate the
        // Visible ObservableCollection from the WinUI dispatcher. Without this
        // the ObservableCollection would throw on cross-thread Add/Clear.
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Func<Action, System.Threading.Tasks.Task> marshal = action =>
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource();
            if (!dq.TryEnqueue(() =>
            {
                try { action(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
            {
                action();
                tcs.TrySetResult();
            }
            return tcs.Task;
        };

        ViewModel = new SessionsViewModel(
            App.Services.GetRequiredService<ISessionDiscoveryService>(),
            App.Services.GetRequiredService<ITerminalDiscoveryService>(),
            App.Services.GetRequiredService<ILaunchService>(),
            App.Services.GetRequiredService<ISettingsService>(),
            App.Services.GetRequiredService<IAfterLaunchAction>(),
            marshal);
        _migration = App.Services.GetRequiredService<IMigrationService>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.RefreshAsync();
            CheckForLegacyInstall();
        };
    }

    private void CheckForLegacyInstall()
    {
        if (_migration.MigrationCompleted) return;
        _legacyDetection = _migration.Detect();
        if (!_legacyDetection.Found) return;

        MigrationBar.Message = "Found a legacy PowerShell-launcher install at " +
                               _legacyDetection.LegacyConfigPath +
                               (_legacyDetection.LegacyAgentsMdPath is not null
                                   ? "; agents.md will be copied into this app's data folder."
                                   : ".");
        MigrationBar.IsOpen = true;
    }

    private void OnImportLegacyClick(object sender, RoutedEventArgs e)
    {
        if (_legacyDetection is null) return;
        var status = _migration.Import(_legacyDetection);
        ViewModel.StatusMessage = status;  // surface in the page status bar
        MigrationBar.IsOpen = false;
    }

    private void OnSkipLegacyClick(object sender, RoutedEventArgs e)
    {
        _migration.MarkCompleted();
        MigrationBar.IsOpen = false;
        ViewModel.StatusMessage = "Migration skipped — won't ask again.";
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

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

        NewShortcutHandoff.Pending = new NewShortcutPayload(
            SuggestedLabel: label,
            WorkingDirectory: row.Cwd,
            ResumeId: row.SessionId);

        // Switch to the New Shortcut tab; its Loaded handler picks up the
        // pending payload and pre-populates the form.
        if (Application.Current is App app && app.MainWindowOrNull is MainWindow mw)
        {
            mw.NavigateToTab("newshortcut");
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

