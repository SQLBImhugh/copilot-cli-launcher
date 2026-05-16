using Microsoft.UI.Xaml;

namespace CopilotLauncher.Helpers;

/// <summary>
/// Applies the user's selected theme to the running app. Supports the four
/// named themes:
///   "system"     — follow the OS (default)
///   "light"      — force Fluent light
///   "dark"       — force Fluent dark
///   "copilotCli" — force Fluent dark + merge our cyan/pink/green palette
///                  overrides on top (see Themes/CopilotCliPalette.xaml).
///
/// The custom palette is added to / removed from
/// <see cref="Application.Resources"/>.<see cref="ResourceDictionary.MergedDictionaries"/>
/// so brushes referenced via <c>{ThemeResource ...}</c> resolve through it.
/// </summary>
public static class ThemeManager
{
    private static ResourceDictionary? _copilotPalette;
    private const string PaletteUri = "ms-appx:///Themes/CopilotCliPalette.xaml";

    public static void Apply(string theme, Window? window)
    {
        var resources = Application.Current?.Resources?.MergedDictionaries;
        var paletteIsMerged = resources is not null
            && _copilotPalette is not null
            && resources.Contains(_copilotPalette);

        var wantPalette = string.Equals(theme, "copilotCli", StringComparison.OrdinalIgnoreCase);

        if (wantPalette && resources is not null && !paletteIsMerged)
        {
            _copilotPalette ??= LoadPalette();
            if (_copilotPalette is not null)
                resources.Add(_copilotPalette);
        }
        else if (!wantPalette && paletteIsMerged && resources is not null && _copilotPalette is not null)
        {
            resources.Remove(_copilotPalette);
        }

        // Map the theme name to ElementTheme on the window's root content.
        // RequestedTheme on Application is read-only after launch; the per-
        // FrameworkElement RequestedTheme propagates down the visual tree
        // and re-evaluates ThemeResource lookups.
        if (window?.Content is FrameworkElement root)
        {
            var target = theme switch
            {
                "light"      => ElementTheme.Light,
                "dark"       => ElementTheme.Dark,
                "copilotCli" => ElementTheme.Dark,
                _            => ElementTheme.Default,
            };

            // Bouncing through Default forces ThemeResource bindings to
            // re-resolve, which is what picks up our newly-merged (or
            // newly-unmerged) palette overrides.
            if (root.RequestedTheme == target)
            {
                root.RequestedTheme = target == ElementTheme.Default
                    ? ElementTheme.Light
                    : ElementTheme.Default;
            }
            root.RequestedTheme = target;
        }
    }

    private static ResourceDictionary? LoadPalette()
    {
        try
        {
            return new ResourceDictionary { Source = new Uri(PaletteUri) };
        }
        catch
        {
            // Theme file missing or malformed — caller falls back to the
            // unmerged Fluent default. Non-fatal.
            return null;
        }
    }
}
