using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class CleanupPage : Page
{
    public CleanupViewModel ViewModel { get; }

    public CleanupPage()
    {
        // The scan runs off the UI thread, so give the VM a dispatcher marshaller
        // for the observable-collection updates.
        var queue = DispatcherQueue.GetForCurrentThread();
        Func<Action, Task> marshal = action =>
        {
            var tcs = new TaskCompletionSource();
            if (!queue.TryEnqueue(() =>
            {
                try { action(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
            {
                tcs.TrySetException(new InvalidOperationException("Could not marshal to the UI thread."));
            }
            return tcs.Task;
        };

        ViewModel = new CleanupViewModel(
            App.Services.GetRequiredService<ISessionDiscoveryService>(),
            App.Services.GetRequiredService<ISessionDeletionService>(),
            marshal);

        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => ViewModel.SetAllSelections(true);

    private void OnSelectNoneClick(object sender, RoutedEventArgs e) => ViewModel.SetAllSelections(false);

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.SelectedCount;
        if (count == 0) return;

        var size = CleanupRow.FormatBytes(ViewModel.SelectedBytes);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = count == 1 ? "Delete 1 session?" : $"Delete {count} sessions?",
            Content = $"This permanently removes {count} session folder(s) and their full transcripts, " +
                      $"freeing {size}. It cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = ViewModel.DeleteSelected();

        if (result.FailedCount > 0)
        {
            var detail = string.Join("\n", result.Failures.Take(8).Select(f => $"{f.SessionId[..8]}… — {f.Error}"));
            if (result.FailedCount > 8) detail += $"\n…and {result.FailedCount - 8} more.";
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"{result.FailedCount} session(s) could not be deleted",
                Content = detail,
                CloseButtonText = "OK",
            }.ShowAsync();
        }
    }
}
