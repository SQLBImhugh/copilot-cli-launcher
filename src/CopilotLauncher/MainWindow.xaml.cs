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

        // Title-bar / taskbar / alt-tab icon. Set BEFORE Activate to avoid
        // flashing the default WinUI placeholder icon. Path is relative to
        // the running .exe; csproj <Content CopyToOutputDirectory> ensures
        // the .ico ships in dist/.
        try
        {
            var iconPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch
        {
            // SetIcon is non-critical — the embedded icon (via csproj
            // ApplicationIcon) still drives Explorer + pinned-taskbar visuals.
        }

        // Backdrop is now driven by ThemeManager.ApplyBackdrop(this, theme)
        // (called from App.OnLaunched after the saved theme is loaded). Mica
        // is the wrong default for a custom-color theme like Copilot CLI
        // because Mica is *translucent* and samples the desktop wallpaper —
        // ApplicationPageBackgroundThemeBrush gets ignored, which is why the
        // window stayed bluish even with our brush overrides applied. The
        // theme manager flips between Mica (built-in themes) and a solid
        // backdrop (copilotCli) on demand.

        // Extend the app content into the title-bar area so the bar's
        // background becomes the same Mica/app surface as the rest of the
        // window. Without this, Windows draws a default white title bar
        // and our ButtonForegroundColor / ButtonHoverColor settings paint
        // the buttons invisibly against the white. NavigationView's pane
        // header naturally fills that strip, so the layout still looks fine.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Make the title bar follow the OS / app theme so the system buttons
        // (min/max/close) are dark in dark mode and light in light mode.
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
                "sessions"    => typeof(SessionsPage),
                "shortcuts"   => typeof(ShortcutsPage),
                "newshortcut" => typeof(NewShortcutPage),
                "briefing"    => typeof(BriefingPage),
                _             => typeof(SessionsPage),
            };
            ContentFrame.Navigate(page);
        }
    }

    /// <summary>Programmatically switch to the tab with the given Tag value
    /// (e.g. "new" for New Shortcut). Triggers the existing SelectionChanged
    /// handler so the Frame navigates and the nav item highlights together.</summary>
    public void NavigateToTab(string tag)
    {
        foreach (var raw in NavView.MenuItems)
        {
            if (raw is NavigationViewItem nvi && nvi.Tag is string t && t == tag)
            {
                NavView.SelectedItem = nvi;
                return;
            }
        }
    }

    /// <summary>
    /// Apply the right system backdrop + root background for a given theme.
    /// Mica is translucent and samples the desktop wallpaper, so it ignores
    /// any ApplicationPageBackgroundThemeBrush override. For our custom
    /// "copilotCli" palette we therefore disable Mica and paint the root
    /// grid with the dark Copilot CLI banner background; for the built-in
    /// system / light / dark themes we keep Mica so the app still feels
    /// native on Windows 11.
    /// </summary>
    public void ApplyBackdrop(string theme)
    {
        var wantMica = !string.Equals(theme, "copilotCli", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (wantMica)
            {
                SystemBackdrop ??= new MicaBackdrop();
                WindowRoot.Background = null;  // let Mica show through
            }
            else
            {
                SystemBackdrop = null;
                // Use a ThemeResource so any future palette tweak (or a
                // theme switch back to dark/light) auto-updates this brush.
                // ThemeManager replaces SolidBackgroundFillColorBaseBrush on
                // Application.Resources to #171717 when copilotCli is active.
                if (Microsoft.UI.Xaml.Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]
                    is Microsoft.UI.Xaml.Media.Brush bg)
                {
                    WindowRoot.Background = bg;
                }
            }
        }
        catch
        {
            // Backdrop changes are non-critical — fall back to whatever
            // Windows decided to render.
        }
    }
}
