using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record OpenSubtitlesCredentials(string ApiKey, string Username, string Password);

public sealed partial class OpenSubtitlesService
{
    private const string DefaultBaseUrl = "https://api.opensubtitles.com/api/v1";
    private const string UserAgent = "EasyFlix/1.4.0";
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts" };
    private readonly HttpClient _apiHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(45) };
    private readonly HttpClient _downloadHttp = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly MediaProbeService _probe = new();
    private readonly SubtitleJobStore _jobs = new();

    public async Task<SubtitleRunSummary> DownloadMissingAsync(
        IEnumerable<MediaItem> selected,
        IReadOnlyCollection<string> excludedPaths,
        OpenSubtitlesCredentials credentials,
        int maximumDownloads,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey) ||
            string.IsNullOrWhiteSpace(credentials.Username) ||
            string.IsNullOrWhiteSpace(credentials.Password))
            throw new InvalidOperationException("Configure the OpenSubtitles API key, username, and password first.");

        var session = await LoginAsync(credentials, cancellationToken);
        var excluded = excludedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(PathSafety.Normalize).ToArray();
        var selectedItems = selected.ToArray();
        var entries = selectedItems
            .SelectMany(item => VideoFiles(item, excluded).Select(path => (Item: item, Path: path)))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var counts = selectedItems.ToDictionary(item => item, _ => new int[4]);
        foreach (var entry in entries) counts[entry.Item][3]++;
        var searches = 0;
        var installed = 0;
        var rejected = 0;
        var deferred = 0;
        var filesChecked = 0;
        var rateLimited = session.AllowedDownloads <= 0;
        var deferredKeys = _jobs.ReadDeferredKeys("OpenSubtitles", TimeSpan.FromDays(7));

        for (var index = 0; index < entries.Length && installed < maximumDownloads && !rateLimited; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, mediaPath) = entries[index];
            progress?.Report($"OpenSubtitles {index + 1:N0}/{entries.Length:N0}: {Path.GetFileName(mediaPath)}");
            var available = (await _probe.SubtitleLanguagesAsync(mediaPath, cancellationToken: cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            filesChecked++;
            counts[item][0]++;
            if (available.Contains("en")) counts[item][1]++;
            if (available.Contains("ja")) counts[item][2]++;
            var missing = new[] { "en", "ja" }.Where(language => !available.Contains(language)).ToArray();
            var needed = missing.Where(language => !deferredKeys.Contains(SubtitleJobStore.AttemptKey(mediaPath, language))).ToArray();
            deferred += missing.Length - needed.Length;
            if (needed.Length == 0) continue;

            string movieHash;
            try { movieHash = OpenSubtitlesMovieHash.Compute(mediaPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { continue; }

            var episode = MediaNameParser.FindSeasonEpisode(Path.GetFileName(mediaPath), Path.GetDirectoryName(mediaPath));
            var query = BuildSearchQuery(mediaPath, movieHash, needed, episode);
            using var search = await SendApiAsync(HttpMethod.Get, $"{session.BaseUrl}/subtitles?{query}", credentials.ApiKey, session.Token, null, cancellationToken);
            searches++;
            if (search.StatusCode == HttpStatusCode.TooManyRequests) { rateLimited = true; break; }
            if (!search.IsSuccessStatusCode) continue;
            using var document = JsonDocument.Parse(await search.Content.ReadAsStreamAsync(cancellationToken));

            foreach (var language in needed)
            {
                if (installed >= maximumDownloads) break;
                var candidate = FindBestCandidate(document.RootElement, item, mediaPath, episode, language);
                if (candidate is null)
                {
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "NotFound", "OpenSubtitles ┬╖ no exact safe match"), cancellationToken);
                    continue;
                }
                var destination = Path.Combine(Path.GetDirectoryName(mediaPath)!, $"{Path.GetFileNameWithoutExtension(mediaPath)}.{language}.srt");
                try
                {
                    var requestBody = JsonSerializer.SerializeToUtf8Bytes(new { file_id = candidate.Value.FileId, sub_format = "srt" });
                    using var downloadRequest = await SendApiAsync(HttpMethod.Post, $"{session.BaseUrl}/download", credentials.ApiKey, session.Token, requestBody, cancellationToken);
                    if (downloadRequest.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.NotAcceptable) { rateLimited = true; break; }
                    downloadRequest.EnsureSuccessStatusCode();
                    using var downloadJson = JsonDocument.Parse(await downloadRequest.Content.ReadAsStreamAsync(cancellationToken));
                    var fileName = Text(downloadJson.RootElement, "file_name");
                    var link = Text(downloadJson.RootElement, "link");
                    if (!fileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("OpenSubtitles did not declare an SRT filename.");
                    var payload = await DownloadSrtAsync(link, fileName, cancellationToken);
                    var duration = await MediaProbeService.DurationAsync(mediaPath, cancellationToken: cancellationToken);
                    var validation = SrtValidator.Validate(payload, duration);
                    if (!validation.IsValid) throw new InvalidDataException(validation.Message);
                    if (duration is { } mediaDuration &&
                        (validation.CueCount < 20 || validation.LastCue < TimeSpan.FromTicks((long)(mediaDuration.Ticks * .65))))
                        throw new InvalidDataException("Subtitle timing coverage is too short for this video.");
                    SrtValidator.InstallNew(destination, payload, duration);
                    installed++;
                    if (available.Add(language))
                    {
                        if (language == "en") counts[item][1]++;
                        if (language == "ja") counts[item][2]++;
                    }
                    var matchKind = candidate.Value.HashMatch ? "exact hash" : "exact metadata + release family";
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "Installed", $"OpenSubtitles ┬╖ {matchKind} ┬╖ {candidate.Value.Release}"), cancellationToken);
                    if (downloadJson.RootElement.TryGetProperty("remaining", out var remaining) && remaining.TryGetInt32(out var left) && left <= 0)
                        rateLimited = true;
                }
                catch (InvalidDataException exception)
                {
                    rejected++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "UnsafeCandidate", $"OpenSubtitles ┬╖ {exception.Message}"), cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    rejected++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "Rejected", $"OpenSubtitles ┬╖ {exception.Message}"), cancellationToken);
                }
                await Task.Delay(1000, cancellationToken);
            }
        }

        foreach (var item in selectedItems)
        {
            var value = counts[item];
            item.SubtitleStatus = value[3] == 0 ? "No videos found" : value[0] == value[3]
                ? $"EN {value[1]}/{value[3]} ┬╖ JA {value[2]}/{value[3]}"
                : $"Checked {value[0]}/{value[3]} ┬╖ EN {value[1]} ┬╖ JA {value[2]}";
        }
        return new(filesChecked, searches, installed, rejected, rateLimited, deferred);
    }

    private async Task<Session> LoginAsync(OpenSubtitlesCredentials credentials, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { username = credentials.Username, password = credentials.Password });
        using var response = await SendApiAsync(HttpMethod.Post, $"{DefaultBaseUrl}/login", credentials.ApiKey, null, body, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("OpenSubtitles rejected the saved username, password, or API key.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var token = Text(document.RootElement, "token");
        var host = Text(document.RootElement, "base_url");
        if (string.IsNullOrWhiteSpace(token) || host is not ("api.opensubtitles.com" or "vip-api.opensubtitles.com"))
            throw new InvalidDataException("OpenSubtitles returned an invalid login session.");
        var allowed = 1;
        if (document.RootElement.TryGetProperty("user", out var user) &&
            user.TryGetProperty("allowed_downloads", out var downloads) && downloads.TryGetInt32(out var parsed))
            allowed = parsed;
        return new($"https://{host}/api/v1", token, allowed);
    }

    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string url, string apiKey, string? token, byte[]? body, CancellationToken cancellationToken)
    {
        var current = new Uri(url);
        for (var redirects = 0; redirects <= 2; redirects++)
        {
            if (!ApprovedApiUri(current)) throw new HttpRequestException("OpenSubtitles API URL is outside the approved host or path.");
            using var request = new HttpRequestMessage(method, current);
            request.Headers.TryAddWithoutValidation("Api-Key", apiKey);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            var response = await _apiHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (>= 300 and < 400)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new HttpRequestException("OpenSubtitles returned a redirect without a location.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        throw new HttpRequestException("OpenSubtitles exceeded the safe API redirect limit.");
    }

    private async Task<byte[]> DownloadSrtAsync(string value, string declaredFileName, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !ApprovedHost(uri.Host))
            throw new InvalidDataException("OpenSubtitles returned an unapproved download URL.");
        using var response = await _downloadHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("Subtitle download redirects are not accepted.");
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri || !ApprovedHost(finalUri.Host))
            throw new InvalidDataException("Subtitle download left the approved OpenSubtitles host.");
        var responseName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        if (!declaredFileName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
            responseName is not null && !responseName.Trim('"').EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Subtitle response was not declared as an SRT file.");
        if (response.Content.Headers.ContentLength > SrtValidator.MaximumBytes)
            throw new InvalidDataException("Subtitle exceeds the safe size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (output.Length + read > SrtValidator.MaximumBytes)
                throw new InvalidDataException("Subtitle exceeds the safe size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string BuildSearchQuery(string mediaPath, string hash, IReadOnlyCollection<string> languages, (int Season, int Episode)? episode)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ai_translated"] = "exclude",
            ["languages"] = string.Join(',', languages.Order(StringComparer.Ordinal)),
            ["machine_translated"] = "exclude",
            ["moviehash"] = hash,
            ["moviehash_match"] = "include",
            ["query"] = Path.GetFileName(mediaPath).ToLowerInvariant(),
            ["trusted_sources"] = "include",
            ["type"] = episode is null ? "movie" : "episode"
        };
        if (episode is { } value)
        {
            values["episode_number"] = value.Episode.ToString(CultureInfo.InvariantCulture);
            values["season_number"] = value.Season.ToString(CultureInfo.InvariantCulture);
        }
        return string.Join('&', values.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Candidate? FindBestCandidate(JsonElement root, MediaItem item, string mediaPath, (int Season, int Episode)? episode, string language)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var candidates = new List<Candidate>();
        foreach (var result in data.EnumerateArray())
        {
            if (!result.TryGetProperty("attributes", out var attributes)) continue;
            if (!Text(attributes, "language").Equals(language, StringComparison.OrdinalIgnoreCase)) continue;
            if (Boolean(attributes, "machine_translated") || Boolean(attributes, "ai_translated") || Boolean(attributes, "foreign_parts_only")) continue;
            if (!attributes.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1) continue;
            var file = files.EnumerateArray().FirstOrDefault();
            if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("file_id", out var id) || !id.TryGetInt32(out var fileId)) continue;
            var hashMatch = Boolean(attributes, "moviehash_match");
            var featureMatch = attributes.TryGetProperty("feature_details", out var feature) && FeatureMatches(item, mediaPath, episode, feature);
            if (episode is not null && !featureMatch) continue;
            var release = Text(attributes, "release");
            if (!hashMatch && (!featureMatch || !ReleaseFamilyMatches(mediaPath, release))) continue;
            var trusted = Boolean(attributes, "from_trusted");
            var downloads = Integer(attributes, "download_count");
            candidates.Add(new(fileId, release, hashMatch, trusted, downloads));
        }
        if (candidates.Count == 0) return null;
        return candidates
            .OrderByDescending(candidate => candidate.HashMatch)
            .ThenByDescending(candidate => candidate.Trusted)
            .ThenByDescending(candidate => candidate.Downloads)
            .FirstOrDefault();
    }

    private static bool TitleMatches(string expected, string actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual)) return false;
        static string Normalize(string value) => NonAlphaNumeric().Replace(value, string.Empty).ToLowerInvariant();
        return Normalize(expected) == Normalize(actual);
    }

    private static bool FeatureMatches(MediaItem item, string mediaPath, (int Season, int Episode)? episode, JsonElement feature)
    {
        if (episode is { } expected)
        {
            if (Integer(feature, "season_number") != expected.Season || Integer(feature, "episode_number") != expected.Episode) return false;
            var series = string.IsNullOrWhiteSpace(item.SeriesTitle)
                ? MediaNameParser.FindSeriesTitle(Path.GetFileName(mediaPath))
                : item.SeriesTitle;
            return TitleMatches(series, Text(feature, "parent_title"));
        }

        var title = MediaNameParser.CleanTitle(Path.GetFileName(mediaPath));
        if (title.Length < 4 || !title.Any(char.IsLetter)) return false;
        var providerTitle = Text(feature, "title");
        if (string.IsNullOrWhiteSpace(providerTitle)) providerTitle = Text(feature, "movie_name");
        if (!TitleMatches(title, providerTitle)) return false;
        var year = MediaNameParser.FindYear(Path.GetFileName(mediaPath)) ?? item.Year;
        return year is null || !int.TryParse(year, out var expectedYear) || Integer(feature, "year") == expectedYear;
    }

    private static bool ReleaseFamilyMatches(string mediaPath, string release)
    {
        var localFamily = ReleaseFamily(mediaPath);
        var providerFamily = ReleaseFamily(release);
        return localFamily is not null && localFamily == providerFamily;
    }

    private static string? ReleaseFamily(string value)
    {
        if (Regex.IsMatch(value, @"\b(?:blu.?ray|b[dr]rip|remux|uhd)\b", RegexOptions.IgnoreCase)) return "disc";
        if (Regex.IsMatch(value, @"\b(?:web[ ._-]?(?:dl|rip)|amzn|netflix|nf)\b", RegexOptions.IgnoreCase)) return "web";
        if (Regex.IsMatch(value, @"\bhdtv\b", RegexOptions.IgnoreCase)) return "hdtv";
        if (Regex.IsMatch(value, @"\b(?:hdcam|camrip|cam|telesync|telecine)\b", RegexOptions.IgnoreCase)) return "cam";
        if (Regex.IsMatch(value, @"\b(?:dvd|dvdrip)\b", RegexOptions.IgnoreCase)) return "dvd";
        return null;
    }

    private static bool ApprovedHost(string host)
        => host.Equals("opensubtitles.com", StringComparison.OrdinalIgnoreCase) ||
           host.EndsWith(".opensubtitles.com", StringComparison.OrdinalIgnoreCase);

    private static bool ApprovedApiUri(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps &&
           uri.Host is "api.opensubtitles.com" or "vip-api.opensubtitles.com" &&
           uri.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal);

    private static string Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;

    private static bool Boolean(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static int Integer(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : 0;

    private static IEnumerable<string> VideoFiles(MediaItem item, IReadOnlyCollection<string> excludedPaths)
    {
        if (PathSafety.IsExcluded(item.SourcePath, excludedPaths)) yield break;
        if (!item.IsFolder)
        {
            if (VideoExtensions.Contains(Path.GetExtension(item.SourcePath))) yield return item.SourcePath;
            yield break;
        }
        var pending = new Stack<string>();
        pending.Push(item.SourcePath);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (PathSafety.IsExcluded(directory, excludedPaths)) continue;
            string[] files;
            string[] directories;
            try { files = Directory.GetFiles(directory); directories = Directory.GetDirectories(directory); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var file in files)
                if (!PathSafety.IsExcluded(file, excludedPaths) && VideoExtensions.Contains(Path.GetExtension(file))) yield return file;
            foreach (var child in directories)
                try { if (!PathSafety.IsReparsePoint(child)) pending.Push(child); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private readonly record struct Session(string BaseUrl, string Token, int AllowedDownloads);
    private readonly record struct Candidate(int FileId, string Release, bool HashMatch, bool Trusted, int Downloads);

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphaNumeric();
}
