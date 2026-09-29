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
    public void RecognizesEpisodeNames(string filename)
        => Assert.True(MediaNameParser.LooksLikeEpisode(filename));

    [Theory]
    [InlineData("Show.S02E04.1080p.mkv", "Shows", 2, 4)]
    [InlineData("[SubsPlease] 2.5-jigen no Ririsa - 01 (1080p).mkv.mp4", "2.5-jigen no Ririsa (Season 1) [1080p]", 1, 1)]
    public void FindsEpisodeNumbersUsingFilenameAndFolderContext(string filename, string parent, int season, int episode)
        => Assert.Equal((season, episode), MediaNameParser.FindSeasonEpisode(filename, parent));

    [Theory]
    [InlineData("One.Piece.S01E03.1080p.mkv", "One Piece")]
    [InlineData("[AnimeRG] Parasyte Season 2 Episode 4.mkv", "Parasyte")]
    public void ExtractsSeriesTitles(string filename, string expected)
        => Assert.Equal(expected, MediaNameParser.FindSeriesTitle(filename));

    [Theory]
    [InlineData("One.Piece.S01E03.mkv", "Season 01")]
    [InlineData("One Piece Season 12", "Season 12")]
    public void ExtractsSortableSeasonLabels(string value, string expected)
        => Assert.Equal(expected, MediaNameParser.FindSeasonLabel(value));

    [Theory]
    [InlineData("5.mp4", "Family Clips", "Family Clips ΓÇö 5")]
    [InlineData("1videoplayback.mp4", "Camera", "Camera ΓÇö 1videoplayback")]
    [InlineData("6.mkv", "movies", "Unidentified video ΓÇö 6")]
    public void AddsFolderContextToAmbiguousVideoNames(string filename, string parent, string expected)
        => Assert.Equal(expected, MediaNameParser.BuildDisplayName(filename, MediaKind.Movie, false, parent));

    [Theory]
    [InlineData("Movie.2026.2160p.DV.mkv")]
    [InlineData("Movie.2026.2160p.Dolby.Vision.mkv")]
    public void RecognizesDolbyVisionAsHdr(string filename)
        => Assert.Equal("4K HDR", MediaNameParser.FindQuality(filename));
}
