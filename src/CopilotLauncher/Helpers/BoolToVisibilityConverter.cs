using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace CopilotLauncher.Helpers;

/// <summary>
/// Converts bool to <see cref="Visibility"/>: true → Visible, false → Collapsed.
/// Pass ConverterParameter="invert" to flip the mapping.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, string language)
    {
        var b = value is bool v && v;
        if (parameter is string s && s.Equals("invert", System.StringComparison.OrdinalIgnoreCase))
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, string language) =>
        throw new System.NotImplementedException();
}
