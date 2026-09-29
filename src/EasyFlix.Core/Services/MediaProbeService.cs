using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed record MediaAudit(int Videos, int EnglishSubtitles, int JapaneseSubtitles, IReadOnlyCollection<string> AudioLanguages)
{
    public string Summary => $"EN {EnglishSubtitles}/{Videos} ┬╖ JA {JapaneseSubtitles}/{Videos}";
}

public sealed record AudioStreamInfo(int Index, string? Language);
public sealed record MediaStreamInfo(IReadOnlyList<AudioStreamInfo> AudioStreams, IReadOnlySet<string> SubtitleLanguages);

public sealed class MediaProbeService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts" };
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".srt", ".ass", ".ssa", ".vtt" };

    public static async Task<bool> IsAvailableAsync(string ffprobePath = "ffprobe", CancellationToken cancellationToken = default)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = ffprobePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task<MediaAudit> AuditAsync(MediaItem item, string ffprobePath = "ffprobe", CancellationToken cancellationToken = default)
    {
        var videos = item.IsFolder
            ? Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories).Where(IsVideo).ToArray()
            : [item.SourcePath];
        var english = 0;
        var japanese = 0;
        var audioLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(video, ffprobePath, cancellationToken);
            var embedded = probe.SubtitleLanguages;
            var sidecars = SidecarLanguages(video);
            if (embedded.Contains("en") || sidecars.Contains("en")) english++;
            if (embedded.Contains("ja") || sidecars.Contains("ja")) japanese++;
            foreach (var language in probe.AudioLanguages) audioLanguages.Add(language);
        }
        return new MediaAudit(videos.Length, english, japanese, audioLanguages.Order().ToArray());
    }

    public async Task<IReadOnlySet<string>> SubtitleLanguagesAsync(string videoPath, string ffprobePath = "ffprobe", CancellationToken cancellationToken = default)
    {
        var embedded = (await ProbeAsync(videoPath, ffprobePath, cancellationToken)).SubtitleLanguages;
        return embedded.Concat(SidecarLanguages(videoPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<MediaStreamInfo> InspectAsync(string videoPath, string ffprobePath = "ffprobe", CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(videoPath, ffprobePath, cancellationToken);
        return new MediaStreamInfo(probe.AudioStreams, probe.SubtitleLanguages.Concat(SidecarLanguages(videoPath)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public static async Task<TimeSpan?> DurationAsync(string videoPath, string ffprobePath = "ffprobe", CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", videoPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFprobe could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFprobe failed for {Path.GetFileName(videoPath)}: {error}");
        return double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public static IReadOnlySet<string> SidecarLanguages(string videoPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = Path.GetDirectoryName(videoPath);
        if (directory is null || !Directory.Exists(directory)) return result;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (stem.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)) stem = Path.GetFileNameWithoutExtension(stem);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!SubtitleExtensions.Contains(Path.GetExtension(file))) continue;
            var subtitleStem = Path.GetFileNameWithoutExtension(file);
            if (!subtitleStem.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
            var tokens = subtitleStem.Replace('-', '.').Replace('_', '.').Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Any(token => token.Equals("en", StringComparison.OrdinalIgnoreCase) || token.Equals("eng", StringComparison.OrdinalIgnoreCase) || token.Equals("english", StringComparison.OrdinalIgnoreCase))) result.Add("en");
            if (tokens.Any(token => token.Equals("ja", StringComparison.OrdinalIgnoreCase) || token.Equals("jpn", StringComparison.OrdinalIgnoreCase) || token.Equals("japanese", StringComparison.OrdinalIgnoreCase))) result.Add("ja");
        }
        return result;
    }

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    private sealed record ProbeResult(IReadOnlySet<string> AudioLanguages, IReadOnlySet<string> SubtitleLanguages, IReadOnlyList<AudioStreamInfo> AudioStreams);

    private static async Task<ProbeResult> ProbeAsync(
        string path, string ffprobePath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=index,codec_type:stream_tags=language,title", "-of", "json", path })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFprobe could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"FFprobe failed for {Path.GetFileName(path)}: {error.Trim()}");

        var audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subtitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audioStreams = new List<AudioStreamInfo>();
        using var document = JsonDocument.Parse(output);
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.GetProperty("codec_type").GetString();
            var hasTags = stream.TryGetProperty("tags", out var tags);
            var language = hasTags && tags.TryGetProperty("language", out var lang) ? NormalizeLanguage(lang.GetString()) : null;
            var title = hasTags && tags.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
            language ??= InferLanguage(title);
            if (type == "audio")
            {
                if (language is not null) audio.Add(language);
                audioStreams.Add(new AudioStreamInfo(stream.GetProperty("index").GetInt32(), language));
            }
            if (type == "subtitle" && language is not null) subtitles.Add(language);
        }
        return new ProbeResult(audio, subtitles, audioStreams);
    }

    private static string? NormalizeLanguage(string? value) => value?.ToLowerInvariant() switch
    {
        "en" or "eng" or "english" => "en",
        "ja" or "jpn" or "japanese" => "ja",
        null or "" or "und" => null,
        var other => other
    };

    private static string? InferLanguage(string? title)
    {
        if (title is null) return null;
        if (title.Contains("english", StringComparison.OrdinalIgnoreCase)) return "en";
        if (title.Contains("japanese", StringComparison.OrdinalIgnoreCase)) return "ja";
        return null;
    }
}
