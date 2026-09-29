using EasyFlix.Core.Models;
using EasyFlix.Core.Services;
using Xunit;

namespace EasyFlix.Core.Tests;

public sealed class CatalogMetadataStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "easyflix-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;

    public CatalogMetadataStoreTests()
    {
        Directory.CreateDirectory(_folder);
        _databasePath = Path.Combine(_folder, "catalog.db");
    }

    [Fact]
    public async Task AddsMetadataWithoutChangingOfflineAvailability()
    {
        var catalog = new CatalogStore(_databasePath);
        var historical = new HistoricalCatalogStore(catalog);
        await historical.ImportOfflineAsync(
        [
            new HistoricalCatalogEntry(
                MediaKind.Movie,
                "Metadata Movie",
                null,
                @"B:\Archive",
                @"B:\Archive\Metadata Movie.mkv",
                "1080p")
        ]);

        var titleId = CatalogIdentity.TitleId(MediaKind.Movie, "Metadata Movie", null);
        var artwork = Path.Combine(_folder, "poster.png");
        var store = new CatalogMetadataStore(catalog);

        await store.ApplyAsync(new CatalogMetadataUpdate(
            titleId,
            "2024",
            "Metadata description.",
            artwork,
            "TestProvider",
            "42",
            "https://example.invalid/title/42",
            "https://example.invalid/poster.png",
            .95));

        var title = Assert.Single(await catalog.LoadTitleSummariesAsync());
        Assert.Equal("2024", title.Year);
        Assert.Equal("Metadata description.", title.Description);
        Assert.Equal(artwork, title.ArtworkPath);
        Assert.Equal(CatalogAvailability.Offline, title.Availability);
    }

    [Fact]
    public async Task DoesNotOverwriteExistingDescriptiveMetadata()
    {
        var catalog = new CatalogStore(_databasePath);
        var historical = new HistoricalCatalogStore(catalog);
        await historical.ImportOfflineAsync(
        [
            new HistoricalCatalogEntry(
                MediaKind.Movie,
                "Stable Metadata",
                null,
                @"B:\Archive",
                @"B:\Archive\Stable Metadata.mkv",
                "Video")
        ]);

        var titleId = CatalogIdentity.TitleId(MediaKind.Movie, "Stable Metadata", null);
        var store = new CatalogMetadataStore(catalog);

        await store.ApplyAsync(new CatalogMetadataUpdate(
            titleId, "2020", "First description.", null,
            "ProviderOne", "1", "https://example.invalid/1", null, .90));
        await store.ApplyAsync(new CatalogMetadataUpdate(
            titleId, "2021", "Second description.", null,
            "ProviderOne", "1", "https://example.invalid/1", null, .91));

        var title = Assert.Single(await catalog.LoadTitleSummariesAsync());
        Assert.Equal("2020", title.Year);
        Assert.Equal("First description.", title.Description);
    }

    [Fact]
    public async Task ClearsWeakAutomaticArtworkButPreservesDescription()
    {
        var catalog = new CatalogStore(_databasePath);
        var historical = new HistoricalCatalogStore(catalog);
        await historical.ImportOfflineAsync(
        [
            new HistoricalCatalogEntry(
                MediaKind.Anime,
                "To Love-Ru",
                null,
                @"B:\Archive",
                @"B:\Archive\To Love-Ru\Episode 01.mkv",
                "1080p")
        ]);

        var titleId = CatalogIdentity.TitleId(MediaKind.Anime, "To Love-Ru", null);
        var artwork = Path.Combine(_folder, "wrong-poster.png");
        var store = new CatalogMetadataStore(catalog);

        await store.ApplyAsync(new CatalogMetadataUpdate(
            titleId, "2024", "Keep this description.", artwork,
            "TVmaze", "weak-match", "https://example.invalid/weak",
            "https://example.invalid/wrong.png", .67));

        var changed = await store.ClearWeakAutomaticArtworkAsync(.80);

        Assert.Equal(1, changed);
        var title = Assert.Single(await catalog.LoadTitleSummariesAsync());
        Assert.Null(title.ArtworkPath);
        Assert.Equal("Keep this description.", title.Description);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch { }
    }
}
