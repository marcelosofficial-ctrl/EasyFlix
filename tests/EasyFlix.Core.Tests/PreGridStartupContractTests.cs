namespace EasyFlix.Core.Tests;

public sealed class PreGridStartupContractTests
{
    [Fact]
    public void InitializeAsync_DoesNotBlockCachedGridOnRootDiscoveryOrWatcherSetup()
    {
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "ViewModels", "BrowseViewModel.cs"));

        var start = viewModel.IndexOf("public async Task InitializeAsync()", StringComparison.Ordinal);
        var end = viewModel.IndexOf("public async Task HydratePersonalizedStartupAsync()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var initialize = viewModel[start..end];
        Assert.Contains("await _store.InitializeAsync(_lifetime.Token);", initialize);
        Assert.Contains("ReloadCatalogViewAsync(_lifetime.Token, includePersonalized: false)", initialize);
        Assert.DoesNotContain("BuildScanOptionsAsync", initialize);
        Assert.DoesNotContain("StartChangeMonitor()", initialize);

        var reconcile = viewModel.IndexOf("public async Task ReconcileAfterStartupAsync()", StringComparison.Ordinal);
        var reconcileEnd = viewModel.IndexOf("public async Task RefreshAsync()", reconcile, StringComparison.Ordinal);
        Assert.True(reconcile >= 0 && reconcileEnd > reconcile);
        var reconcileMethod = viewModel[reconcile..reconcileEnd];
        Assert.Contains("await ReconcileAndReloadAsync();", reconcileMethod);
        Assert.Contains("StartChangeMonitor();", reconcileMethod);
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
