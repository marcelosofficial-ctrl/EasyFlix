namespace EasyFlix.Core.Tests;

public sealed class SetupBrowseRefreshContractTests
{
    [Fact]
    public void BrowseReconcilesLibraryAfterSetupCloses()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "EasyFlix.App", "BrowseWindow.xaml.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private async void Setup_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async void Library_Click", start, StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var handler = source[start..end];
        Assert.Contains("FirstRunWindow", handler);
        Assert.Contains("ShowDialog()", handler);
        Assert.Contains("await _viewModel.RefreshAsync();", handler);
        Assert.True(
            handler.IndexOf("ShowDialog()", StringComparison.Ordinal)
            < handler.IndexOf("await _viewModel.RefreshAsync();", StringComparison.Ordinal));
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

