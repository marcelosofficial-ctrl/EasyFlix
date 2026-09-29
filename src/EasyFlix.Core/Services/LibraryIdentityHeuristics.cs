using System.Text.RegularExpressions;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public static class LibraryIdentityHeuristics
{
    private static readonly Regex StandardEpisode = new(
        @"\bS(?<season>\d{1,2})[ ._-]*E(?<episode>\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitEpisode = new(
        @"\b(?:Episode|Ep)[ ._-]*(?<episode>\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Season = new(
        @"(?:\bS(?<short>\d{1,2})(?:E\d{1,3}|P\d+)?\b|\bSeason[ ._-]*(?<long>\d{1,2})\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GenericBrowserVideo = new(
        @"^(?:[a-z])?(?<leading>\d+)?videoplayback(?:\s*\((?<paren>\d+)\))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PureNumber = new(@"^\d{1,3}$", RegexOptions.Compiled);

    private static readonly Regex ResolutionAdjacentEpisode = new(
        @"(?<episode>\d{1,3})(?=(?:720|1080|2160)p\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeparatedEpisode = new(
        @"(?:^|[ ._-])(?<episode>\d{1,3})(?=[ ._\-\(\[]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GenericContext = new(
        @"^(?:movies?|videos?|tv(?:\s+shows?)?|anime|series|episodes?|downloads?|jacvid|seasons?(?:\s+\d+)?|seasons?\s+\d+(?:\s+.*)?|s\d{1,2}(?:p\d+)?(?:\s+.*)?|part\s+\d+|extras?|specials?|featurettes?|trailers?|drama|bonus(?:\s+features?)?|samples?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeriesContainerTail = new(
        @"\b(?:seasons?[ ._-]*\d{1,2}|s\d{1,2}(?:e\d{1,3})?|extras?|specials?|featurettes?|trailers?|bonus(?:[ ._-]+features?)?|drama|ovas?|oads?)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SupplementalContext = new(
        @"(?:^|[ ._-])(?:extras?|specials?|featurettes?|bonus(?:[ ._-]+features?)?|ovas?|oads?|textless(?:[ ._-]+credits?)?|ncop|nced)(?:$|[ ._-])|^other$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NumberedMovie = new(
        @"\bmovies?[ ._-]*\d{1,2}\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Year = new(
        @"\b(?:19|20)\d{2}\b",
        RegexOptions.Compiled);

    private static readonly Regex AnimeSignal = new(
        @"anime|horriblesubs|subsplease|animerg|judas|ember|tenrai|japanese|\bova\b|\[(?:ah\d*|horriblesubs|subsplease|anime\s*time)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LeadingReleaseGroup = new(@"^\[[^\]]+\]\s*", RegexOptions.Compiled);
    private static readonly Regex KnownReleasePrefix = new(@"^(?:horriblesubs|subsplease)[ ._-]*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LooseSeriesEpisodeTail = new(
        @"^(?<title>.*[A-Za-z])(?:[ ._-]+)(?<episode>\d{1,3})(?:[ ._-]+.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool LooksLikeEpisode(string value, string? parentContext)
    {
        var fileName = Path.GetFileName(value);
        if (StandardEpisode.IsMatch(fileName) || ExplicitEpisode.IsMatch(fileName))
            return true;
        if (NumberedMovie.IsMatch(fileName) || SharesExplicitYear(fileName, parentContext))
            return false;

        return HasMeaningfulSeriesContext(parentContext)
            && TryEpisodeNumber(fileName) is not null;
    }

    public static (int Season, int Episode)? FindSeasonEpisode(string filename, string? parentContext = null)
    {
        var combined = StandardEpisode.Match(filename);
        if (combined.Success
            && int.TryParse(combined.Groups["season"].Value, out var combinedSeason)
            && int.TryParse(combined.Groups["episode"].Value, out var combinedEpisode))
            return (combinedSeason, combinedEpisode);

        var episode = TryEpisodeNumber(filename);
        return episode is null
            ? null
            : (TrySeasonNumber($"{filename} {parentContext}") ?? 1, episode.Value);
    }

    public static string FindSeriesTitle(string path, string name, string? sampleName = null)
    {
        var supplementalParent = FindSeriesContextAboveSupplement(path);
        if (!string.IsNullOrWhiteSpace(supplementalParent))
            return supplementalParent;

        foreach (var candidate in ContextCandidates(path, name, sampleName))
        {
            var normalized = NormalizeSeriesCandidate(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        var fallback = DeriveSeriesFromFileName(sampleName ?? name);
        return IsMeaningfulSeriesContext(fallback) ? fallback : "Unsorted series";
    }

    public static bool IsSeriesSupplement(string path)
    {
        DirectoryInfo? current;
        try
        {
            current = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path).Directory;
        }
        catch
        {
            return false;
        }

        for (var probe = current; probe is not null; probe = probe.Parent)
            if (SupplementalContext.IsMatch(probe.Name))
                return true;

        return false;
    }

    public static string FindSeasonLabel(string path, string name, string? sampleName = null)
    {
        foreach (var candidate in new[] { name, sampleName }.Concat(PathSegments(path)))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var season = TrySeasonNumber(candidate);
            if (season is not null) return $"Season {season.Value:00}";
        }

        return "Episodes";
    }

    public static bool LooksLikeAnimeSeries(string directory, IReadOnlyCollection<string> directVideos)
    {
        if (AnimeSignal.IsMatch(directory))
            return true;

        return directVideos.Any(path =>
            AnimeSignal.IsMatch(Path.GetFileName(path)));
    }

    public static bool IsMeaningfulSeriesContext(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var cleaned = CleanContext(value);
        if (string.IsNullOrWhiteSpace(cleaned) || !cleaned.Any(char.IsLetter)) return false;
        if (GenericContext.IsMatch(cleaned) || IsAmbiguousName(cleaned)) return false;
        return true;
    }

    public static int? TryEpisodeNumber(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value);
        if (stem.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            stem = Path.GetFileNameWithoutExtension(stem);

        var standard = StandardEpisode.Match(stem);
        if (standard.Success && int.TryParse(standard.Groups["episode"].Value, out var standardEpisode))
            return standardEpisode;

        var explicitEpisode = ExplicitEpisode.Match(stem);
        if (explicitEpisode.Success && int.TryParse(explicitEpisode.Groups["episode"].Value, out var namedEpisode))
            return namedEpisode;

        var browser = GenericBrowserVideo.Match(stem);
        if (browser.Success)
        {
            if (browser.Groups["leading"].Success
                && int.TryParse(browser.Groups["leading"].Value, out var leading))
                return leading;

            if (browser.Groups["paren"].Success
                && int.TryParse(browser.Groups["paren"].Value, out var paren))
                return paren + 1;

            return 1;
        }

        if (PureNumber.IsMatch(stem) && int.TryParse(stem, out var numeric))
            return numeric;

        var adjacent = ResolutionAdjacentEpisode.Match(stem);
        if (adjacent.Success && int.TryParse(adjacent.Groups["episode"].Value, out var adjacentEpisode))
            return adjacentEpisode;

        var separated = SeparatedEpisode.Match(stem);
        return separated.Success
            && int.TryParse(separated.Groups["episode"].Value, out var separatedEpisode)
                ? separatedEpisode
                : null;
    }

    private static string? FindSeriesContextAboveSupplement(string path)
    {
        DirectoryInfo? current;
        try
        {
            current = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path).Directory;
        }
        catch
        {
            return null;
        }

        for (var probe = current; probe is not null; probe = probe.Parent)
        {
            if (!SupplementalContext.IsMatch(probe.Name))
                continue;

            var ownBase = NormalizeSeriesCandidate(probe.Name);
            if (!string.IsNullOrWhiteSpace(ownBase) && !SupplementalContext.IsMatch(ownBase))
                return ownBase;

            for (var parent = probe.Parent; parent is not null; parent = parent.Parent)
            {
                var normalized = NormalizeSeriesCandidate(parent.Name);
                if (!string.IsNullOrWhiteSpace(normalized))
                    return normalized;
            }
        }

        return null;
    }

    private static string? NormalizeSeriesCandidate(string candidate)
    {
        var cleaned = CleanContext(candidate);
        if (string.IsNullOrWhiteSpace(cleaned))
            return null;

        var collapsed = SeriesContainerTail.Replace(cleaned, string.Empty)
            .Trim(' ', '-', '_', '[', ']', '(', ')');
        if (collapsed.Length >= 4)
        {
            var collapsedDerived = DeriveSeriesFromFileName(collapsed);
            if (IsMeaningfulSeriesContext(collapsedDerived))
                return CleanContext(collapsedDerived);
        }

        var derived = DeriveSeriesFromFileName(candidate);
        if (IsMeaningfulSeriesContext(derived))
            return CleanContext(derived);
        return IsMeaningfulSeriesContext(cleaned) ? cleaned : null;
    }

    private static bool SharesExplicitYear(string fileName, string? parentContext)
    {
        if (string.IsNullOrWhiteSpace(parentContext))
            return false;
        var match = Year.Match(fileName);
        if (!match.Success)
            return false;
        var parentName = Path.GetFileName(parentContext.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return Regex.IsMatch(parentName, $@"\b{Regex.Escape(match.Value)}\b");
    }

    private static bool HasMeaningfulSeriesContext(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (IsMeaningfulSeriesContext(value)) return true;

        try
        {
            var directory = Directory.Exists(value)
                ? new DirectoryInfo(value)
                : new DirectoryInfo(Path.GetDirectoryName(value) ?? value);

            for (var current = directory; current is not null; current = current.Parent)
                if (IsMeaningfulSeriesContext(current.Name))
                    return true;
        }
        catch
        {
        }

        return false;
    }

    private static int? TrySeasonNumber(string value)
    {
        var match = Season.Match(value);
        if (!match.Success) return null;
        var text = match.Groups["short"].Success ? match.Groups["short"].Value : match.Groups["long"].Value;
        return int.TryParse(text, out var season) ? season : null;
    }

    private static IEnumerable<string> ContextCandidates(string path, string name, string? sampleName)
    {
        if (Directory.Exists(path))
            yield return name;

        foreach (var segment in PathSegments(path))
            yield return segment;

        if (!string.IsNullOrWhiteSpace(sampleName))
            yield return DeriveSeriesFromFileName(sampleName);

        yield return DeriveSeriesFromFileName(name);
    }

    private static IEnumerable<string> PathSegments(string path)
    {
        DirectoryInfo? parent;
        try
        {
            parent = Directory.Exists(path)
                ? new DirectoryInfo(path).Parent
                : new FileInfo(path).Directory;
        }
        catch
        {
            yield break;
        }

        while (parent is not null)
        {
            yield return parent.Name;
            parent = parent.Parent;
        }
    }

    private static string DeriveSeriesFromFileName(string value)
    {
        var stem = MediaNameParser.FindSeriesTitle(value);
        stem = LeadingReleaseGroup.Replace(stem, string.Empty);
        stem = KnownReleasePrefix.Replace(stem, string.Empty);
        var loose = LooseSeriesEpisodeTail.Match(stem.Replace('_', ' '));
        if (loose.Success) stem = loose.Groups["title"].Value;
        return MediaNameParser.FindSeriesTitle(stem);
    }

    private static string CleanContext(string value)
    {
        var trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(leaf)) leaf = trimmed;
        var series = MediaNameParser.FindSeriesTitle(leaf);
        return string.IsNullOrWhiteSpace(series)
            ? MediaNameParser.CleanTitle(leaf)
            : series;
    }

    private static bool IsAmbiguousName(string value)
    {
        var compact = Regex.Replace(value, @"\s+", string.Empty);
        return PureNumber.IsMatch(compact)
            || Regex.IsMatch(
                compact,
                @"^(?:[a-z]?\d*)?videoplayback(?:\(\d+\))?$",
                RegexOptions.IgnoreCase);
    }
}
