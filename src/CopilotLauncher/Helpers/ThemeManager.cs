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
    // VT323 — pure pixel font, used ONLY on headings (page titles + Settings
    // section subheadings). The user's preferred copilotCli-mode body /
    // control text is the CLI font (Consolas), not pixel art, because
    // pixel-font body text is hard to read.
    //
    // Fallback chain dropped Cascadia Mono / Code per user feedback — those
    // looked too clean / modern; Consolas matches the classic terminal feel
    // and ships with every Windows install.
    private static readonly FontFamily PixelFont =
        new("ms-appx:///Assets/Fonts/VT323-Regular.ttf#VT323, Consolas");

    /// <summary>Plain Consolas — used for body / control / nav surfaces in
    /// copilotCli mode. Public so MarkdownTextBlock and similar can resolve
    /// the right body font for the active theme.</summary>
    private static readonly FontFamily CliMonoFont =
        new("Consolas, Courier New");

    public static FontFamily GetActiveBodyFontFamily()
    {
        var app = Application.Current;
        if (app?.Resources is null) return new FontFamily("Segoe UI Variable Text");
        if (app.Resources.TryGetValue("ContentControlThemeFontFamily", out var resolved)
            && resolved is FontFamily ff)
            return ff;
        return new FontFamily("Segoe UI Variable Text");
    }

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
        // CheckBox — green INNER square + INNER glyph only (the prior
        // overrides also colored the OUTER container which made the whole
        // CheckBox row green; we only want the small box itself green).
        // CheckBoxBackgroundChecked / CheckBoxBorderBrushChecked are NOT
        // overridden here so the outer container background stays
        // transparent / inherits from the surrounding surface.
        ("CheckBoxCheckBackgroundFillChecked",   Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("CheckBoxCheckBackgroundFillCheckedPointerOver", Color.FromArgb(0xFF, 0x3F, 0xD8, 0x77)),
        ("CheckBoxCheckBackgroundFillCheckedPressed", Color.FromArgb(0xFF, 0x28, 0xB0, 0x5A)),
        ("CheckBoxCheckBackgroundStrokeChecked", Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("CheckBoxCheckBackgroundStrokeCheckedPointerOver", Color.FromArgb(0xFF, 0x3F, 0xD8, 0x77)),
        ("CheckBoxCheckBackgroundStrokeCheckedPressed", Color.FromArgb(0xFF, 0x28, 0xB0, 0x5A)),
        ("CheckBoxCheckGlyphForegroundChecked",  Color.FromArgb(0xFF, 0x00, 0x00, 0x00)),
        // ToggleSwitch — green "on" state (round pills now match the green
        // square checkboxes; user wanted both binary controls to read the
        // same way). Knob stays white so it pops against the green track.
        ("ToggleSwitchFillOn",                   Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("ToggleSwitchFillOnPointerOver",        Color.FromArgb(0xFF, 0x3F, 0xD8, 0x77)),
        ("ToggleSwitchFillOnPressed",            Color.FromArgb(0xFF, 0x28, 0xB0, 0x5A)),
        ("ToggleSwitchStrokeOn",                 Color.FromArgb(0xFF, 0x30, 0xC8, 0x68)),
        ("ToggleSwitchStrokeOnPointerOver",      Color.FromArgb(0xFF, 0x3F, 0xD8, 0x77)),
        ("ToggleSwitchStrokeOnPressed",          Color.FromArgb(0xFF, 0x28, 0xB0, 0x5A)),
        ("ToggleSwitchKnobFillOn",               Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ("ToggleSwitchKnobFillOnPointerOver",    Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        ("ToggleSwitchKnobFillOnPressed",        Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
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

        // Pixel font + thicker card borders + bumped font sizes for the
        // Copilot CLI palette. Three font-family resources are overridden:
        //   - ContentControlThemeFontFamily — what most controls inherit
        //   - XamlAutoFontFamily — what NavigationViewItem and a few other
        //     legacy/system styles use; without overriding this, the nav
        //     items stay in Segoe UI Variable
        //   - CliMonoFontFamily (custom) — a public CascadiaMono-only resource
        //     so MarkdownTextBlock and similar surfaces can opt into the CLI
        //     font without the pixel-art shape
        // Font sizes bumped because VT323 reads small at default 14px.
        // FONT POLICY in copilotCli mode:
        //   - Pixel font (VT323) is used ONLY on page titles + Settings
        //     section subheadings, via the HeadingFontFamily theme resource
        //     bound inline by those TextBlocks. Pixel-art body text is hard
        //     to read so we deliberately keep it off everything else.
        //   - Body / control / nav text uses the CLI font (Cascadia Mono /
        //     Consolas) by overriding ContentControlThemeFontFamily and
        //     XamlAutoFontFamily — that covers Button, ComboBox, TextBox,
        //     CheckBox, ToggleSwitch, NavigationViewItem, and most other
        //     surfaces.
        //   - Sizes bumped because Cascadia Mono at 14px still feels small
        //     against the bright pink card borders.
        if (wantPalette)
        {
            app.Resources["HeadingFontFamily"] = PixelFont;
            app.Resources["BodyFontFamily"] = CliMonoFont;
            app.Resources["ContentControlThemeFontFamily"] = CliMonoFont;
            app.Resources["XamlAutoFontFamily"] = CliMonoFont;
            app.Resources["CliMonoFontFamily"] = CliMonoFont;
            app.Resources["ContentControlThemeFontSize"] = 16.0;
            app.Resources["ControlContentThemeFontSize"] = 16.0;
            app.Resources["NavigationViewItemFontSize"] = 16.0;
            app.Resources["FilterCheckBoxFontSize"] = 16.0;
            app.Resources["CardBorderThickness"] = new Thickness(2);
        }
        else
        {
            // Removing forces fallback to whatever XamlControlsResources defines
            // (Segoe UI Variable Text + 14px + 1px). HeadingFontFamily,
            // BodyFontFamily, CardBorderThickness, FilterCheckBoxFontSize all
            // have non-pixel defaults in App.xaml so the lookup never fails.
            if (app.Resources.ContainsKey("HeadingFontFamily"))
                app.Resources.Remove("HeadingFontFamily");
            if (app.Resources.ContainsKey("BodyFontFamily"))
                app.Resources.Remove("BodyFontFamily");
            if (app.Resources.ContainsKey("ContentControlThemeFontFamily"))
                app.Resources.Remove("ContentControlThemeFontFamily");
            if (app.Resources.ContainsKey("XamlAutoFontFamily"))
                app.Resources.Remove("XamlAutoFontFamily");
            if (app.Resources.ContainsKey("CliMonoFontFamily"))
                app.Resources.Remove("CliMonoFontFamily");
            if (app.Resources.ContainsKey("ContentControlThemeFontSize"))
                app.Resources.Remove("ContentControlThemeFontSize");
            if (app.Resources.ContainsKey("ControlContentThemeFontSize"))
                app.Resources.Remove("ControlContentThemeFontSize");
            if (app.Resources.ContainsKey("NavigationViewItemFontSize"))
                app.Resources.Remove("NavigationViewItemFontSize");
            if (app.Resources.ContainsKey("FilterCheckBoxFontSize"))
                app.Resources["FilterCheckBoxFontSize"] = 14.0;
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
