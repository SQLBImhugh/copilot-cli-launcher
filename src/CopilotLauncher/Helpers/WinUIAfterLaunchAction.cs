using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using CopilotLauncher.Services;
using WinRT.Interop;

namespace CopilotLauncher.Helpers;

/// <summary>
/// WinUI-side implementation of <see cref="IAfterLaunchAction"/>. Translates
/// the user's "After launch" preference into actual window operations.
/// "hideToTray" hides the window only when a tray icon is active; otherwise
/// it falls back to minimize so the window never becomes unrecoverable.
/// </summary>
public sealed class WinUIAfterLaunchAction : IAfterLaunchAction
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_MINIMIZE = 6;

    private readonly ITrayIconService _tray;

    public WinUIAfterLaunchAction(ITrayIconService tray)
    {
        _tray = tray;
    }

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
                {
                    var hwnd = WindowNative.GetWindowHandle(win);
                    ShowWindow(hwnd, SW_MINIMIZE);
                    break;
                }
                case "hideToTray":
                    if (_tray.IsActive)
                    {
                        win.AppWindow.Hide();
                    }
                    else
                    {
                        var hwnd = WindowNative.GetWindowHandle(win);
                        ShowWindow(hwnd, SW_MINIMIZE);
                    }
                    break;
                case "close":
                    win.Close();
                    break;
                // "stayOpen" or unknown: do nothing
            }
        });
    }
}
