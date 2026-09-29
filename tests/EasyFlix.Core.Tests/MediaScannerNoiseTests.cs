using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MediaScannerNoiseTests
{
    [Theory]
    [InlineData("MVI_2196.MP4")]
    [InlineData("MVI 2306.mov")]
    [InlineData("mvi-2451.m4v")]
    public void CameraStyleMviNamesAreIgnored(string fileName)
        => Assert.True(MediaScanner.IsIgnoredCameraClip(fileName));

    [Theory]
    [InlineData("MVI (2018).mkv")]
    [InlineData("Movie 2196.mp4")]
    [InlineData("MVI Documentary 2020.mkv")]
    public void RealTitleLikeNamesAreNotIgnored(string fileName)
        => Assert.False(MediaScanner.IsIgnoredCameraClip(fileName));
}
