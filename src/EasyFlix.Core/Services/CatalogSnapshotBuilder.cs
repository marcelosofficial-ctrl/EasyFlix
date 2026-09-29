using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed class CatalogSnapshotBuilder
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    public CatalogRootSnapshot BuildRoot(string rootPath, IReadOnlyCollection<MediaItem> items)
    {
        var root = PathSafety.Normalize(rootPath);
        var titles = new Dictionary<string, CatalogTitleRecord>(StringComparer.OrdinalIgnoreCase);
        var seasons = new Dictionary<string, CatalogSeasonRecord>(StringComparer.OrdinalIgnoreCase);
        var episodes = new Dictionary<string, CatalogEpisodeRecord>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, CatalogFileRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.OrderBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            if (EasyFlixSafetyPolicy.IsHardExcluded(item.SourcePath)) continue;

            if (item.Kind == MediaKind.Movie)
            {
                AddMovie(root, item, titles, files);
                continue;
            }

            AddSeries(root, item, titles, seasons, episodes, files);
        }

        return new CatalogRootSnapshot(
            root,
            titles.Values.OrderBy(title => title.SortTitle, StringComparer.OrdinalIgnoreCase).ToArray(),
            seasons.Values.OrderBy(season => season.SeasonNumber ?? int.MaxValue).ThenBy(season => season.Label, StringComparer.OrdinalIgnoreCase).ToArray(),
            episodes.Values.OrderBy(episode => episode.SortKey, StringComparer.OrdinalIgnoreCase).ToArray(),
            files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void AddMovie(
        string root,
        MediaItem item,
        IDictionary<string, CatalogTitleRecord> titles,
        IDictionary<string, CatalogFileRecord> files)
    {
        if (item.IsFolder) return;
        var display = MediaNameParser.CleanTitle(item.OriginalName);
        var parentDisplay = FindMeaningfulYearMatchedMovieFolderTitle(root, item);
        if (!string.IsNullOrWhiteSpace(parentDisplay))
            display = parentDisplay;
        if (string.IsNullOrWhiteSpace(display)) display = item.SuggestedName;
        var titleId = CatalogIdentity.TitleId(MediaKind.Movie, display, item.Year);
        titles[titleId] = new CatalogTitleRecord(
            titleId,
            MediaKind.Movie,
            display,
            display,
            item.Year,
            string.Empty,
            item.Quality,
            item.ArtworkPath,
            null);

        AddFile(root, item.SourcePath, titleId, null, null, item.Quality, files);
    }

    private static void AddSeries(
        string root,
        MediaItem item,
        IDictionary<string, CatalogTitleRecord> titles,
        IDictionary<string, CatalogSeasonRecord> seasons,
        IDictionary<string, CatalogEpisodeRecord> episodes,
        IDictionary<string, CatalogFileRecord> files)
    {
        var display = string.IsNullOrWhiteSpace(item.SeriesTitle)
            ? MediaNameParser.FindSeriesTitle(item.OriginalName)
            : item.SeriesTitle;
        if (string.IsNullOrWhiteSpace(display)) display = MediaNameParser.CleanTitle(item.OriginalName);
        if (string.IsNullOrWhiteSpace(display)) display = "Unsorted series";

        var titleId = CatalogIdentity.SeriesTitleId(display);
        if (!titles.TryGetValue(titleId, out var existing))
        {
            titles[titleId] = new CatalogTitleRecord(
                titleId,
                item.Kind,
                display,
                display,
                null,
                string.Empty,
                item.Quality,
                item.ArtworkPath,
                null);
        }
        else if (existing.Kind != MediaKind.Anime && item.Kind == MediaKind.Anime)
        {
            titles[titleId] = new CatalogTitleRecord(
                titleId,
                MediaKind.Anime,
                display,
                display,
                null,
                string.Empty,
                item.Quality,
                item.ArtworkPath,
                null);
        }

        if (item.IsFolder)
        {
            var sourceFiles = SafeDirectVideos(item.SourcePath);
            var fallbackSeason = ParseSeasonNumber(item.SeasonLabel);
            for (var index = 0; index < sourceFiles.Count; index++)
            {
                var path = sourceFiles[index];
                var parsed = LibraryIdentityHeuristics.FindSeasonEpisode(
                    Path.GetFileName(path),
                    item.SourcePath);
                var seasonNumber = parsed?.Season ?? fallbackSeason;
                var episodeNumber = parsed?.Episode;
                AddEpisodeFile(root, item, titleId, path, seasonNumber, episodeNumber, index, seasons, episodes, files);
            }
            return;
        }

        var standalone = LibraryIdentityHeuristics.FindSeasonEpisode(
            item.OriginalName,
            Path.GetDirectoryName(item.SourcePath));
        AddEpisodeFile(root, item, titleId, item.SourcePath, standalone?.Season ?? ParseSeasonNumber(item.SeasonLabel), standalone?.Episode, 0, seasons, episodes, files);
    }

    private static void AddEpisodeFile(
        string root,
        MediaItem item,
        string titleId,
        string path,
        int? seasonNumber,
        int? episodeNumber,
        int fallbackIndex,
        IDictionary<string, CatalogSeasonRecord> seasons,
        IDictionary<string, CatalogEpisodeRecord> episodes,
        IDictionary<string, CatalogFileRecord> files)
    {
        if (EasyFlixSafetyPolicy.IsHardExcluded(path)) return;

        var seasonLabel = seasonNumber switch
        {
            0 => "Specials",
            int number when number > 0 => $"Season {number}",
            _ => string.IsNullOrWhiteSpace(item.SeasonLabel) || item.SeasonLabel.Equals("Episodes", StringComparison.OrdinalIgnoreCase)
                ? "Unassigned"
                : item.SeasonLabel
        };

        var seasonId = CatalogIdentity.SeasonId(titleId, seasonNumber, seasonLabel);
        seasons[seasonId] = new CatalogSeasonRecord(seasonId, titleId, seasonNumber, seasonLabel);

        var fileName = Path.GetFileName(path);
        var episodeId = CatalogIdentity.EpisodeId(seasonId, episodeNumber, fileName);
        var clean = MediaNameParser.CleanTitle(fileName);
        var display = episodeNumber is null
            ? (string.IsNullOrWhiteSpace(clean) ? $"Episode {fallbackIndex + 1}" : clean)
            : $"Episode {episodeNumber}";
        var sortKey = $"{seasonNumber ?? 9999:D4}|{episodeNumber ?? 9999:D4}|{fileName}";

        episodes[episodeId] = new CatalogEpisodeRecord(episodeId, titleId, seasonId, episodeNumber, display, sortKey);
        AddFile(root, path, titleId, seasonId, episodeId, MediaNameParser.FindQuality(fileName), files);
    }

    private static void AddFile(
        string root,
        string path,
        string titleId,
        string? seasonId,
        string? episodeId,
        string quality,
        IDictionary<string, CatalogFileRecord> files)
    {
        if (EasyFlixSafetyPolicy.IsHardExcluded(path)) return;
        long? size = null;
        DateTimeOffset? lastWrite = null;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                size = info.Length;
                lastWrite = info.LastWriteTimeUtc;
            }
        }
        catch { }

        var id = CatalogIdentity.FileId(path);
        files[id] = new CatalogFileRecord(id, titleId, seasonId, episodeId, root, PathSafety.Normalize(path), quality, size, lastWrite);
    }

    private static IReadOnlyList<string> SafeDirectVideos(string folder)
    {
        if (EasyFlixSafetyPolicy.IsHardExcluded(folder)) return [];
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
                .Where(path => !EasyFlixSafetyPolicy.IsHardExcluded(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? FindMeaningfulYearMatchedMovieFolderTitle(string root, MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Year))
            return null;

        string? parent;
        try { parent = Path.GetDirectoryName(item.SourcePath); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(parent)
            || PathSafety.Normalize(parent).Equals(root, StringComparison.OrdinalIgnoreCase))
            return null;

        var folder = Path.GetFileName(parent.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folder)
            || !folder.Contains(item.Year, StringComparison.OrdinalIgnoreCase))
            return null;

        var display = MediaNameParser.CleanTitle(folder);
        if (string.IsNullOrWhiteSpace(display)
            || display.Equals("Movie", StringComparison.OrdinalIgnoreCase)
            || display.Equals("Movies", StringComparison.OrdinalIgnoreCase)
            || display.Equals("Video", StringComparison.OrdinalIgnoreCase)
            || display.Equals("Videos", StringComparison.OrdinalIgnoreCase))
            return null;

        return display;
    }

    private static int? ParseSeasonNumber(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        if (label.Equals("Specials", StringComparison.OrdinalIgnoreCase)) return 0;
        var digits = new string(label.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? number : null;
    }
}
