using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Services;

namespace CopilotLauncher.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        var settings = (ISettingsService)App.Services.GetService(typeof(ISettingsService))!;
        AppDataPathLabel.Text = $"App data folder: {settings.AppDataDirectory}";

        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "unknown";
        VersionLabel.Text = $"CopilotLauncher version: {version}";
    }
}
