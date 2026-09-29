using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyFlix.Core.Models;

public sealed class MediaItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private MediaKind _kind;
    private string _suggestedName = string.Empty;
    private string _subtitleStatus = "Not audited";
    private string _artworkStatus = "Not searched";
    private string? _artworkPath;

    public required string SourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string OriginalName { get; init; }
    public string? Year { get; init; }
    public required string Quality { get; init; }
    public bool IsFolder { get; init; }
    public bool IsRemovable { get; init; }
    public int VideoCount { get; init; }
    public string SeriesTitle { get; init; } = string.Empty;
    public string SeasonLabel { get; init; } = string.Empty;
    public string LibrarySection => Kind switch
    {
        MediaKind.TvShow => "TV Shows",
        MediaKind.Anime => "Anime Series",
        _ => "Movies"
    };
    public string KindLabel => Kind switch
    {
        MediaKind.TvShow => "TV Show",
        MediaKind.Anime => "Anime",
        _ => "Movie"
    };
    public string SeriesGroup => Kind == MediaKind.Movie ? string.Empty : SeriesTitle;
    public string SeasonGroup => Kind == MediaKind.Movie ? string.Empty : SeasonLabel;
    public string SubtitleStatus { get => _subtitleStatus; set => SetField(ref _subtitleStatus, value); }
    public string ArtworkStatus { get => _artworkStatus; set => SetField(ref _artworkStatus, value); }
    public string? ArtworkPath { get => _artworkPath; set => SetField(ref _artworkPath, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public MediaKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LibrarySection));
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(SeriesGroup));
            OnPropertyChanged(nameof(SeasonGroup));
        }
    }

    public string SuggestedName
    {
        get => _suggestedName;
        set => SetField(ref _suggestedName, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }
}
