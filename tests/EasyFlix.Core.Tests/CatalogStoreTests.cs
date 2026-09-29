using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class CatalogStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "EasyFlixCatalogStore-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RootTransitions_KeepTitleWhileOfflineAndMissing()
    {
        Directory.CreateDirectory(_folder);
        var database = Path.Combine(_folder, "catalog.db");
        var root = Path.Combine(_folder, "media");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Example.Movie.2020.1080p.mkv");
        File.WriteAllBytes(path, [1, 2, 3]);

        var titleId = CatalogIdentity.TitleId(MediaKind.Movie, "Example Movie", "2020");
        var fileId = CatalogIdentity.FileId(path);
        var snapshot = new CatalogRootSnapshot(
            root,
            [new CatalogTitleRecord(titleId, MediaKind.Movie, "Example Movie", "Example Movie", "2020", "", "1080p", null, null)],
            [],
            [],
            [new CatalogFileRecord(fileId, titleId, null, null, PathSafety.Normalize(root), PathSafety.Normalize(path), "1080p", 3, DateTimeOffset.UtcNow)]);

        var store = new CatalogStore(database);
        await store.ReplaceOnlineRootAsync(snapshot);
        var online = Assert.Single(await store.LoadTitleSummariesAsync());
        Assert.Equal(CatalogAvailability.Online, online.Availability);

        await store.MarkRootOfflineAsync(root);
        var offline = Assert.Single(await store.LoadTitleSummariesAsync());
        Assert.Equal(CatalogAvailability.Offline, offline.Availability);
        Assert.Equal(PathSafety.Normalize(root), offline.RequiredRootPath);
        Assert.Contains(
            Path.GetPathRoot(root)!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            offline.AvailabilityLabel,
            StringComparison.OrdinalIgnoreCase);

        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(root, [], [], [], []));
        var missing = Assert.Single(await store.LoadTitleSummariesAsync());
        Assert.Equal(CatalogAvailability.Missing, missing.Availability);
    }

    [Fact]
    public async Task Details_RoundTripMovieFile()
    {
        Directory.CreateDirectory(_folder);
        var database = Path.Combine(_folder, "details.db");
        var root = Path.Combine(_folder, "media2");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Moon.2009.mkv");
        File.WriteAllBytes(path, [1]);
        var titleId = CatalogIdentity.TitleId(MediaKind.Movie, "Moon", "2009");

        var store = new CatalogStore(database);
        await store.ReplaceOnlineRootAsync(new CatalogRootSnapshot(
            root,
            [new CatalogTitleRecord(titleId, MediaKind.Movie, "Moon", "Moon", "2009", "", "1080p", null, null)],
            [],
            [],
            [new CatalogFileRecord(CatalogIdentity.FileId(path), titleId, null, null, PathSafety.Normalize(root), PathSafety.Normalize(path), "1080p", 1, DateTimeOffset.UtcNow)]));

        var details = await store.LoadTitleDetailsAsync(titleId);

        Assert.NotNull(details);
        Assert.Equal("Moon", details.Title.DisplayTitle);
        Assert.Single(details.MovieFiles);
        Assert.Empty(details.Seasons);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
        catch { }
    }
}
