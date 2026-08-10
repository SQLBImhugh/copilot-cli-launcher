using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CopilotLauncher.Models;
using CopilotLauncher.Services;
using CopilotLauncher.ViewModels;

namespace CopilotLauncher.Pages;

public sealed partial class ChangelogPage : Page
{
    public ChangelogPageViewModel ViewModel { get; }
    private readonly ISettingsService _settings;

    public ChangelogPage()
    {
        _settings = App.Services.GetRequiredService<ISettingsService>();
        ViewModel = new ChangelogPageViewModel(
            App.Services.GetRequiredService<IChangelogHistoryService>(),
            App.Services.GetRequiredService<IBriefingHistoryService>(),
            App.Services.GetRequiredService<IUpdateCheckService>(),
            App.Services.GetRequiredService<IBriefingService>(),
            _settings,
            App.Services.GetRequiredService<IReleaseNotesService>(),
            App.Services.GetRequiredService<IAISummaryService>());
        InitializeComponent();
        Loaded += OnPageLoaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Reload();
        ApplySelectedSubView();
        RefreshLatestChangelogCard();
        _ = DispatchBackfillAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangelogPageViewModel.SelectedView))
            ApplySelectedSubView();
    }

    // ---------- Sub-view selector wiring ----------

    private void OnSubViewSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var selected = sender.SelectedItem;
        if (selected?.Tag is not string tag) return;
        ViewModel.SelectedView = tag switch
        {
            "briefings" => ChangelogPageSubView.Briefings,
            _ => ChangelogPageSubView.Changelog,
        };
    }

    private void ApplySelectedSubView()
    {
        var showChangelog = ViewModel.SelectedView == ChangelogPageSubView.Changelog;
        ChangelogSubView.Visibility = showChangelog ? Visibility.Visible : Visibility.Collapsed;
        BriefingsSubView.Visibility = showChangelog ? Visibility.Collapsed : Visibility.Visible;
        // Keep the SelectorBar's visual state in sync when the VM updates
        // SelectedView programmatically (e.g. after Generate AI Briefing).
        var desiredItem = showChangelog ? SubViewChangelogItem : SubViewBriefingsItem;
        if (SubViewSelector.SelectedItem != desiredItem)
            SubViewSelector.SelectedItem = desiredItem;
    }

    // ---------- Latest changelog card ----------
    //
    // We render the newest ChangelogEntry as a highlighted "Latest" card
    // and put the rest in a collapsible Expander. The card lives in XAML
    // (no DataTemplate) so we just push values into named elements when
    // the underlying collection changes.

    private void RefreshLatestChangelogCard()
    {
        var latest = ViewModel.Changelogs.Count > 0 ? ViewModel.Changelogs[0] : null;
        if (latest is null)
        {
            LatestChangelogCard.Visibility = Visibility.Collapsed;
            PreviousChangelogsExpander.Visibility = Visibility.Collapsed;
            return;
        }

        LatestChangelogCard.Visibility = Visibility.Visible;
        LatestChangelogFromText.Text = latest.FromVersion;
        LatestChangelogToText.Text = latest.ToVersion;
        LatestChangelogSourceText.Text = latest.Source;
        LatestChangelogBody.Markdown = latest.Body;

        if (ViewModel.Changelogs.Count > 1)
        {
            PreviousChangelogsExpander.Visibility = Visibility.Visible;
            var previous = new System.Collections.Generic.List<ChangelogEntry>(ViewModel.Changelogs.Count - 1);
            for (var i = 1; i < ViewModel.Changelogs.Count; i++)
                previous.Add(ViewModel.Changelogs[i]);
            PreviousChangelogsList.ItemsSource = previous;
            var count = ViewModel.Changelogs.Count - 1;
            PreviousChangelogsHeader.Text = count == 1
                ? "1 previous changelog"
                : $"{count} previous changelogs";
        }
        else
        {
            PreviousChangelogsExpander.Visibility = Visibility.Collapsed;
            PreviousChangelogsList.ItemsSource = null;
        }
    }

    private async System.Threading.Tasks.Task DispatchBackfillAsync()
    {
        try { await ViewModel.BackfillMissingReleaseNotesAsync(); }
        catch { }
        RefreshLatestChangelogCard();
    }

    // ---------- Button click handlers ----------

    private async void OnCheckNowClick(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        CheckSpinner.IsActive = true;
        try
        {
            await ViewModel.CheckNowAsync();
            RefreshLatestChangelogCard();
        }
        finally
        {
            CheckSpinner.IsActive = false;
            CheckNowButton.IsEnabled = true;
        }
    }

    private async void OnGenerateBriefingClick(object sender, RoutedEventArgs e)
    {
        GenerateBriefingButton.IsEnabled = false;
        BriefingSpinner.IsActive = true;
        try
        {
            await ViewModel.GenerateAIBriefingAsync();
        }
        finally
        {
            BriefingSpinner.IsActive = false;
            GenerateBriefingButton.IsEnabled = true;
        }
    }

    private async void OnClearChangelogsClick(object sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmClearAsync(
            "Clear changelogs?",
            "This permanently removes all saved changelog history. This can't be undone.");
        if (!confirmed) return;
        ViewModel.ClearChangelogs();
        RefreshLatestChangelogCard();
    }

    private async void OnClearBriefingsClick(object sender, RoutedEventArgs e)
    {
        var confirmed = await ConfirmClearAsync(
            "Clear briefings?",
            "This permanently removes all saved AI briefing history. This can't be undone.");
        if (!confirmed) return;
        ViewModel.ClearBriefings();
    }

    private async System.Threading.Tasks.Task<bool> ConfirmClearAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            // Default to Cancel so an accidental Enter press doesn't wipe history.
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    // ---------- Briefing instructions editor ----------
    //
    // Edits the instruction block prepended to every AI briefing prompt. The
    // Changelog / Repository-context data sections are appended by
    // AISummaryPromptBuilder and are deliberately NOT editable here, so a
    // custom block can't break the data plumbing.

    private async void OnCustomizeInstructionsClick(object sender, RoutedEventArgs e)
    {
        var current = _settings.Current.Briefings.PromptInstructions;
        var editor = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(current)
                ? AISummaryPromptBuilder.DefaultInstructions
                : current,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = AISummaryPromptBuilder.InstructionsLimit,
            Height = 320,
            // Explicit family rather than a ThemeResource lookup: the mono font
            // resource isn't guaranteed to exist under every theme.
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(editor, "BriefingInstructionsTextBox");

        var help = new TextBlock
        {
            Text = "Sent to Copilot ahead of the release notes. Use {from} and {to} for the version range. "
                 + "The changelog and your agents.md context are appended automatically — don't add them here.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8),
        };
        // Defensive lookup: an indexer miss throws, which would take down the
        // whole dialog just to style a caption.
        if (Application.Current.Resources.TryGetValue("CaptionTextBlockStyle", out var captionStyle)
            && captionStyle is Style s)
        {
            help.Style = s;
        }

        var panel = new StackPanel { Width = 620 };
        panel.Children.Add(help);
        panel.Children.Add(editor);

        var dialog = new ContentDialog
        {
            Title = "Briefing instructions",
            Content = panel,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Reset to default",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        // Reset repopulates the box in place instead of closing, so the user can
        // review (and still cancel) before committing.
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            editor.Text = AISummaryPromptBuilder.DefaultInstructions;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var text = editor.Text?.Trim() ?? string.Empty;
        // Persist null when blank or unchanged from the default, so settings.json
        // stays clean and future default improvements still reach the user.
        var isDefault = string.Equals(text, AISummaryPromptBuilder.DefaultInstructions.Trim(), StringComparison.Ordinal);
        _settings.Current.Briefings.PromptInstructions = (text.Length == 0 || isDefault) ? null : text;
        try
        {
            _settings.Save();
            ViewModel.NoteBriefingStatus(_settings.Current.Briefings.PromptInstructions is null
                ? "Briefing instructions reset to the default."
                : "Custom briefing instructions saved — used on the next Generate AI Briefing.");
        }
        catch (Exception ex)
        {
            ViewModel.NoteBriefingStatus($"Could not save briefing instructions: {ex.Message}");
        }
    }
}
