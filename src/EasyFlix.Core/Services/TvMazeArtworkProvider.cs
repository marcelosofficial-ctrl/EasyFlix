using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed class TvMazeArtworkProvider(HttpClient httpClient) : IArtworkProvider
{
    public string Name => "TVmaze";

    public async Task<ArtworkMatch?> FindAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        var query = Uri.EscapeDataString(MediaNameParser.CleanTitle(item.OriginalName));
        var rows = await httpClient.GetFromJsonAsync<TvMazeResult[]>($"https://api.tvmaze.com/search/shows?q={query}", cancellationToken) ?? [];
        var candidates = rows.Where(row => row.Show?.Image?.Original is not null && row.Show.Url is not null).Select(row =>
        {
            var score = TitleSimilarity.Score(MediaNameParser.CleanTitle(item.OriginalName), row.Show!.Name ?? string.Empty);
            if (item.Year is not null && row.Show.Premiered?.StartsWith(item.Year, StringComparison.Ordinal) == true) score += .25;
            return (row.Show, Score: Math.Min(score, 1));
        }).OrderByDescending(candidate => candidate.Score).ToArray();
        if (candidates.FirstOrDefault() is not { Score: >= .6 } best) return null;
        return new ArtworkMatch(Name, best.Show.Name!, new Uri(best.Show.Url!), new Uri(best.Show.Image!.Original!), best.Score);
    }

    private sealed record TvMazeResult([property: JsonPropertyName("show")] TvMazeShow? Show);
    private sealed record TvMazeShow(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("premiered")] string? Premiered,
        [property: JsonPropertyName("image")] TvMazeImage? Image);
    private sealed record TvMazeImage([property: JsonPropertyName("original")] string? Original);
}

internal static class TitleSimilarity
{
    public static double Score(string left, string right)
    {
        var wanted = Tokens(left);
        if (wanted.Count == 0) return 0;
        var actual = Tokens(right);
        return (double)wanted.Intersect(actual).Count() / wanted.Count;
    }

    private static HashSet<string> Tokens(string value) => value.ToLowerInvariant()
        .Split([' ', '-', ':', '.', ',', 'ΓÇô'], StringSplitOptions.RemoveEmptyEntries)
        .Where(token => token is not ("the" or "a" or "an" or "and"))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
