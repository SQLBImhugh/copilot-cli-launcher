using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Windowing;
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

        // Make the title bar follow the OS / app theme so the system buttons
        // (min/max/close) are dark in dark mode and light in light mode. Without
        // this, WinUI 3 unpackaged apps render a white title bar on every
        // system regardless of theme.
        ApplyTitleBarTheme();

        // Default landing page.
        ContentFrame.Navigate(typeof(SessionsPage));
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            if (AppWindow?.TitleBar is not { } tb) return;

            // Manually paint the system-button colors to match the current theme.
            // (AppWindowTitleBar.PreferredTheme would do this for us, but it
            // landed in Windows App SDK 1.7; we're on 1.6 so we color the
            // buttons by hand. Re-applies on theme change.)
            if (Content is FrameworkElement root)
            {
                ApplyButtonColors(tb, IsCurrentlyDark(root));
                root.ActualThemeChanged += (s, _) => ApplyButtonColors(tb, IsCurrentlyDark(s));
            }
            else
            {
                ApplyButtonColors(tb, Application.Current.RequestedTheme == ApplicationTheme.Dark);
            }
        }
        catch
        {
            // Title-bar customization is non-critical; ignore platform issues.
        }
    }

    private static bool IsCurrentlyDark(FrameworkElement root) =>
        root.ActualTheme == ElementTheme.Dark
        || (root.ActualTheme == ElementTheme.Default
            && Application.Current.RequestedTheme == ApplicationTheme.Dark);

    private static void ApplyButtonColors(AppWindowTitleBar tb, bool dark)
    {
        var fg            = dark ? Colors.White : Colors.Black;
        var hover         = dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        var pressed       = dark ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x00, 0x00, 0x00);
        var inactiveFg    = dark ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0x00, 0x00, 0x00);
        tb.ButtonForegroundColor                = fg;
        tb.ButtonHoverForegroundColor           = fg;
        tb.ButtonHoverBackgroundColor           = hover;
        tb.ButtonPressedForegroundColor         = fg;
        tb.ButtonPressedBackgroundColor         = pressed;
        tb.ButtonInactiveForegroundColor        = inactiveFg;
        tb.ButtonInactiveBackgroundColor        = Colors.Transparent;
        tb.ButtonBackgroundColor                = Colors.Transparent;
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
