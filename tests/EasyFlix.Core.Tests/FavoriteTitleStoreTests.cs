using EasyFlix.Core.Services;
using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Tests;

public sealed class FavoriteTitleStoreTests
{
    [Fact]
    public async Task FavoriteState_RoundTripsThroughExistingUserOverrideStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easyflix-favorites-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "catalog.db");

        try
        {
            var store = new CatalogStore(databasePath);
            await store.InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO MediaTitle(Id, Kind, DisplayTitle, SortTitle, CreatedUtc, UpdatedUtc)
                    VALUES('title-1', 0, 'Example', 'Example', $now, $now);
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            Assert.False(await store.IsTitleFavoriteAsync("title-1"));

            await store.SetTitleFavoriteAsync("title-1", true);
            await store.SetTitleFavoriteAsync("title-1", true);

            Assert.True(await store.IsTitleFavoriteAsync("title-1"));
            var favorites = await store.LoadFavoriteTitleIdsAsync();
            Assert.Single(favorites);
            Assert.Equal("title-1", favorites[0]);

            await store.SetTitleFavoriteAsync("title-1", false);

            Assert.False(await store.IsTitleFavoriteAsync("title-1"));
            Assert.Empty(await store.LoadFavoriteTitleIdsAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Favorites_AreWiredIntoBrowseAndTitleDetails()
    {
        var root = FindRepositoryRoot();
        var browse = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "BrowseWindow.xaml"));
        var browseViewModel = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "ViewModels", "BrowseViewModel.cs"));
        var details = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "TitleDetailsWindow.xaml"));
        var detailsCode = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "TitleDetailsWindow.xaml.cs"));

        Assert.Contains("Tag=\"Favorites\"", browse);
        Assert.Contains("ShowFavoritesEmptyState", browse);
        Assert.Contains("\"Favorites\" => _allTitles.Where(title => _favoriteTitleIds.Contains(title.Id))", browseViewModel);
        Assert.Contains("Click=\"Favorite_Click\" Content=\"Add to Favorites\"", details);
        Assert.Contains("_store.SetTitleFavoriteAsync(_titleId, next)", detailsCode);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyFlix.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("EasyFlix repository root not found.");
    }
}
