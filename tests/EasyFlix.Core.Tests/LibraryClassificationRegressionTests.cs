using EasyFlix.Core.Models;
using EasyFlix.Core.Services;
using Xunit;

namespace EasyFlix.Core.Tests;

public sealed class LibraryClassificationRegressionTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "EasyFlixLibraryClassification-" + Guid.NewGuid().ToString("N"));

    public LibraryClassificationRegressionTests()
        => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task Scanner_DoesNotTreatGenericEncodeTokensAsAnime()
    {
        var show = Directory.CreateDirectory(
            Path.Combine(_temp, "Example Scripted Show 1080p 10bit Dual Audio BDRip")).FullName;
        Touch(Path.Combine(show, "Example.Scripted.Show.S01E01.1080p.10bit.Dual.Audio.BDRip.mkv"));
        Touch(Path.Combine(show, "Example.Scripted.Show.S01E02.1080p.10bit.Dual.Audio.BDRip.mkv"));

        var item = Assert.Single(await ScanAsync(show));
        Assert.Equal(MediaKind.TvShow, item.Kind);
    }

    [Fact]
    public async Task Scanner_KeepsTrueDetectiveAsTvAndOneCanonicalSeries()
    {
        var show = Directory.CreateDirectory(Path.Combine(_temp, "True Detective")).FullName;
        var season = Directory.CreateDirectory(Path.Combine(show, "Season 1")).FullName;
        Touch(Path.Combine(season, "True.Detective.S01E01.1080p.WEB-DL.mkv"));
        Touch(Path.Combine(season, "True.Detective.S01E02.1080p.WEB-DL.mkv"));

        var item = Assert.Single(await ScanAsync(show));
        Assert.Equal(MediaKind.TvShow, item.Kind);
        Assert.Equal("True Detective", item.SeriesTitle);

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(show, [item]);
        Assert.Single(snapshot.Titles);
        Assert.DoesNotContain(snapshot.Titles, title => title.Kind == MediaKind.Anime);
    }

    [Theory]
    [InlineData("Game of Thrones")]
    [InlineData("The Leftovers")]
    public async Task Scanner_KeepsKnownScriptedTvRegressionsOutOfAnime(string title)
    {
        var show = Directory.CreateDirectory(Path.Combine(_temp, title)).FullName;
        var season = Directory.CreateDirectory(Path.Combine(show, "Season 1")).FullName;
        var stem = title.Replace(' ', '.');
        Touch(Path.Combine(season, $"{stem}.S01E01.1080p.WEB-DL.mkv"));
        Touch(Path.Combine(season, $"{stem}.S01E02.1080p.WEB-DL.mkv"));

        var item = Assert.Single(await ScanAsync(show));
        Assert.Equal(MediaKind.TvShow, item.Kind);
        Assert.Equal(title, item.SeriesTitle);

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(show, [item]);
        Assert.Single(snapshot.Titles);
        Assert.All(snapshot.Titles, value => Assert.NotEqual(MediaKind.Anime, value.Kind));
    }

    [Fact]
    public async Task Scanner_StillTreatsExplicitAnimeReleaseSignalsAsAnime()
    {
        var show = Directory.CreateDirectory(Path.Combine(_temp, "Odd Taxi")).FullName;
        Touch(Path.Combine(show, "[SubsPlease] Odd Taxi - 01 (1080p).mkv"));
        Touch(Path.Combine(show, "[SubsPlease] Odd Taxi - 02 (1080p).mkv"));

        var item = Assert.Single(await ScanAsync(show));
        Assert.Equal(MediaKind.Anime, item.Kind);
    }

    private Task<IReadOnlyList<MediaItem>> ScanAsync(string root)
        => new MediaScanner().ScanAsync(new ScanOptions([root], [], root));

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
