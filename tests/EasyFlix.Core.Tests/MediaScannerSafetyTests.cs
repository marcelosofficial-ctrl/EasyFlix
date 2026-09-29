using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MediaScannerSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ReelShelfScannerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExcludedSubtreeIsPrunedFromResults()
    {
        var allowed = Directory.CreateDirectory(Path.Combine(_folder, "Allowed"));
        var excluded = Directory.CreateDirectory(Path.Combine(_folder, "NeverScan", "Nested"));
        await File.WriteAllTextAsync(Path.Combine(allowed.FullName, "Allowed.Movie.2024.1080p.mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(excluded.FullName, "Private.Movie.2024.1080p.mkv"), string.Empty);
        var options = new ScanOptions([_folder], [Path.Combine(_folder, "NeverScan")], Path.Combine(_folder, "Library"));

        var results = await new MediaScanner().ScanAsync(options);

        Assert.Single(results);
        Assert.Contains("Allowed.Movie", results[0].SourcePath);
    }

    [Fact]
    public async Task OverlappingRootsDoNotDuplicateMedia()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_folder, "Movies"));
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "One.Movie.2025.1080p.mkv"), string.Empty);
        var options = new ScanOptions([_folder, nested.FullName, _folder], [], Path.Combine(_folder, "Library"));

        var results = await new MediaScanner().ScanAsync(options);

        Assert.Single(results);
    }

    [Fact]
    public async Task AnimeDashAndPhaseEpisodesAreCoalescedInsteadOfEmittedAsMovies()
    {
        var anime = Directory.CreateDirectory(Path.Combine(_folder, "Anime"));
        var planetes = Directory.CreateDirectory(Path.Combine(anime.FullName, "Planetes"));
        var parasyte = Directory.CreateDirectory(Path.Combine(anime.FullName, "Parasyte"));

        await File.WriteAllTextAsync(Path.Combine(planetes.FullName, "Planetes Phase 01 1080p.mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(planetes.FullName, "Planetes Phase 02 1080p.mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(planetes.FullName, "Planetes Phase 03 1080p.mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(parasyte.FullName, "[AnimeRG] Parasyte - 01 [1080p].mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(parasyte.FullName, "[AnimeRG] Parasyte - 02 [1080p].mkv"), string.Empty);

        var results = await new MediaScanner().ScanAsync(
            new ScanOptions([anime.FullName], [], Path.Combine(_folder, "Library")));

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, item => item.Kind == MediaKind.Movie);

        var planetesResult = Assert.Single(results, item => item.SeriesTitle == "Planetes");
        Assert.True(planetesResult.IsFolder);
        Assert.Equal(MediaKind.Anime, planetesResult.Kind);
        Assert.Equal(3, planetesResult.VideoCount);

        var parasyteResult = Assert.Single(results, item => item.SeriesTitle == "Parasyte");
        Assert.True(parasyteResult.IsFolder);
        Assert.Equal(MediaKind.Anime, parasyteResult.Kind);
        Assert.Equal(2, parasyteResult.VideoCount);
    }

    [Fact]
    public async Task ContextualBareEpisodeNumbersCoalesceReleaseGroupAnimeSeries()
    {
        var anime = Directory.CreateDirectory(Path.Combine(_folder, "Anime"));
        var parasyte = Directory.CreateDirectory(Path.Combine(anime.FullName, "Parasyte the Maxim"));
        var conan = Directory.CreateDirectory(Path.Combine(anime.FullName, "Mirai Shounen Conan"));
        var champloo = Directory.CreateDirectory(Path.Combine(anime.FullName, "Samurai Champloo"));

        await File.WriteAllTextAsync(Path.Combine(parasyte.FullName, "[AnimeRG] Parasyte the Maxim 01 [1080p].mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(parasyte.FullName, "[AnimeRG] Parasyte the Maxim 02 [1080p].mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(conan.FullName, "[DB]Mirai Shounen Conan 01 [1080p].mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(conan.FullName, "[DB]Mirai Shounen Conan 02 [1080p].mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(champloo.FullName, "Samurai Champloo 01 [1080p].mkv"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(champloo.FullName, "Samurai Champloo 02 [1080p].mkv"), string.Empty);

        var results = await new MediaScanner().ScanAsync(
            new ScanOptions([anime.FullName], [], Path.Combine(_folder, "Library")));

        Assert.Equal(3, results.Count);
        Assert.DoesNotContain(results, item => item.Kind == MediaKind.Movie);
        Assert.All(results, item =>
        {
            Assert.True(item.IsFolder);
            Assert.Equal(MediaKind.Anime, item.Kind);
            Assert.Equal(2, item.VideoCount);
        });
        Assert.Contains(results, item => item.SeriesTitle == "Parasyte the Maxim");
        Assert.Contains(results, item => item.SeriesTitle == "Mirai Shounen Conan");
        Assert.Contains(results, item => item.SeriesTitle == "Samurai Champloo");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
