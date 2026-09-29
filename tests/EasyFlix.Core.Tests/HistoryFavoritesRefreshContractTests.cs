namespace EasyFlix.Core.Tests;

public sealed class HistoryFavoritesRefreshContractTests
{
    [Fact]
    public void BrowseRefreshesFavoritesAfterWatchHistoryCloses()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "EasyFlix.App", "BrowseWindow.xaml.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private async void History_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async void Setup_Click", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var handler = source[start..end];
        Assert.Contains("PlaybackHistoryWindow", handler);
        Assert.Contains("RefreshFavoritesAsync", handler);
        Assert.Contains("RefreshContinueWatchingAsync", handler);
        Assert.Contains("RefreshUpNextAsync", handler);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyFlix.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("EasyFlix repository root not found.");
    }
}
