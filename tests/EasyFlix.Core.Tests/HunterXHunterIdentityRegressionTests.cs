using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class HunterXHunterIdentityRegressionTests
{
    [Fact]
    public void GenericIdentityPipeline_PreservesHunterXHunterSeriesIdentity()
    {
        const string directory = @"M:\Anime\Hunter x Hunter";
        const string episode = "Hunter.x.Hunter.S01E01.1080p.WEB-DL.mkv";

        Assert.True(LibraryIdentityHeuristics.LooksLikeAnimeSeries(directory, [episode]));
        Assert.Equal("Hunter X Hunter", MediaNameParser.FindSeriesTitle(episode));
        Assert.True(MetadataMatchPolicy.IsAcceptable("Hunter x Hunter", "Hunter X Hunter"));
    }
}
