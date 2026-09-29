using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record SubtitleRunSummary(int FilesChecked, int Searches, int Installed, int Rejected, bool RateLimited, int Deferred);

public sealed partial class SubDlSubtitleService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts" };
    private readonly HttpClient _apiHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly HttpClient _downloadHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    private readonly MediaProbeService _probe = new();
    private readonly SubtitleJobStore _jobs = new();

    public async Task<SubtitleRunSummary> DownloadMissingAsync(
        IEnumerable<MediaItem> selected,
        IReadOnlyCollection<string> excludedPaths,
        string apiKey,
        int maximumDownloads,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Configure a SubDL API key first.");
        var excluded = excludedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathSafety.Normalize)
            .ToArray();
        var selectedItems = selected.ToArray();
        var entries = selectedItems
            .SelectMany(item => VideoFiles(item, excluded).Select(path => (Item: item, Path: path)))
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var counts = selectedItems.ToDictionary(item => item, _ => new int[4]); // checked, English, Japanese, total
        foreach (var entry in entries) counts[entry.Item][3]++;
        var searches = 0;
        var installed = 0;
        var rejected = 0;
        var deferred = 0;
        var rateLimited = false;
        var filesChecked = 0;
        var deferredKeys = _jobs.ReadDeferredKeys("SubDL", TimeSpan.FromDays(7));

        for (var index = 0; index < entries.Length && installed < maximumDownloads; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var mediaPath = entry.Path;
            progress?.Report($"Subtitle search {index + 1:N0}/{entries.Length:N0}: {Path.GetFileName(mediaPath)}");
            var available = (await _probe.SubtitleLanguagesAsync(mediaPath, cancellationToken: cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            filesChecked++;
            counts[entry.Item][0]++;
            if (available.Contains("en")) counts[entry.Item][1]++;
            if (available.Contains("ja")) counts[entry.Item][2]++;
            var missing = new[] { "en", "ja" }.Where(language => !available.Contains(language)).ToArray();
            var needed = missing.Where(language => !deferredKeys.Contains(SubtitleJobStore.AttemptKey(mediaPath, language))).ToArray();
            deferred += missing.Length - needed.Length;
            if (needed.Length == 0) continue;

            var parsedEpisode = MediaNameParser.FindSeasonEpisode(
                Path.GetFileName(mediaPath), Path.GetDirectoryName(mediaPath));
            var expectedType = parsedEpisode is not null || MediaNameParser.LooksLikeEpisode(Path.GetFileName(mediaPath)) ? "tv" : "movie";
            using var search = await GetJsonAsync(
                $"https://api.subdl.com/api/v2/files/search?filename={Uri.EscapeDataString(Path.GetFileName(mediaPath))}&engine=local&languages=en,ja&type={expectedType}&subs_per_page=30",
                apiKey, cancellationToken);
            searches++;
            if (search.StatusCode == HttpStatusCode.TooManyRequests) { rateLimited = true; break; }
            if (!search.IsSuccessStatusCode) continue;
            using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStreamAsync(cancellationToken));
            if (!TryHighConfidenceMatch(searchJson.RootElement, expectedType, out var match))
            {
                foreach (var language in needed)
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "NotFound", "SubDL ┬╖ no high-confidence title or episode match"), cancellationToken);
                continue;
            }
            if (parsedEpisode is { } episode)
            {
                match = match with
                {
                    Season = episode.Season.ToString(CultureInfo.InvariantCulture),
                    Episode = episode.Episode.ToString(CultureInfo.InvariantCulture)
                };
            }

            var candidates = BestCandidates(searchJson.RootElement, needed, parsedEpisode is not null);
            foreach (var language in needed.Where(language => !candidates.ContainsKey(language)))
                await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "NotFound", "SubDL ┬╖ no high-confidence release match"), cancellationToken);
            if (candidates.Count == 0) continue;
            var query = $"sd_id={Uri.EscapeDataString(match.SdId)}&type={match.Type}&languages={Uri.EscapeDataString(string.Join(',', candidates.Keys))}&unpack=1&subs_per_page=30";
            if (match.Type == "tv") query += $"&season={match.Season}&episode={match.Episode}";
            using var unpack = await GetJsonAsync($"https://api.subdl.com/api/v2/subtitles/search?{query}", apiKey, cancellationToken);
            if (unpack.StatusCode == HttpStatusCode.TooManyRequests) { rateLimited = true; break; }
            if (!unpack.IsSuccessStatusCode) continue;
            using var unpackJson = JsonDocument.Parse(await unpack.Content.ReadAsStreamAsync(cancellationToken));

            foreach (var candidate in candidates.Values)
            {
                if (installed >= maximumDownloads) break;
                var direct = FindSingleDirectSrt(
                    unpackJson.RootElement, candidate.PageId, match.Season, match.Episode);
                if (direct is null)
                {
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, candidate.Language, "NotFound", "SubDL ┬╖ no single exact direct SRT"), cancellationToken);
                    continue;
                }
                var destination = Path.Combine(Path.GetDirectoryName(mediaPath)!, $"{Path.GetFileNameWithoutExtension(mediaPath)}.{candidate.Language}.srt");
                try
                {
                    var body = await DownloadDirectAsync(direct.Value.Path, direct.Value.Md5, apiKey, cancellationToken);
                    SrtValidator.InstallNew(destination, body);
                    installed++;
                    if (available.Add(candidate.Language))
                    {
                        if (candidate.Language.Equals("en", StringComparison.OrdinalIgnoreCase)) counts[entry.Item][1]++;
                        if (candidate.Language.Equals("ja", StringComparison.OrdinalIgnoreCase)) counts[entry.Item][2]++;
                    }
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, candidate.Language, "Installed", $"SubDL ┬╖ {candidate.ReleaseName}"), cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidDataException or CryptographicException)
                {
                    rejected++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, candidate.Language, "UnsafeCandidate", $"SubDL ┬╖ {exception.Message}"), cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException)
                {
                    rejected++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, candidate.Language, "Rejected", $"SubDL ┬╖ {exception.Message}"), cancellationToken);
                }
                await Task.Delay(1000, cancellationToken);
            }
        }
        foreach (var item in selectedItems)
        {
            var itemCounts = counts[item];
            item.SubtitleStatus = itemCounts[3] == 0
                ? "No videos found"
                : itemCounts[0] == itemCounts[3]
                    ? $"EN {itemCounts[1]}/{itemCounts[3]} ┬╖ JA {itemCounts[2]}/{itemCounts[3]}"
                    : $"Checked {itemCounts[0]}/{itemCounts[3]} ┬╖ EN {itemCounts[1]} ┬╖ JA {itemCounts[2]}";
        }
        return new(filesChecked, searches, installed, rejected, rateLimited, deferred);
    }

    private async Task<HttpResponseMessage> GetJsonAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.ParseAdd("EasyFlix/1.4.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await _apiHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            response.Dispose();
            throw new HttpRequestException("Provider API redirects are not accepted.");
        }
        return response;
    }

    private async Task<byte[]> DownloadDirectAsync(string relativePath, string md5, string apiKey, CancellationToken cancellationToken)
    {
        var directPath = ApprovedDirectPath(relativePath)
            ?? throw new InvalidDataException("Provider URL is not an approved direct-SRT path.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://dl.subdl.com{directPath}?api_key={Uri.EscapeDataString(apiKey)}");
        request.Headers.UserAgent.ParseAdd("EasyFlix/1.4.0");
        using var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new InvalidDataException("Subtitle download redirects are not accepted.");
        response.EnsureSuccessStatusCode();
        if (!response.RequestMessage!.RequestUri!.Host.Equals("dl.subdl.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Subtitle download redirected outside the approved host.");
        var filename = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        if (filename is null || !filename.Trim('"').EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Download was not declared as an SRT file.");
        if (response.Content.Headers.ContentLength > SrtValidator.MaximumBytes)
            throw new InvalidDataException("Subtitle exceeds the safe size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) break;
            if (output.Length + read > SrtValidator.MaximumBytes)
                throw new InvalidDataException("Subtitle exceeds the safe size limit.");
            output.Write(buffer, 0, read);
        }
        var body = output.ToArray();
        var actual = Convert.ToHexString(MD5.HashData(body));
        if (string.IsNullOrWhiteSpace(md5) || !actual.Equals(md5, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Subtitle checksum did not match provider metadata.");
        return body;
    }

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
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
                if (!PathSafety.IsExcluded(file, excludedPaths) && VideoExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
            foreach (var child in directories)
            {
                try
                {
                    if (!PathSafety.IsReparsePoint(child)) pending.Push(child);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool TryHighConfidenceMatch(JsonElement root, string expectedType, out Match match)
    {
        match = default;
        if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.True) return false;
        if (!root.TryGetProperty("match", out var value) || value.ValueKind != JsonValueKind.Object) return false;
        var confidence = Text(value, "confidence");
        var type = Text(value, "type");
        var sdId = Text(value, "sd_id");
        var degraded = value.TryGetProperty("degraded", out var degradedValue) && degradedValue.ValueKind == JsonValueKind.True;
        if (confidence != "high" || degraded || type != expectedType || string.IsNullOrWhiteSpace(sdId)) return false;
        match = new Match(sdId, type, Text(value, "season"), Text(value, "episode"));
        return true;
    }

    private static Dictionary<string, Candidate> BestCandidates(
        JsonElement root, IEnumerable<string> needed, bool allowExactEpisodePackage)
    {
        var wanted = needed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("subtitles", out var subtitles) || subtitles.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in subtitles.EnumerateArray())
        {
            var language = Text(item, "language").ToLowerInvariant();
            if (!wanted.Contains(language)) continue;
            var score = Number(item, "match_score");
            var page = Text(item, "subtitlePage").TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
            if ((score < .8 && !allowExactEpisodePackage) || string.IsNullOrWhiteSpace(page)) continue;
            var candidate = new Candidate(language, page, Text(item, "release_name"), score);
            if (!result.TryGetValue(language, out var current) || candidate.Score > current.Score) result[language] = candidate;
        }
        return result;
    }

    private static (string Path, string Md5)? FindSingleDirectSrt(
        JsonElement root, string pageId, string season, string episode)
    {
        if (!root.TryGetProperty("subtitles", out var parents) || parents.ValueKind != JsonValueKind.Array) return null;
        var files = new List<(string Path, string Md5, string Name)>();
        foreach (var parent in parents.EnumerateArray())
        {
            if (!Text(parent, "subtitlePage").TrimEnd('/').EndsWith('/' + pageId, StringComparison.Ordinal)) continue;
            if (!parent.TryGetProperty("unpack_files", out var unpack) || unpack.ValueKind != JsonValueKind.Array) continue;
            foreach (var file in unpack.EnumerateArray())
            {
                var format = Text(file, "format");
                var name = Text(file, "name");
                var path = Text(file, "url");
                if (format.Equals("srt", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) && ApprovedDirectPath(path) is not null)
                    files.Add((path, Text(file, "md5"), name));
            }
        }
        if (!string.IsNullOrWhiteSpace(season) && !string.IsNullOrWhiteSpace(episode) &&
            int.TryParse(season, out var seasonNumber) && int.TryParse(episode, out var episodeNumber))
        {
            var episodeToken = new Regex($@"\bS0*{seasonNumber}E0*{episodeNumber}\b", RegexOptions.IgnoreCase);
            files = files.Where(file => episodeToken.IsMatch(file.Name)).ToList();
        }
        return files.Count == 1 ? (files[0].Path, files[0].Md5) : null;
    }

    private static string Text(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return string.Empty;
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString();
    }

    private static double Number(JsonElement value, string name)
        => double.TryParse(Text(value, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string? ApprovedDirectPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('#')) return null;
        var parts = value.Split('?', 2);
        if (parts.Length == 2 &&
            (!parts[1].StartsWith("api_key=", StringComparison.Ordinal) || parts[1].Contains('&')))
            return null;
        return DirectPath().IsMatch(parts[0]) ? parts[0] : null;
    }

    private readonly record struct Match(string SdId, string Type, string Season, string Episode);
    private readonly record struct Candidate(string Language, string PageId, string ReleaseName, double Score);

    [GeneratedRegex("^/subtitle/[A-Za-z0-9_-]+/[A-Za-z0-9_-]+$")]
    private static partial Regex DirectPath();
}
