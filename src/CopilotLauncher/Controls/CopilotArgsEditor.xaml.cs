using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Controls;

public sealed partial class CopilotArgsEditor : UserControl
{
    public CopilotArgsEditorViewModel ViewModel { get; }

    public event EventHandler? Changed;

    public CopilotArgsEditor()
    {
        ViewModel = new CopilotArgsEditorViewModel();
        ViewModel.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        InitializeComponent();
    }

    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public string ReadArgs() => ViewModel.ToArgString();

    public void Load(string? argsText) => ViewModel.LoadFrom(argsText);
}
