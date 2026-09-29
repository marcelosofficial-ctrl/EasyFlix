using EasyFlix.Core.Services;
using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Tests;

public sealed class LegacyArtworkPathRepairTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "EasyFlix-LegacyArtworkRepair-" + Guid.NewGuid().ToString("N"));

    public LegacyArtworkPathRepairTests()
        => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task RewritesBindingTitleAndManualArtworkAssetTogether()
    {
        var dbPath = Path.Combine(_folder, "catalog", "easyflix.db");
        var oldPath = Path.Combine(_folder, "old-legacy.ico");
        var newPath = Path.Combine(_folder, "old-legacy.png");
        await File.WriteAllBytesAsync(oldPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(newPath, [4, 5, 6]);

        var store = new CatalogStore(dbPath);
        await store.InitializeAsync();
        var service = new LegacyLibraryMigrationService(store);
        await service.HasBindingsAsync();

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
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO MediaTitle(
                    Id, Kind, DisplayTitle, SortTitle, Year, Description, Quality,
                    ArtworkPath, BackdropPath, CreatedUtc, UpdatedUtc)
                VALUES('title', 0, 'Title', 'Title', NULL, '', 'Video',
                       $oldPath, NULL, $now, $now);

                INSERT INTO ArtworkAsset(
                    Id, TitleId, Kind, SourceUrl, LocalPath, Width, Height)
                VALUES('title:poster-manual', 'title', 'poster-manual', NULL, $oldPath, NULL, NULL);

                INSERT INTO LegacyLibraryBinding(
                    Id, TargetPath, IsDirectory, Kind, CanonicalTitle, Year,
                    SeasonHint, LocalArtworkPath, ShortcutName, UpdatedUtc)
                VALUES('binding', 'C:\\media\\title.mkv', 0, 0, 'Title', NULL,
                       NULL, $oldPath, 'Title', $now);
                """;
            command.Parameters.AddWithValue("$oldPath", oldPath);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync();
        }

        await service.ReplaceLegacyArtworkPathsAsync(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [oldPath] = newPath
            });

        await using var verify = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await verify.OpenAsync();

        static async Task<string?> ReadScalarAsync(SqliteConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (string?)await command.ExecuteScalarAsync();
        }

        Assert.Equal(newPath, await ReadScalarAsync(
            verify, "SELECT ArtworkPath FROM MediaTitle WHERE Id='title';"));
        Assert.Equal(newPath, await ReadScalarAsync(
            verify, "SELECT LocalPath FROM ArtworkAsset WHERE Id='title:poster-manual';"));
        Assert.Equal(newPath, await ReadScalarAsync(
            verify, "SELECT LocalArtworkPath FROM LegacyLibraryBinding WHERE Id='binding';"));
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
