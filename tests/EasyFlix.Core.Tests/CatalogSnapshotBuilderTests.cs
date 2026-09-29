using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class CatalogSnapshotBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "EasyFlixCatalogBuilder-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildRoot_CoalescesSeasonFolderIntoOneSeriesWithEpisodes()
    {
        var season = Path.Combine(_root, "Example Show", "Season 1");
        Directory.CreateDirectory(season);
        File.WriteAllBytes(Path.Combine(season, "Example.Show.S01E01.1080p.mkv"), [1]);
        File.WriteAllBytes(Path.Combine(season, "Example.Show.S01E02.1080p.mkv"), [2]);

        var item = new MediaItem
        {
            SourcePath = season,
            SourceRoot = _root,
            OriginalName = "Season 1",
            SuggestedName = "Example Show",
            Quality = "1080p",
            Kind = MediaKind.TvShow,
            IsFolder = true,
            VideoCount = 2,
            SeriesTitle = "Example Show",
            SeasonLabel = "Season 01"
        };

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(_root, [item]);

        Assert.Single(snapshot.Titles);
        Assert.Single(snapshot.Seasons);
        Assert.Equal(2, snapshot.Episodes.Count);
        Assert.Equal(2, snapshot.Files.Count);
        Assert.All(snapshot.Files, file => Assert.Equal(PathSafety.Normalize(_root), file.RootPath));
    }

    [Fact]
    public void BuildRoot_CoalescesSameSeriesAcrossSeasons()
    {
        var first = Path.Combine(_root, "Example Show", "Season 1");
        var second = Path.Combine(_root, "Example Show", "Season 2");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllBytes(Path.Combine(first, "Example.Show.S01E01.mkv"), [1]);
        File.WriteAllBytes(Path.Combine(second, "Example.Show.S02E01.mkv"), [2]);

        MediaItem Make(string folder, string seasonLabel) => new()
        {
            SourcePath = folder,
            SourceRoot = _root,
            OriginalName = Path.GetFileName(folder),
            SuggestedName = "Example Show",
            Quality = "Video",
            Kind = MediaKind.TvShow,
            IsFolder = true,
            VideoCount = 1,
            SeriesTitle = "Example Show",
            SeasonLabel = seasonLabel
        };

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(_root, [Make(first, "Season 01"), Make(second, "Season 02")]);

        Assert.Single(snapshot.Titles);
        Assert.Equal(2, snapshot.Seasons.Count);
        Assert.Equal(2, snapshot.Episodes.Count);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }
}
