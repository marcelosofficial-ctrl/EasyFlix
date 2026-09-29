using System.IO;
using System.Text.Json;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record SubtitleJobEvent(DateTimeOffset Timestamp, string MediaPath, string Language, string Status, string Detail);

public sealed class SubtitleJobStore
{
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyFlix", "jobs");

    private readonly string _legacyFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReelShelf", "jobs");

    public async Task AppendAsync(SubtitleJobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, $"subtitles-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var line = JsonSerializer.Serialize(jobEvent) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, cancellationToken);
    }

    public string Folder => _folder;

    public static string AttemptKey(string mediaPath, string language)
        => $"{Path.GetFullPath(mediaPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}|{language}";

    public HashSet<string> ReadDeferredKeys(string provider, TimeSpan retryAfter)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTimeOffset.Now - retryAfter;
        foreach (var folder in ReadFolders())
        {
            foreach (var path in Directory.EnumerateFiles(folder, "subtitles-*.jsonl")
                         .Where(path => File.GetLastWriteTimeUtc(path) >= cutoff.UtcDateTime))
            {
                foreach (var line in File.ReadLines(path))
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<SubtitleJobEvent>(line);
                        if (item is null || !ProviderAttemptPolicy.ShouldDefer(
                                item.Status, item.Detail, provider, item.Timestamp, DateTimeOffset.Now, retryAfter)) continue;
                        keys.Add(AttemptKey(item.MediaPath, item.Language));
                    }
                    catch (Exception exception) when (exception is JsonException or IOException or ArgumentException) { }
                }
            }
        }

        return keys;
    }

    public IReadOnlyList<SubtitleJobEvent> ReadRecent(int maximum = 250)
    {
        var lines = ReadFolders()
            .SelectMany(folder => Directory.EnumerateFiles(folder, "subtitles-*.jsonl"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .SelectMany(path => File.ReadLines(path).Reverse())
            .Take(maximum);

        var events = new List<SubtitleJobEvent>();
        foreach (var line in lines)
        {
            try
            {
                var item = JsonSerializer.Deserialize<SubtitleJobEvent>(line);
                if (item is not null) events.Add(item);
            }
            catch (JsonException) { }
        }

        return events;
    }

    private IEnumerable<string> ReadFolders()
    {
        if (Directory.Exists(_folder))
            yield return _folder;

        if (!string.Equals(_legacyFolder, _folder, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(_legacyFolder))
            yield return _legacyFolder;
    }
}
