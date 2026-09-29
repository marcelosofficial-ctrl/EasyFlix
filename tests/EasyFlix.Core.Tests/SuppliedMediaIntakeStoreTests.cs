using EasyFlix.Core.Models;
using EasyFlix.Core.Services;
using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Tests;

public sealed class SuppliedMediaIntakeStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "easyflix-intake-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AppliesExplicitMetadataByExactMediaPathAndPersistsManualIntent()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "catalog.db");
        var store = new CatalogStore(database);
        await store.InitializeAsync();

        await SeedAsync(
            database,
            "bad-title",
            (int)MediaKind.Movie,
            "[",
            "1917",
            @"C:\Media\1917 (2019)\1917.mkv");

        var service = new SuppliedMediaIntakeStore(store);
        var result = Assert.Single(await service.ApplyAsync(
        [
            new SuppliedMediaMetadataUpdate(
                "1917",
                MediaKind.Movie,
                "2019",
                "Two soldiers race to deliver a message.",
                [@"C:\Media\1917 (2019)\1917.mkv"],
                @"C:\Cache\1917-manual.png",
                "TMDB",
                "530915",
                "https://www.themoviedb.org/movie/530915-1917")
        ]));

        Assert.True(result.Applied);
        Assert.True(result.Changed);
        Assert.Equal("bad-title", result.TitleId);

        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();

        await using (var title = connection.CreateCommand())
        {
            title.CommandText = """
                SELECT Kind, DisplayTitle, SortTitle, Year, Description, ArtworkPath
                FROM MediaTitle WHERE Id='bad-title';
                """;
            await using var reader = await title.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal((int)MediaKind.Movie, reader.GetInt32(0));
            Assert.Equal("1917", reader.GetString(1));
            Assert.Equal("1917", reader.GetString(2));
            Assert.Equal("2019", reader.GetString(3));
            Assert.Equal("Two soldiers race to deliver a message.", reader.GetString(4));
            Assert.Equal(@"C:\Cache\1917-manual.png", reader.GetString(5));
        }

        await using (var provider = connection.CreateCommand())
        {
            provider.CommandText = """
                SELECT ProviderId, Confidence, IsManual
                FROM ProviderMatch
                WHERE TitleId='bad-title' AND lower(Provider)='tmdb';
                """;
            await using var reader = await provider.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("530915", reader.GetString(0));
            Assert.Equal(1d, reader.GetDouble(1));
            Assert.Equal(1, reader.GetInt32(2));
        }

        await using (var overrides = connection.CreateCommand())
        {
            overrides.CommandText = "SELECT COUNT(*) FROM UserOverride WHERE TitleId='bad-title';";
            Assert.Equal(5L, (long)(await overrides.ExecuteScalarAsync())!);
        }

        await using (var artwork = connection.CreateCommand())
        {
            artwork.CommandText = """
                SELECT Kind, LocalPath FROM ArtworkAsset
                WHERE Id='bad-title:poster-manual';
                """;
            await using var reader = await artwork.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("poster-manual", reader.GetString(0));
            Assert.Equal(@"C:\Cache\1917-manual.png", reader.GetString(1));
        }
    }

    [Fact]
    public async Task FolderPathMatchingIsLiteralAndIdempotent()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "catalog.db");
        var store = new CatalogStore(database);
        await store.InitializeAsync();

        await SeedAsync(
            database,
            "series",
            (int)MediaKind.TvShow,
            "Altered Carbon",
            null,
            @"X:\Sample Media\Example.Show.S01\Example.Show.S01E01.mkv");

        var service = new SuppliedMediaIntakeStore(store);
        var update = new SuppliedMediaMetadataUpdate(
            "Altered Carbon",
            MediaKind.TvShow,
            "2018",
            "A resurrected soldier investigates a conspiracy.",
            [@"X:\Sample Media\Example.Show.S01"],
            null,
            "TVmaze",
            "12036",
            "https://www.tvmaze.com/shows/12036/altered-carbon");

        var first = Assert.Single(await service.ApplyAsync([update]));
        var second = Assert.Single(await service.ApplyAsync([update]));

        Assert.True(first.Applied);
        Assert.True(first.Changed);
        Assert.True(second.Applied);
        Assert.False(second.Changed);
        Assert.Equal("series", second.TitleId);
    }

    private static async Task SeedAsync(
        string database,
        string titleId,
        int kind,
        string displayTitle,
        string? year,
        string mediaPath)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MediaTitle(
                Id, Kind, DisplayTitle, SortTitle, Year, Description, Quality,
                ArtworkPath, BackdropPath, CreatedUtc, UpdatedUtc)
            VALUES($id, $kind, $display, $display, $year, '', 'Video', NULL, NULL, $now, $now);

            INSERT INTO MediaFile(
                Id, TitleId, SeasonId, EpisodeId, RootPath, Path, Quality,
                SizeBytes, LastWriteUtc, Availability, MissingSinceUtc, AddedUtc, UpdatedUtc)
            VALUES('file', $id, NULL, NULL, 'C:\', $path, 'Video',
                100, $now, 0, NULL, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", titleId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$display", displayTitle);
        command.Parameters.AddWithValue("$year", (object?)year ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", mediaPath);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
