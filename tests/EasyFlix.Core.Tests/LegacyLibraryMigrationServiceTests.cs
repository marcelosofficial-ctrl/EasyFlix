using EasyFlix.Core.Models;
using EasyFlix.Core.Services;
using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Tests;

public sealed class LegacyLibraryMigrationServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "EasyFlix-LegacyMigrationTests-" + Guid.NewGuid().ToString("N"));

    public LegacyLibraryMigrationServiceTests()
        => Directory.CreateDirectory(_folder);

    [Theory]
    [InlineData("TV", "For All Mankind - Season 5 [4K HDR]", "For All Mankind", null, 5, MediaKind.TvShow)]
    [InlineData("Anime", "To Love-Ru - Seasons 1-4 and OVAs [1080p]", "To Love-Ru", null, null, MediaKind.Anime)]
    [InlineData("Anime", "Future Boy Conan - Complete Series [BD]", "Future Boy Conan", null, null, MediaKind.Anime)]
    [InlineData("Anime", "Ghost in the Shell - Stand Alone Complex - Season 2 [1080p]", "Ghost in the Shell - Stand Alone Complex", null, 2, MediaKind.Anime)]
    [InlineData("TV", "Silo (2023) - Season 1 [1080p]", "Silo", "2023", 1, MediaKind.TvShow)]
    [InlineData("Movies", "Hunter x Hunter - Phantom Rouge (2013) [1080p]", "Hunter x Hunter - Phantom Rouge", "2013", null, MediaKind.Movie)]
    [InlineData("Movies", "[EXT-F] Blade Runner 2049 (2017) [4K HDR]", "Blade Runner 2049", "2017", null, MediaKind.Movie)]
    public void LegacyShortcutNamesProduceCanonicalIdentity(
        string category,
        string shortcut,
        string title,
        string? year,
        int? season,
        MediaKind kind)
    {
        var identity = LegacyLibraryIdentity.Parse(category, shortcut);
        Assert.Equal(kind, identity.Kind);
        Assert.Equal(title, identity.CanonicalTitle);
        Assert.Equal(year, identity.Year);
        Assert.Equal(season, identity.SeasonHint);
    }

    [Fact]
    public async Task ImportRegroupsFragmentedSeriesAndPersistsLocalArtwork()
    {
        var dbPath = Path.Combine(_folder, "catalog", "easyflix.db");
        var root = Path.Combine(_folder, "media");
        var series = Path.Combine(root, "Parasyte the Maxim");
        Directory.CreateDirectory(series);

        var firstPath = Path.Combine(series, "[AnimeRG] Parasyte -the maxim- 01 [1080p].mkv");
        var secondPath = Path.Combine(series, "[AnimeRG] Parasyte -the maxim- 02 [1080p].mkv");
        await File.WriteAllTextAsync(firstPath, string.Empty);
        await File.WriteAllTextAsync(secondPath, string.Empty);

        var iconPath = Path.Combine(_folder, "series_parasyte.ico");
        await File.WriteAllBytesAsync(iconPath, [0, 1, 2, 3]);

        var store = new CatalogStore(dbPath);
        await store.InitializeAsync();

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();

            var now = DateTimeOffset.UtcNow.ToString("O");
            for (var index = 0; index < 2; index++)
            {
                var path = index == 0 ? firstPath : secondPath;
                var titleId = CatalogIdentity.TitleId(MediaKind.Movie, "Wrong " + index, null);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO MediaTitle(
                        Id, Kind, DisplayTitle, SortTitle, Year, Description, Quality,
                        ArtworkPath, BackdropPath, CreatedUtc, UpdatedUtc)
                    VALUES($title, 0, $name, $name, NULL, 'wrong description', '1080p',
                           'wrong-poster.png', NULL, $now, $now);
                    INSERT INTO MediaFile(
                        Id, TitleId, SeasonId, EpisodeId, RootPath, Path, Quality,
                        SizeBytes, LastWriteUtc, Availability, MissingSinceUtc, AddedUtc, UpdatedUtc)
                    VALUES($file, $title, NULL, NULL, $root, $path, '1080p',
                           NULL, NULL, 0, NULL, $now, $now);
                    """;
                command.Parameters.AddWithValue("$title", titleId);
                command.Parameters.AddWithValue("$name", "Wrong " + index);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$file", CatalogIdentity.FileId(path));
                command.Parameters.AddWithValue("$root", root);
                command.Parameters.AddWithValue("$path", path);
                await command.ExecuteNonQueryAsync();
            }
        }

        var service = new LegacyLibraryMigrationService(store);
        var result = await service.ImportAsync(
            [new LegacyLibraryEntry(
                "Anime",
                "Parasyte - The Maxim - Complete Series [1080p]",
                series,
                iconPath)],
            Path.Combine(_folder, "cache"));

        Assert.Equal(2, result.FilesReassigned);
        Assert.Equal(1, result.CanonicalTitles);
        Assert.Empty(result.UnmatchedEntries);

        var title = Assert.Single(await store.LoadTitleSummariesAsync());
        Assert.Equal(MediaKind.Anime, title.Kind);
        Assert.Equal("Parasyte - The Maxim", title.DisplayTitle);
        Assert.Equal(2, title.EpisodeCount);
        Assert.True(File.Exists(title.ArtworkPath));
        Assert.Equal(string.Empty, title.Description);

        var appliedAgain = await service.ApplySavedBindingsAsync();
        Assert.Equal(2, appliedAgain.FilesReassigned);
        Assert.Single(await store.LoadTitleSummariesAsync());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch
        {
        }
    }
}
