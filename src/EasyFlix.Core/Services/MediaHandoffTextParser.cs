using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed record MediaHandoffParsedEntry(
    string Title,
    MediaKind Kind,
    string? Year,
    string Description,
    string MediaPath,
    string? ArtworkPath,
    string? ShortcutPath,
    string? Provider,
    string? ProviderId,
    string? ProviderUrl);

public sealed record MediaHandoffParseResult(
    IReadOnlyList<MediaHandoffParsedEntry> Entries,
    IReadOnlyList<string> Warnings);

public static class MediaHandoffTextParser
{
    private static readonly Regex YearRegex = new(@"\((?<year>\d{4})\)", RegexOptions.Compiled);
    private static readonly Regex ProviderRegex = new(
        @"(?<provider>TVMaze|TMDB)(?:\s+[A-Za-z]+)?\s+(?<id>\d+)\s*[ΓÇö-]\s*(?<url>https?://\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonMediaRegex = new(
        @"^(?<label>Season\s+\d+)\s+media:\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonShortcutRegex = new(
        @"^(?<label>Season\s+\d+)\s+shortcut:\s*(?<value>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static MediaHandoffParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var parsed = new List<MediaHandoffParsedEntry>();
        var warnings = new List<string>();
        MediaKind? sectionKind = null;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (TryParseSection(line, out var kind))
            {
                sectionKind = kind;
                continue;
            }

            if (sectionKind is null || !IsRecordHeader(lines, index))
                continue;

            var end = index + 1;
            while (end < lines.Length)
            {
                var next = lines[end].Trim();
                if (TryParseSection(next, out _)
                    || end > index + 1 && IsRecordHeader(lines, end))
                    break;
                end++;
            }

            ParseRecord(lines[index].Trim(), lines[(index + 1)..end], sectionKind.Value, parsed, warnings);
            index = end - 1;
        }

        var normalized = CombineSeriesDescriptions(parsed);
        if (normalized.Count == 0)
            warnings.Add("No media records were recognized. Use the standard EASYFLIX media handoff headings and fields.");

