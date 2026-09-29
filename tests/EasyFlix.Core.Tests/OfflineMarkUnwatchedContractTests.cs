namespace EasyFlix.Core.Tests;

public sealed class OfflineMarkUnwatchedContractTests
{
    [Fact]
    public void MarkUnwatchedRequiresSelectionButNotPlayableMedia()
    {
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(
            Path.Combine(root, "src", "EasyFlix.App", "ViewModels", "TitleDetailsViewModel.cs"));
        var xaml = File.ReadAllText(
            Path.Combine(root, "src", "EasyFlix.App", "TitleDetailsWindow.xaml"));
        var code = File.ReadAllText(
            Path.Combine(root, "src", "EasyFlix.App", "TitleDetailsWindow.xaml.cs"));

        Assert.Contains("public bool HasSelectedEntry => SelectedEntry is not null;", viewModel);
        Assert.Contains("OnPropertyChanged(nameof(HasSelectedEntry));", viewModel);
        Assert.Contains(
            "Click=\"MarkUnwatched_Click\" Content=\"Mark unwatched\"",
            xaml);

        var button = xaml.IndexOf("Click=\"MarkUnwatched_Click\"", StringComparison.Ordinal);
        Assert.True(button >= 0);
        var buttonEnd = xaml.IndexOf("/>", button, StringComparison.Ordinal);
        Assert.True(buttonEnd > button);
        var buttonMarkup = xaml[button..buttonEnd];
        Assert.Contains("IsEnabled=\"{Binding HasSelectedEntry}\"", buttonMarkup);
        Assert.DoesNotContain("IsEnabled=\"{Binding CanPlay}\"", buttonMarkup);

        Assert.Contains("await _store.DeletePlaybackProgressAsync(file.Id);", code);
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

