using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using CopilotLauncher.Helpers;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class NewShortcutPage : Page
{
    public NewShortcutViewModel ViewModel { get; }

    public NewShortcutPage()
    {
        ViewModel = new NewShortcutViewModel(
            App.Services.GetRequiredService<IShortcutsService>(),
            App.Services.GetRequiredService<ILaunchService>(),
            App.Services.GetRequiredService<ITerminalDiscoveryService>(),
            App.Services.GetRequiredService<ISettingsService>());
        InitializeComponent();
        PopulateTerminals();
        Loaded += (_, _) => ConsumePendingHandoff();
        ViewModel.RecalcPreview();
    }

    private void ConsumePendingHandoff()
    {
        if (NewShortcutHandoff.Pending is { } payload)
        {
            ViewModel.PopulateFrom(payload.SuggestedLabel, payload.WorkingDirectory, payload.ResumeId);
            NewShortcutHandoff.Pending = null;
        }
    }

    private void PopulateTerminals()
    {
        TerminalCombo.Items.Clear();
        foreach (var (id, name) in ViewModel.TerminalOptions)
            TerminalCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        TerminalCombo.SelectedIndex = 0;
    }

    private void OnTerminalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TerminalCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
            ViewModel.TerminalOverride = id;
    }

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            // WinUI 3 unpackaged apps need the parent window handle wired up.
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindowOrNull
                ?? throw new InvalidOperationException("No main window."));
            InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
                ViewModel.WorkingDirectory = folder.Path;
        }
        catch
        {
            // Folder picker can fail in odd states; user can still type the path.
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Save() is not null)
            ViewModel.Reset();
    }

    private void OnSaveAndLaunchClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SaveAndLaunch())
            ViewModel.Reset();
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ViewModel.Reset();
}

