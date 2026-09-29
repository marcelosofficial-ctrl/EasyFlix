using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class CanonicalSeriesIdentityRegressionTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "EasyFlixCanonicalSeriesIdentity-" + Guid.NewGuid().ToString("N"));

    public CanonicalSeriesIdentityRegressionTests()
        => Directory.CreateDirectory(_temp);

    [Theory]
    [InlineData("btgaateoth_2012_1080_son.mkv", "2012")]
    [InlineData("Movie.Title.2013.1080p.mkv", "2013")]
    [InlineData("Movie-2014-Remux.mkv", "2014")]
    public void FindYear_RecognizesCommonSeparatorBoundaries(string raw, string expected)
        => Assert.Equal(expected, MediaNameParser.FindYear(raw));

    [Theory]
    [InlineData("[Judas] Dr. Stone S03", "Dr. Stone")]
    [InlineData("[Anime Time] Serial Experiments Lain [BD]", "Serial Experiments Lain")]
    [InlineData("www.torrenting.com - Solo Leveling S01", "Solo Leveling")]
    public void FindSeriesTitle_StripsReleaseAndSitePrefixesWithoutBreakingDottedNames(string raw, string expected)
        => Assert.Equal(expected, MediaNameParser.FindSeriesTitle(raw));

    [Fact]
    public async Task Snapshot_CoalescesSameSeriesAcrossTvAnimeAndIncidentalYear()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "mixed")).FullName;
        var main = Directory.CreateDirectory(Path.Combine(root, "Example Show", "Season 1")).FullName;
        var ova = Directory.CreateDirectory(Path.Combine(root, "[Judas] Example Show S1 OVAs - Trouble (2009)")).FullName;
        Touch(Path.Combine(main, "Example.Show.S01E01.mkv"));
        Touch(Path.Combine(main, "Example.Show.S01E02.mkv"));
        Touch(Path.Combine(ova, "[Judas] Example Show - OVA 01.mkv"));
        Touch(Path.Combine(ova, "[Judas] Example Show - OVA 02.mkv"));

        var items = await new MediaScanner().ScanAsync(new ScanOptions([root], [], root));
        var snapshot = new CatalogSnapshotBuilder().BuildRoot(root, items);

        var title = Assert.Single(snapshot.Titles);
        Assert.Equal("Example Show", title.DisplayTitle);
        Assert.Null(title.Year);
        Assert.Equal(4, snapshot.Files.Count);
        Assert.All(snapshot.Files, file => Assert.Equal(title.Id, file.TitleId));
    }

    [Fact]
    public async Task SupplementalSingletonsStayUnderParentSeriesInsteadOfBecomingMovieCards()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "supplement")).FullName;
        var season = Directory.CreateDirectory(Path.Combine(root, "Example Anime", "Season 1")).FullName;
        var extras = Directory.CreateDirectory(Path.Combine(root, "Example Anime", "Season 1", "Featurettes", "Textless Credits")).FullName;
        Touch(Path.Combine(season, "[Judas] Example Anime - S01E01.mkv"));
        Touch(Path.Combine(season, "[Judas] Example Anime - S01E02.mkv"));
        Touch(Path.Combine(extras, "[Judas] Example Anime - NCOP.mkv"));

        var items = await new MediaScanner().ScanAsync(new ScanOptions([root], [], root));
        var snapshot = new CatalogSnapshotBuilder().BuildRoot(root, items);

        var title = Assert.Single(snapshot.Titles);
        Assert.Equal("Example Anime", title.DisplayTitle);
        Assert.Equal(3, snapshot.Files.Count);
        Assert.DoesNotContain(snapshot.Titles, candidate => candidate.Kind == MediaKind.Movie);
    }

    [Fact]
    public async Task YearMatchedNumberedFeatureFilmRemainsMovie()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "movies")).FullName;
        var a = Directory.CreateDirectory(Path.Combine(root, "Example Saga Arc 2 Battle 2012")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(root, "Example Saga Arc 3 Advent 2013")).FullName;
        Touch(Path.Combine(a, "Example.Saga.Arc.2.Battle.2012.1080p.mkv"));
        Touch(Path.Combine(b, "Example.Saga.Arc.3.Advent.2013.1080p.mkv"));

        var items = await new MediaScanner().ScanAsync(new ScanOptions([root], [], root));
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(MediaKind.Movie, item.Kind));

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(root, items);
        Assert.Equal(2, snapshot.Titles.Count);
        Assert.Contains(snapshot.Titles, title => title.DisplayTitle.Contains("Arc 2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.Titles, title => title.DisplayTitle.Contains("Arc 3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AbbreviatedSingleFileMovieUsesMeaningfulYearMatchedParentFolderTitle()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "movie-title")).FullName;
        var folder = Directory.CreateDirectory(
            Path.Combine(root, "Example Golden Arc The First Movie 2012 1080p BluRay")).FullName;
        Touch(Path.Combine(folder, "egatfm_2012_1080_son.mkv"));

        var items = await new MediaScanner().ScanAsync(new ScanOptions([root], [], root));
        var item = Assert.Single(items);
        Assert.Equal(MediaKind.Movie, item.Kind);

        var title = Assert.Single(new CatalogSnapshotBuilder().BuildRoot(root, items).Titles);
        Assert.Equal("Example Golden Arc the First Movie", title.DisplayTitle);
        Assert.Equal("2012", title.Year);
    }

    [Fact]
    public async Task NumberedMovieFilesInMoviesContainerRemainMovies()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "numbered-movies")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "Example Show - Movies")).FullName;
        Touch(Path.Combine(folder, "Example Show - Movie 1.mkv"));
        Touch(Path.Combine(folder, "Example Show - Movie 2.mkv"));

        var items = await new MediaScanner().ScanAsync(new ScanOptions([root], [], root));
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(MediaKind.Movie, item.Kind));
    }

    [Fact]
    public void SeriesTitleId_IgnoresKindAndYearInputsByConstruction()
    {
        var id = CatalogIdentity.SeriesTitleId("Example Show");
        Assert.Equal(id, CatalogIdentity.SeriesTitleId("  Example   Show  "));
        Assert.NotEqual(id, CatalogIdentity.TitleId(MediaKind.Movie, "Example Show", "2012"));
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
