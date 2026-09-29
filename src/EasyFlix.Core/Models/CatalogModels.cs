namespace EasyFlix.Core.Models;

public enum CatalogAvailability
{
    Online = 0,
    Offline = 1,
    Missing = 2
}

public sealed record CatalogTitleSummary(
    string Id,
    MediaKind Kind,
    string DisplayTitle,
    string? Year,
    string Description,
    string Quality,
    string? ArtworkPath,
    string? BackdropPath,
    CatalogAvailability Availability,
    int SeasonCount,
    int EpisodeCount,
    DateTimeOffset UpdatedUtc,
    string? RequiredRootPath = null)
{
    public string KindLabel => Kind switch
    {
        MediaKind.TvShow => "TV Show",
        MediaKind.Anime => "Anime",
        _ => "Movie"
    };

    public string AvailabilityLabel => Availability switch
    {
        CatalogAvailability.Online => "Available",
        CatalogAvailability.Offline => $"Connect {ReconnectLocation(RequiredRootPath)} to play",
        _ => $"Missing from {ReconnectLocation(RequiredRootPath)}"
    };

    private static string ReconnectLocation(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return "the source drive";
        try
        {
            var root = Path.GetPathRoot(rootPath);
            if (!string.IsNullOrWhiteSpace(root))
                return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
        }
        return rootPath;
    }

    public string DescriptionText => string.IsNullOrWhiteSpace(Description)
        ? "Local media in your EasyFlix library. Open details to inspect seasons, episodes, audio, and subtitles before playback."
        : Description;

    public string MetaLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Year)) parts.Add(Year!);
            if (!string.IsNullOrWhiteSpace(Quality) && !Quality.Equals("Video", StringComparison.OrdinalIgnoreCase)) parts.Add(Quality);
            if (Kind != MediaKind.Movie)
            {
                if (SeasonCount > 0) parts.Add($"{SeasonCount} season{(SeasonCount == 1 ? string.Empty : "s")}");
                if (EpisodeCount > 0) parts.Add($"{EpisodeCount} episode{(EpisodeCount == 1 ? string.Empty : "s")}");
            }
            return string.Join("   |   ", parts);
        }
    }
}

public sealed record CatalogMediaFileSummary(
    string Id,
    string Path,
    string RootPath,
    string Quality,
    CatalogAvailability Availability,
    long? SizeBytes,
    DateTimeOffset? LastWriteUtc)
{
    public bool IsPlayable => Availability == CatalogAvailability.Online && File.Exists(Path);

    public string ReconnectHint
    {
        get
        {
            if (Availability == CatalogAvailability.Online)
                return "This file is available.";

            string location;
            try
            {
                var root = System.IO.Path.GetPathRoot(RootPath);
                location = string.IsNullOrWhiteSpace(root)
                    ? RootPath
                    : root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            }
            catch
            {
                location = RootPath;
            }

            return Availability == CatalogAvailability.Offline
                ? $"This file is offline. Connect {location} to play it."
                : $"This file is missing from {location}. EasyFlix keeps the catalog entry without deleting anything.";
        }
    }
}

public sealed record CatalogEpisodeSummary(
    string Id,
    int? EpisodeNumber,
    string DisplayTitle,
    CatalogMediaFileSummary File)
{
    public string EpisodeLabel => EpisodeNumber is null ? DisplayTitle : $"E{EpisodeNumber:00}  {DisplayTitle}";
}

public sealed record CatalogSeasonSummary(
    string Id,
    int? SeasonNumber,
    string Label,
    IReadOnlyList<CatalogEpisodeSummary> Episodes);

public sealed record CatalogTitleDetails(
    CatalogTitleSummary Title,
    IReadOnlyList<CatalogSeasonSummary> Seasons,
    IReadOnlyList<CatalogMediaFileSummary> MovieFiles);

public sealed record CatalogRootState(string Path, bool IsOnline, DateTimeOffset LastSeenUtc);
