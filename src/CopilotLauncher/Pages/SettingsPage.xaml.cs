using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Services;

namespace CopilotLauncher.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly ISettingsService _settings;
    private readonly ITerminalDiscoveryService _terminals;
    private bool _initializing = true;

    public SettingsPage()
    {
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _terminals = App.Services.GetRequiredService<ITerminalDiscoveryService>();
        InitializeComponent();
        AppDataPathLabel.Text = $"App data folder: {_settings.AppDataDirectory}";

        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "unknown";
        VersionLabel.Text = $"CopilotLauncher version: {version}";

        PopulateTerminals();
        _initializing = false;
    }

    private void PopulateTerminals()
    {
        TerminalCombo.Items.Clear();
        TerminalCombo.Items.Add(new ComboBoxItem { Content = "Auto-detect (best available)", Tag = "auto" });
        foreach (var t in _terminals.Discovered)
        {
            TerminalCombo.Items.Add(new ComboBoxItem { Content = $"{t.DisplayName} ({t.ExecutablePath})", Tag = t.Id });
        }
        var pref = _settings.Current.Terminal.DefaultTerminal ?? "auto";
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
        TerminalDetail.Text = "Order of preference when auto-detecting: Windows Terminal → PowerShell 7 → Windows PowerShell 5.1 → Command Prompt.";
    }

    private void OnTerminalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (TerminalCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string id)
        {
            _settings.Current.Terminal.DefaultTerminal = id;
            _settings.Save();
            UpdateTerminalDetail();
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
            // Best effort — folder open is non-critical.
        }
    }
}

