using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CopilotLauncher.Helpers;

/// <summary>
/// Applies the user's selected theme to the running app. Supports the four
/// named themes:
///   "system"     — follow the OS
///   "light"      — force Fluent light
///   "dark"       — force Fluent dark
///   "copilotCli" — Fluent dark + cyan/pink/green palette overrides (default)
///                  Also swaps in the VT323 pixel font, bumps card borders to
///                  2px, and recolors toggle/checkbox accents from cyan to
///                  green to match the GitHub Copilot CLI banner mascot.
///
/// Implementation note: in WinUI 3 Windows App SDK 1.6, MergedDictionaries
/// added at runtime do NOT reliably trigger {ThemeResource} re-resolution
/// even with a RequestedTheme bounce. The brushes/values that controls
/// already resolved on first paint stay stale, so only some surfaces re-color.
///
/// Instead we directly set/remove individual keys on
/// Application.Current.Resources. WinUI re-evaluates {ThemeResource}
/// references against the live Resources dictionary on RequestedTheme
/// change, and a Light↔Dark bounce is enough to force every existing
/// control to pick up the new values.
///
/// HighContrast is intentionally not customised; when the OS reports HC
/// the user sees the standard Fluent HC palette regardless of which theme
/// they picked here. (See winui-design code-review note #1.)
/// </summary>
public static class ThemeManager
{
    private static readonly FontFamily PixelFont =
        new("ms-appx:///Assets/Fonts/VT323-Regular.ttf#VT323");

