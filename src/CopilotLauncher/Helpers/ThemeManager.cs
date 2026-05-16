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
///
/// Implementation note: in WinUI 3 Windows App SDK 1.6, MergedDictionaries
/// added at runtime do NOT reliably trigger {ThemeResource} re-resolution
/// even with a RequestedTheme bounce. The brushes that controls already
/// resolved on first paint stay stale, so only some surfaces re-color.
///
/// Instead we directly set/remove individual brush keys on
/// Application.Current.Resources. WinUI re-evaluates {ThemeResource}
/// references against the live Resources dictionary on RequestedTheme
/// change, and a Light↔Dark bounce is enough to force every existing
/// control to pick up the new brushes.
/// </summary>
public static class ThemeManager
{
    /// <summary>Sampled from the GitHub Copilot CLI welcome banner.
    /// Cyan = primary accent; pink = secondary stroke/attention;
    /// green = success; near-black surfaces; white text.</summary>
    private static readonly (string Key, Color Color)[] CopilotPalette =
    {
        // Page / window backgrounds
        ("ApplicationPageBackgroundThemeBrush",  Color.FromArgb(0xFF, 0x17, 0x17, 0x17)),
        ("SolidBackgroundFillColorBaseBrush",    Color.FromArgb(0xFF, 0x17, 0x17, 0x17)),
        ("SolidBackgroundFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B)),
        ("SolidBackgroundFillColorTertiaryBrush", Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F)),
        ("LayerFillColorDefaultBrush",           Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F)),
        ("LayerOnAcrylicFillColorDefaultBrush",  Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F)),
        // Card surfaces
        ("CardBackgroundFillColorDefaultBrush",  Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B)),
        ("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(0xFF, 0x22, 0x22, 0x22)),
        ("CardStrokeColorDefaultBrush",          Color.FromArgb(0xFF, 0x3A, 0x26, 0x40)),
        ("CardStrokeColorDefaultSolidBrush",     Color.FromArgb(0xFF, 0x3A, 0x26, 0x40)),
        // Subtle fills
        ("SubtleFillColorTransparentBrush",      Color.FromArgb(0x00, 0x00, 0x00, 0x00)),
        ("SubtleFillColorSecondaryBrush",        Color.FromArgb(0x1F, 0xCC, 0x66, 0xCC)),
        ("SubtleFillColorTertiaryBrush",         Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F)),
        ("SubtleFillColorDisabledBrush",         Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)),
        // Control fills (Button/ComboBox/TextBox)
        ("ControlFillColorDefaultBrush",         Color.FromArgb(0xFF, 0x22, 0x22, 0x22)),
        ("ControlFillColorSecondaryBrush",       Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)),
        ("ControlFillColorTertiaryBrush",        Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F)),
        ("ControlStrokeColorDefaultBrush",       Color.FromArgb(0xFF, 0x3A, 0x26, 0x40)),
        ("ControlStrokeColorSecondaryBrush",     Color.FromArgb(0xFF, 0x3A, 0x26, 0x40)),
        // Accent (cyan)
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
        ("FocusStrokeColorInnerBrush",           Color.FromArgb(0xFF, 0x17, 0x17, 0x17)),
        // System status (chips / badges)
        ("SystemFillColorSuccessBrush",          Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("SystemFillColorAttentionBrush",        Color.FromArgb(0xFF, 0xCC, 0x66, 0xCC)),
        ("SystemFillColorCautionBrush",          Color.FromArgb(0xFF, 0xF2, 0xC9, 0x4C)),
        ("SystemFillColorCriticalBrush",         Color.FromArgb(0xFF, 0xE0, 0x61, 0x7A)),
        ("SystemFillColorNeutralBrush",          Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)),
        ("SystemFillColorSolidNeutralBrush",     Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)),
        ("SystemFillColorSuccessBackgroundBrush", Color.FromArgb(0x40, 0x30, 0xC8, 0x68)),
        ("SystemFillColorAttentionBackgroundBrush", Color.FromArgb(0x40, 0xCC, 0x66, 0xCC)),
        ("SystemFillColorCautionBackgroundBrush", Color.FromArgb(0x40, 0xF2, 0xC9, 0x4C)),
        ("SystemFillColorCriticalBackgroundBrush", Color.FromArgb(0x40, 0xE0, 0x61, 0x7A)),
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
                // Remove our override so the underlying ThemeDictionary
                // brush (Fluent Light or Dark) takes over again.
                if (app.Resources.ContainsKey(key))
                    app.Resources.Remove(key);
            }
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
            // every loaded element invalidates and re-resolves. Just setting
            // RequestedTheme to its current value is a no-op.
            var bounce = target == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            root.RequestedTheme = bounce;
            root.RequestedTheme = target;
        }
    }
}

