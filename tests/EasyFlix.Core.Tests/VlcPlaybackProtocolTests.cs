using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class VlcPlaybackProtocolTests
{
    [Theory]
    [InlineData("125", 125)]
    [InlineData("> 61", 61)]
    [InlineData(" 3600.5 ", 3600.5)]
    public void ParsesRcSecondResponses(string line, double expected)
    {
        Assert.True(VlcPlaybackProtocol.TryParseSeconds(line, out var actual));
        Assert.Equal(expected, actual, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("VLC media player")]
    [InlineData("-1")]
    public void RejectsNonTimeResponses(string line)
        => Assert.False(VlcPlaybackProtocol.TryParseSeconds(line, out _));

    [Fact]
    public void ResumeVerificationAllowsNormalStartupDrift()
    {
        Assert.True(VlcPlaybackProtocol.IsResumeApplied(126, 120));
        Assert.False(VlcPlaybackProtocol.IsResumeApplied(20, 120));
    }
}
