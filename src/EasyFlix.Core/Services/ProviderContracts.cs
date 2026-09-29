using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed record ArtworkMatch(string Provider, string Title, Uri PageUrl, Uri ImageUrl, double Confidence);
public sealed record SubtitleMatch(string Provider, string Language, string ReleaseName, Uri DownloadUrl, double Confidence, string Format);

public interface IArtworkProvider
{
    string Name { get; }
    Task<ArtworkMatch?> FindAsync(MediaItem item, CancellationToken cancellationToken = default);
}

public interface ISubtitleProvider
{
    string Name { get; }
    Task<IReadOnlyList<SubtitleMatch>> FindAsync(MediaItem item, IReadOnlyCollection<string> languages, CancellationToken cancellationToken = default);
}
