using Microsoft.UI.Xaml;
using WinRT.Interop;
using CopilotLauncher.Services;
using System.Runtime.InteropServices;

namespace CopilotLauncher.Helpers;

/// <summary>
/// WinUI-side implementation of <see cref="IAfterLaunchAction"/>. Translates
/// the user's "After launch" preference into actual window operations.
/// "hideToTray" currently falls back to "minimize" because the system-tray
/// icon (NotifyIcon) ships in a separate NuGet package that we haven't
/// adopted yet — the setting persists so when we add tray support the
/// behavior will just upgrade automatically.
/// </summary>
public sealed class WinUIAfterLaunchAction : IAfterLaunchAction
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_MINIMIZE = 6;
    private const int SW_HIDE = 0;

    public void Apply(string behavior)
    {
        if (Application.Current is not App app) return;
        var win = app.MainWindowOrNull;
        if (win is null) return;

        // Marshal to UI thread because Apply may be invoked from a VM method
        // that itself runs on the UI thread today, but defensive dispatching
        // protects against future refactors that move launch off the UI thread.
        win.DispatcherQueue.TryEnqueue(() =>
        {
            switch (behavior)
            {
                case "minimize":
                case "hideToTray": // tray fallback
                    var hwnd = WindowNative.GetWindowHandle(win);
                    ShowWindow(hwnd, SW_MINIMIZE);
                    break;
                case "close":
                    win.Close();
                    break;
                // "stayOpen" or unknown: do nothing
            }
        });
    }
}
