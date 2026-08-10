using System.Diagnostics;
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
        var contextSvc = App.Services.GetRequiredService<IBriefingContextService>();
        var contextPath = contextSvc.ResolvePath();
        var originalContext = await contextSvc.ReadAsync();

        var current = _settings.Current.Briefings.PromptInstructions;

        // --- Pane 1: the ask (settings-stored) ---
        var instructionsBox = NewEditor(
            string.IsNullOrWhiteSpace(current) ? AISummaryPromptBuilder.DefaultInstructions : current,
            AISummaryPromptBuilder.InstructionsLimit,
            "BriefingInstructionsTextBox");

        // --- Pane 2: the project context (AGENTS.md file) ---
        var contextBox = NewEditor(originalContext, 0, "BriefingContextTextBox");
        contextBox.Visibility = Visibility.Collapsed;

        var help = NewCaption(
            "The ask sent to Copilot. Use {from} and {to} for the version range. "
            + "The changelog and your project context are appended automatically — don't add them here.");
        var contextHelp = NewCaption(
            $"Background about your project, appended to every briefing as \"Repository context\". "
            + $"This is what makes briefings say things like \"Highlights for <your project>\".\n{contextPath}");
        contextHelp.Visibility = Visibility.Collapsed;

        var openFileButton = new Button
        {
            Content = "Open in editor…",
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        openFileButton.Click += (_, _) => OpenContextFileExternally(contextPath, contextBox.Text);

        var selector = new SelectorBar { Margin = new Thickness(0, 0, 0, 8) };
        var instructionsItem = new SelectorBarItem { Text = "Instructions" };
        var contextItem = new SelectorBarItem { Text = "Project context" };
        selector.Items.Add(instructionsItem);
        selector.Items.Add(contextItem);
        selector.SelectedItem = instructionsItem;

        var resetHint = NewCaption("“Reset to default” applies to the Instructions pane only.");
        resetHint.Margin = new Thickness(0, 8, 0, 0);

        selector.SelectionChanged += (s, _) =>
        {
            var onInstructions = s.SelectedItem == instructionsItem;
            instructionsBox.Visibility = onInstructions ? Visibility.Visible : Visibility.Collapsed;
            help.Visibility = onInstructions ? Visibility.Visible : Visibility.Collapsed;
            resetHint.Visibility = onInstructions ? Visibility.Visible : Visibility.Collapsed;
            contextBox.Visibility = onInstructions ? Visibility.Collapsed : Visibility.Visible;
            contextHelp.Visibility = onInstructions ? Visibility.Collapsed : Visibility.Visible;
            openFileButton.Visibility = onInstructions ? Visibility.Collapsed : Visibility.Visible;
        };

        // No fixed width: let the dialog size to its max and the editors stretch.
        // A hardcoded width wider than ContentDialogMaxWidth clips the content.
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(selector);
        panel.Children.Add(help);
        panel.Children.Add(contextHelp);
        panel.Children.Add(instructionsBox);
        panel.Children.Add(contextBox);
        panel.Children.Add(openFileButton);
        panel.Children.Add(resetHint);

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
        // Widen beyond the ~548px default so long prompt lines aren't cramped.
        dialog.Resources["ContentDialogMaxWidth"] = 1100.0;

        // Reset repopulates the instructions box in place instead of closing, so
        // the user can review (and still cancel) before committing. Never touches
        // the project-context file, which is hand-authored.
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            selector.SelectedItem = instructionsItem;
            instructionsBox.Text = AISummaryPromptBuilder.DefaultInstructions;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // 1) Instructions -> settings. Persist null when blank or unchanged from
        // the default, so settings.json stays clean and future default
        // improvements still reach the user.
        var text = instructionsBox.Text?.Trim() ?? string.Empty;
        var isDefault = string.Equals(text, AISummaryPromptBuilder.DefaultInstructions.Trim(), StringComparison.Ordinal);
        _settings.Current.Briefings.PromptInstructions = (text.Length == 0 || isDefault) ? null : text;

        var messages = new List<string>();
        try
        {
            _settings.Save();
            messages.Add(_settings.Current.Briefings.PromptInstructions is null
                ? "instructions reset to default"
                : "custom instructions saved");
        }
        catch (Exception ex)
        {
            messages.Add($"could not save instructions ({ex.Message})");
        }

        // 2) Project context -> file, only when actually edited.
        if (!string.Equals(contextBox.Text, originalContext, StringComparison.Ordinal))
        {
            var updated = contextBox.Text ?? string.Empty;
            if (BriefingContextService.IsSuspiciousShrink(originalContext, updated)
                && !await ConfirmContextShrinkAsync(originalContext.Length, updated.Length))
            {
                messages.Add("project context left unchanged");
            }
            else
            {
                try
                {
                    await contextSvc.WriteAsync(updated);
                    messages.Add("project context saved");
                }
                catch (Exception ex)
                {
                    messages.Add($"could not save project context ({ex.Message})");
                }
            }
        }

        ViewModel.NoteBriefingStatus(
            string.Join("; ", messages) + " — applied on the next Generate AI Briefing.");
    }

    private static TextBox NewEditor(string text, int maxLength, string automationId)
    {
        // ORDER IS LOAD-BEARING: a TextBox with AcceptsReturn=false (the
        // default) silently truncates assigned text at the first newline.
        // Setting Text inside the object initializer — i.e. before
        // AcceptsReturn=true — therefore dropped every line but the first, and
        // saving wrote that truncated value back over the user's file.
        // Configure the multi-line behavior FIRST, then assign Text.
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 320,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // Explicit family rather than a ThemeResource lookup: the mono font
            // resource isn't guaranteed to exist under every theme.
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        if (maxLength > 0) box.MaxLength = maxLength;
        box.Text = text ?? string.Empty;
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    private static TextBlock NewCaption(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8),
        };
        // Defensive lookup: an indexer miss throws, which would take down the
        // whole dialog just to style a caption.
        if (Application.Current.Resources.TryGetValue("CaptionTextBlockStyle", out var captionStyle)
            && captionStyle is Style s)
        {
            block.Style = s;
        }
        return block;
    }

    /// <summary>Guard against a save that would wipe most of a substantial,
    /// hand-authored context file. Defaults to Cancel.</summary>
    private async System.Threading.Tasks.Task<bool> ConfirmContextShrinkAsync(int beforeChars, int afterChars)
    {
        var dialog = new ContentDialog
        {
            Title = "Save a much smaller project context?",
            Content = $"This would replace {beforeChars:N0} characters with {afterChars:N0} — "
                    + "most of the file would be removed.\n\n"
                    + "If you didn't intend that, choose Cancel. A timestamped backup is kept either way.",
            PrimaryButtonText = "Save anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Hand the context file to the user's default editor. Saves the
    /// in-dialog text first so they don't edit a stale copy.</summary>
    private async void OpenContextFileExternally(string path, string pendingText)
    {
        try
        {
            var svc = App.Services.GetRequiredService<IBriefingContextService>();
            // Same shrink guard as the Save path — never let a truncated editor
            // value reach disk just because the user clicked "Open in editor".
            var onDisk = await svc.ReadAsync();
            if (!BriefingContextService.IsSuspiciousShrink(onDisk, pendingText ?? string.Empty))
            {
                await svc.WriteAsync(pendingText ?? string.Empty);
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            ViewModel.NoteBriefingStatus($"Opened {path} — reopen this dialog after saving to see your changes.");
        }
        catch (Exception ex)
        {
            ViewModel.NoteBriefingStatus($"Could not open the context file: {ex.Message}");
        }
    }
}
