using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public static class MetadataSearchAliasBuilder
{
    private const int MaximumAliases = 6;

    public static IReadOnlyList<string> Build(
        CatalogTitleSummary title,
        CatalogTitleDetails? details)
    {
        var aliases = new List<string>();
        AddAlias(aliases, title.DisplayTitle);

        if (details is not null)
        {
            foreach (var path in EnumeratePaths(details).Take(6))
            {
                var fileName = Path.GetFileName(path);
                var fromFile = title.Kind == MediaKind.Movie
                    ? MediaNameParser.CleanTitle(fileName)
                    : MediaNameParser.FindSeriesTitle(fileName);
                AddAlias(aliases, fromFile);

                var directory = Path.GetDirectoryName(path);
                for (var depth = 0; depth < 2 && !string.IsNullOrWhiteSpace(directory); depth++)
                {
                    var segment = Path.GetFileName(directory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                    if (!IsGenericDirectory(segment))
                        AddAlias(aliases, MediaNameParser.CleanTitle(segment));
                    directory = Path.GetDirectoryName(directory);
                }

                if (aliases.Count >= MaximumAliases) break;
            }
        }

        return aliases.Take(MaximumAliases).ToArray();
    }

    private static IEnumerable<string> EnumeratePaths(CatalogTitleDetails details)
    {
        foreach (var file in details.MovieFiles)
            yield return file.Path;

        foreach (var season in details.Seasons)
            foreach (var episode in season.Episodes)
                yield return episode.File.Path;
    }

    private static void AddAlias(ICollection<string> aliases, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (trimmed.Length < 2) return;
        if (IsGenericDirectory(trimmed)) return;
        if (aliases.Any(existing => existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return;
        aliases.Add(trimmed);
    }

    private static bool IsGenericDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var normalized = new string(value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (normalized.StartsWith("season", StringComparison.Ordinal))
            return true;
        if (normalized.StartsWith("episode", StringComparison.Ordinal))
            return true;

        return normalized is
            "movie" or "movies" or "film" or "films" or
            "anime" or "tv" or "tvshow" or "tvshows" or
            "show" or "shows" or "series" or
            "video" or "videos" or "media" or "library" or
            "extra" or "extras" or "special" or "specials" or
            "trailer" or "trailers" or "featurette" or "featurettes" or
            "drama" or "bonus" or "bonusfeatures" or
            "sample" or "samples";
    }
}
