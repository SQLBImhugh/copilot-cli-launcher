using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CopilotLauncher.Helpers;
using CopilotLauncher.Models;

namespace CopilotLauncher.ViewModels;

public sealed partial class CopilotArgRowViewModel : ObservableObject
{
    public CopilotArgRowViewModel(CopilotArgSpec spec)
    {
        Spec = spec;
    }

    public CopilotArgSpec Spec { get; }
    public string Flag => Spec.Flag;
    public string Description => Spec.Description;
    public IReadOnlyList<string> Choices => Spec.Choices;
    public string Placeholder => Spec.Placeholder;
    public bool HasValue => Spec.Kind != CopilotArgValueKind.Switch;
    public bool IsRepeatable => Spec.Repeatable;
    public bool UsesEditableComboBox => HasValue && Spec.Kind != CopilotArgValueKind.Choice && Choices.Count > 0;
    public bool UsesChoiceComboBox => Spec.Kind == CopilotArgValueKind.Choice;
    public bool UsesTextBox => HasValue && !UsesChoiceComboBox && !UsesEditableComboBox;
    public bool ValueControlEnabled => IsEnabled && HasValue;
    public string RepeatableHint => IsRepeatable ? "One value per line." : string.Empty;

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _value = string.Empty;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(ValueControlEnabled));
}

public sealed class CopilotArgCategoryGroup
{
    public required string Category { get; init; }
    public ObservableCollection<CopilotArgRowViewModel> Rows { get; } = new();
}

public sealed partial class CopilotArgsEditorViewModel : ObservableObject
{
    public ObservableCollection<CopilotArgRowViewModel> Rows { get; } = new();
    public ObservableCollection<CopilotArgCategoryGroup> Groups { get; } = new();

    public event EventHandler? Changed;

    [ObservableProperty] private string _unrecognizedText = string.Empty;

    public int EnabledCount => Rows.Count(r => r.IsEnabled);
    public string EnabledSummary => EnabledCount == 0 ? "No advanced args enabled" : $"{EnabledCount} advanced arg(s) enabled";

    public CopilotArgsEditorViewModel()
    {
        foreach (var category in CopilotArgCatalog.All.GroupBy(s => s.Category))
        {
            var group = new CopilotArgCategoryGroup { Category = category.Key };
            Groups.Add(group);
            foreach (var spec in category)
            {
                var row = new CopilotArgRowViewModel(spec);
                row.PropertyChanged += OnRowChanged;
                Rows.Add(row);
                group.Rows.Add(row);
            }
        }
    }

    public void LoadFrom(string? argsText)
    {
        var argSet = CopilotArgSet.Parse(argsText);
        foreach (var row in Rows)
        {
            var values = argSet.GetValues(row.Flag);
            row.IsEnabled = argSet.IsEnabled(row.Flag);
            row.Value = row.IsRepeatable ? string.Join(Environment.NewLine, values.Where(v => v.Length > 0)) : values.FirstOrDefault() ?? string.Empty;
        }

        UnrecognizedText = argSet.UnrecognizedText;
        RaiseChanged();
    }

    public string ToArgString()
    {
        var argSet = new CopilotArgSet { UnrecognizedText = UnrecognizedText };
        foreach (var row in Rows.Where(r => r.IsEnabled))
        {
            if (!row.HasValue)
            {
                argSet.SetSwitch(row.Flag, true);
                continue;
            }

            var values = row.IsRepeatable
                ? row.Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : new[] { row.Value.Trim() };
            argSet.SetValues(row.Flag, values);
        }
        return argSet.Format();
    }

    partial void OnUnrecognizedTextChanged(string value) => RaiseChanged();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CopilotArgRowViewModel.IsEnabled) or nameof(CopilotArgRowViewModel.Value))
        {
            OnPropertyChanged(nameof(EnabledCount));
            OnPropertyChanged(nameof(EnabledSummary));
            RaiseChanged();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
