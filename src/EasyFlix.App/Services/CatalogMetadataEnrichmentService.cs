using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record CatalogMetadataBatchResult(
    int Attempted,
    int Updated,
    int NoMatch,
    int ProviderFailures,
    int ArtworkFailures);

public sealed class CatalogMetadataEnrichmentService
{
    private const int MaximumProviderAliasAttempts = 6;
    private readonly CatalogStore _catalogStore;
    private readonly CatalogMetadataStore _store;
    private readonly SecretStore _secrets = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CatalogArtworkCacheService _artwork;

    public CatalogMetadataEnrichmentService(CatalogStore catalogStore)
    {
        _catalogStore = catalogStore;
        _store = new CatalogMetadataStore(catalogStore);
        _artwork = new CatalogArtworkCacheService(_http);
    }

    public bool HasMovieProviderConfigured => !string.IsNullOrWhiteSpace(GetTmdbApiKey());

    public async Task<CatalogMetadataBatchResult> EnrichAsync(
        IEnumerable<CatalogTitleSummary> titles,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = titles
            .Where(NeedsEnrichment)
            .GroupBy(title => title.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var updates = new ConcurrentBag<CatalogMetadataUpdate>();
        var noMatch = 0;
        var providerFailures = 0;
        var artworkFailures = 0;

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = 6
            },
            async (title, token) =>
            {
                try
                {
                    var details = await _catalogStore.LoadTitleDetailsAsync(title.Id, token);
                    var aliases = MetadataSearchAliasBuilder.Build(title, details);
                    progress?.Report($"Finding metadata for {title.DisplayTitle} ({aliases.Count} local title hint(s))");
                    var candidate = title.Kind == MediaKind.Movie
                        ? await FindMovieAsync(title, aliases, token)
                        : await FindShowAsync(title, aliases, token);

                    if (candidate is null)
                    {
                        Interlocked.Increment(ref noMatch);
                        return;
                    }

                    string? artworkPath = null;
                    if (string.IsNullOrWhiteSpace(title.ArtworkPath)
                        || IsLegacyArtwork(title.ArtworkPath))
                    {
                        try
                        {
                            artworkPath = await _artwork.CachePosterAsync(
                                title.Id,
                                candidate.PosterUrl,
                                token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            Interlocked.Increment(ref artworkFailures);
                            // Valid descriptive metadata should survive a transient poster failure.
                        }
                    }

                    string? backdropPath = null;
                    if (title.Kind == MediaKind.Movie
                        && string.IsNullOrWhiteSpace(title.BackdropPath)
                        && candidate.BackdropUrl is not null)
                    {
                        try
                        {
                            backdropPath = await _artwork.CacheBackdropAsync(
                                title.Id,
                                candidate.BackdropUrl,
                                token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            Interlocked.Increment(ref artworkFailures);
                        }
                    }

                    updates.Add(new CatalogMetadataUpdate(
                        title.Id,
                        candidate.Year,
                        candidate.Description,
                        artworkPath,
                        candidate.Provider,
                        candidate.ProviderId,
                        candidate.PageUrl.ToString(),
                        candidate.PosterUrl?.ToString(),
                        candidate.Confidence,
                        backdropPath,
                        candidate.BackdropUrl?.ToString()));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    Interlocked.Increment(ref providerFailures);
                    // Provider or network failures never block local browsing.
                }
            });

        var updated = 0;
        foreach (var update in updates)
        {
            await _store.ApplyAsync(update, cancellationToken);
            updated++;
        }

        return new CatalogMetadataBatchResult(
            pending.Length,
            updated,
            noMatch,
            providerFailures,
            artworkFailures);
    }

    public Task<int> ClearWeakAutomaticArtworkAsync(CancellationToken cancellationToken = default)
        => _store.ClearWeakAutomaticArtworkAsync(
            MetadataMatchPolicy.MinimumConfidence,
            cancellationToken);

    private static bool NeedsEnrichment(CatalogTitleSummary title)
        => string.IsNullOrWhiteSpace(title.Description)
            || string.IsNullOrWhiteSpace(title.ArtworkPath)
            || IsLegacyArtwork(title.ArtworkPath)
            || (title.Kind == MediaKind.Movie && string.IsNullOrWhiteSpace(title.BackdropPath));

    private static bool IsLegacyArtwork(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && (path.EndsWith("-legacy.png", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("-legacy.ico", StringComparison.OrdinalIgnoreCase));

    private async Task<MetadataCandidate?> FindMovieAsync(
        CatalogTitleSummary title,
        IReadOnlyList<string> aliases,
        CancellationToken cancellationToken)
    {
        var apiKey = GetTmdbApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var attempts = aliases.Take(MaximumProviderAliasAttempts).ToArray();
        for (var index = 0; index < attempts.Length; index++)
        {
            var alias = attempts[index];
            var candidate = await SearchMovieAliasAsync(
                title,
                alias,
                apiKey,
                includeYear: index == 0 && !string.IsNullOrWhiteSpace(title.Year),
                cancellationToken);
            if (candidate is not null)
                return await CompleteTmdbMovieArtworkAsync(candidate, apiKey, cancellationToken);

            if (index == 0 && !string.IsNullOrWhiteSpace(title.Year))
            {
                candidate = await SearchMovieAliasAsync(
                    title,
                    alias,
                    apiKey,
                    includeYear: false,
                    cancellationToken);
                if (candidate is not null)
                    return await CompleteTmdbMovieArtworkAsync(candidate, apiKey, cancellationToken);
            }
        }

        return null;
    }

    private async Task<MetadataCandidate?> SearchMovieAliasAsync(
        CatalogTitleSummary title,
        string alias,
        string apiKey,
        bool includeYear,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(alias);
        var year = includeYear && !string.IsNullOrWhiteSpace(title.Year)
            ? $"&year={Uri.EscapeDataString(title.Year)}"
            : string.Empty;
        var response = await GetJsonWithRetryAsync<TmdbResponse>(
            $"https://api.themoviedb.org/3/search/movie?api_key={Uri.EscapeDataString(apiKey)}&query={query}{year}",
            cancellationToken);

        return (response?.Results ?? [])
            .Where(result => !string.IsNullOrWhiteSpace(result.Title)
                || !string.IsNullOrWhiteSpace(result.OriginalTitle))
            .Select(result =>
            {
                var candidateYear = YearFromDate(result.ReleaseDate);
                var names = new[] { result.Title, result.OriginalTitle }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var best = names
                    .Select(name => new
                    {
                        Score = MetadataMatchPolicy.Score(alias, name, title.Year, candidateYear),
                        Acceptable = MetadataMatchPolicy.IsAcceptable(alias, name, title.Year, candidateYear)
                    })
                    .OrderByDescending(value => value.Score)
                    .First();

                Uri? poster = string.IsNullOrWhiteSpace(result.PosterPath)
                    ? null
                    : new Uri($"https://image.tmdb.org/t/p/w780{result.PosterPath}");

                Uri? backdrop = string.IsNullOrWhiteSpace(result.BackdropPath)
                    ? null
                    : new Uri($"https://image.tmdb.org/t/p/w1280{result.BackdropPath}");

                return new
                {
                    Candidate = new MetadataCandidate(
                        "TMDB",
                        result.Id.ToString(),
                        candidateYear,
                        result.Overview ?? string.Empty,
                        new Uri($"https://www.themoviedb.org/movie/{result.Id}"),
                        poster,
                        backdrop,
                        best.Score),
                    best.Acceptable
                };
            })
            .Where(value => value.Acceptable)
            .OrderByDescending(value => value.Candidate.Confidence)
            .Select(value => value.Candidate)
            .FirstOrDefault();
    }

    private async Task<MetadataCandidate> CompleteTmdbMovieArtworkAsync(
        MetadataCandidate candidate,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (candidate.PosterUrl is not null && candidate.BackdropUrl is not null)
            return candidate;

        var details = await GetJsonWithRetryAsync<TmdbMovieDetails>(
            $"https://api.themoviedb.org/3/movie/{Uri.EscapeDataString(candidate.ProviderId)}?api_key={Uri.EscapeDataString(apiKey)}",
            cancellationToken);
        if (details is null) return candidate;

        var poster = candidate.PosterUrl ?? TmdbImageUri("w780", details.PosterPath);
        var backdrop = candidate.BackdropUrl ?? TmdbImageUri("w1280", details.BackdropPath);
        return candidate with { PosterUrl = poster, BackdropUrl = backdrop };
    }

    private async Task<MetadataCandidate?> FindShowAsync(
        CatalogTitleSummary title,
        IReadOnlyList<string> aliases,
        CancellationToken cancellationToken)
    {
        foreach (var alias in aliases.Take(MaximumProviderAliasAttempts))
        {
            var query = Uri.EscapeDataString(alias);
            var rows = await GetJsonWithRetryAsync<TvMazeSearchRow[]>(
                $"https://api.tvmaze.com/search/shows?q={query}",
                cancellationToken) ?? [];

            var candidate = rows
                .Where(row => row.Show is { Name.Length: > 0, Url.Length: > 0 })
                .Select(row =>
                {
                    var show = row.Show!;
                    var candidateYear = YearFromDate(show.Premiered);
                    var confidence = MetadataMatchPolicy.Score(
                        alias,
                        show.Name!,
                        title.Year,
                        candidateYear);

                    var posterText = !string.IsNullOrWhiteSpace(show.Image?.Original)
                        ? show.Image.Original
                        : show.Image?.Medium;
                    Uri? poster = Uri.TryCreate(posterText, UriKind.Absolute, out var imageUri)
                        ? imageUri
                        : null;

                    return new
                    {
                        Candidate = new MetadataCandidate(
                            "TVmaze",
                            show.Id.ToString(),
                            candidateYear,
                            StripHtml(show.Summary),
                            new Uri(show.Url!),
                            poster,
                            null,
                            confidence),
                        Acceptable = MetadataMatchPolicy.IsAcceptable(
                            alias,
                            show.Name!,
                            title.Year,
                            candidateYear)
                    };
                })
                .Where(value => value.Acceptable)
                .OrderByDescending(value => value.Candidate.Confidence)
                .Select(value => value.Candidate)
                .FirstOrDefault();

            if (candidate is not null) return candidate;
        }

        var apiKey = GetTmdbApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            foreach (var alias in aliases.Take(MaximumProviderAliasAttempts))
            {
                var candidate = await SearchTmdbShowAliasAsync(
                    title,
                    alias,
                    apiKey,
                    cancellationToken);
                if (candidate is not null) return candidate;
            }
        }

        return null;
    }

    private async Task<MetadataCandidate?> SearchTmdbShowAliasAsync(
        CatalogTitleSummary title,
        string alias,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(alias);
        var response = await GetJsonWithRetryAsync<TmdbTvResponse>(
            $"https://api.themoviedb.org/3/search/tv?api_key={Uri.EscapeDataString(apiKey)}&query={query}",
            cancellationToken);

        return (response?.Results ?? [])
            .Where(result => !string.IsNullOrWhiteSpace(result.Name)
                || !string.IsNullOrWhiteSpace(result.OriginalName))
            .Select(result =>
            {
                var candidateYear = YearFromDate(result.FirstAirDate);
                var names = new[] { result.Name, result.OriginalName }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var best = names
                    .Select(name => new
                    {
                        Score = MetadataMatchPolicy.Score(alias, name, title.Year, candidateYear),
                        Acceptable = MetadataMatchPolicy.IsAcceptable(alias, name, title.Year, candidateYear)
                    })
                    .OrderByDescending(value => value.Score)
                    .First();

                return new
                {
                    Candidate = new MetadataCandidate(
                        "TMDB",
                        result.Id.ToString(),
                        candidateYear,
                        result.Overview ?? string.Empty,
                        new Uri($"https://www.themoviedb.org/tv/{result.Id}"),
                        TmdbImageUri("w780", result.PosterPath),
                        TmdbImageUri("w1280", result.BackdropPath),
                        best.Score),
                    best.Acceptable
                };
            })
            .Where(value => value.Acceptable)
            .OrderByDescending(value => value.Candidate.Confidence)
            .Select(value => value.Candidate)
            .FirstOrDefault();
    }

    private static Uri? TmdbImageUri(string size, string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : new Uri($"https://image.tmdb.org/t/p/{size}{path}");

    private async Task<T?> GetJsonWithRetryAsync<T>(
        string url,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(
                    cancellationToken: cancellationToken);

            var transient = response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
            if (!transient || attempt == maxAttempts - 1)
            {
                response.EnsureSuccessStatusCode();
                return default;
            }

            var delay = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromMilliseconds(750 * (attempt + 1));
            if (delay > TimeSpan.FromSeconds(10))
                delay = TimeSpan.FromSeconds(10);

            await Task.Delay(delay, cancellationToken);
        }

        return default;
    }

    private string GetTmdbApiKey()
        => _secrets.Get(SecretStore.TmdbApiKey)
            ?? Environment.GetEnvironmentVariable("TMDB_API_KEY")
            ?? string.Empty;

    private static string? YearFromDate(string? value)
        => value is { Length: >= 4 } && int.TryParse(value[..4], out _)
            ? value[..4]
            : null;

    private static string StripHtml(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " "))
                .Replace("  ", " ")
                .Trim();

    private sealed record MetadataCandidate(
        string Provider,
        string ProviderId,
        string? Year,
        string Description,
        Uri PageUrl,
        Uri? PosterUrl,
        Uri? BackdropUrl,
        double Confidence);

    private sealed record TmdbResponse(
        [property: JsonPropertyName("results")] TmdbResult[]? Results);

    private sealed record TmdbResult(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("original_title")] string? OriginalTitle,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath);

    private sealed record TmdbMovieDetails(
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath);

    private sealed record TvMazeSearchRow(
        [property: JsonPropertyName("show")] TvMazeShow? Show);

    private sealed record TvMazeShow(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("premiered")] string? Premiered,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("image")] TvMazeImage? Image);

    private sealed record TvMazeImage(
        [property: JsonPropertyName("medium")] string? Medium,
        [property: JsonPropertyName("original")] string? Original);

    private sealed record TmdbTvResponse(
        [property: JsonPropertyName("results")] TmdbTvResult[]? Results);

    private sealed record TmdbTvResult(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("original_name")] string? OriginalName,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("poster_path")] string? PosterPath,
        [property: JsonPropertyName("backdrop_path")] string? BackdropPath);
}
