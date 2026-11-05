using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;
using CopilotLauncher.Services;

namespace CopilotLauncher.ViewModels;

public enum ProjectSortField
{
    Name,
    WorkingDirectory,
}

/// <summary>One plugin row with a per-repo on/off checkbox.</summary>
public sealed partial class RepoPluginToggle : ObservableObject
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Whether the plugin is enabled at the user level. A repo allowlist can only
    /// narrow what the CLI loads relative to what's installed, so this is shown as context.</summary>
    public bool EnabledForUser { get; init; }

    [ObservableProperty]
    private bool _isEnabled = true;

    public string Caption => EnabledForUser ? "enabled globally" : "disabled globally";
}

/// <summary>One detected in-repo config file, for the read-only status list.</summary>
public sealed class RepoConfigRow
{
    public required string RelativePath { get; init; }
    public required string Description { get; init; }
    public required bool Exists { get; init; }
    public required bool Managed { get; init; }

    public string StatusGlyph => Exists ? "\uE73E" : "\uE711";      // check / dismiss
    public string StatusLabel => Exists ? "present" : "not present";
    public double RowOpacity => Exists ? 1.0 : 0.45;
}

/// <summary>One importable working directory, with its tick box.</summary>
public sealed partial class ProjectImportRow : ObservableObject
{
    public required ProjectImportCandidate Candidate { get; init; }

    /// <summary>Odd-numbered row — the view tints these for readability.</summary>
    [ObservableProperty]
    private bool _isAlternate;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isHidden;

    public string SuggestedName => Candidate.SuggestedName;
    public string Path => Candidate.Path;
    public string Caption => IsHidden ? $"{Candidate.Caption} · hidden from import" : Candidate.Caption;
    public string SessionsText => Candidate.SessionsText;
    public string DetailText => !IsHidden ? Candidate.DetailText
        : string.IsNullOrEmpty(Candidate.DetailText) ? "hidden from import"
        : $"hidden from import · {Candidate.DetailText}";
    public string LastUsedDate => Candidate.LastUsedDate;
    public string LastUsedRelative => Candidate.LastUsedRelative;
    public bool CanSelect => !Candidate.AlreadyImported;
    public bool CanImport => CanSelect && !IsHidden;
    public double RowOpacity => Candidate.AlreadyImported || IsHidden ? 0.5 : 1.0;

    /// <summary>Single-line label for screen readers — the row's visual content is a stack of
    /// TextBlocks, which would otherwise announce as nothing.</summary>
    public string AccessibleName => $"{SuggestedName}, {Path}, last used {LastUsedDate}, {Caption}";
}

