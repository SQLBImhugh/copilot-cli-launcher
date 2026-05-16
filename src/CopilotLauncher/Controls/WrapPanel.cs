using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace CopilotLauncher.Controls;

/// <summary>
/// Minimal panel that arranges children left-to-right and wraps to a new
/// row when the next child would overflow the available width. WinUI 3
/// 1.6 doesn't ship a built-in WrapPanel (only ItemsRepeater-based layouts);
/// rather than pull in CommunityToolkit just for this, we own ~50 lines.
///
/// Intended for the Sessions filter row where 4 filter cells with wrappable
/// captions need to flow to a second row when the window narrows.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0d, OnLayoutPropertyChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel p) p.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var availableWidth = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? double.MaxValue
            : availableSize.Width;

        double rowWidth = 0, rowHeight = 0;
        double widestRow = 0, totalHeight = 0;
        bool firstInRow = true;

        foreach (var child in Children)
        {
            child.Measure(new Size(availableWidth, double.PositiveInfinity));
            var sz = child.DesiredSize;
            var advance = firstInRow ? sz.Width : HorizontalSpacing + sz.Width;

            if (!firstInRow && rowWidth + advance > availableWidth)
            {
                widestRow = Math.Max(widestRow, rowWidth);
                totalHeight += rowHeight + VerticalSpacing;
                rowWidth = sz.Width;
                rowHeight = sz.Height;
                firstInRow = false;  // child has been placed in the new row
                continue;
            }

            rowWidth += advance;
            rowHeight = Math.Max(rowHeight, sz.Height);
            firstInRow = false;
        }

        widestRow = Math.Max(widestRow, rowWidth);
        totalHeight += rowHeight;

        return new Size(
            double.IsInfinity(availableSize.Width) ? widestRow : Math.Min(widestRow, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;
        bool firstInRow = true;

        foreach (var child in Children)
        {
            var sz = child.DesiredSize;
            var advance = firstInRow ? sz.Width : HorizontalSpacing + sz.Width;

            if (!firstInRow && x + advance > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
                firstInRow = true;
                advance = sz.Width;
            }

            var leftPad = firstInRow ? 0 : HorizontalSpacing;
            x += leftPad;
            child.Arrange(new Rect(x, y, sz.Width, sz.Height));
            x += sz.Width;
            rowHeight = Math.Max(rowHeight, sz.Height);
            firstInRow = false;
        }

        return finalSize;
    }
}
