using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MetadataSearchAliasBuilderTests
{
    [Fact]
    public void MovieAliasesUseCleanParentFolderWhenDisplayTitleContainsEditionNoise()
    {
        var title = MakeTitle(MediaKind.Movie, "The Prestige Custom Encode", "2006");
        var file = new CatalogMediaFileSummary(
            "file-1",
            @"M:\Movies\The Prestige (2006)\The.Prestige.Custom.Encode.1080p.mkv",
            @"M:\Movies",
            "1080p",
            CatalogAvailability.Online,
            null,
            null);
        var details = new CatalogTitleDetails(title, [], [file]);

        var aliases = MetadataSearchAliasBuilder.Build(title, details);

        Assert.Contains("The Prestige", aliases);
    }

    [Fact]
    public void AnimeAliasesStripReleaseGroupAndEpisodeNumber()
    {
        var title = MakeTitle(MediaKind.Anime, "[AnimeRG] Parasyte the Maxim 01", null);
        var file = new CatalogMediaFileSummary(
            "file-1",
            @"M:\Anime\Parasyte the Maxim\[AnimeRG] Parasyte the Maxim 01 [1080p].mkv",
            @"M:\Anime",
            "1080p",
            CatalogAvailability.Online,
            null,
            null);
        var episode = new CatalogEpisodeSummary("episode-1", 1, "Episode 1", file);
        var season = new CatalogSeasonSummary("season-1", 1, "Season 1", [episode]);
        var details = new CatalogTitleDetails(title, [season], []);

        var aliases = MetadataSearchAliasBuilder.Build(title, details);

        Assert.Contains("Parasyte the Maxim", aliases);
    }

    [Fact]
    public void GenericLibraryFoldersAreNotUsedAsSearchAliases()
    {
        var title = MakeTitle(MediaKind.Movie, "Nausicaa of the Valley of the Wind", "1984");
        var file = new CatalogMediaFileSummary(
            "file-1",
            @"M:\Movies\Nausicaa.of.the.Valley.of.the.Wind.1984.1080p.mkv",
            @"M:\Movies",
            "1080p",
            CatalogAvailability.Online,
            null,
            null);
        var details = new CatalogTitleDetails(title, [], [file]);

        var aliases = MetadataSearchAliasBuilder.Build(title, details);

        Assert.DoesNotContain(aliases, alias => alias.Equals("Movies", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Nausicaa of the Valley of the Wind", aliases);
    }

    private static CatalogTitleSummary MakeTitle(MediaKind kind, string name, string? year)
        => new(
            "title-1",
            kind,
            name,
            year,
            string.Empty,
            "1080p",
            null,
            null,
            CatalogAvailability.Online,
            0,
            0,
            DateTimeOffset.UtcNow);
}
