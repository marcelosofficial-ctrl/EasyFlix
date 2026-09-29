using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App;

public sealed record PlaybackHistoryItem(
    CatalogTitleSummary Title,
    PlaybackProgressEntry Progress)
{
    public string ContextText
        => string.IsNullOrWhiteSpace(Progress.ContextLabel)
            ? Title.KindLabel
            : Progress.ContextLabel;

    public string ProgressText
    {
        get
        {
            if (PlaybackProgressPolicy.IsCompleted(Progress.PositionSeconds, Progress.DurationSeconds))
                return "Watched";

            if (Progress.DurationSeconds is > 0)
            {
                var percent = Math.Clamp(
                    Progress.PositionSeconds / Progress.DurationSeconds.Value * 100,
                    0,
                    100);
                return $"{percent:0}% watched";
            }

            return $"Resume at {FormatPosition(Progress.PositionSeconds)}";
        }
    }

    public string UpdatedText => Progress.UpdatedUtc.ToLocalTime().ToString("g");

    private static string FormatPosition(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }
}

public partial class PlaybackHistoryWindow : Window
{
    private readonly CatalogStore _store;
    private readonly ObservableCollection<PlaybackHistoryItem> _items = [];
    private readonly List<PlaybackHistoryItem> _allItems = [];

    public PlaybackHistoryWindow(CatalogStore store)
    {
        InitializeComponent();
        _store = store;
        HistoryItems.ItemsSource = _items;
        HistoryFilter.SelectedIndex = 0;
        Loaded += PlaybackHistoryWindow_Loaded;
    }

    private async void PlaybackHistoryWindow_Loaded(object sender, RoutedEventArgs e)
        => await ReloadAsync();

    private async Task ReloadAsync()
    {
        try
        {
            var titles = await _store.LoadTitleSummariesAsync();
            var titlesById = titles.ToDictionary(
                title => title.Id,
                StringComparer.OrdinalIgnoreCase);
            var progress = await _store.LoadPlaybackHistoryAsync(100);

            _allItems.Clear();
            foreach (var entry in progress)
            {
                if (titlesById.TryGetValue(entry.TitleId, out var title))
                    _allItems.Add(new PlaybackHistoryItem(title, entry));
            }

            ApplyFilter();
        }
        catch (Exception ex)
        {
            HistoryStatus.Text = "Could not load watch history: " + ex.Message;
        }
    }

    private void HistoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = (HistoryFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        IEnumerable<PlaybackHistoryItem> filtered = filter switch
        {
            "InProgress" => _allItems.Where(item => PlaybackProgressPolicy.IsContinueWatching(
                item.Progress.PositionSeconds,
                item.Progress.DurationSeconds)),
            "Watched" => _allItems.Where(item => PlaybackProgressPolicy.IsCompleted(
                item.Progress.PositionSeconds,
                item.Progress.DurationSeconds)),
            _ => _allItems
        };

        _items.Clear();
        foreach (var item in filtered)
            _items.Add(item);

        HistoryStatus.Text = _allItems.Count == 0
            ? "No meaningful playback history yet."
            : filter == "All"
                ? $"{_allItems.Count} history item(s)."
                : $"{_items.Count} of {_allItems.Count} history item(s).";
    }

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count == 0)
            return;

        var choice = MessageBox.Show(
            this,
            "Clear all saved watch history and playback progress?\n\nThis will remove Continue Watching and Up Next until new playback is recorded.",
            "Clear all watch history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (choice != MessageBoxResult.Yes)
            return;

        try
        {
            var removed = await _store.DeleteAllPlaybackProgressAsync();
            await ReloadAsync();
            HistoryStatus.Text = $"Cleared {removed} saved progress item(s).";
        }
        catch (Exception ex)
        {
            HistoryStatus.Text = "Could not clear watch history: " + ex.Message;
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PlaybackHistoryItem item })
            return;

        var resumable = PlaybackProgressPolicy.IsContinueWatching(
            item.Progress.PositionSeconds,
            item.Progress.DurationSeconds);
        double? resumePosition = resumable ? item.Progress.PositionSeconds : null;
        double? resumeDuration = resumable ? item.Progress.DurationSeconds : null;

        new TitleDetailsWindow(
            _store,
            item.Title.Id,
            item.Progress.MediaFileId,
            resumePosition,
            resumeDuration)
        {
            Owner = this
        }.ShowDialog();

        await ReloadAsync();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PlaybackHistoryItem item })
            return;

        try
        {
            await _store.DeletePlaybackProgressAsync(item.Progress.MediaFileId);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            HistoryStatus.Text = "Could not clear saved progress: " + ex.Message;
        }
    }
}
