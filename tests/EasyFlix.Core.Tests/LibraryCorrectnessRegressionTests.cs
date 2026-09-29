using EasyFlix.Core.Models;
using EasyFlix.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EasyFlix.Core.Tests;

public sealed class LibraryCorrectnessRegressionTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "EasyFlixLibraryCorrectness-" + Guid.NewGuid().ToString("N"));

    public LibraryCorrectnessRegressionTests()
        => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task Scanner_GroupsBrowserNamedLainEpisodes()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temp, "Lain")).FullName;
        Touch(Path.Combine(folder, "videoplayback.mp4"));
        for (var i = 1; i <= 12; i++)
            Touch(Path.Combine(folder, $"videoplayback ({i}).mp4"));

        var items = await ScanAsync(_temp);
        var item = Assert.Single(items);
        Assert.True(item.IsFolder);
        Assert.Equal("Lain", item.SeriesTitle);
        Assert.Equal(13, item.VideoCount);

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(_temp, items);
        Assert.Single(snapshot.Titles);
        Assert.Equal(13, snapshot.Files.Count);
        Assert.Equal(13, snapshot.Episodes.Count);
        Assert.Equal(
            Enumerable.Range(1, 13).ToArray(),
            snapshot.Episodes.Select(e => e.EpisodeNumber ?? -1).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Scanner_GroupsNumericAndBrowserEpisodesUnderMeaningfulParent()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temp, "EF - Tale of memories")).FullName;
        for (var i = 1; i <= 4; i++)
            Touch(Path.Combine(folder, $"{i}videoplayback.mp4"));
        for (var i = 5; i <= 12; i++)
            Touch(Path.Combine(folder, $"{i}.mp4"));

        var item = Assert.Single(await ScanAsync(_temp));
        Assert.Equal("Ef - Tale of Memories", item.SeriesTitle);

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(_temp, [item]);
        Assert.Single(snapshot.Titles);
        Assert.Equal(12, snapshot.Files.Count);
        Assert.DoesNotContain(snapshot.Titles, title => title.Kind == MediaKind.Movie);
    }

    [Fact]
    public async Task Scanner_GroupsConcatenatedReleaseEpisodesAsAnime()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_temp, "Aho Girl")).FullName;
        for (var i = 1; i <= 9; i++)
            Touch(Path.Combine(folder, $"HorribleSubsAhoGirl{i:00}720p-rh-{100 + i}.x.mp4"));

        var item = Assert.Single(await ScanAsync(_temp));
        Assert.Equal(MediaKind.Anime, item.Kind);
        Assert.Equal("Aho Girl", item.SeriesTitle);

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(_temp, [item]);
        Assert.Single(snapshot.Titles);
        Assert.Equal(9, snapshot.Episodes.Count);
    }

    [Fact]
    public async Task Scanner_GroupsNumberedHighschoolEpisodesInsteadOfMovies()
    {
        var folder = Directory.CreateDirectory(
            Path.Combine(_temp, "High school of the dead", "Highschool Of The Dead")).FullName;
        for (var i = 1; i <= 12; i++)
            Touch(Path.Combine(folder, $"highschool_of_the_dead_{i:00}_episode_title.mkv"));

        var item = Assert.Single(await ScanAsync(_temp));
        Assert.Equal("Highschool of the Dead", item.SeriesTitle);

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(_temp, [item]);
        Assert.Single(snapshot.Titles);
        Assert.Equal(12, snapshot.Episodes.Count);
        Assert.DoesNotContain(snapshot.Titles, title => title.Kind == MediaKind.Movie);
    }

    [Fact]
    public async Task Scanner_ClimbsPastGenericAnimeAndDownloadFolders()
    {
        var folder = Directory.CreateDirectory(
            Path.Combine(_temp, "Neon Genesis Evangelion", "anime", "jacvid")).FullName;
        Touch(Path.Combine(folder, "avideoplayback(0).mp4"));
        for (var i = 1; i <= 25; i++)
            Touch(Path.Combine(folder, $"videoplayback ({i}).mp4"));

        var item = Assert.Single(await ScanAsync(_temp));
        Assert.Equal(MediaKind.Anime, item.Kind);
        Assert.Equal("Neon Genesis Evangelion", item.SeriesTitle);
        Assert.Equal(26, new CatalogSnapshotBuilder().BuildRoot(_temp, [item]).Episodes.Count);
    }

    [Fact]
    public async Task Scanner_CoalescesSeasonPartFoldersUnderOneSeries()
    {
        var p1 = Directory.CreateDirectory(Path.Combine(_temp, "Re Zero", "Season 2 Part 1")).FullName;
        var p2 = Directory.CreateDirectory(Path.Combine(_temp, "Re Zero", "Season 2 Part 2")).FullName;
        for (var i = 1; i <= 2; i++)
        {
            Touch(Path.Combine(
                p1,
                $"[AH2] Re Zero kara Hajimeru Isekai Seikatsu S2P1 - {i:00} (1080p).mkv.mp4"));
            Touch(Path.Combine(
                p2,
                $"[AH2] Re Zero kara Hajimeru Isekai Seikatsu S2P2 - {i:00} (1080p).mkv.mp4"));
        }

        var items = await ScanAsync(_temp);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("Re Zero", item.SeriesTitle));
        Assert.All(items, item => Assert.Equal(MediaKind.Anime, item.Kind));

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(_temp, items);
        Assert.Single(snapshot.Titles);
        Assert.Equal(4, snapshot.Files.Count);
        Assert.All(snapshot.Seasons, season => Assert.Equal(2, season.SeasonNumber));
    }

    [Fact]
    public async Task CatalogStore_ReusesUniqueHighConfidenceCanonicalSeriesIdentity()
    {
        var db = Path.Combine(_temp, "catalog", "test.db");
        var root = Directory.CreateDirectory(Path.Combine(_temp, "media")).FullName;
        var file = Path.Combine(root, "videoplayback.mp4");
        Touch(file);

        var store = new CatalogStore(db);
        await store.InitializeAsync();

        var canonicalId = CatalogIdentity.TitleId(
            MediaKind.Anime,
            "Serial Experiments Lain",
            "1998");

        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(
            root,
            [new CatalogTitleRecord(
                canonicalId,
                MediaKind.Anime,
                "Serial Experiments Lain",
                "Serial Experiments Lain",
                "1998",
                "",
                "Video",
                null,
                null)],
            [],
            [],
            [new CatalogFileRecord(
                CatalogIdentity.FileId(file),
                canonicalId,
                null,
                null,
                root,
                PathSafety.Normalize(file),
                "Video",
                0,
                null)]));

        await using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ProviderMatch(
                    Id, TitleId, Provider, ProviderId, ProviderUrl,
                    Confidence, IsManual, UpdatedUtc)
                VALUES(
                    $id, $title, 'TVmaze', '1938',
                    'https://www.tvmaze.com/shows/1938/serial-experiments-lain',
                    1.0, 0, $now);
                """;
            command.Parameters.AddWithValue("$id", canonicalId + ":tvmaze");
            command.Parameters.AddWithValue("$title", canonicalId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var incomingId = CatalogIdentity.TitleId(MediaKind.TvShow, "Lain", null);
        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(
            root,
            [new CatalogTitleRecord(
                incomingId,
                MediaKind.TvShow,
                "Lain",
                "Lain",
                null,
                "",
                "Video",
                null,
                null)],
            [],
            [],
            [new CatalogFileRecord(
                CatalogIdentity.FileId(file),
                incomingId,
                null,
                null,
                root,
                PathSafety.Normalize(file),
                "Video",
                0,
                null)]));

        var title = Assert.Single(await store.LoadTitleSummariesAsync());
        Assert.Equal(canonicalId, title.Id);
        Assert.Equal("Serial Experiments Lain", title.DisplayTitle);
        Assert.Equal(MediaKind.Anime, title.Kind);
    }



    [Fact]
    public async Task Scanner_TreatsNestedSeasonVariantFolderAsSeriesContext()
    {
        var show = Directory.CreateDirectory(Path.Combine(_temp, "Example Show")).FullName;
        var season = Directory.CreateDirectory(Path.Combine(show, "season 1")).FullName;
        var variant = Directory.CreateDirectory(Path.Combine(season, "season 1 diff")).FullName;

        Touch(Path.Combine(season, "Example Show - 01.mkv"));
        Touch(Path.Combine(season, "Example Show - 02.mkv"));
        Touch(Path.Combine(variant, "Example Show - 03.mkv"));
        Touch(Path.Combine(variant, "Example Show - 04.mkv"));

        var items = await ScanAsync(show);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("Example Show", item.SeriesTitle));
        Assert.All(items, item => Assert.Equal("Season 01", item.SeasonLabel));

        var snapshot = new CatalogSnapshotBuilder().BuildRoot(show, items);
        var title = Assert.Single(snapshot.Titles);
        Assert.Equal("Example Show", title.DisplayTitle);
        Assert.Equal(4, snapshot.Files.Count);
        Assert.All(snapshot.Files, file => Assert.Equal(title.Id, file.TitleId));
    }

    [Fact]
    public async Task CatalogStore_CoalescesDuplicateStrongProviderIdentityAcrossKinds()
    {
        var db = Path.Combine(_temp, "catalog", "provider-duplicate.db");
        var rootA = Directory.CreateDirectory(Path.Combine(_temp, "root-a")).FullName;
        var rootB = Directory.CreateDirectory(Path.Combine(_temp, "root-b")).FullName;
        var fileA = Path.Combine(rootA, "episode-01.mkv");
        var fileB = Path.Combine(rootB, "episode-02.mkv");
        Touch(fileA);
        Touch(fileB);

        var store = new CatalogStore(db);
        await store.InitializeAsync();

        var animeId = CatalogIdentity.TitleId(MediaKind.Anime, "Re:Zero", "2016");
        var tvId = CatalogIdentity.TitleId(MediaKind.TvShow, "Re:Zero", null);

        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(
            rootA,
            [new CatalogTitleRecord(
                animeId,
                MediaKind.Anime,
                "Re:Zero",
                "Re:Zero",
                "2016",
                "",
                "Video",
                null,
                null)],
            [],
            [],
            [new CatalogFileRecord(
                CatalogIdentity.FileId(fileA),
                animeId,
                null,
                null,
                rootA,
                PathSafety.Normalize(fileA),
                "Video",
                0,
                null)]));

        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(
            rootB,
            [new CatalogTitleRecord(
                tvId,
                MediaKind.TvShow,
                "Re:Zero",
                "Re:Zero",
                null,
                "",
                "Video",
                null,
                null)],
            [],
            [],
            [new CatalogFileRecord(
                CatalogIdentity.FileId(fileB),
                tvId,
                null,
                null,
                rootB,
                PathSafety.Normalize(fileB),
                "Video",
                0,
                null)]));

        await using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            foreach (var titleId in new[] { animeId, tvId })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO ProviderMatch(
                        Id, TitleId, Provider, ProviderId, ProviderUrl,
                        Confidence, IsManual, UpdatedUtc)
                    VALUES(
                        $id, $title, 'TVmaze', '14459',
                        'https://www.tvmaze.com/shows/14459/rezero-starting-life-in-another-world',
                        1.0, 0, $now);
                    """;
                command.Parameters.AddWithValue("$id", titleId + ":tvmaze");
                command.Parameters.AddWithValue("$title", titleId);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
        }

        var incomingAnimeId = CatalogIdentity.TitleId(MediaKind.Anime, "Re Zero", null);
        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(
            rootA,
            [new CatalogTitleRecord(
                incomingAnimeId,
                MediaKind.Anime,
                "Re Zero",
                "Re Zero",
                null,
                "",
                "Video",
                null,
                null)],
            [],
            [],
            [new CatalogFileRecord(
                CatalogIdentity.FileId(fileA),
                incomingAnimeId,
                null,
                null,
                rootA,
                PathSafety.Normalize(fileA),
                "Video",
                0,
                null)]));

        var incomingTvId = CatalogIdentity.TitleId(MediaKind.TvShow, "Re Zero", null);
        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(
            rootB,
            [new CatalogTitleRecord(
                incomingTvId,
                MediaKind.TvShow,
                "Re Zero",
                "Re Zero",
                null,
                "",
                "Video",
                null,
                null)],
            [],
            [],
            [new CatalogFileRecord(
                CatalogIdentity.FileId(fileB),
                incomingTvId,
                null,
                null,
                rootB,
                PathSafety.Normalize(fileB),
                "Video",
                0,
                null)]));

        await using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT TitleId
                FROM MediaFile
                WHERE Path IN ($a, $b)
                ORDER BY TitleId;
                """;
            command.Parameters.AddWithValue("$a", PathSafety.Normalize(fileA));
            command.Parameters.AddWithValue("$b", PathSafety.Normalize(fileB));

            var titleIds = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                titleIds.Add(reader.GetString(0));

            Assert.Single(titleIds);
        }
    }

    [Fact]
    public async Task Scanner_StillHonorsExcludedPaths()
    {
        var included = Directory.CreateDirectory(Path.Combine(_temp, "Included Show")).FullName;
        var excluded = Directory.CreateDirectory(Path.Combine(_temp, "Excluded Show")).FullName;
        Touch(Path.Combine(included, "episode - 01.mkv"));
        Touch(Path.Combine(included, "episode - 02.mkv"));
        Touch(Path.Combine(excluded, "episode - 01.mkv"));
        Touch(Path.Combine(excluded, "episode - 02.mkv"));

        var items = await new MediaScanner().ScanAsync(
            new ScanOptions([_temp], [excluded], _temp));

        Assert.Single(items);
        Assert.DoesNotContain(
            items,
            item => PathSafety.IsSameOrDescendant(item.SourcePath, excluded));
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
