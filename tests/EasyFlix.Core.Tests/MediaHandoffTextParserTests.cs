using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MediaHandoffTextParserTests
{
    [Fact]
    public void ParsesMultiSeasonHandoffIntoOneEntryPerSource()
    {
        const string text = """
            EASYFLIX ΓÇö MEDIA ADDITIONS

            TV SHOWS

            Example Series (2024) ΓÇö Seasons 1ΓÇô2 [1080p]
            Description: A supplied series description.
            Season 1 media: X:\Sample Media\Example.S01
            Season 1 shortcut: C:\Library\TV Shows\Example - Season 1.lnk
            Season 2 media: X:\Sample Media\Example.S02
            Season 2 shortcut: C:\Library\TV Shows\Example - Season 2.lnk
            Artwork: C:\Library\_Artwork\example.ico
            Match: TVMaze show 12345 ΓÇö https://www.tvmaze.com/shows/12345/example
            """;

        var result = MediaHandoffTextParser.Parse(text);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, item =>
        {
            Assert.Equal("Example Series", item.Title);
            Assert.Equal(MediaKind.TvShow, item.Kind);
            Assert.Equal("2024", item.Year);
            Assert.Equal("A supplied series description.", item.Description);
            Assert.Equal("TVmaze", item.Provider);
            Assert.Equal("12345", item.ProviderId);
            Assert.Equal(@"C:\Library\_Artwork\example.ico", item.ArtworkPath);
        });
        Assert.Equal(@"X:\Sample Media\Example.S01", result.Entries[0].MediaPath);
        Assert.Equal(@"C:\Library\TV Shows\Example - Season 1.lnk", result.Entries[0].ShortcutPath);
        Assert.Equal(@"X:\Sample Media\Example.S02", result.Entries[1].MediaPath);
        Assert.Equal(@"C:\Library\TV Shows\Example - Season 2.lnk", result.Entries[1].ShortcutPath);
    }

    [Fact]
    public void ReplacementUsesFinalMediaAndIgnoresHistoricalLocations()
    {
        const string text = """
            TV SHOWS

            THE EXAMPLE (2020) ΓÇö SEASON 5 [1080p] ΓÇö REPLACEMENT/UPDATE
            Description: A replacement season
            whose description wraps onto another line.
            Final media: X:\Sample Media\TV Shows\The Example Season 5
            Temporary replacement location (removed after verified relocation): Y:\Temporary\The Example Season 5
            Original deleted 4K location: X:\Sample Media\TV Shows\The.Example.S05.2160p
            Shortcut: C:\Library\TV Shows\The Example - Season 5 [1080p].lnk
            Artwork: C:\Library\_Artwork\example.ico
            Files: 10 episodes
            What changed:
            - The old version was deleted.
            """;

        var result = MediaHandoffTextParser.Parse(text);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("The Example", entry.Title);
        Assert.Equal(MediaKind.TvShow, entry.Kind);
        Assert.Equal("2020", entry.Year);
        Assert.Equal("A replacement season whose description wraps onto another line.", entry.Description);
        Assert.Equal(@"X:\Sample Media\TV Shows\The Example Season 5", entry.MediaPath);
        Assert.DoesNotContain(@"Y:\\", entry.MediaPath, StringComparison.OrdinalIgnoreCase);
        Assert.Null(entry.Provider);
    }

    [Fact]
    public void CombinesDistinctSeasonDescriptionsForOneLogicalSeries()
    {
        const string text = """
            TV SHOWS

            EXAMPLE (2022) ΓÇö SEASON 1 [1080p]
            Description: First supplied season sentence.
            Media: X:\Sample Media\Example.S01
            Shortcut: C:\Library\Example - Season 1.lnk
            Artwork: C:\Library\example.ico
            Exact match: TVMaze 444 ΓÇö https://www.tvmaze.com/shows/444/example

            EXAMPLE (2022) ΓÇö SEASON 2 [1080p]
            Description: Second supplied season sentence.
            Media: X:\Sample Media\Example.S02
            Shortcut: C:\Library\Example - Season 2.lnk
            Artwork: C:\Library\example.ico
            Exact match: TVMaze 444 ΓÇö https://www.tvmaze.com/shows/444/example
            """;

        var result = MediaHandoffTextParser.Parse(text);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, item =>
            Assert.Equal(
                "First supplied season sentence. Second supplied season sentence.",
                item.Description));
    }
}
