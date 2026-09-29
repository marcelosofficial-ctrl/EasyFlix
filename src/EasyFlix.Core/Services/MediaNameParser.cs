using System.Globalization;
using System.Text.RegularExpressions;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public static partial class MediaNameParser
{
    private static readonly string[] ReleaseTokens =
    [
        "bluray", "brrip", "webrip", "web-dl", "remux", "uhd", "hdr10", "hdr",
        "dv", "dolby vision", "hevc", "x264", "x265", "h264", "h265", "av1",
        "aac", "eac3", "ddp", "dts", "atmos", "proper", "complete", "dual audio",
        "multi subs", "multi-subs", "yts", "tgx", "rartv"
    ];

    public static string CleanTitle(string rawName)
    {
        var name = Path.GetFileNameWithoutExtension(rawName);
        if (name.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);

        name = BracketNoise().Replace(name, " ");
        name = EpisodeAndAfter().Replace(name, " ");
        name = ResolutionAndAfter().Replace(name, " ");
        name = name.Replace('.', ' ').Replace('_', ' ');
        foreach (var token in ReleaseTokens)
            name = Regex.Replace(name, $@"\b{Regex.Escape(token)}\b.*$", "", RegexOptions.IgnoreCase);
        name = YearToken().Replace(name, " ");
        name = EmptyBrackets().Replace(name, " ");
        name = Whitespace().Replace(name, " ").Trim(' ', '-', 'ΓÇô');

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant())
            .Replace(" And ", " and ")
            .Replace(" Of ", " of ")
            .Replace(" The ", " the ");
    }

    public static string? FindYear(string value)
        => YearToken().Match(value) is { Success: true } match ? match.Value : null;

    public static string FindQuality(string value)
    {
        if (Regex.IsMatch(value, "2160p|4k|uhd", RegexOptions.IgnoreCase))
        {
            if (Regex.IsMatch(value, @"hdr|dolby.?vision|\bdv\b", RegexOptions.IgnoreCase)) return "4K HDR";
            return "4K";
        }
        if (Regex.IsMatch(value, "1080p", RegexOptions.IgnoreCase)) return "1080p";
        if (Regex.IsMatch(value, "720p", RegexOptions.IgnoreCase)) return "720p";
        return "Video";
    }

    public static bool LooksLikeEpisode(string value)
        => EpisodeToken().IsMatch(value) || Regex.IsMatch(value, @"\b(?:episode|ep)[ ._-]*\d{1,3}\b", RegexOptions.IgnoreCase);

    public static (int Season, int Episode)? FindSeasonEpisode(string filename, string? parentContext = null)
    {
        var searchable = $"{filename} {parentContext}";
        var combined = EpisodeNumbers().Match(searchable);
        if (combined.Success &&
            int.TryParse(combined.Groups["season"].Value, out var combinedSeason) &&
            int.TryParse(combined.Groups["episode"].Value, out var combinedEpisode))
            return (combinedSeason, combinedEpisode);

        var season = SeasonToken().Match(searchable);
        var episode = LooseEpisodeNumber().Match(filename);
        if (!season.Success || !episode.Success) return null;
        var seasonText = season.Groups["short"].Success ? season.Groups["short"].Value : season.Groups["long"].Value;
        return int.TryParse(seasonText, out var seasonNumber) &&
               int.TryParse(episode.Groups["episode"].Value, out var episodeNumber)
            ? (seasonNumber, episodeNumber)
            : null;
    }

    public static bool LooksLikeAnime(string value)
        => Regex.IsMatch(value,
            @"anime|dual.?audio|japanese|\bova\b|\bbd\b|subsplease|animerg|judas|ember|tenrai|bdrip|1080p.*10bit",
            RegexOptions.IgnoreCase);

    public static string FindSeriesTitle(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value);
        name = LeadingReleaseGroup().Replace(name, string.Empty);
        name = SeriesBoundaryAndAfter().Replace(name, string.Empty);
        return CleanTitle(name);
    }

    public static string FindSeasonLabel(string value)
    {
        var match = SeasonToken().Match(value);
        if (!match.Success) return string.Empty;
        var text = match.Groups["short"].Success ? match.Groups["short"].Value : match.Groups["long"].Value;
        return int.TryParse(text, out var season) ? $"Season {season:00}" : string.Empty;
    }

    public static string BuildDisplayName(string rawName, MediaKind kind, bool removable, string? parentContext = null)
    {
        var title = CleanTitle(rawName);
        if (AmbiguousTitle().IsMatch(title))
        {
            var context = string.IsNullOrWhiteSpace(parentContext) ? string.Empty : CleanTitle(parentContext);
            var genericContext = context.Equals("Movies", StringComparison.OrdinalIgnoreCase)
                || context.Equals("Movie", StringComparison.OrdinalIgnoreCase)
                || context.Equals("Videos", StringComparison.OrdinalIgnoreCase)
                || context.Equals("Video", StringComparison.OrdinalIgnoreCase);
            var original = Path.GetFileNameWithoutExtension(rawName);
            title = !genericContext && context.Any(char.IsLetter)
                ? $"{context} ΓÇö {original}"
                : $"Unidentified video ΓÇö {original}";
        }
        var year = FindYear(rawName);
        var quality = FindQuality(rawName);
        var prefix = removable ? "[EXT] " : string.Empty;
        var yearText = year is null ? string.Empty : $" ({year})";
        var qualityText = quality == "Video" ? string.Empty : $" [{quality}]";
        return $"{prefix}{title}{yearText}{qualityText}";
    }

    [GeneratedRegex(@"\[(?=[^\]]*(?:group|tgx|yts|rartv|codec|audio|sub|1080|2160|720))[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketNoise();

    [GeneratedRegex(@"\bS\d{1,2}E\d{1,3}\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeAndAfter();

    [GeneratedRegex(@"\b(?:2160p|1080p|720p|4k|uhd)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionAndAfter();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeToken();

    [GeneratedRegex(@"\bS(?<season>\d{1,2})[ ._-]*E(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumbers();

    [GeneratedRegex(@"(?:\bEpisode[ ._-]*|\bEp[ ._-]*|\s-\s)(?<episode>\d{1,3})(?=\D|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LooseEpisodeNumber();

    [GeneratedRegex(@"^\[[^\]]+\]\s*")]
    private static partial Regex LeadingReleaseGroup();

    [GeneratedRegex(@"(?:\bS\d{1,2}(?:E\d{1,3})?\b|\bSeason[ ._-]*\d{1,2}\b).*$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesBoundaryAndAfter();

    [GeneratedRegex(@"(?:\bS(?<short>\d{1,2})(?:E\d{1,3})?\b|\bSeason[ ._-]*(?<long>\d{1,2})\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\(\[\{]\s*[\)\]\}]")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"^\d+(?:(?:video)?playback|video)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AmbiguousTitle();
}
