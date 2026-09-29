using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using EasyFlix.App.Services;
using EasyFlix.App.ViewModels;
using EasyFlix.Core.Services;

namespace EasyFlix.App;

public partial class TitleDetailsWindow : Window
{
    private readonly CatalogStore _store;
    private readonly TitleDetailsViewModel _viewModel;
    private readonly string _titleId;
    private string? _resumeMediaFileId;
    private double? _resumePositionSeconds;
    private double? _resumeDurationSeconds;
    private IPlaybackLauncher _playbackLauncher;
    private bool _resumeConsumed;
    private bool _isFavorite;
    private Task? _loadTask;
    private Window? _embeddedOwner;

    public TitleDetailsWindow(
        CatalogStore store,
        string titleId,
        string? resumeMediaFileId = null,
        double? resumePositionSeconds = null,
        double? resumeDurationSeconds = null)
    {
        InitializeComponent();
        FitToWorkingArea();
        _store = store;
        _titleId = titleId;
        _resumeMediaFileId = resumeMediaFileId;
        _resumePositionSeconds = resumePositionSeconds;
        _resumeDurationSeconds = resumeDurationSeconds;
        _playbackLauncher = CreatePlaybackLauncher();
        _viewModel = new TitleDetailsViewModel(store, titleId, _playbackLauncher);
        DataContext = _viewModel;
        Loaded += TitleDetailsWindow_Loaded;
    }

    public async Task<FrameworkElement> PrepareEmbeddedContentAsync(Window owner)
    {
        _embeddedOwner = owner;
        await EnsureLoadedAsync();

        if (Content is not FrameworkElement content)
            throw new InvalidOperationException("Title details content could not be prepared for the EasyFlix shell.");

        Content = null;
        content.DataContext = _viewModel;
        return content;
    }

    private void FitToWorkingArea()
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(640, workArea.Width - 48);
        MaxHeight = Math.Max(520, workArea.Height - 48);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    private IPlaybackLauncher CreatePlaybackLauncher()
    {
        var settings = new PlaybackSettingsService().Load();

        if (!string.IsNullOrWhiteSpace(settings.MpvPath) && File.Exists(settings.MpvPath))
            return new MpvPlaybackLauncher(_store, settings.MpvPath);

        var vlcPath = !string.IsNullOrWhiteSpace(settings.VlcPath) && File.Exists(settings.VlcPath)
            ? settings.VlcPath
            : PlaybackSettingsService.FindVlcExecutable();

        return !string.IsNullOrWhiteSpace(vlcPath) && File.Exists(vlcPath)
            ? new VlcPlaybackLauncher(_store, vlcPath)
            : new WindowsDefaultPlaybackLauncher();
    }

    private async void TitleDetailsWindow_Loaded(object sender, RoutedEventArgs e)
        => await EnsureLoadedAsync();

    private Task EnsureLoadedAsync()
        => _loadTask ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        await _viewModel.LoadAsync();
        await RefreshFavoriteStateAsync();
        await DiscoverResumeContextAsync();

        if (!string.IsNullOrWhiteSpace(_resumeMediaFileId) &&
            !_viewModel.SelectEntryByMediaFileId(_resumeMediaFileId))
            _viewModel.SetPlaybackStatus("The saved Continue Watching file is no longer available for this title.");

        if (_viewModel.SelectedEntry is not null)
            await _viewModel.InspectSelectedAsync();

