namespace CopilotLauncher.Helpers;

public static class WindowSizing
{
    public const int MinNormalWidth = 900;
    public const int MinNormalHeight = 700;
    public const int DefaultNormalWidth = 1180;
    public const int DefaultNormalHeight = 1040;

    public static (int width, int height) ClampNormalSize(
        int width,
        int height,
        int minWidth = MinNormalWidth,
        int minHeight = MinNormalHeight,
        int defaultWidth = DefaultNormalWidth,
        int defaultHeight = DefaultNormalHeight) =>
        width < minWidth || height < minHeight
            ? (defaultWidth, defaultHeight)
            : (width, height);

    public static double ScaleFromDpi(uint dpi) => dpi == 0 ? 1.0 : dpi / 96.0;
}
