using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MediaNameParserTests
{
    [Fact]
    public void CleanTitle_RemovesEmptyYearBrackets()
        => Assert.Equal("28 Days Later", MediaNameParser.CleanTitle("28 Days Later (2002) 1080p.mkv"));

    [Fact]
    public void BuildsCleanMovieNameFromReleaseFilename()
    {
        const string release = "Oppenheimer.2023.2160p.UHD.Bluray.REMUX.HDR10.HEVC.DTS-HD.MA.5.1-GHD[TGx].mkv";

        var result = MediaNameParser.BuildDisplayName(release, MediaKind.Movie, removable: false);

        Assert.Equal("Oppenheimer (2023) [4K HDR]", result);
    }

    [Fact]
    public void LabelsRemovableMediaWithoutChangingTheSource()
    {
        const string release = "Trainspotting.1996.1080p.BrRip.x264.mp4";

        var result = MediaNameParser.BuildDisplayName(release, MediaKind.Movie, removable: true);

        Assert.Equal("[EXT] Trainspotting (1996) [1080p]", result);
    }

    [Theory]
    [InlineData("Show.S02E04.1080p.mkv")]
    [InlineData("Show Episode 12.mp4")]
    [InlineData("[Ember] Dr. Stone S4 - 01 [1080p].mkv")]
    [InlineData("[AnimeRG] Parasyte - 01 [1080p].mkv")]
    [InlineData("Samurai Champloo - 07.mkv")]
    [InlineData("Planetes Phase 01 1080p.mkv")]
    public void RecognizesEpisodeNames(string filename)
        => Assert.True(MediaNameParser.LooksLikeEpisode(filename));

    [Theory]
    [InlineData("Show.S02E04.1080p.mkv", "Shows", 2, 4)]
    [InlineData("[SubsPlease] 2.5-jigen no Ririsa - 01 (1080p).mkv.mp4", "2.5-jigen no Ririsa (Season 1) [1080p]", 1, 1)]
    [InlineData("[Ember] Dr. Stone S4 - 01 [1080p].mkv", "Dr. Stone", 4, 1)]
    [InlineData("[AnimeRG] Parasyte - 03 [1080p].mkv", "Parasyte", 1, 3)]
    [InlineData("Planetes Phase 12 1080p.mkv", "Planetes", 1, 12)]
    [InlineData("[AnimeRG] Parasyte the Maxim 03 [1080p].mkv", "Parasyte the Maxim", 1, 3)]
    [InlineData("[DB]Mirai Shounen Conan 07 [1080p].mkv", "Mirai Shounen Conan", 1, 7)]
    [InlineData("Samurai Champloo 18 [1080p].mkv", "Samurai Champloo", 1, 18)]
    public void FindsEpisodeNumbersUsingFilenameAndFolderContext(string filename, string parent, int season, int episode)
        => Assert.Equal((season, episode), MediaNameParser.FindSeasonEpisode(filename, parent));

    [Fact]
    public void PreservesDottedAbbreviationAndDoesNotTreatTitleWordAsExtension()
    {
        Assert.Equal("Dr. Stone", MediaNameParser.CleanTitle("Dr. Stone"));
        Assert.Equal("Dr. Stone", MediaNameParser.FindSeriesTitle("Dr. Stone"));
        Assert.Equal("Dr. Stone", MediaNameParser.FindSeriesTitle("Dr. Stone.S01E01.1080p.mkv"));
        Assert.Equal("Dr. Stone", MediaNameParser.FindSeriesTitle("[Ember] Dr. Stone S4 - 01 [1080p].mkv"));
    }

    [Theory]
    [InlineData("One.Piece.S01E03.1080p.mkv", "One Piece")]
    [InlineData("[AnimeRG] Parasyte Season 2 Episode 4.mkv", "Parasyte")]
    [InlineData("[AnimeRG] Parasyte - 04 [1080p].mkv", "Parasyte")]
    [InlineData("Samurai Champloo - 18.mkv", "Samurai Champloo")]
    [InlineData("Planetes Phase 26 1080p.mkv", "Planetes")]
    [InlineData("[AnimeRG] Parasyte the Maxim 04 [1080p].mkv", "Parasyte the Maxim")]
    [InlineData("[DB]Mirai Shounen Conan 11 [1080p].mkv", "Mirai Shounen Conan")]
    [InlineData("Samurai Champloo 21 [1080p].mkv", "Samurai Champloo")]
    public void ExtractsSeriesTitles(string filename, string expected)
        => Assert.Equal(expected, MediaNameParser.FindSeriesTitle(filename));

    [Theory]
    [InlineData("[AnimeRG] Parasyte the Maxim 01 [1080p].mkv", "C:\\Anime\\Parasyte the Maxim")]
    [InlineData("[DB]Mirai Shounen Conan 01 [1080p].mkv", "C:\\Anime\\Mirai Shounen Conan")]
    [InlineData("Samurai Champloo 01 [1080p].mkv", "C:\\Anime\\Samurai Champloo")]
    public void RecognizesBareEpisodeNumbersOnlyWithMatchingSeriesContext(string filename, string context)
        => Assert.True(MediaNameParser.LooksLikeEpisode(filename, context));

    [Theory]
    [InlineData("[YTS] Dune 2 [1080p].mkv", "C:\\Movies\\Dune 2")]
    [InlineData("Apollo 13.mkv", "C:\\Movies\\Apollo 13")]
    public void DoesNotTurnMovieTitleNumbersIntoEpisodes(string filename, string context)
        => Assert.False(MediaNameParser.LooksLikeEpisode(filename, context));

    [Theory]
    [InlineData("One.Piece.S01E03.mkv", "Season 01")]
    [InlineData("One Piece Season 12", "Season 12")]
    public void ExtractsSortableSeasonLabels(string value, string expected)
        => Assert.Equal(expected, MediaNameParser.FindSeasonLabel(value));

    [Theory]
    [InlineData("5.mp4", "Family Clips", "Family Clips \u2014 5")]
    [InlineData("1videoplayback.mp4", "Camera", "Camera \u2014 1videoplayback")]
    [InlineData("6.mkv", "movies", "Unidentified video \u2014 6")]
    public void AddsFolderContextToAmbiguousVideoNames(string filename, string parent, string expected)
        => Assert.Equal(expected, MediaNameParser.BuildDisplayName(filename, MediaKind.Movie, false, parent));

    [Theory]
    [InlineData("Movie.2026.2160p.DV.mkv")]
    [InlineData("Movie.2026.2160p.Dolby.Vision.mkv")]
    public void RecognizesDolbyVisionAsHdr(string filename)
        => Assert.Equal("4K HDR", MediaNameParser.FindQuality(filename));
}