        UpdateResumeOffer();
    }

    private async Task DiscoverResumeContextAsync()
    {
        if (!string.IsNullOrWhiteSpace(_resumeMediaFileId)) return;

        var progress = await _store.LoadLatestResumableProgressForTitleAsync(_titleId);
        if (progress is null) return;

        _resumeMediaFileId = progress.MediaFileId;
        _resumePositionSeconds = progress.PositionSeconds;
        _resumeDurationSeconds = progress.DurationSeconds;
    }

    private async void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await _viewModel.InspectSelectedAsync();
        UpdateResumeOffer();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
        => await LaunchSelectedAsync(useSavedResume: true);

    private async void StartOver_Click(object sender, RoutedEventArgs e)
    {
        if (!await ClearSelectedProgressAsync("Starting from the beginning")) return;
        await LaunchSelectedAsync(useSavedResume: false);
    }

    private async Task RefreshFavoriteStateAsync()
    {
        try
        {
            _isFavorite = await _store.IsTitleFavoriteAsync(_titleId);
            FavoriteButton.Content = _isFavorite ? "Remove from Favorites" : "Add to Favorites";
        }
        catch (Exception ex)
        {
            _viewModel.SetPlaybackStatus("Could not read favorite state: " + ex.Message);
        }
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = !_isFavorite;
            await _store.SetTitleFavoriteAsync(_titleId, next);
            _isFavorite = next;
            FavoriteButton.Content = _isFavorite ? "Remove from Favorites" : "Add to Favorites";
            _viewModel.SetPlaybackStatus(_isFavorite ? "Added to Favorites." : "Removed from Favorites.");
        }
        catch (Exception ex)
        {
            _viewModel.SetPlaybackStatus("Could not update favorite state: " + ex.Message);
        }
    }

    private async void MarkWatched_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedEntry?.File is not { IsPlayable: true } file)
            return;

        try
        {
            if (!await CatalogMediaProbeService.IsAvailableAsync())
            {
                _viewModel.SetPlaybackStatus("FFprobe is required to mark a file watched without playing it.");
                return;
            }

            var duration = await CatalogMediaProbeService.DurationAsync(file.Path);
            if (duration is null || duration.Value.TotalSeconds <= 0)
            {
                _viewModel.SetPlaybackStatus("Could not determine this file's duration, so watch state was not changed.");
                return;
            }

            var durationSeconds = duration.Value.TotalSeconds;
            await _store.SavePlaybackProgressAsync(file.Id, durationSeconds, durationSeconds);

            if (string.Equals(file.Id, _resumeMediaFileId, StringComparison.OrdinalIgnoreCase))
            {
                _resumeConsumed = true;
                _resumeMediaFileId = null;
                _resumePositionSeconds = null;
                _resumeDurationSeconds = null;
                _viewModel.SetResumeOffer(null);
            }

            _viewModel.SetPlaybackStatus("Marked watched for this file.");
        }
        catch (Exception ex)
        {
            _viewModel.SetPlaybackStatus("Could not mark watched: " + ex.Message);
        }
    }

    private async void MarkUnwatched_Click(object sender, RoutedEventArgs e)
        => await ClearSelectedProgressAsync("Marked unwatched");

    private async Task<bool> ClearSelectedProgressAsync(string successMessage)
    {
        if (_viewModel.SelectedEntry?.File is not { } file) return false;

        try
        {
            await _store.DeletePlaybackProgressAsync(file.Id);

            if (string.Equals(file.Id, _resumeMediaFileId, StringComparison.OrdinalIgnoreCase))
            {
                _resumeConsumed = true;
                _resumeMediaFileId = null;
                _resumePositionSeconds = null;
                _resumeDurationSeconds = null;
                _viewModel.SetResumeOffer(null);
            }

            _viewModel.SetPlaybackStatus(successMessage + " for this file.");
            return true;
        }
        catch (Exception ex)
        {
            _viewModel.SetPlaybackStatus("Could not clear saved progress: " + ex.Message);
            return false;
        }
    }

    private async Task LaunchSelectedAsync(bool useSavedResume)
    {
        if (_viewModel.SelectedEntry?.File is not { IsPlayable: true } file) return;

        _playbackLauncher = CreatePlaybackLauncher();
        _viewModel.SetPlaybackLauncher(_playbackLauncher);

        double? resumePosition = null;
        double? resumeDuration = null;
        if (useSavedResume &&
            !_resumeConsumed &&
            string.Equals(file.Id, _resumeMediaFileId, StringComparison.OrdinalIgnoreCase) &&
            _resumePositionSeconds is { } savedPosition &&
            PlaybackProgressPolicy.IsContinueWatching(savedPosition, _resumeDurationSeconds))
        {
            resumePosition = savedPosition;
            resumeDuration = _resumeDurationSeconds;
        }

        var result = await _playbackLauncher.LaunchAsync(
            file.Path,
            file.Id,
            _viewModel.BuildPlaybackSelection(resumePosition, resumeDuration));

        if (result.Started && resumePosition is not null)
        {
            _resumeConsumed = true;
            _viewModel.SetResumeOffer(null);
        }

        _viewModel.SetPlaybackStatus(result.Message);
    }

    private void UpdateResumeOffer()
    {
        if (!_resumeConsumed &&
            _viewModel.SelectedEntry?.File is { } file &&
            string.Equals(file.Id, _resumeMediaFileId, StringComparison.OrdinalIgnoreCase) &&
            _resumePositionSeconds is { } savedPosition &&
            PlaybackProgressPolicy.IsContinueWatching(savedPosition, _resumeDurationSeconds))
        {
            _viewModel.SetResumeOffer(savedPosition);
            return;
        }

        _viewModel.SetResumeOffer(null);
    }

    private void PlaybackSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PlaybackSettingsWindow { Owner = _embeddedOwner ?? this };
        if (dialog.ShowDialog() == true)
        {
            _playbackLauncher = CreatePlaybackLauncher();
            _viewModel.SetPlaybackLauncher(_playbackLauncher);
            _viewModel.SetPlaybackStatus($"Playback profile: {_playbackLauncher.Name}.");
        }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var file = _viewModel.SelectedEntry?.File;
        if (file is null)
        {
            _viewModel.SetPlaybackStatus("Select a file or episode first.");
            return;
        }

        if (!File.Exists(file.Path))
        {
            _viewModel.SetPlaybackStatus(
                "This file is currently unavailable. Reconnect its drive or restore the file, then refresh EasyFlix.");
            return;
        }

        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(file.Path);
        Process.Start(start);
    }
}
