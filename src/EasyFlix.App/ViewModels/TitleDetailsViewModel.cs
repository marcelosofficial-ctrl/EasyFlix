using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using EasyFlix.App.Services;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.ViewModels;

public sealed record PlayableEntry(string Label, CatalogMediaFileSummary File);
public sealed record AudioTrackChoice(AudioStreamInfo Stream, string Label);
public sealed record SubtitleTrackChoice(CatalogSubtitleStreamInfo? Track, string Label);

public sealed class TitleDetailsViewModel : INotifyPropertyChanged
{
    private readonly CatalogStore _store;
    private readonly string _titleId;
    private readonly CatalogMediaProbeService _probe = new();
    private IPlaybackLauncher _playbackLauncher;
    private CatalogTitleDetails? _details;
    private PlayableEntry? _selectedEntry;
    private AudioTrackChoice? _selectedAudio;
    private SubtitleTrackChoice? _selectedSubtitle;
    private string _statusText = "Loading details...";
    private bool _hasResumeOffer;
    private string _primaryPlayLabel = "Play";
    private string _resumeSummary = string.Empty;

    public TitleDetailsViewModel(CatalogStore store, string titleId, IPlaybackLauncher playbackLauncher)
    {
        _store = store;
        _titleId = titleId;
        _playbackLauncher = playbackLauncher;
    }

    public ObservableCollection<PlayableEntry> Entries { get; } = [];
    public ObservableCollection<AudioTrackChoice> AudioTracks { get; } = [];
    public ObservableCollection<SubtitleTrackChoice> SubtitleTracks { get; } = [];

    public string Title => _details?.Title.DisplayTitle ?? "Title";
    public string Meta => _details?.Title is { } title
        ? $"{title.KindLabel} | {title.MetaLine} | {title.AvailabilityLabel}"
        : string.Empty;
    public string Description => _details?.Title.DescriptionText ?? string.Empty;
    public string? ArtworkPath => _details?.Title.ArtworkPath;
    public string PlaybackNotice
    {
        get
        {
            var capabilities = new List<string>();
            if (_playbackLauncher.SupportsAudioSelection && _playbackLauncher.SupportsSubtitleSelection)
                capabilities.Add("verified audio/subtitle selection");
            if (_playbackLauncher.SupportsResume)
                capabilities.Add("verified resume");
            if (_playbackLauncher.SupportsProgressTracking)
                capabilities.Add("Continue Watching / history tracking");

            return capabilities.Count > 0
                ? $"Playback uses {_playbackLauncher.Name}: {string.Join(", ", capabilities)}."
                : $"Playback uses {_playbackLauncher.Name}. Playback is supported, but this profile does not expose trustworthy resume or progress telemetry to EasyFlix.";
        }
    }

