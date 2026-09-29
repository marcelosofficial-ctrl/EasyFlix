namespace EasyFlix.Core.Tests;

public sealed class ManualWatchStateContractTests
{
    [Fact]
    public void TitleDetails_UsesRealDurationForManualWatchState()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "TitleDetailsWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "TitleDetailsWindow.xaml.cs"));

        Assert.Contains("Click=\"MarkWatched_Click\" Content=\"Mark watched\"", xaml);
        Assert.Contains("Click=\"MarkUnwatched_Click\" Content=\"Mark unwatched\"", xaml);
        Assert.Contains("CatalogMediaProbeService.DurationAsync(file.Path)", code);
        Assert.Contains("_store.SavePlaybackProgressAsync(file.Id, durationSeconds, durationSeconds)", code);
        Assert.Contains("ClearSelectedProgressAsync(\"Marked unwatched\")", code);
        Assert.Contains("FFprobe is required to mark a file watched without playing it.", code);
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
