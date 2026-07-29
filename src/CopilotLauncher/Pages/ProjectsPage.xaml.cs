using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class ProjectsPage : Page
{
    public ProjectsViewModel ViewModel { get; }

    /// <summary>Guards the ListView SelectionChanged handler while we mutate the
    /// selection programmatically (add / delete / save), so the handler doesn't
    /// stomp the editor state we just set.</summary>
    private bool _suppressSelectionChanged;

    /// <summary>Same idea for the terminal combo: rebuilding its items must not
    /// write the rebuilt selection back into the view-model.</summary>
    private bool _suppressTerminalChanged;

    /// <summary>Monotonic id for folder-scoped loads. A load whose id is no longer
    /// current has been superseded and must not touch the editor.</summary>
    private int _loadGeneration;

    /// <summary>Serializes folder-scoped loads so a slow earlier load can't land
    /// after a later one and leave the wrong catalog in the editor.</summary>
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    public ProjectsPage()
    {
        ViewModel = new ProjectsViewModel(
            App.Services.GetRequiredService<IProjectsService>(),
            App.Services.GetRequiredService<IRepoConfigService>(),
            App.Services.GetRequiredService<ISessionCapabilityService>(),
            App.Services.GetRequiredService<ISettingsService>(),
            App.Services.GetRequiredService<ITerminalDiscoveryService>());
        InitializeComponent();
        CapEditor.WorkingDirectoryProvider = () => ViewModel.EditPath;
        Loaded += OnPageLoaded;
    }

    public Visibility BoolToVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Reload();
        PopulateTerminals(ViewModel.EditTerminal);
    }

    /// <summary>
    /// Rebuild the terminal picker for <paramref name="selectedId"/>. When the saved
    /// terminal isn't installed on this machine we add a placeholder entry carrying it,
    /// so selecting the project and saving an unrelated field can't silently erase the
    /// override.
    /// </summary>
    private void PopulateTerminals(string? selectedId)
    {
        var wanted = selectedId ?? string.Empty;
        _suppressTerminalChanged = true;
        try
        {
            TerminalCombo.Items.Clear();
            TerminalCombo.Items.Add(new ComboBoxItem { Content = "(use global default)", Tag = string.Empty });

            var known = false;
            foreach (var id in ViewModel.TerminalOptions)
            {
                if (string.IsNullOrEmpty(id)) continue;
                TerminalCombo.Items.Add(new ComboBoxItem { Content = id, Tag = id });
                if (id == wanted) known = true;
            }

            if (!string.IsNullOrEmpty(wanted) && !known)
                TerminalCombo.Items.Add(new ComboBoxItem { Content = $"{wanted} (not detected)", Tag = wanted });

            TerminalCombo.SelectedItem = TerminalCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string ?? string.Empty) == wanted)
                ?? TerminalCombo.Items[0];
        }
        finally
        {
            _suppressTerminalChanged = false;
        }
    }

    private void OnTerminalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTerminalChanged) return;
        if (TerminalCombo.SelectedItem is ComboBoxItem item)
            ViewModel.EditTerminal = item.Tag as string ?? string.Empty;
    }

    private async void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (ProjectList.SelectedItem is not ProjectProfile project) return;

        ViewModel.Edit(project);
        PopulateTerminals(ViewModel.EditTerminal);
        await LoadEditorSideDataAsync();
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        _suppressSelectionChanged = true;
        ProjectList.SelectedItem = null;
        _suppressSelectionChanged = false;

        ViewModel.StartNew();
        PopulateTerminals(string.Empty);
        await LoadEditorSideDataAsync();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _suppressSelectionChanged = true;
        ViewModel.Reload();
        ProjectList.SelectedItem = null;
        _suppressSelectionChanged = false;
        PopulateTerminals(ViewModel.EditTerminal);
    }

    /// <summary>Load the capability catalog + in-repo config for whatever folder is
    /// currently in the editor. Both are folder-scoped, so they refresh together.
    /// Superseded loads bail out rather than overwriting a newer selection.</summary>
    private async Task LoadEditorSideDataAsync()
    {
        var generation = ++_loadGeneration;
        ViewModel.IsLoadingEditor = true;
        await _loadLock.WaitAsync();
        try
        {
            if (generation != _loadGeneration) return;
            await CapEditor.LoadAsync(ViewModel.EditPath, ViewModel.CapabilitiesForEditor());
            if (generation != _loadGeneration) return;
            await ViewModel.RefreshRepoConfigAsync();
        }
        finally
        {
            _loadLock.Release();
            if (generation == _loadGeneration) ViewModel.IsLoadingEditor = false;
        }
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
            if (folder is null) return;

            ViewModel.EditPath = folder.Path;
            await LoadEditorSideDataAsync();
        }
        catch
        {
            // Folder picker can fail in odd states; user can still type the path.
        }
    }

    private async void OnInspectRepoClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshRepoConfigAsync(forceRefresh: true);

    private async void OnWriteRepoClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ApplyPluginsToRepoAsync();

    private async void OnClearRepoClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ClearPluginsFromRepoAsync();

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var caps = ViewModel.EditOverrideCapabilities ? CapEditor.ReadCapabilities() : null;
        var saved = ViewModel.Save(caps);
        if (saved is null) return;

        _suppressSelectionChanged = true;
        ProjectList.SelectedItem = saved;
        _suppressSelectionChanged = false;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _suppressSelectionChanged = true;
        ViewModel.CancelEdit();
        ProjectList.SelectedItem = null;
        _suppressSelectionChanged = false;
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is not { } project) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete project?",
            Content = $"'{project.Label}' will no longer apply to sessions under {project.Path}.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        _suppressSelectionChanged = true;
        ViewModel.Delete(project);
        ProjectList.SelectedItem = null;
        _suppressSelectionChanged = false;
    }
}