    public PlayableEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (ReferenceEquals(_selectedEntry, value)) return;
            _selectedEntry = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedEntry));
            OnPropertyChanged(nameof(CanPlay));
        }
    }

    public AudioTrackChoice? SelectedAudio
    {
        get => _selectedAudio;
        set
        {
            if (Equals(_selectedAudio, value)) return;
            _selectedAudio = value;
            OnPropertyChanged();
        }
    }

    public SubtitleTrackChoice? SelectedSubtitle
    {
        get => _selectedSubtitle;
        set
        {
            if (Equals(_selectedSubtitle, value)) return;
            _selectedSubtitle = value;
            OnPropertyChanged();
        }
    }

    public bool HasSelectedEntry => SelectedEntry is not null;
    public bool CanPlay => SelectedEntry?.File.IsPlayable == true;
    public bool HasResumeOffer => _hasResumeOffer;
    public string PrimaryPlayLabel => _primaryPlayLabel;
    public string ResumeSummary => _resumeSummary;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public async Task LoadAsync()
    {
        _details = await _store.LoadTitleDetailsAsync(_titleId);
        Entries.Clear();

        if (_details is null)
        {
            StatusText = "This catalog title could not be loaded.";
            NotifyHeader();
            return;
        }

        foreach (var file in _details.MovieFiles)
            Entries.Add(new PlayableEntry($"{file.Quality} | {Path.GetFileName(file.Path)} | {file.Availability}", file));

        foreach (var season in _details.Seasons)
        {
            foreach (var episode in season.Episodes)
                Entries.Add(new PlayableEntry($"{season.Label} | {episode.EpisodeLabel} | {episode.File.Availability}", episode.File));
        }

        SelectedEntry = Entries.FirstOrDefault(entry => entry.File.IsPlayable) ?? Entries.FirstOrDefault();
        StatusText = Entries.Count == 0 ? "No media files are attached to this title." : "Select a file or episode to inspect tracks.";
        NotifyHeader();
    }

    public async Task InspectSelectedAsync()
    {
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        SelectedAudio = null;
        SelectedSubtitle = null;

        if (SelectedEntry?.File is not { } file)
        {
            StatusText = "Select a file or episode first.";
            return;
        }

        if (!file.IsPlayable)
        {
            StatusText = file.ReconnectHint;
            return;
        }

        if (!await CatalogMediaProbeService.IsAvailableAsync())
        {
            StatusText = "FFprobe is not available. Playback can still use your Windows default player.";
            return;
        }

        try
        {
            StatusText = "Inspecting audio and subtitle tracks...";
            var streams = await _probe.InspectAsync(file.Path);
            await _store.SaveStreamSnapshotAsync(file.Id, streams);

            foreach (var stream in streams.AudioStreams)
                AudioTracks.Add(new AudioTrackChoice(stream, LanguageDisplay.Audio(stream)));

            SubtitleTracks.Add(new SubtitleTrackChoice(null, "Off"));
            foreach (var track in streams.SubtitleTracks)
                SubtitleTracks.Add(new SubtitleTrackChoice(track, LanguageDisplay.Subtitle(track)));

            SelectedAudio = AudioTracks.FirstOrDefault();
            SelectedSubtitle = SubtitleTracks.FirstOrDefault();
            StatusText = $"Found {AudioTracks.Count} audio track(s) and {Math.Max(0, SubtitleTracks.Count - 1)} subtitle option(s).";
        }
        catch (Exception ex)
        {
            StatusText = "Track inspection failed: " + ex.Message;
        }
    }

    public PlaybackSelection BuildPlaybackSelection(
        double? resumePositionSeconds = null,
        double? resumeDurationSeconds = null)
        => new(
            SelectedAudio?.Stream.Index,
            SelectedSubtitle?.Track,
            SelectedSubtitle is not null,
            resumePositionSeconds,
            resumeDurationSeconds);

    public bool SelectEntryByMediaFileId(string mediaFileId)
    {
        var entry = Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.File.Id, mediaFileId, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        SelectedEntry = entry;
        return true;
    }

    public void SetPlaybackLauncher(IPlaybackLauncher playbackLauncher)
    {
        _playbackLauncher = playbackLauncher;
        OnPropertyChanged(nameof(PlaybackNotice));
    }

    public void SetPlaybackStatus(string message)
        => StatusText = message;

    public void SetResumeOffer(double? positionSeconds)
    {
        _hasResumeOffer = positionSeconds is not null;
        _primaryPlayLabel = positionSeconds is { } position
            ? $"Resume from {FormatPosition(position)}"
            : "Play";
        _resumeSummary = positionSeconds is { } resumePosition
            ? $"Continue Watching is ready at {FormatPosition(resumePosition)} for this exact file."
            : string.Empty;

        OnPropertyChanged(nameof(HasResumeOffer));
        OnPropertyChanged(nameof(PrimaryPlayLabel));
        OnPropertyChanged(nameof(ResumeSummary));
    }

    private static string FormatPosition(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }

    private void NotifyHeader()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ArtworkPath));
        OnPropertyChanged(nameof(PlaybackNotice));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
