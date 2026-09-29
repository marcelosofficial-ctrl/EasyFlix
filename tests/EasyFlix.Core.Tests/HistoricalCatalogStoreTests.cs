using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class HistoricalCatalogStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "easyflix-historical-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public HistoricalCatalogStoreTests()
    {
        Directory.CreateDirectory(_folder);
        _databasePath = Path.Combine(_folder, "catalog.db");
    }

    [Fact]
    public async Task ImportsOfflineEntryWithoutTouchingSourceDrive()
    {
        var catalog = new CatalogStore(_databasePath);
        var store = new HistoricalCatalogStore(catalog);

        var count = await store.ImportOfflineAsync(
        [
            new HistoricalCatalogEntry(
                MediaKind.Movie,
                "Historical Movie",
                "2001",
                @"B:\Archive",
                @"B:\Archive\Historical Movie.mkv",
                "1080p")
        ]);

        var title = Assert.Single(await catalog.LoadTitleSummariesAsync());
        Assert.Equal(1, count);
        Assert.Equal("Historical Movie", title.DisplayTitle);
        Assert.Equal(CatalogAvailability.Offline, title.Availability);
    }

    [Fact]
    public async Task RejectsConfiguredProtectedPath()
    {
        var previous = EasyFlix.Core.EasyFlixSafetyPolicy.ProtectedPaths.ToArray();
        try
        {
            EasyFlix.Core.EasyFlixSafetyPolicy.ConfigureProtectedPaths([@"X:\Protected Media"]);

            var catalog = new CatalogStore(_databasePath);
            var store = new HistoricalCatalogStore(catalog);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportOfflineAsync(
            [
                new HistoricalCatalogEntry(
                    MediaKind.Movie,
                    "Protected",
                    null,
                    @"X:\Protected Media",
                    @"X:\Protected Media\Protected.mkv",
                    "Video")
            ]));
        }
        finally
        {
            EasyFlix.Core.EasyFlixSafetyPolicy.ConfigureProtectedPaths(previous);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch { }
    }
}
