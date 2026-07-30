using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class NewSessionPage : Page
{
    public NewSessionViewModel ViewModel { get; }

    /// <summary>Guards the project combo while it's being repopulated.</summary>
    private bool _suppressProjectChanged;

    public NewSessionPage()
    {
        ViewModel = new NewSessionViewModel(
            App.Services.GetRequiredService<ILaunchService>(),
            App.Services.GetRequiredService<ITerminalDiscoveryService>(),
            App.Services.GetRequiredService<ISettingsService>(),
            App.Services.GetRequiredService<IAfterLaunchAction>(),
            App.Services.GetRequiredService<IExtensionPermissionService>(),
            App.Services.GetRequiredService<IProjectsService>());
        InitializeComponent();
        PopulateTerminals();
        PopulateProjects();
        CapEditor.WorkingDirectoryProvider = () => ViewModel.WorkingDirectory;
        CapEditor.Changed += (_, _) => ViewModel.Capabilities = CapEditor.ReadCapabilities();
        ArgsEditor.Load(ViewModel.ExtraArgs);
        ArgsEditor.Changed += (_, _) => ViewModel.ExtraArgs = ArgsEditor.ReadArgs();
        Loaded += OnPageLoaded;
        ViewModel.RecalcPreview();
    }

    public Visibility BoolToVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await CapEditor.LoadAsync(ViewModel.WorkingDirectory, ViewModel.Capabilities);
    }

    private void PopulateProjects()
    {
        _suppressProjectChanged = true;
        try
        {
            ProjectCombo.Items.Clear();
            foreach (var p in ViewModel.Projects)
                ProjectCombo.Items.Add(new ComboBoxItem { Content = $"{p.Label}  —  {p.Path}", Tag = p });
        }
        finally
        {
            _suppressProjectChanged = false;
        }
    }

    private async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectChanged) return;
        if (ProjectCombo.SelectedItem is not ComboBoxItem { Tag: ProjectProfile project }) return;

        ViewModel.ApplyProject(project);
        SelectTerminal(ViewModel.TerminalOverride);
        ArgsEditor.Load(ViewModel.ExtraArgs);
        // The capability catalog is folder-scoped, so reload it for the project's folder
        // and seed the form from the project's own selection.
        await CapEditor.LoadAsync(ViewModel.WorkingDirectory, ViewModel.Capabilities);
    }

    private void PopulateTerminals()
    {
        TerminalCombo.Items.Clear();
        foreach (var (id, name) in ViewModel.TerminalOptions)
            TerminalCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        TerminalCombo.SelectedIndex = 0;
    }

    private void SelectTerminal(string? id)
    {
        var wanted = string.IsNullOrWhiteSpace(id) ? "auto" : id;
        foreach (var raw in TerminalCombo.Items)
        {
            if (raw is ComboBoxItem item && (item.Tag as string) == wanted)
            {
                TerminalCombo.SelectedItem = item;
                return;
            }
        }
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

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ExtraArgs = ArgsEditor.ReadArgs();
        ViewModel.Capabilities = CapEditor.ReadCapabilities();
        ViewModel.StartSession();
    }
}
