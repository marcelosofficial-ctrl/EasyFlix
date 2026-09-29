using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class SeriesContainerGroupingRegressionTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "EasyFlixSeriesContainerGrouping-" + Guid.NewGuid().ToString("N"));

    public SeriesContainerGroupingRegressionTests()
        => Directory.CreateDirectory(_temp);

    [Theory]
    [InlineData("Drama")]
    [InlineData("Specials")]
    [InlineData("Extras")]
    [InlineData("Featurettes")]
    [InlineData("Trailers")]
    public async Task Scanner_ClimbsPastLowInformationNestedSeriesContainers(string nested)
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, Guid.NewGuid().ToString("N"))).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "Example Show", nested)).FullName;
        Touch(Path.Combine(folder, "Example.Show.S00E01.mkv"));
        Touch(Path.Combine(folder, "Example.Show.S00E02.mkv"));

        var item = Assert.Single(await new MediaScanner().ScanAsync(new ScanOptions([root], [], root)));

        Assert.Equal("Example Show", item.SeriesTitle);
        Assert.False(string.Equals(nested, item.SeriesTitle, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scanner_StripsSeasonAndExtrasTailFromSeriesFolder()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, Guid.NewGuid().ToString("N"))).FullName;
        var folder = Directory.CreateDirectory(
            Path.Combine(root, "Example Detective", "Example Detective Season 1 S01 + Extras")).FullName;
        Touch(Path.Combine(folder, "Example.Detective.S01E01.mkv"));
        Touch(Path.Combine(folder, "Example.Detective.S01E02.mkv"));

        var item = Assert.Single(await new MediaScanner().ScanAsync(new ScanOptions([root], [], root)));

        Assert.Equal("Example Detective", item.SeriesTitle);
    }

    [Fact]
    public async Task Snapshot_CoalescesMainSeasonAndNestedSpecialsUnderOneSeries()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, Guid.NewGuid().ToString("N"))).FullName;
        var season = Directory.CreateDirectory(Path.Combine(root, "Example Anime", "Season 1")).FullName;
        var specials = Directory.CreateDirectory(Path.Combine(root, "Example Anime", "Specials")).FullName;
        Touch(Path.Combine(season, "Example.Anime.S01E01.mkv"));
        Touch(Path.Combine(season, "Example.Anime.S01E02.mkv"));
        Touch(Path.Combine(specials, "Example.Anime.S00E01.mkv"));
        Touch(Path.Combine(specials, "Example.Anime.S00E02.mkv"));

        var items = await new MediaScanner().ScanAsync(new ScanOptions([root], [], root));
        Assert.All(items, item => Assert.Equal("Example Anime", item.SeriesTitle));

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(root, items);
        Assert.Single(snapshot.Titles);
        Assert.Equal(4, snapshot.Files.Count);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { }
    }
}
