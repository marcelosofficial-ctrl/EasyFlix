using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class LibraryPlannerTests
{
    [Theory]
    [InlineData(MediaKind.Movie, "")]
    [InlineData(MediaKind.TvShow, "TV Shows")]
    [InlineData(MediaKind.Anime, "Anime Series")]
    public void RoutesItemsToTheirLibraryCategory(MediaKind kind, string expectedFolder)
    {
        var item = new MediaItem
        {
            SourcePath = @"D:\Media\Example.mkv",
            SourceRoot = @"D:\Media",
            OriginalName = "Example.mkv",
            Quality = "1080p",
            Kind = kind,
            SuggestedName = "Example [1080p]",
            VideoCount = 1
        };
        var planner = new LibraryPlanner();

        var action = Assert.Single(planner.BuildPlan([item], @"C:\Library"));

        Assert.Equal(Path.Combine(@"C:\Library", expectedFolder, "Example [1080p].lnk"), action.ShortcutPath);
    }

    [Fact]
    public void OmitsUnselectedItems()
    {
        var item = new MediaItem
        {
            SourcePath = @"D:\Media\Example.mkv",
            SourceRoot = @"D:\Media",
            OriginalName = "Example.mkv",
            Quality = "1080p",
            Kind = MediaKind.Movie,
            SuggestedName = "Example [1080p]",
            VideoCount = 1,
            IsSelected = false
        };

        Assert.Empty(new LibraryPlanner().BuildPlan([item], @"C:\Library"));
    }
}
