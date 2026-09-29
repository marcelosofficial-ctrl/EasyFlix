namespace EasyFlix.Core.Models;

public sealed record CatalogTitleRecord(
    string Id,
    MediaKind Kind,
    string DisplayTitle,
    string SortTitle,
    string? Year,
    string Description,
    string Quality,
    string? ArtworkPath,
    string? BackdropPath);

public sealed record CatalogSeasonRecord(
    string Id,
    string TitleId,
    int? SeasonNumber,
    string Label);

public sealed record CatalogEpisodeRecord(
    string Id,
    string TitleId,
    string SeasonId,
    int? EpisodeNumber,
    string DisplayTitle,
    string SortKey);

public sealed record CatalogFileRecord(
    string Id,
    string TitleId,
    string? SeasonId,
    string? EpisodeId,
    string RootPath,
    string Path,
    string Quality,
    long? SizeBytes,
    DateTimeOffset? LastWriteUtc);

public sealed record CatalogRootSnapshot(
    string RootPath,
    IReadOnlyList<CatalogTitleRecord> Titles,
    IReadOnlyList<CatalogSeasonRecord> Seasons,
    IReadOnlyList<CatalogEpisodeRecord> Episodes,
    IReadOnlyList<CatalogFileRecord> Files);
