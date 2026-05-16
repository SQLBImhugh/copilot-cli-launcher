using System;
using Windows.Graphics;
using Windows.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CopilotLauncher.Pages;
using CopilotLauncher.Services;

namespace CopilotLauncher;

public sealed partial class MainWindow : Window
{
    private SizeInt32? _savedNormalSize;
    private string? _savedNormalNavTag;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (System.IO.File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch
        {
            // SetIcon is non-critical — the embedded icon (via csproj
            // ApplicationIcon) still drives Explorer + pinned-taskbar visuals.
        }

        // Backdrop is driven by ThemeManager.ApplyBackdrop(this, theme)
        // (called from App.OnLaunched after the saved theme is loaded).

        ExtendsContentIntoTitleBar = true;
        // AppTitleBar contains ONLY non-interactive content (the title text).
        // The Theme + Compact buttons live in a sibling Grid column outside
        // the SetTitleBar region so their clicks reach them instead of being
        // captured as drag input.
        SetTitleBar(AppTitleBar);

        ApplyTitleBarTheme();

        // Default landing page.
        ContentFrame.Navigate(typeof(SessionsPage));
        NavView.SelectedItem = NavView.MenuItems[0];

        // Sync the theme MenuFlyout's checked item with the saved setting so
        // the radio shows the current state when the flyout is opened.
        // Window doesn't have a Loaded event in WinUI 3, so use Activated
        // (fires once shortly after Activate()).
        bool synced = false;
        Activated += (_, _) =>
        {
            if (synced) return;
            synced = true;
            SyncThemeMenuChecked();
        };

        // If SettingsPage's combo updates the theme, our title-bar radio
        // should follow. ThemeManager.ThemeChanged fires after every Apply.
        Helpers.ThemeManager.ThemeChanged += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            SyncThemeMenuChecked();
            UpdateCompactGlyph(e.Compact);
        });
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            if (AppWindow?.TitleBar is not { } tb) return;

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
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            var page = tag switch
            {
                "sessions"    => typeof(SessionsPage),
                "shortcuts"   => typeof(ShortcutsPage),
                "newshortcut" => typeof(NewShortcutPage),
                "briefing"    => typeof(BriefingPage),
                "settings"    => typeof(SettingsPage),
                _             => typeof(SessionsPage),
            };
            ContentFrame.Navigate(page);
        }
    }

    /// <summary>Programmatically switch to the tab with the given Tag value
    /// (e.g. "newshortcut" for New Shortcut). Triggers the existing
    /// SelectionChanged handler so the Frame navigates and the nav item
    /// highlights together.</summary>
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
    /// Mica is translucent and ignores the page-background brush, so for
    /// copilotCli we disable Mica and paint the root grid with the dark
    /// brush; for the built-in themes we keep Mica.
    /// </summary>
    public void ApplyBackdrop(string theme)
    {
        var wantMica = !string.Equals(theme, "copilotCli", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (wantMica)
            {
                SystemBackdrop ??= new MicaBackdrop();
                WindowRoot.Background = null;
            }
            else
            {
                SystemBackdrop = null;
                if (Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] is Brush bg)
                    WindowRoot.Background = bg;
            }
        }
        catch
        {
            // Backdrop changes are non-critical.
        }
    }

    // ---------------- Title-bar buttons: theme picker + compact toggle ----

    private void OnThemeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem item) return;
        if (item.Tag is not string tag) return;

        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            settings.Current.LauncherBehavior.Theme = tag;
            settings.Save();
            Helpers.ThemeManager.Apply(tag, this, settings.Current.LauncherBehavior.CompactMode);
        }
        catch
        {
            // Theme switch is non-critical UI; swallow.
        }
    }

    private void SyncThemeMenuChecked()
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            var theme = settings.Current.LauncherBehavior.Theme;
            ThemeSystemItem.IsChecked  = theme == "system";
            ThemeLightItem.IsChecked   = theme == "light";
            ThemeDarkItem.IsChecked    = theme == "dark";
            ThemeCopilotItem.IsChecked = theme == "copilotCli";
        }
        catch { }
    }

    private void OnCompactToggleClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsService>();
            var nowCompact = !settings.Current.LauncherBehavior.CompactMode;
            ApplyCompactMode(nowCompact, persist: true);
        }
        catch
        {
            // Compact toggle is non-critical UI; swallow.
        }
    }

    /// <summary>Toggle between compact and normal mode. Saves the previous
    /// non-compact size so the restore is sensible even if the user launched
    /// directly into compact. Called from App.OnLaunched at startup with the
    /// saved CompactMode value.</summary>
    public void ApplyCompactMode(bool compact, bool persist)
    {
        var settings = App.Services.GetRequiredService<ISettingsService>();
        var behavior = settings.Current.LauncherBehavior;

        if (compact)
        {
            // Save current size + selected nav so we can restore on exit.
            // Only update LastNormalWindowWidth/Height if we're transitioning
            // FROM normal mode (not on initial launch when CompactMode was
            // already true).
            if (!behavior.CompactMode && AppWindow is not null)
            {
                _savedNormalSize = AppWindow.Size;
                behavior.LastNormalWindowWidth  = AppWindow.Size.Width;
                behavior.LastNormalWindowHeight = AppWindow.Size.Height;
            }
            if (NavView.SelectedItem is NavigationViewItem item && item.Tag is string tag)
                _savedNormalNavTag = tag;

            // Resize. Multiply by RasterizationScale so we get the requested
            // *effective* pixel size on high-DPI displays.
            ResizeWindow(320, 640);

            // Hide the nav and pin to the Shortcuts page (the compact view's
            // single purpose is the saved-shortcuts launcher). Frame is
            // navigated explicitly so no SelectionChanged-no-op risk.
            NavView.IsPaneVisible = false;
            ContentFrame.Navigate(typeof(ShortcutsPage));

            UpdateCompactGlyph(true);
        }
        else
        {
            NavView.IsPaneVisible = true;

            var w = _savedNormalSize?.Width  ?? Math.Max(behavior.LastNormalWindowWidth,  640);
            var h = _savedNormalSize?.Height ?? Math.Max(behavior.LastNormalWindowHeight, 480);
            ResizeWindow(w, h);

            // Explicit navigate + selection so the Frame and nav re-sync
            // even when SelectedItem == savedSelection (the SelectionChanged
            // event would otherwise be a no-op).
            var restoreTag = _savedNormalNavTag ?? "sessions";
            var page = restoreTag switch
            {
                "shortcuts"   => typeof(ShortcutsPage),
                "newshortcut" => typeof(NewShortcutPage),
                "briefing"    => typeof(BriefingPage),
                "settings"    => typeof(SettingsPage),
                _             => typeof(SessionsPage),
            };
            ContentFrame.Navigate(page);
            NavigateToTab(restoreTag);

            UpdateCompactGlyph(false);
        }

        behavior.CompactMode = compact;
        if (persist) settings.Save();

        // Re-apply theme so font sizes re-resolve for the new mode.
        Helpers.ThemeManager.Apply(behavior.Theme, this, compact);
    }

    private void ResizeWindow(int effectiveWidth, int effectiveHeight)
    {
        if (AppWindow is null) return;
        try
        {
            // AppWindow.Resize takes physical pixels. Multiply by the current
            // rasterization scale so the resulting window is roughly the
            // requested *effective* (XAML) pixel size on high-DPI displays.
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            var w = (int)Math.Round(effectiveWidth  * scale);
            var h = (int)Math.Round(effectiveHeight * scale);
            AppWindow.Resize(new SizeInt32(w, h));
        }
        catch { }
    }

    /// <summary>Apply the saved non-compact window size at startup. Called
    /// from App.OnLaunched. Also wires AppWindow.Changed so subsequent user
    /// resizes are persisted (only when not in compact mode — the compact
    /// 320x640 size is intentionally hard-coded and not user-controlled).</summary>
    public void ApplyNormalSize(int effectiveWidth, int effectiveHeight)
    {
        ResizeWindow(effectiveWidth, effectiveHeight);

        // Persist size on resize. Debounce isn't strictly necessary because
        // settings.Save is already debounced via SettingsViewModel — but
        // here we just write directly. AppWindow.Changed fires for every
        // resize/move event, so guard against compact mode + only update
        // when actually resized (not just moved).
        if (AppWindow is null) return;
        AppWindow.Changed += (sender, args) =>
        {
            if (!args.DidSizeChange) return;
            try
            {
                var settings = App.Services.GetRequiredService<ISettingsService>();
                var behavior = settings.Current.LauncherBehavior;
                if (behavior.CompactMode) return;  // 320x640 compact size shouldn't override the saved normal size

                var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
                var w = (int)Math.Round(sender.Size.Width  / scale);
                var h = (int)Math.Round(sender.Size.Height / scale);
                if (w == behavior.LastNormalWindowWidth && h == behavior.LastNormalWindowHeight) return;
                behavior.LastNormalWindowWidth = w;
                behavior.LastNormalWindowHeight = h;
                settings.Save();
            }
            catch { }
        };
    }

    private void UpdateCompactGlyph(bool compact)
    {
        // E73F = MiniExpand-style icon; E740 = FullScreen "expand back out"
        // icon. Swap so the button always shows the action it'll perform.
        CompactGlyph.Glyph = compact ? "\uE740" : "\uE73F";
    }
}