/// <summary>
/// ViewModel for the Projects page: CRUD over <see cref="IProjectsService"/>
/// plus the in-repo config panel backed by <see cref="IRepoConfigService"/>.
/// Lives in Core so the edit/save/match behavior is unit-testable.
/// </summary>
public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly IProjectsService _store;
    private readonly IRepoConfigService _repoConfig;
    private readonly ISessionCapabilityService _capabilities;
    private readonly ISettingsService _settings;
    private readonly ITerminalDiscoveryService _terminals;
    private readonly ISessionDiscoveryService? _sessions;
    private readonly IProjectLaunchService? _projectLaunch;

    public ObservableCollection<ProjectProfile> Items { get; } = new();
    public ObservableCollection<RepoPluginToggle> RepoPlugins { get; } = new();
    public ObservableCollection<RepoConfigRow> RepoConfigFiles { get; } = new();
    public ObservableCollection<ProjectImportRow> ImportCandidates { get; } = new();

    // View-lifetime presentation state only; never reorder or write the project store.
    [ObservableProperty] private ProjectSortField _sortField = ProjectSortField.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionGlyph))]
    [NotifyPropertyChangedFor(nameof(SortDirectionDescription))]
    private bool _isSortDescending;

    public string SortDirectionGlyph => IsSortDescending ? "\uE74B" : "\uE74A";
    public string SortDirectionDescription => IsSortDescending
        ? "Sorted descending (Z to A). Activate to sort ascending (A to Z)."
        : "Sorted ascending (A to Z). Activate to sort descending (Z to A).";

    partial void OnSortFieldChanged(ProjectSortField value) => ApplySort();
    partial void OnIsSortDescendingChanged(bool value) => ApplySort();

    /// <summary>Terminal ids offered in the override picker; "" = inherit the global default.</summary>
    public ObservableCollection<string> TerminalOptions { get; } = new();

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _repoConfigMessage = string.Empty;

    /// <summary>True while the folder-scoped catalog / repo config is still loading. Saving
    /// during that window would persist the previously selected project's data.</summary>
    [ObservableProperty] private bool _isLoadingEditor;

    partial void OnIsLoadingEditorChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    public bool CanSave => !IsLoadingEditor;

    // ---- editor state (a flat projection of the selected ProjectProfile) ----

    [ObservableProperty] private ProjectProfile? _selected;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editLabel = string.Empty;
    [ObservableProperty] private string _editPath = string.Empty;
    [ObservableProperty] private bool _editIncludeSubdirectories = true;
    [ObservableProperty] private bool _editEnabled = true;
    [ObservableProperty] private string _editExtraArgs = string.Empty;
    [ObservableProperty] private string _editTerminal = string.Empty;
    [ObservableProperty] private bool _editSyncRepoConfig;

    // Tri-state overrides are surfaced as an "override?" checkbox plus a value
    // checkbox, because WinUI's CheckBox IsThreeState reads as a null/indeterminate
    // that users routinely mistake for "off".
    [ObservableProperty] private bool _editOverrideAllowAll;
    [ObservableProperty] private bool _editAllowAll;
    [ObservableProperty] private bool _editOverridePreApprove;
    [ObservableProperty] private bool _editPreApprove;
    [ObservableProperty] private bool _editOverrideCapabilities;

    public bool HasSelection => Selected is not null;
    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => Items.Count == 0;
    public bool HasRepoPlugins => RepoPlugins.Count > 0;

    public ProjectsViewModel(
        IProjectsService store,
        IRepoConfigService repoConfig,
        ISessionCapabilityService capabilities,
        ISettingsService settings,
        ITerminalDiscoveryService terminals,
        ISessionDiscoveryService? sessions = null,
        IProjectLaunchService? projectLaunch = null)
    {
        _store = store;
        _repoConfig = repoConfig;
        _capabilities = capabilities;
        _settings = settings;
        _terminals = terminals;
        _sessions = sessions;
        _projectLaunch = projectLaunch;
    }

    /// <summary>
    /// Start a fresh Copilot session in this project's folder, applying the project's own
    /// settings. Goes through the same <see cref="IProjectLaunchService"/> the Sessions tab
    /// uses, so it behaves identically to resuming there.
    /// </summary>
    public bool StartSession(ProjectProfile project)
    {
        if (_projectLaunch is null)
        {
            StatusMessage = "Launching is unavailable.";
            return false;
        }
        if (!Directory.Exists(project.Path))
        {
            StatusMessage = $"Folder no longer exists: {project.Path}";
            return false;
        }

        var result = _projectLaunch.Launch(project.Path, resumeTarget: null);
        StatusMessage = result.Success
            ? $"Started a new session in {project.Path} {result.Describe()}"
            : $"Launch failed: {result.Error}";
        return result.Success;
    }

    partial void OnSelectedChanged(ProjectProfile? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is not null) LoadEditor(value);
    }

    public void Reload()
    {
        try
        {
            _store.Reload();
            Items.Clear();
            foreach (var p in _store.All) Items.Add(p);
            ApplySort();

            TerminalOptions.Clear();
            TerminalOptions.Add(string.Empty);   // inherit
            foreach (var t in _terminals.Discovered) TerminalOptions.Add(t.Id);

            StatusMessage = Items.Count switch
            {
                0 => "No projects yet. Add one to pin how a folder always starts.",
                1 => "1 project configured.",
                _ => $"{Items.Count} projects configured.",
            };
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(HasNoItems));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load projects.json: {ex.Message}";
        }
    }

    private void ApplySort()
    {
        var sorted = SortField == ProjectSortField.WorkingDirectory
            ? Items.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
            : Items.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var ordered = sorted.ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        if (IsSortDescending) ordered.Reverse();

        // Keep the same instances and never clear the list: selection and unsaved editor
        // state must not change just because rows move. IDs make case-insensitive ties
        // deterministic even when the store is reloaded in a different order.
        for (var index = 0; index < ordered.Count; index++)
        {
            var currentIndex = Items.IndexOf(ordered[index]);
            if (currentIndex != index) Items.Move(currentIndex, index);
        }
    }

    /// <summary>Begin editing a brand-new profile. Nothing is persisted until <see cref="Save"/>.</summary>
    public void StartNew(string? path = null)
    {
        Selected = null;
        IsEditing = true;
        EditLabel = string.Empty;
        EditPath = path ?? string.Empty;
        EditIncludeSubdirectories = true;
        EditEnabled = true;
        EditExtraArgs = string.Empty;
        EditTerminal = string.Empty;
        EditOverrideAllowAll = false;
        EditAllowAll = false;
        EditOverridePreApprove = false;
        EditPreApprove = false;
        EditOverrideCapabilities = false;
        EditSyncRepoConfig = false;
        RepoPlugins.Clear();
        RepoConfigFiles.Clear();
        RepoConfigMessage = string.Empty;
        OnPropertyChanged(nameof(HasRepoPlugins));
    }

    public void Edit(ProjectProfile project)
    {
        Selected = project;
        IsEditing = true;
    }

    private void LoadEditor(ProjectProfile p)
    {
        EditLabel = p.Label;
        EditPath = p.Path;
        EditIncludeSubdirectories = p.IncludeSubdirectories;
        EditEnabled = p.Enabled;
        EditExtraArgs = p.ExtraCopilotArgs ?? string.Empty;
        EditTerminal = p.TerminalOverride ?? string.Empty;
        EditOverrideAllowAll = p.EnableAllowAll.HasValue;
        EditAllowAll = p.EnableAllowAll ?? false;
        EditOverridePreApprove = p.PreApproveExtensions.HasValue;
        EditPreApprove = p.PreApproveExtensions ?? false;
        EditOverrideCapabilities = p.Capabilities is not null;
        EditSyncRepoConfig = p.SyncRepoConfigOnLaunch;

        // Drop the previous project's folder-scoped data immediately. It is
        // repopulated asynchronously; leaving it in place would let a Save during
        // the load window persist the wrong plugin allowlist.
        RepoPlugins.Clear();
        RepoConfigFiles.Clear();
        RepoConfigMessage = string.Empty;
        OnPropertyChanged(nameof(HasRepoPlugins));
    }

    public void CancelEdit()
    {
        IsEditing = false;
        Selected = null;
    }

    /// <summary>
    /// Persist the editor state. <paramref name="capabilities"/> comes from the shared
    /// CapabilitiesEditor control (null when the user didn't enable the capability override).
    /// Returns the saved profile, or null when validation failed.
    /// </summary>
    public ProjectProfile? Save(LaunchCapabilities? capabilities)
    {
        var path = ProjectMatcher.Normalize(EditPath);
        if (path is null)
        {
            StatusMessage = "Enter a working directory for this project.";
            return null;
        }
        if (!Directory.Exists(path))
        {
            StatusMessage = $"Directory does not exist: {path}";
            return null;
        }

        var duplicate = Items.FirstOrDefault(p =>
            p.Id != Selected?.Id &&
            string.Equals(ProjectMatcher.Normalize(p.Path), path, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            StatusMessage = $"'{duplicate.Label}' already covers {path}.";
            return null;
        }

        var label = string.IsNullOrWhiteSpace(EditLabel) ? Path.GetFileName(path) : EditLabel.Trim();
        if (string.IsNullOrWhiteSpace(label)) label = path;

        var target = Selected ?? new ProjectProfile { Id = Guid.NewGuid().ToString(), Label = label, Path = path };
        target.Label = label;
        target.Path = path;
        target.IncludeSubdirectories = EditIncludeSubdirectories;
        target.Enabled = EditEnabled;
        target.EnableAllowAll = EditOverrideAllowAll ? EditAllowAll : null;
        target.PreApproveExtensions = EditOverridePreApprove ? EditPreApprove : null;
        target.ExtraCopilotArgs = string.IsNullOrWhiteSpace(EditExtraArgs) ? null : EditExtraArgs.Trim();
        target.TerminalOverride = string.IsNullOrWhiteSpace(EditTerminal) ? null : EditTerminal;
        target.Capabilities = EditOverrideCapabilities ? capabilities : null;
        target.SyncRepoConfigOnLaunch = EditSyncRepoConfig;
        target.RepoEnabledPlugins = RepoPlugins.Count > 0
            ? RepoPlugins.Where(p => p.IsEnabled).Select(p => p.Key).ToList()
            : target.RepoEnabledPlugins;

        try
        {
            if (Selected is null)
            {
                _store.Add(target);
                Items.Add(target);
            }
            else
            {
                _store.Update(target);
                var idx = Items.IndexOf(Selected);
                if (idx >= 0)
                {
                    // The row template binds OneTime, and assigning the same
                    // instance back into the indexer doesn't reliably regenerate
                    // the container. Remove + insert forces the label/path to
                    // repaint after an edit.
                    Items.RemoveAt(idx);
                    Items.Insert(idx, target);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            return null;
        }

        ApplySort();
        Selected = target;
        IsEditing = false;
        StatusMessage = $"Saved '{target.Label}'.";
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        return target;
    }

    public void Delete(ProjectProfile project)
    {
        try
        {
            _store.Remove(project.Id);
        }
        catch (Exception ex)
        {
            // The caller is an async void WinUI handler, so an escaping IO exception
            // would tear down the app instead of surfacing here.
            StatusMessage = $"Delete failed: {ex.Message}";
            return;
        }

        Items.Remove(project);
        if (Selected?.Id == project.Id) { Selected = null; IsEditing = false; }
        StatusMessage = $"Deleted '{project.Label}'.";
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
    }

    /// <summary>A human-readable summary of what this profile changes vs. the global defaults.</summary>
    public string DescribeOverrides(ProjectProfile p)
    {
        var parts = new List<string>();
        if (p.EnableAllowAll.HasValue) parts.Add(p.EnableAllowAll.Value ? "--allow-all" : "no --allow-all");
        if (!string.IsNullOrWhiteSpace(p.ExtraCopilotArgs)) parts.Add(p.ExtraCopilotArgs.Trim());
        if (!string.IsNullOrWhiteSpace(p.TerminalOverride)) parts.Add($"terminal: {p.TerminalOverride}");
        if (p.PreApproveExtensions == true) parts.Add("pre-approve extensions");
        if (p.Capabilities is { IsEmpty: false } caps)
        {
            if (!string.IsNullOrWhiteSpace(caps.Agent)) parts.Add($"agent: {caps.Agent}");
            if (caps.DisabledMcpServers.Count > 0) parts.Add($"{caps.DisabledMcpServers.Count} MCP off");
            if (caps.ToolMode != ToolFilterMode.None && caps.Tools.Count > 0) parts.Add($"{caps.Tools.Count} tool rules");
            if (caps.DisableAllSkills) parts.Add("skills off");
        }
        if (p.SyncRepoConfigOnLaunch) parts.Add("syncs repo plugin config");
        return parts.Count == 0 ? "Inherits all global defaults." : string.Join(" · ", parts);
    }

    /// <summary>Load the in-repo config panel for the directory currently in the editor.</summary>
    public async Task RefreshRepoConfigAsync(bool forceRefresh = false)
    {
        var path = ProjectMatcher.Normalize(EditPath);
        RepoConfigFiles.Clear();
        RepoPlugins.Clear();
        OnPropertyChanged(nameof(HasRepoPlugins));

        if (path is null || !Directory.Exists(path))
        {
            RepoConfigMessage = "Pick an existing folder to inspect its in-repo copilot config.";
            return;
        }

        RepoConfigStatus status;
        try
        {
            status = _repoConfig.Inspect(path);
        }
        catch (Exception ex)
        {
            RepoConfigMessage = $"Could not read repo config: {ex.Message}";
            return;
        }

        foreach (var f in status.Files)
        {
            RepoConfigFiles.Add(new RepoConfigRow
            {
                RelativePath = f.RelativePath,
                Description = f.Description,
                Exists = f.Exists,
                Managed = f.Kind == RepoConfigKind.Managed,
            });
        }

        IReadOnlyList<InstalledPluginInfo> plugins = Array.Empty<InstalledPluginInfo>();
        try
        {
            // Cheap local read — no CLI shell-out, so this stays responsive.
            plugins = _capabilities.GetInstalledPlugins();
        }
        catch
        {
            // Plugin list is best-effort; the file status list is still useful.
        }

        // Seed each toggle from (1) what the repo file already pins, else
        // (2) the saved project selection, else (3) the user-level enabled state.
        var repoPinned = status.EnabledPlugins;
        var projectPinned = Selected?.RepoEnabledPlugins is { } saved
            ? new HashSet<string>(saved, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var p in plugins)
        {
            var enabled = repoPinned is not null && repoPinned.TryGetValue(p.Key, out var fromRepo)
                ? fromRepo
                : projectPinned?.Contains(p.Key) ?? p.Enabled;

            RepoPlugins.Add(new RepoPluginToggle
            {
                Key = p.Key,
                DisplayName = p.DisplayName,
                EnabledForUser = p.Enabled,
                IsEnabled = enabled,
            });
        }
        OnPropertyChanged(nameof(HasRepoPlugins));

        RepoConfigMessage = status.ManagesPlugins
            ? $"{status.PresentCount} config file(s) present. This repo already pins {status.EnabledPlugins!.Count(kv => kv.Value)} plugin(s) in {RepoConfigService.SettingsRelativePath.Replace('\\', '/')}."
            : $"{status.PresentCount} of {status.Files.Count} known config file(s) present in this folder.";
    }

    /// <summary>Write the current plugin toggles into the repo's
    /// <c>.github/copilot/settings.json</c>.</summary>
    public async Task<bool> ApplyPluginsToRepoAsync()
    {
        var path = ProjectMatcher.Normalize(EditPath);
        if (path is null || !Directory.Exists(path))
        {
            RepoConfigMessage = "Pick an existing folder first.";
            return false;
        }
        if (RepoPlugins.Count == 0)
        {
            RepoConfigMessage = "No installed plugins to write.";
            return false;
        }

        try
        {
            var plugins = _capabilities.GetInstalledPlugins();
            var enabled = RepoPlugins.Where(p => p.IsEnabled).Select(p => p.Key).ToList();
            if (!_repoConfig.WriteEnabledPlugins(path, plugins, enabled))
            {
                RepoConfigMessage = "Could not write .github/copilot/settings.json (unreadable or unwritable).";
                return false;
            }

            // Persist the same list on the profile. Without this, a project with
            // SyncRepoConfigOnLaunch would rewrite the file from its stale saved list on
            // the next launch and silently undo this write.
            var persisted = string.Empty;
            if (Selected is { } project)
            {
                project.RepoEnabledPlugins = enabled;
                try
                {
                    _store.Update(project);
                    persisted = " Saved to the project too.";
                }
                catch (Exception ex)
                {
                    persisted = $" (could not update the project profile: {ex.Message})";
                }
            }
            else
            {
                persisted = " Save the project to keep this selection.";
            }

            RepoConfigMessage = $"Wrote {enabled.Count} enabled / {plugins.Count - enabled.Count} disabled plugin(s) to {RepoConfigService.SettingsRelativePath.Replace('\\', '/')}.{persisted}";
            await RefreshRepoConfigAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            RepoConfigMessage = $"Write failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>Drop the repo's plugin pin so the user-level config governs again.</summary>
    public async Task<bool> ClearPluginsFromRepoAsync()
    {
        var path = ProjectMatcher.Normalize(EditPath);
        if (path is null) return false;

        var cleared = _repoConfig.ClearEnabledPlugins(path);
        RepoConfigMessage = cleared
            ? "Removed the repo plugin allowlist — user config governs again."
            : "Nothing to clear.";
        await RefreshRepoConfigAsync().ConfigureAwait(true);
        return cleared;
    }

    /// <summary>Capabilities to seed the shared CapabilitiesEditor with for the current edit.</summary>
    public LaunchCapabilities? CapabilitiesForEditor() =>
        Selected?.Capabilities ?? _settings.Current.DefaultCapabilities;

    // ---- bulk import from past sessions ----

    private readonly List<ProjectImportRow> _allImportRows = new();
    private bool _hasLoadedImportCandidates;
    private bool _isUpdatingImportRows;
    private int _hiddenImportPathCount;

    [ObservableProperty] private string _importMessage = string.Empty;

    /// <summary>True while past sessions are being scanned on a background thread.</summary>
    [ObservableProperty] private bool _isScanningSessions;

    partial void OnIsScanningSessionsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunImport));
        OnPropertyChanged(nameof(CanToggleImportPathVisibility));
    }

    /// <summary>View-lifetime filter only. Revealing hidden paths never selects them.</summary>
    [ObservableProperty] private bool _showHiddenImportPaths;

    partial void OnShowHiddenImportPathsChanged(bool value)
    {
        ApplyImportVisibility();
        if (_hasLoadedImportCandidates && !IsScanningSessions) UpdateImportSummary();
    }

    /// <summary>Gates the dialog's Import / Select all buttons while the scan is in flight.</summary>
    public bool CanRunImport => !IsScanningSessions;

    public bool CanToggleImportPathVisibility =>
        !IsScanningSessions && ImportCandidates.Any(r => r.IsSelected && r.CanSelect);

    public bool HasImportCandidates => ImportCandidates.Count > 0;

    public int SelectedImportCount => ImportCandidates.Count(r => r.IsSelected && r.CanImport);

    /// <summary>Test seam: the user-profile root used to skip the home directory and to
    /// classify install/tooling folders. Null = the real profile. Tests set this because
    /// their temp folders live under %LOCALAPPDATA%\Temp, which is legitimately a tooling
    /// path in production.</summary>
    internal string? ImportHomeRootOverride { get; set; }

    /// <summary>
    /// Scan past sessions for working directories that could become projects. Groups by git
    /// root where one is known, skips the user-profile root, and pre-ticks everything that
    /// isn't already a project, missing from disk, or an install/tooling folder.
    /// </summary>
    /// <remarks>
    /// The scan runs on a background thread: <see cref="ISessionDiscoveryService.Enumerate"/>
    /// walks every session folder recursively to total its size, which is far too much disk
    /// I/O to do on the UI thread.
    /// </remarks>
    public async Task LoadImportCandidatesAsync(CancellationToken ct = default)
    {
        if (IsScanningSessions) return;

        foreach (var row in _allImportRows) row.PropertyChanged -= OnImportRowChanged;
        _allImportRows.Clear();
        ImportCandidates.Clear();
        _hasLoadedImportCandidates = false;
        _hiddenImportPathCount = 0;
        NotifyImportState();

        if (_sessions is null)
        {
            ImportMessage = "Session discovery is unavailable.";
            return;
        }

        IsScanningSessions = true;
        ImportMessage = "Scanning past sessions…";

        IReadOnlyList<ProjectImportCandidate> candidates;
        try
        {
            // Snapshot the store before leaving the UI thread.
            var existing = _store.All.ToList();
            var homeRoot = ImportHomeRootOverride;
            candidates = await Task.Run(
                () => ProjectImportPlanner.BuildCandidates(_sessions.Enumerate().ToList(), existing, homeRoot), ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            ImportMessage = "Scan canceled.";
            return;
        }
        catch (Exception ex)
        {
            ImportMessage = $"Could not scan sessions: {ex.Message}";
            return;
        }
        finally
        {
            IsScanningSessions = false;
        }

        foreach (var c in candidates)
        {
            var row = new ProjectImportRow
            {
                Candidate = c,
                IsSelected = c.RecommendedByDefault,
            };
            row.PropertyChanged += OnImportRowChanged;
            _allImportRows.Add(row);
        }

        _hasLoadedImportCandidates = true;
        ApplyImportVisibility();
        UpdateImportSummary();
    }

    private HashSet<string> ExistingImportPaths() => _store.All
        .Select(p => ProjectMatcher.Normalize(p?.Path))
        .OfType<string>()
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Literal folder matching, not globbing; roots cover descendants but not siblings.</summary>
    internal static bool IsImportPathHidden(string? path, IEnumerable<string>? hiddenPaths)
    {
        var target = ProjectMatcher.Normalize(path);
        if (target is null || hiddenPaths is null) return false;
        return hiddenPaths.Any(entry =>
        {
            var root = ProjectMatcher.Normalize(entry);
            if (root is null) return false;
            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });
    }

    private void ApplyImportVisibility()
    {
        var existing = ExistingImportPaths();
        var hiddenPaths = _settings.Current.ProjectImport?.HiddenPaths;
        _hiddenImportPathCount = 0;
        _isUpdatingImportRows = true;
        try
        {
            ImportCandidates.Clear();
            foreach (var row in _allImportRows)
            {
                var isHidden = IsImportPathHidden(row.Path, hiddenPaths);
                if (row.IsHidden != isHidden) row.IsSelected = false;
                row.IsHidden = isHidden;

                var path = ProjectMatcher.Normalize(row.Path);
                if (path is null || row.Candidate.AlreadyImported || existing.Contains(path))
                {
                    row.IsSelected = false;
                    continue;
                }

                if (isHidden) _hiddenImportPathCount++;
                if (isHidden && !ShowHiddenImportPaths)
                {
                    row.IsSelected = false;
                    continue;
                }

                row.IsAlternate = ImportCandidates.Count % 2 == 1;
                ImportCandidates.Add(row);
            }
        }
        finally
        {
            _isUpdatingImportRows = false;
        }
        NotifyImportState();
    }

    private void NotifyImportState()
    {
        OnPropertyChanged(nameof(HasImportCandidates));
        OnPropertyChanged(nameof(SelectedImportCount));
        OnPropertyChanged(nameof(CanToggleImportPathVisibility));
    }

    private string ImportSummary()
    {
        if (ImportCandidates.Count == 0 && _hiddenImportPathCount == 0)
            return "No session working directories found to import.";

        var summary = $"{ImportCandidates.Count(r => r.CanImport)} folder(s) available, {SelectedImportCount} selected.";
        if (_hiddenImportPathCount > 0)
            summary += $" {_hiddenImportPathCount} hidden path(s){(ShowHiddenImportPaths ? " shown" : "")}.";
        var selectedHidden = ImportCandidates.Count(r => r.IsSelected && r.CanSelect && r.IsHidden);
        if (selectedHidden > 0) summary += $" {selectedHidden} hidden path(s) selected to unhide.";
        return summary;
    }

    private void UpdateImportSummary() => ImportMessage = ImportSummary();

    private void OnImportRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isUpdatingImportRows || e.PropertyName != nameof(ProjectImportRow.IsSelected)) return;
        NotifyImportState();
        if (!IsScanningSessions) UpdateImportSummary();
    }

    public void SetAllImportSelections(bool selected)
    {
        if (IsScanningSessions || ImportCandidates.Count == 0) return;
        _isUpdatingImportRows = true;
        try
        {
            foreach (var row in ImportCandidates)
                if (row.CanSelect) row.IsSelected = selected;
        }
        finally
        {
            _isUpdatingImportRows = false;
        }
        NotifyImportState();
        UpdateImportSummary();
    }

    /// <summary>
    /// Hide selected visible paths, or unhide paths that were hidden when the action began.
    /// Unhiding a descendant removes every persisted root covering it, but leaves independent
    /// roots intact. Returns the number of selected paths whose hidden state changed.
    /// </summary>
    public int ToggleSelectedImportPaths()
    {
        if (IsScanningSessions) return 0;

        var existing = ExistingImportPaths();
        var selected = ImportCandidates.Where(r => r.IsSelected && r.CanSelect)
            .Select(r => (Row: r, Path: ProjectMatcher.Normalize(r.Path), IsOriginallyHidden: r.IsHidden))
            .Where(s => s.Path is not null && !existing.Contains(s.Path))
            .ToList();
        if (selected.Count == 0) return 0;

        var importSettings = _settings.Current.ProjectImport;
        var original = importSettings.HiddenPaths;
        var updated = original.ToList();

        // Remove covering roots using the original row states before adding any new roots:
        // mixed parent/child selections must not depend on their display order.
        updated.RemoveAll(root => selected.Any(s => s.IsOriginallyHidden && IsImportPathHidden(s.Path, new[] { root })));
        foreach (var item in selected.Where(s => !s.IsOriginallyHidden))
        {
            if (!updated.Any(root => string.Equals(ProjectMatcher.Normalize(root), item.Path, StringComparison.OrdinalIgnoreCase)))
                updated.Add(item.Path!);
        }
        if (original.SequenceEqual(updated)) return 0;

        var changed = selected.Count(s => s.IsOriginallyHidden != IsImportPathHidden(s.Path, updated));
        importSettings.HiddenPaths = updated;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            importSettings.HiddenPaths = original;
            ImportMessage = $"Could not save hidden import paths: {ex.Message}. No visibility changes were saved.";
            return 0;
        }

        _isUpdatingImportRows = true;
        try
        {
            foreach (var item in selected) item.Row.IsSelected = false;
        }
        finally
        {
            _isUpdatingImportRows = false;
        }
        ApplyImportVisibility();
        ImportMessage = $"Updated import visibility for {changed} path(s). {ImportSummary()}";
        return changed;
    }

    /// <summary>
    /// Create a project for each ticked candidate. Names come from the git repo when known.
    /// Returns the number created; skips anything already covered so a double-click can't
    /// create duplicates.
    /// </summary>
    public int ImportSelected()
    {
        if (IsScanningSessions) return 0;
        if (!_hasLoadedImportCandidates && ImportCandidates.Count == 0) return 0;

        var chosen = ImportCandidates.Where(r => r.IsSelected && r.CanImport).ToList();
        if (chosen.Count == 0)
        {
            ApplyImportVisibility();
            ImportMessage = ImportCandidates.Any(r => r.IsSelected && r.IsHidden)
                ? $"Unhide selected hidden paths before importing. {ImportSummary()}"
                : $"Nothing selected for import. {ImportSummary()}";
            return 0;
        }

        var created = 0;
        var failed = 0;
        var existing = ExistingImportPaths();
        foreach (var row in chosen)
        {
            // Re-check against the live store: the list is a snapshot.
            var path = ProjectMatcher.Normalize(row.Path);
            if (path is null || existing.Contains(path) || IsImportPathHidden(path, _settings.Current.ProjectImport?.HiddenPaths))
                continue;

            var project = new ProjectProfile
            {
                Id = Guid.NewGuid().ToString(),
                Label = row.SuggestedName,
                Path = path,
            };

            try
            {
                _store.Add(project);
                Items.Add(project);
                existing.Add(path);
                created++;
            }
            catch
            {
                failed++;
            }
        }

        if (created > 0) ApplySort();
        StatusMessage = created switch
        {
            0 => "No projects imported.",
            1 => "Imported 1 project.",
            _ => $"Imported {created} projects.",
        };
        if (failed > 0) StatusMessage += $" {failed} failed to save.";

        ApplyImportVisibility();
        ImportMessage = $"{StatusMessage} {ImportSummary()}";
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        return created;
    }
}
