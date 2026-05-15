using System.Drawing;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace CopilotLauncher.Services;

public sealed class TrayIconService : ITrayIconService
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private readonly ISettingsService _settings;
    private readonly RelayCommand _showWindowCommand;
    private readonly RelayCommand _quitCommand;
    private TaskbarIcon? _taskbarIcon;
    private Icon? _applicationIcon;

    public TrayIconService(ISettingsService settings)
    {
        _settings = settings;
        _showWindowCommand = new RelayCommand(ShowMainWindow);
        _quitCommand = new RelayCommand(QuitApplication);
    }

    public bool IsActive { get; private set; }

    public bool IsQuitting { get; private set; }

    public void Initialize()
    {
        if (_taskbarIcon is not null || IsActive)
            return;

        if (!_settings.Current.LauncherBehavior.SystemTrayIconEnabled)
            return;

        try
        {
            IsQuitting = false;
            _applicationIcon = TryLoadApplicationIcon();

            _taskbarIcon = new TaskbarIcon
            {
                ToolTipText = "Copilot CLI Launcher",
                NoLeftClickDelay = true,
                LeftClickCommand = _showWindowCommand,
                Icon = _applicationIcon,
                ContextFlyout = BuildContextMenu(),
            };

            _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
            IsActive = _taskbarIcon.IsCreated;

            if (!IsActive)
                Shutdown();
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    public void Shutdown()
    {
        IsActive = false;

        if (_taskbarIcon is not null)
        {
            try
            {
                _taskbarIcon.Dispose();
            }
            catch
            {
                // Best-effort cleanup on shutdown.
            }
            finally
            {
                _taskbarIcon = null;
            }
        }

        if (_applicationIcon is not null)
        {
            try
            {
                _applicationIcon.Dispose();
            }
            catch
            {
                // Best-effort cleanup on shutdown.
            }
            finally
            {
                _applicationIcon = null;
            }
        }
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Show Copilot CLI Launcher",
            FontWeight = Windows.UI.Text.FontWeights.Bold,
            Command = _showWindowCommand,
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Quit",
            Command = _quitCommand,
        });
        return menu;
    }

    private void ShowMainWindow()
    {
        if (Application.Current is not App app || app.MainWindowOrNull is not Window window)
            return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                window.AppWindow.Show();
                window.Activate();

                var hwnd = WindowNative.GetWindowHandle(window);
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
            }
            catch
            {
                // Showing the main window is best-effort.
            }
        });
    }

    private void QuitApplication()
    {
        IsQuitting = true;
        Shutdown();
        Application.Current.Exit();
    }

    private static Icon? TryLoadApplicationIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
                return new Icon(iconPath);

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
                return Icon.ExtractAssociatedIcon(processPath);
        }
        catch
        {
            // Fall back to the default tray icon if custom icon loading fails.
        }

        return null;
    }
}
