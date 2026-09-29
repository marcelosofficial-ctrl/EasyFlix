using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class CatalogReconcilerTimingTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "EasyFlix-ReconcileTiming-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReconcileAsync_ReportsBoundedPerRootTimingsWithoutChangingSummaryCounts()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "media")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "Example.Movie.2024.mkv"), "test");

        var store = new CatalogStore(Path.Combine(_temp, "catalog", "test.db"));
        var result = await new CatalogReconciler(store).ReconcileAsync(new ScanOptions([root], [], _temp));

        Assert.Equal(1, result.RootsOnline);
        Assert.Equal(0, result.RootsOffline);
        Assert.True(result.FilesObserved >= 1);
        Assert.True(result.Elapsed >= TimeSpan.Zero);

        var timing = Assert.Single(result.RootTimings);
        Assert.True(timing.Online);
        Assert.True(timing.ScanElapsed >= TimeSpan.Zero);
        Assert.True(timing.SnapshotElapsed >= TimeSpan.Zero);
        Assert.True(timing.CatalogWriteElapsed >= TimeSpan.Zero);
        Assert.Equal(result.FilesObserved, timing.FilesObserved);
        Assert.Equal(timing.ScanElapsed, result.ScanElapsed);
        Assert.Equal(timing.SnapshotElapsed, result.SnapshotElapsed);
        Assert.Equal(timing.CatalogWriteElapsed, result.CatalogWriteElapsed);
        Assert.Null(typeof(CatalogRootReconcileTiming).GetProperty("RootPath"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp))
                Directory.Delete(_temp, recursive: true);
        }
        catch
        {
        }
    }
}