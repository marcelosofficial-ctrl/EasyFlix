using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using EasyFlix.App.Services;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.ViewModels;

public sealed record ContinueWatchingItem(
    CatalogTitleSummary Title,
    PlaybackProgressEntry Progress)
{
    public double ProgressPercent
        => Progress.DurationSeconds is > 0
            ? Math.Clamp(Progress.PositionSeconds / Progress.DurationSeconds.Value * 100, 0, 100)
            : 0;

    public string ContextText
        => string.IsNullOrWhiteSpace(Progress.ContextLabel)
            ? Title.KindLabel
            : Progress.ContextLabel;

    public string ProgressText
        => Progress.DurationSeconds is > 0
            ? $"{ProgressPercent:0}% watched"
            : $"Resume at {FormatPosition(Progress.PositionSeconds)}";

    private static string FormatPosition(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
    }
}

public sealed record UpNextItem(
    CatalogTitleSummary Title,
    UpNextEntry Next);

public sealed class BrowseViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CatalogStore _store = new(CatalogLocation.DatabasePath);
    private readonly LocalCatalogRefreshService _localRefresh;
    private readonly CatalogMetadataEnrichmentService _metadataEnrichment;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<CatalogTitleSummary> _allTitles = [];
    private IReadOnlySet<string> _favoriteTitleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _metadataBusy;
    private LibraryChangeMonitor? _changeMonitor;
    private IReadOnlyList<string> _monitoredRoots = [];
    private ScanOptions? _scanOptions;
    private string _searchText = string.Empty;
    private string _selectedSection = "Home";
    private string _allLibraryViewMode = "Grouped";
    private string _allLibraryTypeFilter = "All";
    private string _allLibrarySortMode = "Title A-Z";
    private string _statusText = "Preparing your library...";
    private CatalogTitleSummary? _hero;
    private IReadOnlyList<CatalogTitleSummary> _heroCandidates = [];
    private int _heroIndex;
    private bool _isBusy;

    public BrowseViewModel()
    {
        _localRefresh = new LocalCatalogRefreshService(_store);
        _metadataEnrichment = new CatalogMetadataEnrichmentService(_store);
    }

    public CatalogStore Store => _store;

    public ObservableCollection<ContinueWatchingItem> ContinueWatchingTitles { get; } = [];
    public ObservableCollection<UpNextItem> UpNextTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> HomeTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> MovieTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> TvTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> AnimeTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> OfflineTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> AllLibraryTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> AllLibraryMovies { get; } = [];
    public ObservableCollection<CatalogTitleSummary> AllLibraryTvShows { get; } = [];
    public ObservableCollection<CatalogTitleSummary> AllLibraryAnime { get; } = [];
    public ObservableCollection<CatalogTitleSummary> BrowseTitles { get; } = [];
    public ObservableCollection<CatalogTitleSummary> SearchResults { get; } = [];

    public IReadOnlyList<string> AllLibraryViewModes { get; } = ["Grouped", "Combined"];
    public IReadOnlyList<string> AllLibraryTypeFilters { get; } = ["All", "Movies", "TV Shows", "Anime"];
    public IReadOnlyList<string> AllLibrarySortModes { get; } = ["Title A-Z", "Recently updated", "Year newest"];

    public string AllLibraryViewMode
    {
        get => _allLibraryViewMode;
        set
        {
            if (_allLibraryViewMode == value || !AllLibraryViewModes.Contains(value)) return;
            _allLibraryViewMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAllLibraryGrouped));
            OnPropertyChanged(nameof(IsAllLibraryCombined));
            RebuildAllLibraryState();
        }
    }

    public string AllLibraryTypeFilter
    {
        get => _allLibraryTypeFilter;
        set
        {
            if (_allLibraryTypeFilter == value || !AllLibraryTypeFilters.Contains(value)) return;
            _allLibraryTypeFilter = value;
            OnPropertyChanged();
            RebuildAllLibraryState();
        }
    }

    public string AllLibrarySortMode
    {
        get => _allLibrarySortMode;
        set
        {
            if (_allLibrarySortMode == value || !AllLibrarySortModes.Contains(value)) return;
            _allLibrarySortMode = value;
            OnPropertyChanged();
            RebuildAllLibraryState();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            RebuildVisibleState();
        }
    }

    public string SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (_selectedSection == value) return;
            _selectedSection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SectionTitle));
            RebuildVisibleState();
        }
    }

    public string SectionTitle => SelectedSection switch
    {
        "All" => "All Library",
        "TV" => "TV Shows",
        "Offline" => "Offline and missing",
        _ => SelectedSection
    };

    public bool HasContinueWatching => ContinueWatchingTitles.Count > 0;
    public bool HasUpNext => UpNextTitles.Count > 0;
    public bool HasOfflineTitles => OfflineTitles.Count > 0;
    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);
    public bool ShowSearchEmptyState => IsSearchActive && SearchResults.Count == 0;
    public bool IsHome => !IsSearchActive && SelectedSection == "Home";
    public bool ShowBrowseNavigation => !IsHome;
    public bool IsAllLibrary => !IsSearchActive && SelectedSection == "All";
    public bool IsAllLibraryGrouped => IsAllLibrary && AllLibraryViewMode == "Grouped";
    public bool IsAllLibraryCombined => IsAllLibrary && AllLibraryViewMode == "Combined";
    public bool ShowAllLibraryMoviesSection => IsAllLibraryGrouped && AllLibraryMovies.Count > 0;
    public bool ShowAllLibraryTvShowsSection => IsAllLibraryGrouped && AllLibraryTvShows.Count > 0;
    public bool ShowAllLibraryAnimeSection => IsAllLibraryGrouped && AllLibraryAnime.Count > 0;
    public bool IsBrowseSection => !IsSearchActive && SelectedSection is not ("Home" or "All");
    public bool ShowOfflineEmptyState => IsBrowseSection
        && SelectedSection == "Offline"
        && OfflineTitles.Count == 0;
    public bool ShowFavoritesEmptyState => IsBrowseSection
        && SelectedSection == "Favorites"
        && _favoriteTitleIds.Count == 0;

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

    public CatalogTitleSummary? Hero
    {
        get => _hero;
        private set
        {
            if (ReferenceEquals(_hero, value)) return;
            _hero = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HeroTitle));
            OnPropertyChanged(nameof(HeroMeta));
            OnPropertyChanged(nameof(HeroDescription));
        }
    }

    public string HeroEyebrow => _heroCandidates.Count > 0
        ? "Unwatched pick from your library"
        : "Featured from your library";
    public string HeroTitle => Hero?.DisplayTitle ?? "Your local library";
    public string HeroMeta => Hero is null ? "Movies | TV | Anime" : $"{Hero.KindLabel} | {Hero.MetaLine} | {Hero.AvailabilityLabel}";
    public string HeroDescription => Hero is null
        ? "Add media folders in Library Maintenance, then refresh EasyFlix to build your private local catalog."
        : !string.IsNullOrWhiteSpace(Hero.Description)
            ? Hero.Description
            : "This title is in your local library. Run Sync metadata to cache a description locally.";

    public void ShowNextHero() => MoveHero(1);
    public void ShowPreviousHero() => MoveHero(-1);

    private void MoveHero(int direction)
    {
        if (_heroCandidates.Count == 0) return;
        _heroIndex = (_heroIndex + direction) % _heroCandidates.Count;
        if (_heroIndex < 0) _heroIndex += _heroCandidates.Count;
        Hero = _heroCandidates[_heroIndex];
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync()
    {
        await _store.InitializeAsync(_lifetime.Token);

        await ReloadCatalogViewAsync(_lifetime.Token, includePersonalized: false);
        StatusText = "Ready from local cache. Loading personalized rows in the background...";
    }

    public async Task HydratePersonalizedStartupAsync()
    {
        await RefreshPersonalizedStateAsync(_lifetime.Token);
        StatusText = "Ready from local cache. Checking the library locally in the background...";
    }

    public async Task ReconcileAfterStartupAsync()
    {
        try
        {
            await Task.Delay(300, _lifetime.Token);
            await ReconcileAndReloadAsync();
            StartChangeMonitor();
            StatusText = "Library check complete. No metadata providers were contacted.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async Task RefreshAsync()
    {
        await ReconcileAndReloadAsync();
        StartChangeMonitor();
        StatusText = "Local library refresh complete. No metadata providers were contacted.";
    }

    public async Task ReloadAfterExternalImportAsync()
    {
        _scanOptions = await _localRefresh.BuildScanOptionsAsync(_lifetime.Token);
        await ReloadCatalogViewAsync(_lifetime.Token);
        StartChangeMonitor();
        StatusText = "Media intake complete. Library view reloaded from the local catalog.";
    }

    public void SelectSection(string section)
    {
        if (section is not ("Home" or "All" or "Favorites" or "Movies" or "TV" or "Anime" or "Offline")) return;
        SelectedSection = section;
    }

    private async Task ReconcileAndReloadAsync(bool fromWatcher = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            var backgroundResult = await _localRefresh.RefreshAsync(progress, _lifetime.Token);
            _scanOptions = backgroundResult.ScanOptions;

            if (!fromWatcher && backgroundResult.Summary is { } summary)
                StatusText = $"Reconciled {summary.RootsOnline} online root(s), {summary.RootsOffline} offline root(s).";

            var legacy = backgroundResult.Legacy;
            await ReloadCatalogViewAsync(_lifetime.Token);

            if (legacy is { ImportedNow: true })
            {
                StatusText =
                    $"Legacy library imported: {legacy.Result.CanonicalTitles} grouped title(s), " +
                    $"{legacy.Result.FilesReassigned} file(s), {legacy.Result.ArtworkCopied} local poster(s), " +
                    $"{legacy.Result.UnmatchedEntries.Count} unresolved shortcut(s).";
            }

            var available = _allTitles.Count(title => title.Availability == CatalogAvailability.Online);
            var unavailable = _allTitles.Count - available;
            var timingText = backgroundResult.Summary is { } timing
                ? $" | check {timing.Elapsed.TotalSeconds:0.0}s (scan {timing.ScanElapsed.TotalSeconds:0.0}s, snapshot {timing.SnapshotElapsed.TotalSeconds:0.0}s, catalog {timing.CatalogWriteElapsed.TotalSeconds:0.0}s)"
                : string.Empty;
            StatusText = $"{_allTitles.Count} title(s) | {available} available | {unavailable} offline or missing{timingText}";
        }
        catch (Exception ex)
        {
            StatusText = "Library refresh failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SyncMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (_metadataBusy)
        {
            StatusText = "Metadata sync is already running.";
            return;
        }

        _metadataBusy = true;
        try
        {
            await _metadataEnrichment.ClearWeakAutomaticArtworkAsync(cancellationToken);
            await ReloadCatalogViewAsync(cancellationToken);

            var progress = new Progress<string>(message => StatusText = message);
            var attemptedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var totalAttempted = 0;
            var totalUpdated = 0;
            var totalNoMatch = 0;
            var totalProviderFailures = 0;
            var totalArtworkFailures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var visiblePriority = HomeTitles
                    .Concat(MovieTitles.Take(12))
                    .Concat(TvTitles.Take(12))
                    .Concat(AnimeTitles.Take(12))
                    .Select((title, index) => new { title.Id, Index = index })
                    .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Min(item => item.Index), StringComparer.OrdinalIgnoreCase);

                var targets = _allTitles
                    .Where(title =>
                        NeedsMetadataSync(title)
                        && (title.Kind != MediaKind.Movie || _metadataEnrichment.HasMovieProviderConfigured)
                        && !attemptedIds.Contains(title.Id))
                    .OrderBy(title => visiblePriority.TryGetValue(title.Id, out var rank) ? rank : int.MaxValue)
                    .ThenByDescending(title => title.Availability == CatalogAvailability.Online)
                    .ThenBy(title => title.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToArray();

                if (targets.Length == 0) break;

                foreach (var title in targets)
                    attemptedIds.Add(title.Id);

                StatusText = $"Metadata sync: {totalUpdated} updated from {totalAttempted} attempted...";

                var result = await _metadataEnrichment.EnrichAsync(
                    targets,
                    progress,
                    cancellationToken);

                totalAttempted += result.Attempted;
                totalUpdated += result.Updated;
                totalNoMatch += result.NoMatch;
                totalProviderFailures += result.ProviderFailures;
                totalArtworkFailures += result.ArtworkFailures;

                if (result.Updated > 0)
                    await ReloadCatalogViewAsync(cancellationToken);

                StatusText =
                    $"Metadata sync: {totalUpdated} updated / {totalAttempted} attempted | " +
                    $"{totalNoMatch} no match | {totalProviderFailures} provider errors | " +
                    $"{totalArtworkFailures} artwork errors.";

                await Task.Delay(100, cancellationToken);
            }

            await ReloadCatalogViewAsync(cancellationToken);

            var remainingEnrichable = _allTitles.Count(title =>
                NeedsMetadataSync(title)
                && (title.Kind != MediaKind.Movie || _metadataEnrichment.HasMovieProviderConfigured));

            var skippedMovies = _metadataEnrichment.HasMovieProviderConfigured
                ? 0
                : _allTitles.Count(title =>
                    title.Kind == MediaKind.Movie
                    && NeedsMetadataSync(title));

            var movieNote = skippedMovies > 0
                ? $" {skippedMovies} movie(s) are waiting for a TMDB API key."
                : string.Empty;

            StatusText =
                $"Metadata sync complete: {totalUpdated} updated from {totalAttempted} attempted; " +
                $"{totalNoMatch} no match; {totalProviderFailures} provider error(s); " +
                $"{totalArtworkFailures} artwork error(s); {remainingEnrichable} title(s) still missing." +
                movieNote;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Metadata sync stopped. Completed metadata remains cached locally.";
        }
        catch (Exception ex)
        {
            StatusText = "Metadata sync paused: " + ex.Message;
        }
        finally
        {
            _metadataBusy = false;
        }
    }

    private async Task ReloadCatalogViewAsync(CancellationToken cancellationToken, bool includePersonalized = true)
    {
        _allTitles = (await _store.LoadTitleSummariesAsync(cancellationToken))
            .Where(title =>
                title.Availability == CatalogAvailability.Online
                || !MediaScanner.IsIgnoredCameraClip(title.DisplayTitle))
            .OrderByDescending(title => title.Availability == CatalogAvailability.Online)
            .ThenByDescending(title => title.UpdatedUtc)
            .ThenBy(title => title.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var onlineTitles = _allTitles
            .Where(title => title.Availability == CatalogAvailability.Online)
            .ToArray();
        var recentlyAvailable = onlineTitles.Take(18).ToArray();
        var recentIds = recentlyAvailable
            .Select(title => title.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Replace(HomeTitles, recentlyAvailable);
        Replace(MovieTitles, _allTitles.Where(title => title.Kind == MediaKind.Movie && !recentIds.Contains(title.Id)).Take(30));
        Replace(TvTitles, _allTitles.Where(title => title.Kind == MediaKind.TvShow && !recentIds.Contains(title.Id)).Take(30));
        Replace(AnimeTitles, _allTitles.Where(title => title.Kind == MediaKind.Anime && !recentIds.Contains(title.Id)).Take(30));
        Replace(OfflineTitles, _allTitles.Where(title => title.Availability != CatalogAvailability.Online).Take(30));
        OnPropertyChanged(nameof(HasOfflineTitles));
        OnPropertyChanged(nameof(ShowOfflineEmptyState));
        RebuildAllLibraryState();
        if (includePersonalized)
            await RefreshPersonalizedStateAsync(cancellationToken);
        else
            RebuildHeroCandidates(new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase));

        RebuildVisibleState();
    }

    private async Task RefreshPersonalizedStateAsync(CancellationToken cancellationToken)
    {
        await RefreshFavoritesAsync(cancellationToken);
        await RefreshContinueWatchingAsync(cancellationToken);
        await RefreshUpNextAsync(cancellationToken);

        var watchedTitleIds = (await _store.LoadPlaybackHistoryAsync(1000, cancellationToken))
            .Select(entry => entry.TitleId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RebuildHeroCandidates(watchedTitleIds);
    }

    private void RebuildHeroCandidates(IReadOnlySet<string> watchedTitleIds)
    {
        _heroCandidates = _allTitles
            .Where(title => title.Availability == CatalogAvailability.Online)
            .Where(title => !watchedTitleIds.Contains(title.Id))
            .OrderByDescending(title => !string.IsNullOrWhiteSpace(title.Description))
            .ThenByDescending(title => !string.IsNullOrWhiteSpace(title.BackdropPath))
            .ThenByDescending(title => !string.IsNullOrWhiteSpace(title.ArtworkPath))
            .ThenByDescending(title => title.UpdatedUtc)
            .ThenBy(title => title.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToArray();

        _heroIndex = 0;
        Hero = _heroCandidates.FirstOrDefault()
            ?? HomeTitles.FirstOrDefault(title => !string.IsNullOrWhiteSpace(title.Description))
            ?? HomeTitles.FirstOrDefault(title => !string.IsNullOrWhiteSpace(title.ArtworkPath))
            ?? HomeTitles.FirstOrDefault()
            ?? _allTitles.FirstOrDefault();
        OnPropertyChanged(nameof(HeroEyebrow));
    }
    public async Task RefreshFavoritesAsync(CancellationToken cancellationToken = default)
    {
        _favoriteTitleIds = (await _store.LoadFavoriteTitleIdsAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        RebuildVisibleState();
    }

    public async Task RefreshContinueWatchingAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _store.LoadContinueWatchingAsync(20, cancellationToken);
        var titlesById = _allTitles.ToDictionary(
            title => title.Id,
            StringComparer.OrdinalIgnoreCase);

        ContinueWatchingTitles.Clear();
        foreach (var entry in entries)
        {
            if (titlesById.TryGetValue(entry.TitleId, out var title))
                ContinueWatchingTitles.Add(new ContinueWatchingItem(title, entry));
        }

        OnPropertyChanged(nameof(HasContinueWatching));
    }

    public async Task RefreshUpNextAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _store.LoadUpNextAsync(20, cancellationToken);
        var titlesById = _allTitles.ToDictionary(
            title => title.Id,
            StringComparer.OrdinalIgnoreCase);

        UpNextTitles.Clear();
        foreach (var entry in entries)
        {
            if (titlesById.TryGetValue(entry.TitleId, out var title))
                UpNextTitles.Add(new UpNextItem(title, entry));
        }

        OnPropertyChanged(nameof(HasUpNext));
    }

    private static bool NeedsMetadataSync(CatalogTitleSummary title)
        => string.IsNullOrWhiteSpace(title.Description)
            || string.IsNullOrWhiteSpace(title.ArtworkPath)
            || IsLegacyArtwork(title.ArtworkPath)
            || (title.Kind == MediaKind.Movie && string.IsNullOrWhiteSpace(title.BackdropPath));

    private static bool IsLegacyArtwork(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && (path.EndsWith("-legacy.png", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("-legacy.ico", StringComparison.OrdinalIgnoreCase));

    private void StartChangeMonitor()
    {
        var roots = _scanOptions?.SourceRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? [];

        if (_changeMonitor is not null
            && roots.SequenceEqual(_monitoredRoots, StringComparer.OrdinalIgnoreCase))
            return;

        _changeMonitor?.Dispose();
        _changeMonitor = null;
        _monitoredRoots = roots;

        if (roots.Length == 0)
            return;

        _changeMonitor = new LibraryChangeMonitor(roots);
        _changeMonitor.Changed += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            _ = dispatcher.InvokeAsync(() => _ = ReconcileAndReloadAsync(fromWatcher: true));
        };
    }
    private void RebuildVisibleState()
    {
        OnPropertyChanged(nameof(IsSearchActive));
        OnPropertyChanged(nameof(ShowSearchEmptyState));
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(ShowBrowseNavigation));
        OnPropertyChanged(nameof(IsAllLibrary));
        OnPropertyChanged(nameof(IsAllLibraryGrouped));
        OnPropertyChanged(nameof(IsAllLibraryCombined));
        OnPropertyChanged(nameof(ShowAllLibraryMoviesSection));
        OnPropertyChanged(nameof(ShowAllLibraryTvShowsSection));
        OnPropertyChanged(nameof(ShowAllLibraryAnimeSection));
        OnPropertyChanged(nameof(IsBrowseSection));
        OnPropertyChanged(nameof(HasOfflineTitles));
        OnPropertyChanged(nameof(ShowOfflineEmptyState));
        OnPropertyChanged(nameof(ShowFavoritesEmptyState));

        if (IsSearchActive)
        {
            var query = SearchText.Trim();
            Replace(SearchResults, _allTitles.Where(title =>
                title.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (title.Year?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || title.KindLabel.Contains(query, StringComparison.OrdinalIgnoreCase)));
            OnPropertyChanged(nameof(ShowSearchEmptyState));
            return;
        }

        IEnumerable<CatalogTitleSummary> selected = SelectedSection switch
        {
            "Favorites" => _allTitles.Where(title => _favoriteTitleIds.Contains(title.Id)),
            "Movies" => _allTitles.Where(title => title.Kind == MediaKind.Movie),
            "TV" => _allTitles.Where(title => title.Kind == MediaKind.TvShow),
            "Anime" => _allTitles.Where(title => title.Kind == MediaKind.Anime),
            "Offline" => _allTitles.Where(title => title.Availability != CatalogAvailability.Online),
            _ => []
        };
        Replace(BrowseTitles, selected);
    }

    private void RebuildAllLibraryState()
    {
        IEnumerable<CatalogTitleSummary> filtered = AllLibraryTypeFilter switch
        {
            "Movies" => _allTitles.Where(title => title.Kind == MediaKind.Movie),
            "TV Shows" => _allTitles.Where(title => title.Kind == MediaKind.TvShow),
            "Anime" => _allTitles.Where(title => title.Kind == MediaKind.Anime),
            _ => _allTitles
        };

        var sorted = AllLibrarySortMode switch
        {
            "Recently updated" => filtered
                .OrderByDescending(title => title.UpdatedUtc)
                .ThenBy(title => title.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            "Year newest" => filtered
                .OrderByDescending(title => ParseYear(title.Year))
                .ThenBy(title => title.DisplayTitle, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(title => title.DisplayTitle, StringComparer.OrdinalIgnoreCase)
        };

        var values = sorted.ToArray();
        Replace(AllLibraryTitles, values);
        Replace(AllLibraryMovies, values.Where(title => title.Kind == MediaKind.Movie));
        Replace(AllLibraryTvShows, values.Where(title => title.Kind == MediaKind.TvShow));
        Replace(AllLibraryAnime, values.Where(title => title.Kind == MediaKind.Anime));

        OnPropertyChanged(nameof(ShowAllLibraryMoviesSection));
        OnPropertyChanged(nameof(ShowAllLibraryTvShowsSection));
        OnPropertyChanged(nameof(ShowAllLibraryAnimeSection));
    }

    private static int ParseYear(string? value)
        => int.TryParse(value, out var year) ? year : int.MinValue;

    private static void Replace(ObservableCollection<CatalogTitleSummary> target, IEnumerable<CatalogTitleSummary> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _changeMonitor?.Dispose();
        _lifetime.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
