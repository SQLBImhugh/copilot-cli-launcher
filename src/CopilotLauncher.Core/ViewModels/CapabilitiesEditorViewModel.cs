using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Models;

namespace CopilotLauncher.ViewModels;

/// <summary>One MCP server row with an on/off checkbox in the capability editor.</summary>
public sealed partial class McpServerToggle : ObservableObject
{
    public required string Name { get; init; }
    public string Source { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>e.g. "user" / "plugin" — shown as a subtle caption.</summary>
    public string SourceLabel => string.IsNullOrWhiteSpace(Source) ? string.Empty : Source;
}

/// <summary>
/// Shared editor state for a <see cref="LaunchCapabilities"/> selection, hosted by
/// the New Session tab, New Shortcut wizard, and Settings (defaults). Pure VM —
/// the WinUI <c>CapabilitiesEditor</c> control feeds it a <see cref="CapabilityCatalog"/>
/// (discovered via <see cref="Services.ISessionCapabilityService"/>) and reads back
/// the selection with <see cref="ToCapabilities"/>.
/// </summary>
public sealed partial class CapabilitiesEditorViewModel : ObservableObject
{
    public ObservableCollection<McpServerToggle> McpServers { get; } = new();
    public ObservableCollection<string> AgentOptions { get; } = new();
    public ObservableCollection<SkillInfo> Skills { get; } = new();

    /// <summary>Raised whenever any selection changes — hosts use it to refresh the command preview.</summary>
    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    [ObservableProperty] private bool _disableBuiltinGitHubMcp;
    [ObservableProperty] private string _selectedAgent = string.Empty;
    [ObservableProperty] private string _toolsText = string.Empty;
    [ObservableProperty] private bool _disableAllSkills;

    // Tool mode is exposed as three bools so a RadioButton group can bind to each.
    [ObservableProperty] private bool _toolModeNone = true;
    [ObservableProperty] private bool _toolModeOnly;
    [ObservableProperty] private bool _toolModeExclude;

    public bool HasMcpServers => McpServers.Count > 0;
    public bool HasNoMcpServers => McpServers.Count == 0;
    public bool HasAgents => AgentOptions.Count > 0;
    public int SkillCount => Skills.Count;
    public bool ToolListEnabled => ToolModeOnly || ToolModeExclude;

    public string SkillSummary => SkillCount == 0
        ? "No skills detected."
        : $"{SkillCount} skill(s) available. Skills are all-or-nothing per session.";

    partial void OnDisableBuiltinGitHubMcpChanged(bool value) => RaiseChanged();
    partial void OnSelectedAgentChanged(string value) => RaiseChanged();
    partial void OnToolsTextChanged(string value) => RaiseChanged();
    partial void OnDisableAllSkillsChanged(bool value) => RaiseChanged();

    partial void OnToolModeNoneChanged(bool value) { OnPropertyChanged(nameof(ToolListEnabled)); if (value) RaiseChanged(); }
    partial void OnToolModeOnlyChanged(bool value) { OnPropertyChanged(nameof(ToolListEnabled)); if (value) RaiseChanged(); }
    partial void OnToolModeExcludeChanged(bool value) { OnPropertyChanged(nameof(ToolListEnabled)); if (value) RaiseChanged(); }

    /// <summary>Current tool-filter mode from the three radio bools.</summary>
    public ToolFilterMode CurrentToolMode =>
        ToolModeOnly ? ToolFilterMode.OnlyThese
        : ToolModeExclude ? ToolFilterMode.ExcludeThese
        : ToolFilterMode.None;

    /// <summary>Replace the catalog (MCP/agents/skills) and seed the form from an existing selection.</summary>
    public void LoadCatalog(CapabilityCatalog catalog, LaunchCapabilities? existing)
    {
        var disabled = new HashSet<string>(
            existing?.DisabledMcpServers ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var t in McpServers) t.PropertyChanged -= OnToggleChanged;
        McpServers.Clear();
        foreach (var s in catalog.McpServers)
        {
            var toggle = new McpServerToggle
            {
                Name = s.Name,
                Source = s.Source,
                IsEnabled = !disabled.Contains(s.Name),
            };
            toggle.PropertyChanged += OnToggleChanged;
            McpServers.Add(toggle);
        }

        AgentOptions.Clear();
        foreach (var a in catalog.Agents) AgentOptions.Add(a);

        Skills.Clear();
        foreach (var sk in catalog.Skills) Skills.Add(sk);

        DisableBuiltinGitHubMcp = existing?.DisableBuiltinMcps ?? false;
        SelectedAgent = existing?.Agent ?? string.Empty;
        ToolsText = existing is { Tools.Count: > 0 } ? string.Join(Environment.NewLine, existing.Tools) : string.Empty;
        DisableAllSkills = existing?.DisableAllSkills ?? false;
        SetToolMode(existing?.ToolMode ?? ToolFilterMode.None);

        OnPropertyChanged(nameof(HasMcpServers));
        OnPropertyChanged(nameof(HasNoMcpServers));
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(SkillCount));
        OnPropertyChanged(nameof(SkillSummary));
        RaiseChanged();
    }

    /// <summary>Read the current selection as a model. Returns null when nothing is selected.</summary>
    public LaunchCapabilities? ToCapabilities()
    {
        var caps = new LaunchCapabilities
        {
            DisabledMcpServers = McpServers.Where(m => !m.IsEnabled).Select(m => m.Name).ToList(),
            DisableBuiltinMcps = DisableBuiltinGitHubMcp,
            Agent = string.IsNullOrWhiteSpace(SelectedAgent) ? null : SelectedAgent.Trim(),
            ToolMode = CurrentToolMode,
            Tools = ParseTools(ToolsText),
            DisableAllSkills = DisableAllSkills,
        };
        return caps.IsEmpty ? null : caps;
    }

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(McpServerToggle.IsEnabled)) RaiseChanged();
    }

    private void SetToolMode(ToolFilterMode mode)
    {
        ToolModeOnly = mode == ToolFilterMode.OnlyThese;
        ToolModeExclude = mode == ToolFilterMode.ExcludeThese;
        ToolModeNone = mode == ToolFilterMode.None;
        OnPropertyChanged(nameof(ToolListEnabled));
    }

    /// <summary>Split a tool list textbox into tool names (one per line or comma-separated).
    /// Whitespace is NOT a delimiter so specs like <c>shell(git push)</c> stay intact.</summary>
    internal static List<string> ParseTools(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
