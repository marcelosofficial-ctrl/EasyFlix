using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class CatalogMediaProbeServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "EasyFlixCatalogProbeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SidecarTracks_PreserveGeneratedAndTranslatedProvenance()
    {
        Directory.CreateDirectory(_folder);
        var video = Path.Combine(_folder, "Example.Movie.2024.mkv");
        File.WriteAllText(video, string.Empty);
        File.WriteAllText(Path.Combine(_folder, "Example.Movie.2024.en.generated.srt"), "1\n00:00:00,000 --> 00:00:01,000\nHello");
        File.WriteAllText(Path.Combine(_folder, "Example.Movie.2024.ja.translated.srt"), "1\n00:00:00,000 --> 00:00:01,000\nTest");

        var tracks = CatalogMediaProbeService.SidecarTracks(video)
            .OrderBy(track => track.Language, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(2, tracks.Length);
        Assert.Equal("en", tracks[0].Language);
        Assert.Equal("generated", tracks[0].Kind);
        Assert.Equal("ja", tracks[1].Language);
        Assert.Equal("translated", tracks[1].Kind);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
