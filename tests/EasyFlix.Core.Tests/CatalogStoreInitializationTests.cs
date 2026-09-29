using EasyFlix.Core.Services;
using Xunit;

namespace EasyFlix.Core.Tests;

public sealed class CatalogStoreInitializationTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "EasyFlixCatalogInit-" + Guid.NewGuid().ToString("N"));

    public CatalogStoreInitializationTests()
        => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task InitializeAsync_AllowsRepeatedAndConcurrentCalls()
    {
        var store = new CatalogStore(Path.Combine(_temp, "catalog.db"));

        await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(_ => store.InitializeAsync()));

        var roots = await store.LoadKnownRootPathsAsync();

        Assert.Empty(roots);
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task InitializeAsync_CanceledFirstCallDoesNotPoisonLaterInitialization()
    {
        var store = new CatalogStore(Path.Combine(_temp, "retry.db"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.InitializeAsync(canceled.Token));

        await store.InitializeAsync();

        var roots = await store.LoadKnownRootPathsAsync();
        Assert.Empty(roots);
        Assert.True(File.Exists(store.DatabasePath));
    }

    [Fact]
    public async Task InitializeAsync_StillHonorsCancellationAfterInitialization()
    {
        var store = new CatalogStore(Path.Combine(_temp, "cancel-after.db"));
        await store.InitializeAsync();

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.InitializeAsync(canceled.Token));
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
