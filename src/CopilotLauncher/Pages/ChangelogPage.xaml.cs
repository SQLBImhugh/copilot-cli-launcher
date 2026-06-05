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

    public ChangelogPage()
    {
        ViewModel = new ChangelogPageViewModel(
            App.Services.GetRequiredService<IChangelogHistoryService>(),
            App.Services.GetRequiredService<IBriefingHistoryService>(),
            App.Services.GetRequiredService<IUpdateCheckService>(),
            App.Services.GetRequiredService<IBriefingService>(),
            App.Services.GetRequiredService<ISettingsService>(),
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

    private void OnClearChangelogsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearChangelogs();
        RefreshLatestChangelogCard();
    }

    private void OnClearBriefingsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearBriefings();
    }
}
