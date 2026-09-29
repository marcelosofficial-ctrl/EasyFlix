using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MediaProbeServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ReelShelfTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DetectsOnlyLanguageNamedSidecarsForTheSameVideo()
    {
        Directory.CreateDirectory(_folder);
        var video = Path.Combine(_folder, "Example.Movie.2024.mkv");
        File.WriteAllText(video, string.Empty);
        File.WriteAllText(Path.Combine(_folder, "Example.Movie.2024.en.srt"), "1\n00:00:00,000 --> 00:00:01,000\nHello");
        File.WriteAllText(Path.Combine(_folder, "Different.Movie.ja.srt"), "1\n00:00:00,000 --> 00:00:01,000\nπüôπéôπü½πüíπü»");

        var languages = MediaProbeService.SidecarLanguages(video);

        Assert.Contains("en", languages);
        Assert.DoesNotContain("ja", languages);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