    /// <summary>Sampled from the GitHub Copilot CLI welcome banner.
    /// Cyan = primary accent; pink = secondary stroke/attention;
    /// green = success / toggle "on"; very-near-black surfaces; white text.</summary>
    private static readonly (string Key, Color Color)[] CopilotPalette =
    {
        // Page / window backgrounds — much darker than before but not full black.
        // SolidBackgroundFillColorBaseBrush is what MainWindow.WindowRoot.Background
        // binds to via ApplyBackdrop; everything else stacks slightly lighter.
        ("ApplicationPageBackgroundThemeBrush",  Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A)),
        ("SolidBackgroundFillColorBaseBrush",    Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A)),
        ("SolidBackgroundFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x10, 0x10, 0x10)),
        ("SolidBackgroundFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x15, 0x15, 0x15)),
        ("LayerFillColorDefaultBrush",           Color.FromArgb(0xFF, 0x12, 0x12, 0x12)),
        ("LayerOnAcrylicFillColorDefaultBrush",  Color.FromArgb(0xFF, 0x12, 0x12, 0x12)),
        // Card surfaces — slightly elevated above the page so they read as cards
        ("CardBackgroundFillColorDefaultBrush",  Color.FromArgb(0xFF, 0x12, 0x12, 0x12)),
        ("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x18, 0x18, 0x18)),
        // Bright pink card outline (was a muted mauve so the boxes barely
        // showed); paired with CardBorderThickness=2 below.
        ("CardStrokeColorDefaultBrush",          Color.FromArgb(0xFF, 0xCC, 0x66, 0xCC)),
        ("CardStrokeColorDefaultSolidBrush",     Color.FromArgb(0xFF, 0xCC, 0x66, 0xCC)),
        // Subtle fills
        ("SubtleFillColorTransparentBrush",      Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
        ("SubtleFillColorSecondaryBrush",        Color.FromArgb(0x1F, 0xCC, 0x66, 0xCC)),
        ("SubtleFillColorTertiaryBrush",         Color.FromArgb(0xFF, 0x12, 0x12, 0x12)),
        ("SubtleFillColorDisabledBrush",         Color.FromArgb(0xFF, 0x10, 0x10, 0x10)),
        // Control fills (Button/ComboBox/TextBox)
        ("ControlFillColorDefaultBrush",         Color.FromArgb(0xFF, 0x18, 0x18, 0x18)),
        ("ControlFillColorSecondaryBrush",       Color.FromArgb(0xFF, 0x22, 0x22, 0x22)),
        ("ControlFillColorTertiaryBrush",        Color.FromArgb(0xFF, 0x14, 0x14, 0x14)),
        ("ControlStrokeColorDefaultBrush",       Color.FromArgb(0xFF, 0xCC, 0x66, 0xCC)),
        ("ControlStrokeColorSecondaryBrush",     Color.FromArgb(0xFF, 0xCC, 0x66, 0xCC)),
        // Accent (cyan) — primary-button + focus-ring color
        ("AccentFillColorDefaultBrush",          Color.FromArgb(0xFF, 0x00, 0x99, 0xCC)),
        ("AccentFillColorSecondaryBrush",        Color.FromArgb(0xFF, 0x0A, 0xAF, 0xE0)),
        ("AccentFillColorTertiaryBrush",         Color.FromArgb(0xFF, 0x00, 0x7A, 0xA8)),
        ("AccentFillColorDisabledBrush",         Color.FromArgb(0xFF, 0x40, 0x40, 0x40)),
        ("AccentTextFillColorPrimaryBrush",      Color.FromArgb(0xFF, 0x22, 0xBC, 0xEE)),
        ("AccentTextFillColorSecondaryBrush",    Color.FromArgb(0xFF, 0x0A, 0xAF, 0xE0)),
        ("AccentTextFillColorTertiaryBrush",     Color.FromArgb(0xFF, 0x00, 0x99, 0xCC)),
        // Text
        ("TextFillColorPrimaryBrush",            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ("TextFillColorSecondaryBrush",          Color.FromArgb(0xFF, 0xCC, 0xCC, 0xCC)),
        ("TextFillColorTertiaryBrush",           Color.FromArgb(0xFF, 0x99, 0x99, 0x99)),
        ("TextFillColorDisabledBrush",           Color.FromArgb(0xFF, 0x5C, 0x5C, 0x5C)),
        ("TextOnAccentFillColorPrimaryBrush",    Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ("TextOnAccentFillColorSecondaryBrush",  Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0)),
        ("TextOnAccentFillColorDisabledBrush",   Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0)),
        // Focus
        ("FocusStrokeColorOuterBrush",           Color.FromArgb(0xFF, 0x0A, 0xAF, 0xE0)),
        ("FocusStrokeColorInnerBrush",           Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0A)),
        // System status (chips / badges)
        ("SystemFillColorSuccessBrush",          Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("SystemFillColorAttentionBrush",        Color.FromArgb(0xFF, 0xCC, 0x66, 0xCC)),
        ("SystemFillColorCautionBrush",          Color.FromArgb(0xFF, 0xF2, 0xC9, 0x4C)),
        ("SystemFillColorCriticalBrush",         Color.FromArgb(0xFF, 0xE0, 0x61, 0x7A)),
        ("SystemFillColorNeutralBrush",          Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)),
        ("SystemFillColorSolidNeutralBrush",     Color.FromArgb(0xFF, 0x22, 0x22, 0x22)),
        ("SystemFillColorSuccessBackgroundBrush", Color.FromArgb(0x40, 0x30, 0xC8, 0x68)),
        ("SystemFillColorAttentionBackgroundBrush", Color.FromArgb(0x40, 0xCC, 0x66, 0xCC)),
        ("SystemFillColorCautionBackgroundBrush", Color.FromArgb(0x40, 0xF2, 0xC9, 0x4C)),
        ("SystemFillColorCriticalBackgroundBrush", Color.FromArgb(0x40, 0xE0, 0x61, 0x7A)),
        // CheckBox — green checked state with BLACK glyph (high contrast on
        // bright green; previously was white but the user wanted black).
        // Toggle switches intentionally NOT recolored — only "square"
        // checkboxes are green; the round toggle pills stay on the cyan
        // AccentFillColor* path so the two binary controls remain visually
        // distinct.
        ("CheckBoxBackgroundChecked",            Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("CheckBoxBackgroundCheckedPointerOver", Color.FromArgb(0xFF, 0x3F, 0xD8, 0x77)),
        ("CheckBoxBackgroundCheckedPressed",     Color.FromArgb(0xFF, 0x28, 0xB0, 0x5A)),
        ("CheckBoxBorderBrushChecked",           Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("CheckBoxBorderBrushCheckedPointerOver", Color.FromArgb(0xFF, 0x3F, 0xD8, 0x77)),
        ("CheckBoxBorderBrushCheckedPressed",    Color.FromArgb(0xFF, 0x28, 0xB0, 0x5A)),
        ("CheckBoxCheckBackgroundFillChecked",   Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("CheckBoxCheckBackgroundFillCheckedPointerOver", Color.FromArgb(0xFF, 0x3F, 0xD8, 0x77)),
        ("CheckBoxCheckBackgroundFillCheckedPressed", Color.FromArgb(0xFF, 0x28, 0xB0, 0x5A)),
        ("CheckBoxCheckBackgroundStrokeChecked", Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("CheckBoxCheckGlyphForegroundChecked",  Color.FromArgb(0xFF, 0x00, 0x00, 0x00)),
    };

    public static void Apply(string theme, Window? window)
    {
        var app = Application.Current;
        if (app is null) return;

        var wantPalette = string.Equals(theme, "copilotCli", StringComparison.OrdinalIgnoreCase);

        // Replace each brush directly in Application.Resources. This is what
        // {ThemeResource} bindings query when re-resolving, so once we bounce
        // RequestedTheme below, every existing surface picks up the new value.
        foreach (var (key, color) in CopilotPalette)
        {
            if (wantPalette)
            {
                app.Resources[key] = new SolidColorBrush(color);
            }
            else
            {
                if (app.Resources.ContainsKey(key))
                    app.Resources.Remove(key);
            }
        }

        // Pixel font + thicker card borders + bumped font size for the Copilot
        // CLI palette. ContentControlThemeFontFamily is what every built-in
        // Fluent text style references, so swapping it propagates to
        // BodyTextBlockStyle, SubtitleTextBlockStyle, etc. without touching
        // any page XAML. Pixel fonts read small at the default 14px so we
        // bump the base content size to 17px in copilotCli mode (everything
        // built on the type ramp scales with it).
        if (wantPalette)
        {
            app.Resources["ContentControlThemeFontFamily"] = PixelFont;
            app.Resources["ContentControlThemeFontSize"] = 17.0;
            app.Resources["CardBorderThickness"] = new Thickness(2);
        }
        else
        {
            // Removing forces fallback to whatever XamlControlsResources defines
            // (Segoe UI Variable Text + 14px + 1px). Default CardBorderThickness=1
            // is also defined in App.xaml so the lookup never fails.
            if (app.Resources.ContainsKey("ContentControlThemeFontFamily"))
                app.Resources.Remove("ContentControlThemeFontFamily");
            if (app.Resources.ContainsKey("ContentControlThemeFontSize"))
                app.Resources.Remove("ContentControlThemeFontSize");
            if (app.Resources.ContainsKey("CardBorderThickness"))
                app.Resources["CardBorderThickness"] = new Thickness(1);
        }

        if (window?.Content is FrameworkElement root)
        {
            var target = theme switch
            {
                "light"      => ElementTheme.Light,
                "dark"       => ElementTheme.Dark,
                "copilotCli" => ElementTheme.Dark,
                _            => ElementTheme.Default,
            };

            // Force a Light↔Dark bounce so every {ThemeResource} marker on
            // every loaded element invalidates and re-resolves.
            var bounce = target == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            root.RequestedTheme = bounce;
            root.RequestedTheme = target;
        }

        // Backdrop policy: Mica is translucent (samples the desktop
        // wallpaper) so it ignores the page-background brush we just
        // set. For copilotCli we disable Mica and paint the root grid
        // with the dark brush; for the built-in themes we restore Mica.
        if (window is CopilotLauncher.MainWindow main)
            main.ApplyBackdrop(theme);
    }
}
