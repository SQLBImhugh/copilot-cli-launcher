using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private readonly ISettingsService _settings;
    private readonly ITerminalDiscoveryService _terminals;
    private bool _initializing = true;

    public SettingsPage()
    {
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _terminals = App.Services.GetRequiredService<ITerminalDiscoveryService>();
        ViewModel = new SettingsViewModel(_settings, _terminals);
        InitializeComponent();
        AppDataPathLabel.Text = $"App data folder: {_settings.AppDataDirectory}";

        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "unknown";
        VersionLabel.Text = $"CopilotLauncher version: {version}";

        PopulateTerminals();
        SyncCombo(UpdateFreqCombo, ViewModel.AutoUpdateFrequency);
        SyncCombo(AfterLaunchCombo, ViewModel.AfterLaunch);
        SyncCombo(ThemeCombo, ViewModel.Theme);

        // Wire the autostart registry write to the toggle. The Core VM raises an
        // event because Microsoft.Win32.Registry isn't available in netstandard
        // / .NET Core class libraries without referencing Windows-specific assemblies.
        ViewModel.StartWithWindowsChanged += (_, enabled) =>
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    Helpers.AutostartRegistry.Sync(enabled, exe);
            }
            catch { }
        };

        // Live-apply theme changes against the main window. ThemeManager
        // handles palette merge/unmerge + bouncing RequestedTheme so any
        // {ThemeResource} bindings in the visual tree re-resolve.
        ViewModel.ThemeChanged += (_, themeName) =>
        {
            try
            {
                Helpers.ThemeManager.Apply(themeName, ((App)Application.Current).MainWindowOrNull);
            }
            catch { }
        };

        _initializing = false;

        Unloaded += (_, _) => ViewModel.Flush();
    }

    private void PopulateTerminals()
    {
        TerminalCombo.Items.Clear();
        TerminalCombo.Items.Add(new ComboBoxItem { Content = "Auto-detect (best available)", Tag = "auto" });
        foreach (var t in _terminals.Discovered)
            TerminalCombo.Items.Add(new ComboBoxItem { Content = $"{t.DisplayName} ({t.ExecutablePath})", Tag = t.Id });
        var pref = ViewModel.DefaultTerminal ?? "auto";
        for (int i = 0; i < TerminalCombo.Items.Count; i++)
        {
            if (TerminalCombo.Items[i] is ComboBoxItem cbi && (string)cbi.Tag! == pref)
            {
                TerminalCombo.SelectedIndex = i;
                break;
            }
        }
        if (TerminalCombo.SelectedIndex < 0) TerminalCombo.SelectedIndex = 0;
        UpdateTerminalDetail();
    }

    private void UpdateTerminalDetail()
    {
        if (TerminalCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id && id != "auto")
        {
            var t = _terminals.Discovered.FirstOrDefault(x => x.Id == id);
            if (t is not null)
            {
                TerminalDetail.Text = t.SupportsTabs
                    ? "Supports new tabs in the same window."
                    : "No tab/window-id wrapper; opens a fresh console window per launch.";
                return;
            }
        }
        TerminalDetail.Text = "Order of preference when auto-detecting: Windows Terminal -> PowerShell 7 -> Windows PowerShell 5.1 -> Command Prompt.";
    }

    private static void SyncCombo(ComboBox combo, string tagValue)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem cbi && cbi.Tag is string t && t == tagValue)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void OnTerminalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (TerminalCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id)
        {
            ViewModel.DefaultTerminal = id;
            UpdateTerminalDetail();
        }
    }

    private void OnUpdateFreqChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (UpdateFreqCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            ViewModel.AutoUpdateFrequency = tag;
    }

    private void OnAfterLaunchChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (AfterLaunchCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            ViewModel.AfterLaunch = tag;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ThemeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            ViewModel.Theme = tag;
    }

    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Slider value is two-way bound to int property; the bound setter calls
        // ScheduleSave. This handler exists in case we want extra behavior later.
    }

    private async void OnBrowseAgentsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".md");
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add("*");
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindowOrNull
                ?? throw new InvalidOperationException("No main window."));
            InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                ViewModel.AgentsContextFilePath = file.Path;
        }
        catch
        {
        }
    }

    private void OnOpenAppDataClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.AppDataDirectory,
                UseShellExecute = true,
            });
        }
        catch
        {
        }
    }
}
