using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CopilotLauncher.Pages;

namespace CopilotLauncher;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Apply Mica backdrop where supported (Windows 11). Falls back gracefully on Win10.
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica not supported (Windows 10) — leave default chrome.
        }

        // Default landing page.
        ContentFrame.Navigate(typeof(SessionsPage));
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            var page = tag switch
            {
                "sessions" => typeof(SessionsPage),
                "saved"    => typeof(SavedLaunchesPage),
                "new"      => typeof(NewLaunchPage),
                "briefing" => typeof(BriefingPage),
                _          => typeof(SessionsPage),
            };
            ContentFrame.Navigate(page);
        }
    }
}