        return new(normalized, warnings);
    }

    private static void ParseRecord(
        string header,
        string[] body,
        MediaKind kind,
        List<MediaHandoffParsedEntry> output,
        List<string> warnings)
    {
        var yearMatch = YearRegex.Match(header);
        var year = yearMatch.Success ? yearMatch.Groups["year"].Value : null;
        var rawTitle = yearMatch.Success ? header[..yearMatch.Index].Trim() : header.Trim();
        var title = NormalizeTitle(rawTitle);

        var description = ReadDescription(body);
        var artwork = ReadField(body, "Artwork:");
        var providerLine = body.Select(line => line.Trim())
            .FirstOrDefault(line =>
                line.StartsWith("Match:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Exact match:", StringComparison.OrdinalIgnoreCase));
        var providerMatch = providerLine is null ? Match.Empty : ProviderRegex.Match(providerLine);
        var provider = providerMatch.Success
            ? NormalizeProvider(providerMatch.Groups["provider"].Value)
            : null;
        var providerId = providerMatch.Success ? providerMatch.Groups["id"].Value : null;
        var providerUrl = providerMatch.Success ? providerMatch.Groups["url"].Value : null;

        var media = new List<(string Label, string Path)>();
        var shortcuts = new List<(string Label, string Path)>();

        foreach (var raw in body)
        {
            var line = raw.Trim();
            var seasonMedia = SeasonMediaRegex.Match(line);
            if (seasonMedia.Success)
            {
                media.Add((NormalizeLabel(seasonMedia.Groups["label"].Value), seasonMedia.Groups["value"].Value.Trim()));
                continue;
            }

            var seasonShortcut = SeasonShortcutRegex.Match(line);
            if (seasonShortcut.Success)
            {
                shortcuts.Add((NormalizeLabel(seasonShortcut.Groups["label"].Value), seasonShortcut.Groups["value"].Value.Trim()));
                continue;
            }

            if (line.StartsWith("Final media:", StringComparison.OrdinalIgnoreCase))
            {
                media.Add(("final", line["Final media:".Length..].Trim()));
                continue;
            }

            if (line.StartsWith("Media:", StringComparison.OrdinalIgnoreCase))
            {
                media.Add((string.Empty, line["Media:".Length..].Trim()));
                continue;
            }

            if (line.StartsWith("Shortcut:", StringComparison.OrdinalIgnoreCase))
                shortcuts.Add((string.Empty, line["Shortcut:".Length..].Trim()));
        }

        if (media.Count == 0)
        {
            warnings.Add($"{title}: no active Media/Final media field was found.");
            return;
        }

        foreach (var item in media)
        {
            var shortcut = shortcuts.FirstOrDefault(candidate =>
                    string.Equals(candidate.Label, item.Label, StringComparison.OrdinalIgnoreCase))
                .Path;

            if (string.IsNullOrWhiteSpace(shortcut) && shortcuts.Count == 1)
                shortcut = shortcuts[0].Path;

            if (string.IsNullOrWhiteSpace(shortcut) && shortcuts.Count > 0)
                warnings.Add($"{title}: no shortcut could be paired with media label '{item.Label}'.");

            output.Add(new(
                title,
                kind,
                year,
                description,
                item.Path,
                artwork,
                string.IsNullOrWhiteSpace(shortcut) ? null : shortcut,
                provider,
                providerId,
                providerUrl));
        }
    }

    private static IReadOnlyList<MediaHandoffParsedEntry> CombineSeriesDescriptions(
        IReadOnlyList<MediaHandoffParsedEntry> entries)
    {
        var combinedByKey = entries
            .GroupBy(SeriesKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    " ",
                    group.Select(item => item.Description.Trim())
                        .Where(value => value.Length > 0)
                        .Distinct(StringComparer.Ordinal)),
                StringComparer.OrdinalIgnoreCase);

        return entries
            .Select(entry => entry with { Description = combinedByKey[SeriesKey(entry)] })
            .ToArray();
    }

    private static string SeriesKey(MediaHandoffParsedEntry entry)
        => string.Join(
            "|",
            (int)entry.Kind,
            entry.Title.Trim(),
            entry.Year?.Trim() ?? string.Empty,
            entry.Provider?.Trim() ?? string.Empty,
            entry.ProviderId?.Trim() ?? string.Empty);

    private static string ReadDescription(string[] body)
    {
        for (var index = 0; index < body.Length; index++)
        {
            var line = body[index].Trim();
            if (!line.StartsWith("Description:", StringComparison.OrdinalIgnoreCase))
                continue;

            var builder = new StringBuilder(line["Description:".Length..].Trim());
            for (var next = index + 1; next < body.Length; next++)
            {
                var continuation = body[next].Trim();
                if (continuation.Length == 0 || IsKnownField(continuation))
                    break;
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(continuation);
            }
            return builder.ToString();
        }

        return string.Empty;
    }

    private static string? ReadField(string[] body, string field)
    {
        var line = body.Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith(field, StringComparison.OrdinalIgnoreCase));
        return line is null ? null : line[field.Length..].Trim();
    }

    private static bool IsKnownField(string line)
        => line.StartsWith("Media:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Final media:", StringComparison.OrdinalIgnoreCase)
           || SeasonMediaRegex.IsMatch(line)
           || line.StartsWith("Shortcut:", StringComparison.OrdinalIgnoreCase)
           || SeasonShortcutRegex.IsMatch(line)
           || line.StartsWith("Artwork:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Files:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Audio:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Subtitles:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Coverage:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Match:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Exact match:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Temporary replacement location:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Original deleted", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("What changed:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Logs", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("Implementation summary", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecordHeader(string[] lines, int index)
    {
        var header = lines[index].Trim();
        if (header.Length == 0
            || header.All(ch => ch == '=')
            || IsKnownField(header)
            || TryParseSection(header, out _))
            return false;

        for (var next = index + 1; next < lines.Length; next++)
        {
            var candidate = lines[next].Trim();
            if (candidate.Length == 0) continue;
            return candidate.StartsWith("Description:", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryParseSection(string line, out MediaKind kind)
    {
        var normalized = line.Trim().ToUpperInvariant();
        if (normalized == "TV SHOWS")
        {
            kind = MediaKind.TvShow;
            return true;
        }
        if (normalized == "MOVIES")
        {
            kind = MediaKind.Movie;
            return true;
        }
        if (normalized == "ANIME")
        {
            kind = MediaKind.Anime;
            return true;
        }

        kind = default;
        return false;
    }

    private static string NormalizeTitle(string raw)
    {
        var title = raw.Trim().Trim('ΓÇö', '-', ' ');
        if (title.Length == 0) return title;

        var letters = title.Where(char.IsLetter).ToArray();
        var allUpper = letters.Length > 0 && letters.All(char.IsUpper);
        if (!allUpper) return title;

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title.ToLowerInvariant());
    }

    private static string NormalizeLabel(string value)
        => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string NormalizeProvider(string value)
        => value.Equals("tmdb", StringComparison.OrdinalIgnoreCase) ? "TMDB" : "TVmaze";
}
