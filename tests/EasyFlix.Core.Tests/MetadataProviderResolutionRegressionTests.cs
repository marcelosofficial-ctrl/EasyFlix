using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MetadataProviderResolutionRegressionTests
{
    [Theory]
    [InlineData("Arcane", "Cane")]
    [InlineData("Trailers", "Travelers")]
    [InlineData("Blade Runner", "Blade Runner 2099")]
    [InlineData("Blade Runner 2049", "Blade Runner 2099")]
    public void FalsePositiveProviderPatterns_AreRejected(string wanted, string candidate)
        => Assert.False(MetadataMatchPolicy.IsAcceptable(wanted, candidate));

    [Theory]
    [InlineData("Drama")]
    [InlineData("Specials")]
    [InlineData("Trailers")]
    [InlineData("Extras")]
    [InlineData("Featurettes")]
    public void LowInformationAliases_AreNeverProviderAuthority(string value)
        => Assert.False(MetadataMatchPolicy.IsAcceptable(value, value));

    [Theory]
    [InlineData("Arcane", "Arcane")]
    [InlineData("Blade Runner 2049", "Blade Runner 2049")]
    [InlineData("Samurai Champloo", "Samurai Champloo")]
    [InlineData("To Love-Ru", "To Love Ru")]
    [InlineData("Hunter x Hunter", "Hunter X Hunter")]
    public void MeaningfulEquivalentTitles_RemainAccepted(string wanted, string candidate)
        => Assert.True(MetadataMatchPolicy.IsAcceptable(wanted, candidate));

    [Fact]
    public void AliasBuilder_SkipsGenericNestedFolderAndKeepsSeriesContext()
    {
        var title = new CatalogTitleSummary(
            "title-1",
            MediaKind.Anime,
            "Trailers",
            null,
            string.Empty,
            "1080p",
            null,
            null,
            CatalogAvailability.Online,
            0,
            0,
            DateTimeOffset.UtcNow);
        var file = new CatalogMediaFileSummary(
            "file-1",
            @"M:\Anime\Samurai Champloo\Trailers\Samurai Champloo - S00E01 - Trailer.mkv",
            @"M:\Anime",
            "1080p",
            CatalogAvailability.Online,
            null,
            null);
        var episode = new CatalogEpisodeSummary("episode-1", 1, "Trailer", file);
        var season = new CatalogSeasonSummary("season-1", 0, "Specials", [episode]);
        var details = new CatalogTitleDetails(title, [season], []);

        var aliases = MetadataSearchAliasBuilder.Build(title, details);

        Assert.DoesNotContain(aliases, alias => alias.Equals("Trailers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(aliases, alias => alias.Equals("Samurai Champloo", StringComparison.OrdinalIgnoreCase));
    }
}
