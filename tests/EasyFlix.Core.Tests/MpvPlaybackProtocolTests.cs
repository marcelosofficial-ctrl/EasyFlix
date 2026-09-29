using System.Text.Json;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MpvPlaybackProtocolTests
{
    [Fact]
    public void EmbeddedTracksRequireUniqueExactFfIndex()
    {
        var tracks = new[]
        {
            new MpvTrackInfo(2, "audio", 3, false, null),
            new MpvTrackInfo(4, "sub", 7, false, null)
        };

        var selection = MpvPlaybackProtocol.ResolveSelection(
            tracks,
            3,
            new CatalogSubtitleStreamInfo("en", "embedded", null, 7),
            true);

        Assert.True(selection.AudioResolved);
        Assert.Equal(2, selection.AudioTrackId);
        Assert.True(selection.SubtitleResolved);
        Assert.Equal(4, selection.SubtitleTrackId);
    }

    [Fact]
    public void AmbiguousFfIndexIsNotResolved()
    {
        var selection = MpvPlaybackProtocol.ResolveSelection(
            new[]
            {
                new MpvTrackInfo(2, "audio", 3, false, null),
                new MpvTrackInfo(5, "audio", 3, false, null)
            },
            3,
            null,
            false);

        Assert.False(selection.AudioResolved);
        Assert.Null(selection.AudioTrackId);
    }

    [Fact]
    public void ExternalSubtitleMatchesByPath()
    {
        var subtitlePath = Path.GetFullPath(Path.Combine("subs", "movie.en.srt"));
        var selection = MpvPlaybackProtocol.ResolveSelection(
            new[] { new MpvTrackInfo(8, "sub", null, true, subtitlePath) },
            null,
            new CatalogSubtitleStreamInfo("en", "external", subtitlePath),
            true);

        Assert.True(selection.SubtitleResolved);
        Assert.Equal(8, selection.SubtitleTrackId);
    }

    [Fact]
    public void ExplicitSubtitleOffIsResolvable()
    {
        var selection = MpvPlaybackProtocol.ResolveSelection([], null, null, true);
        Assert.True(selection.SubtitleResolved);
        Assert.True(selection.DisableSubtitles);
    }

    [Theory]
    [InlineData("""{"event":"end-file","reason":"eof"}""", true)]
    [InlineData("""{"event":"end-file","reason":"stop"}""", false)]
    [InlineData("""{"event":"shutdown"}""", false)]
    public void IsNaturalEndFile_OnlyAcceptsEof(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, MpvPlaybackProtocol.IsNaturalEndFile(document.RootElement));
    }

    [Fact]
    public void ParseTrackListReadsStableMpvFields()
    {
        using var document = JsonDocument.Parse(
            """
            [
              {"id":2,"type":"audio","ff-index":3,"external":false},
              {"id":7,"type":"sub","external":true,"external-filename":"C:\\subs\\movie.en.srt"}
            ]
            """);

        var tracks = MpvPlaybackProtocol.ParseTrackList(document.RootElement);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(3, tracks[0].FfIndex);
        Assert.True(tracks[1].External);
        Assert.EndsWith("movie.en.srt", tracks[1].ExternalFilename, StringComparison.OrdinalIgnoreCase);
    }
}
