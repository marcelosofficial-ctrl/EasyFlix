namespace EasyFlix.Core.Tests;

public sealed class SearchEmptyStateContractTests
{
    [Fact]
    public void SearchProvidesNoResultsFeedbackAndClearAction()
    {
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "ViewModels", "BrowseViewModel.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "BrowseWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "BrowseWindow.xaml.cs"));

        Assert.Contains(
            "public bool ShowSearchEmptyState => IsSearchActive && SearchResults.Count == 0;",
            viewModel);
        Assert.Contains("OnPropertyChanged(nameof(ShowSearchEmptyState));", viewModel);
        Assert.Contains(
            "Visibility=\"{Binding ShowSearchEmptyState, Converter={StaticResource BoolToVisibility}}\"",
            xaml);
        Assert.Contains("No titles match your search.", xaml);
        Assert.Contains("Click=\"ClearSearch_Click\"", xaml);

        var start = code.IndexOf("private void ClearSearch_Click", StringComparison.Ordinal);
        var end = code.IndexOf("private async void Section_Click", start, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.True(end > start);
        var handler = code[start..end];
        Assert.Contains("SearchBox.Text = string.Empty;", handler);
        Assert.Contains("SearchBox.Focus();", handler);
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

