using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed class TmdbArtworkProvider(HttpClient httpClient, string apiKey) : IArtworkProvider
{
    public string Name => "TMDB";

    public async Task<ArtworkMatch?> FindAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var query = Uri.EscapeDataString(MediaNameParser.CleanTitle(item.OriginalName));
        var year = item.Year is null ? string.Empty : $"&year={item.Year}";
        var response = await httpClient.GetFromJsonAsync<TmdbResponse>(
            $"https://api.themoviedb.org/3/search/movie?api_key={Uri.EscapeDataString(apiKey)}&query={query}{year}", cancellationToken);
        var candidates = (response?.Results ?? []).Where(result => result.PosterPath is not null).Select(result =>
        {
            var score = TitleSimilarity.Score(MediaNameParser.CleanTitle(item.OriginalName), result.Title ?? string.Empty);
            if (item.Year is not null && result.ReleaseDate?.StartsWith(item.Year, StringComparison.Ordinal) == true) score += .25;
            return (Result: result, Score: Math.Min(score, 1));
        }).OrderByDescending(candidate => candidate.Score).ToArray();
        if (candidates.FirstOrDefault() is not { Score: >= .6 } best) return null;
        return new ArtworkMatch(
            Name,
            best.Result.Title!,
            new Uri($"https://www.themoviedb.org/movie/{best.Result.Id}"),
            new Uri($"https://image.tmdb.org/t/p/w500{best.Result.PosterPath}"),
            best.Score);
    }

    private sealed record TmdbResponse([property: JsonPropertyName("results")] TmdbResult[]? Results);
    private sealed record TmdbResult(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("poster_path")] string? PosterPath);
}
