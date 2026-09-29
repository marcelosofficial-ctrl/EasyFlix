using System.Globalization;
using System.Text.RegularExpressions;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public static partial class MediaNameParser
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    private static readonly string[] ReleaseTokens =
    [
        "bluray", "brrip", "webrip", "web-dl", "remux", "uhd", "hdr10", "hdr",
        "dv", "dolby vision", "hevc", "x264", "x265", "h264", "h265", "av1",
        "aac", "eac3", "ddp", "dts", "atmos", "proper", "complete", "dual audio",
        "multi subs", "multi-subs", "yts", "tgx", "rartv"
    ];

    public static string CleanTitle(string rawName)
    {
        var name = Path.GetFileName(rawName);
        while (VideoExtensions.Contains(Path.GetExtension(name)))
            name = Path.GetFileNameWithoutExtension(name);

        name = StripLeadingReleaseGroup(name);
        name = StripLeadingSitePrefix(name);
        name = BracketNoise().Replace(name, " ");
        name = StripTrailingReleaseBracket(name);
        name = EpisodeAndAfter().Replace(name, " ");
        name = ResolutionAndAfter().Replace(name, " ");
        name = Regex.Replace(name, @"\b(?<abbr>Dr|Mr|Mrs|Ms|St)\.", match => match.Groups["abbr"].Value + '\uE000', RegexOptions.IgnoreCase);
        name = name.Replace('.', ' ').Replace('_', ' ');
        foreach (var token in ReleaseTokens)
            name = Regex.Replace(name, $@"\b{Regex.Escape(token)}\b.*$", "", RegexOptions.IgnoreCase);
        name = YearToken().Replace(name, " ");
        name = EmptyBrackets().Replace(name, " ");
        name = Whitespace().Replace(name, " ").Trim(' ', '-', '\u2013');

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant())
            .Replace('\uE000', '.')
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

    public static bool LooksLikeEpisode(string value, string? parentContext = null)
    {
        var name = Path.GetFileName(value);
        return EpisodeToken().IsMatch(name)
            || SeasonDashEpisode().IsMatch(name)
            || LooseEpisodeNumber().IsMatch(name)
            || PhaseEpisode().IsMatch(name)
            || (BareTrailingEpisode().IsMatch(name) && MatchesSeriesContext(name, parentContext));
    }

    public static (int Season, int Episode)? FindSeasonEpisode(string filename, string? parentContext = null)
    {
        var searchable = $"{filename} {parentContext}";
        var combined = EpisodeNumbers().Match(searchable);
        if (combined.Success &&
            int.TryParse(combined.Groups["season"].Value, out var combinedSeason) &&
            int.TryParse(combined.Groups["episode"].Value, out var combinedEpisode))
            return (combinedSeason, combinedEpisode);

        var seasonDash = SeasonDashEpisode().Match(filename);
        if (seasonDash.Success &&
            int.TryParse(seasonDash.Groups["season"].Value, out var dashSeason) &&
            int.TryParse(seasonDash.Groups["episode"].Value, out var dashEpisode))
            return (dashSeason, dashEpisode);

        var season = SeasonToken().Match(searchable);
        var episode = LooseEpisodeNumber().Match(filename);
        if (!episode.Success)
            episode = PhaseEpisode().Match(filename);
        if (!episode.Success && MatchesSeriesContext(filename, parentContext))
            episode = BareTrailingEpisode().Match(filename);
        if (!episode.Success || !int.TryParse(episode.Groups["episode"].Value, out var episodeNumber))
            return null;

        if (!season.Success)
            return (1, episodeNumber);

        var seasonText = season.Groups["short"].Success ? season.Groups["short"].Value : season.Groups["long"].Value;
        return int.TryParse(seasonText, out var seasonNumber)
            ? (seasonNumber, episodeNumber)
            : null;
    }

    public static bool LooksLikeAnime(string value)
        => Regex.IsMatch(value,
            @"anime|dual.?audio|japanese|\bova\b|\bbd\b|subsplease|animerg|judas|ember|tenrai|bdrip|1080p.*10bit",
            RegexOptions.IgnoreCase);

    public static string FindSeriesTitle(string value)
    {
        var name = Path.GetFileName(value);
        while (VideoExtensions.Contains(Path.GetExtension(name)))
            name = Path.GetFileNameWithoutExtension(name);
        name = StripLeadingReleaseGroup(name);
        name = StripLeadingSitePrefix(name);
        name = SeriesBoundaryAndAfter().Replace(name, string.Empty);
        return CleanTitle(name);
    }

    private static string StripLeadingReleaseGroup(string value)
    {
        if (!value.StartsWith("[", StringComparison.Ordinal))
            return value;
        var close = value.IndexOf(']');
        return close > 0 ? value[(close + 1)..].TrimStart() : value;
    }

    private static string StripLeadingSitePrefix(string value)
    {
        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0)
            return value;
        var prefix = value[..separator].Trim();
        var normalized = prefix.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? prefix[4..]
            : prefix;
        var dot = normalized.LastIndexOf('.');
        if (dot <= 0 || dot == normalized.Length - 1)
            return value;
        var suffix = normalized[(dot + 1)..];
        if (!suffix.Equals("com", StringComparison.OrdinalIgnoreCase)
            && !suffix.Equals("net", StringComparison.OrdinalIgnoreCase)
            && !suffix.Equals("org", StringComparison.OrdinalIgnoreCase))
            return value;
        return value[(separator + 3)..].TrimStart();
    }

    private static string StripTrailingReleaseBracket(string value)
    {
        var current = value.TrimEnd();
        if (!current.EndsWith("]", StringComparison.Ordinal))
            return current;
        var open = current.LastIndexOf('[');
        if (open < 0)
            return current;
        var token = current[(open + 1)..^1].Trim();
        if (token.Length == 0)
            return current[..open].TrimEnd();
        if (ReleaseTokens.Any(release => token.Contains(release, StringComparison.OrdinalIgnoreCase))
            || token.Equals("BD", StringComparison.OrdinalIgnoreCase)
            || token.Equals("BDrip", StringComparison.OrdinalIgnoreCase)
            || token.Equals("BluRay", StringComparison.OrdinalIgnoreCase)
            || token.Equals("HEVC", StringComparison.OrdinalIgnoreCase)
            || token.Equals("10bit", StringComparison.OrdinalIgnoreCase))
            return current[..open].TrimEnd();
        return current;
    }

    private static bool MatchesSeriesContext(string value, string? parentContext)
    {
        if (string.IsNullOrWhiteSpace(parentContext)) return false;

        var name = Path.GetFileName(value);
        while (VideoExtensions.Contains(Path.GetExtension(name)))
            name = Path.GetFileNameWithoutExtension(name);

        var episode = BareTrailingEpisode().Match(name);
        if (!episode.Success) return false;

        var prefix = name[..episode.Index];
        prefix = LeadingReleaseGroup().Replace(prefix, string.Empty);
        var candidate = NormalizeComparison(CleanTitle(prefix));
        if (candidate.Length == 0) return false;

        var segments = parentContext
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments.Reverse())
        {
            if (SeasonToken().IsMatch(segment)) continue;
            var context = NormalizeComparison(CleanTitle(segment));
            if (context is "" or "anime" or "tv" or "tvshows" or "shows" or "series" or "movies" or "movie")
                continue;
            if (candidate == context) return true;
        }

        return false;
    }

    private static string NormalizeComparison(string value)
        => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

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
                ? $"{context} \u2014 {original}"
                : $"Unidentified video \u2014 {original}";
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

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?!\d)")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"\bS\d{1,2}E\d{1,3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeToken();

    [GeneratedRegex(@"\bS(?<season>\d{1,2})\s*[-._ ]+\s*(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonDashEpisode();

    [GeneratedRegex(@"\bS(?<season>\d{1,2})[ ._-]*E(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumbers();

    [GeneratedRegex(@"(?:\bEpisode[ ._-]*|\bEp[ ._-]*|\s-\s)(?<episode>\d{1,3})(?=\D|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LooseEpisodeNumber();

    [GeneratedRegex(@"\bPhase[ ._-]*(?<episode>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseEpisode();

    [GeneratedRegex(@"(?:^|[ ._-])(?<episode>\d{1,3})(?=\s*(?:\[[^\]]*\]|\([^)]*\)|(?:720p|1080p|2160p|4k|uhd)\b|(?:\.(?:mkv|mp4|m4v|avi|mov|wmv|ts|m2ts))?$))", RegexOptions.IgnoreCase)]
    private static partial Regex BareTrailingEpisode();

    [GeneratedRegex(@"^\[[^\]]+\]\s*")]
    private static partial Regex LeadingReleaseGroup();

    [GeneratedRegex(@"(?:\bS\d{1,2}(?:E\d{1,3}|\s*[-._ ]+\s*\d{1,3})?\b|\bSeason[ ._-]*\d{1,2}\b|\b(?:Episode|Ep|Phase)[ ._-]*\d{1,3}\b|\s-\s\d{1,3}(?=\D|$)|[ ._-]\d{1,3}(?=\s*(?:\[[^\]]*\]|\([^)]*\)|(?:720p|1080p|2160p|4k|uhd)\b|$))).*$", RegexOptions.IgnoreCase)]
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
