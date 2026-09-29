using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class SubtitleFileSafetyTests
{
    [Theory]
    [InlineData("en", "Movie.en.srt")]
    [InlineData("English", "Movie.en.srt")]
    [InlineData("ja", "Movie.ja.srt")]
    [InlineData("jpn", "Movie.ja.srt")]
    public void DestinationFor_UsesVlcLanguageSuffix(string language, string expected)
    {
        var result = SubtitleFileSafety.DestinationFor(Path.Combine(Path.GetTempPath(), "Movie.mkv"), language);
        Assert.Equal(Path.Combine(Path.GetTempPath(), expected), result);
    }

    [Fact]
    public void DestinationFor_RejectsUnsupportedLanguage()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SubtitleFileSafety.DestinationFor(Path.Combine(Path.GetTempPath(), "Movie.mkv"), "fr"));

    [Fact]
    public void DestinationForGenerated_LabelsMachineGeneratedTrack()
    {
        var result = SubtitleFileSafety.DestinationForGenerated(Path.Combine(Path.GetTempPath(), "Movie.mkv"), "en");

        Assert.EndsWith("Movie.en.generated.srt", result, StringComparison.OrdinalIgnoreCase);
    }
}
