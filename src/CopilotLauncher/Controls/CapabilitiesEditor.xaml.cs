using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Controls;

/// <summary>
/// Reusable editor for a <see cref="LaunchCapabilities"/> selection (which MCP
/// servers / agent / tools / skills load). Hosted by the New Session tab, the
/// New Shortcut wizard, and the Settings "defaults" section. Owns its own
/// <see cref="CapabilitiesEditorViewModel"/> and enumerates the catalog via
/// <see cref="ISessionCapabilityService"/>.
/// </summary>
public sealed partial class CapabilitiesEditor : UserControl
{
    public CapabilitiesEditorViewModel ViewModel { get; }

    private readonly ISessionCapabilityService _caps;

    /// <summary>Host-supplied accessor for the current working directory (used by Refresh and
    /// for MCP/agent discovery, which are folder-aware).</summary>
    public Func<string?>? WorkingDirectoryProvider { get; set; }

    /// <summary>Forwarded from the VM — fires whenever any capability selection changes.</summary>
    public event EventHandler? Changed;

    public CapabilitiesEditor()
    {
        _caps = App.Services.GetRequiredService<ISessionCapabilityService>();
        ViewModel = new CapabilitiesEditorViewModel();
        ViewModel.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        InitializeComponent();
    }

    public Visibility BoolToVis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Current selection (null when nothing is selected).</summary>
    public LaunchCapabilities? ReadCapabilities() => ViewModel.ToCapabilities();

    /// <summary>Enumerate the catalog for <paramref name="workingDir"/> and seed the form from
    /// <paramref name="existing"/>. Best-effort: failures leave the form empty.</summary>
    public async Task LoadAsync(string? workingDir, LaunchCapabilities? existing, bool forceRefresh = false)
    {
        try
        {
            LoadingRing.IsActive = true;
            var catalog = await _caps.DiscoverAsync(workingDir, forceRefresh);
            ViewModel.LoadCatalog(catalog, existing);
        }
        catch
        {
            ViewModel.LoadCatalog(CapabilityCatalog.Empty, existing);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        // Preserve the user's current picks across the refresh.
        var existing = ViewModel.ToCapabilities();
        await LoadAsync(WorkingDirectoryProvider?.Invoke(), existing, forceRefresh: true);
    }
}
