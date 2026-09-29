using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;
using Whisper.net;
using Whisper.net.Ggml;

namespace EasyFlix.App.Services;

public sealed record LocalSubtitleSummary(
    int FilesChecked,
    int Generated,
    int Skipped,
    int Failed,
    int JapaneseUnavailable,
    int DraftTranslationsPaused);

public sealed class LocalSubtitleService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts" };
    private readonly MediaProbeService _probe = new();
    private readonly SubtitleJobStore _jobs = new();

    private static string PreferredModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyFlix", "models", "ggml-small.bin");

    private static string LegacyModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReelShelf", "models", "ggml-small.bin");

    private static bool IsReadyModel(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 100_000_000;

    public static string ModelPath =>
        IsReadyModel(PreferredModelPath) || !IsReadyModel(LegacyModelPath)
            ? PreferredModelPath
            : LegacyModelPath;

    public static bool IsModelReady => IsReadyModel(ModelPath);

    public static async Task<bool> IsFfmpegAvailableAsync(CancellationToken cancellationToken = default)
        => await ToolAvailableAsync("ffmpeg", cancellationToken) && await MediaProbeService.IsAvailableAsync(cancellationToken: cancellationToken);

    public async Task EnsureModelAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsModelReady) return;
        var folder = Path.GetDirectoryName(ModelPath)!;
        Directory.CreateDirectory(folder);
        var temporary = ModelPath + ".download";
        progress?.Report("Downloading the multilingual Whisper Small model once (about 460 MB)ΓÇª");
        try
        {
            await using var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Small, cancellationToken: cancellationToken);
            await using var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (copied % (25 * 1024 * 1024) < buffer.Length)
                    progress?.Report($"Downloading local speech modelΓÇª {copied / 1024 / 1024:N0} MB received");
            }
            await destination.FlushAsync(cancellationToken);
            destination.Close();
            File.Move(temporary, ModelPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<LocalSubtitleSummary> GenerateMissingAsync(
        IEnumerable<MediaItem> selected,
        IReadOnlyCollection<string> excludedPaths,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsFfmpegAvailableAsync(cancellationToken))
            throw new InvalidOperationException("FFmpeg and FFprobe are required for offline subtitle generation.");

        var excluded = excludedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(PathSafety.Normalize).ToArray();
        var entries = selected.SelectMany(item => VideoFiles(item, excluded))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var generated = 0;
        var skipped = 0;
        var failed = 0;
        var japaneseUnavailable = 0;
        var draftTranslationsPaused = 0;

        var inspected = new List<(string Path, MediaStreamInfo Media)>();
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Planning local subtitles {index + 1:N0}/{entries.Length:N0}: {Path.GetFileName(entries[index])}");
            try { inspected.Add((entries[index], await _probe.InspectAsync(entries[index], cancellationToken: cancellationToken))); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                await _jobs.AppendAsync(new(DateTimeOffset.Now, entries[index], "-", "Failed", $"Probe: {exception.Message}"), cancellationToken);
            }
        }

        var hasWork = inspected.Any(entry =>
            (!entry.Media.SubtitleLanguages.Contains("en") && entry.Media.AudioStreams.Any(stream => stream.Language == "en")) ||
            (!entry.Media.SubtitleLanguages.Contains("ja") && entry.Media.AudioStreams.Any(stream => stream.Language == "ja")));
        if (!hasWork)
        {
            foreach (var entry in inspected)
            {
                skipped += entry.Media.SubtitleLanguages.Contains("en") ? 1 : 0;
                skipped += entry.Media.SubtitleLanguages.Contains("ja") ? 1 : 0;
                if (!entry.Media.SubtitleLanguages.Contains("en"))
                {
                    if (entry.Media.AudioStreams.Count == 0) failed++;
                    else if (entry.Media.AudioStreams.All(stream => stream.Language != "en")) draftTranslationsPaused++;
                }
                if (!entry.Media.SubtitleLanguages.Contains("ja") && entry.Media.AudioStreams.All(stream => stream.Language != "ja")) japaneseUnavailable++;
            }
            return new(entries.Length, generated, skipped, failed, japaneseUnavailable, draftTranslationsPaused);
        }

        await EnsureModelAsync(progress, cancellationToken);

        using var awake = new SystemAwakeScope();
        using var factory = WhisperFactory.FromPath(ModelPath);
        for (var fileIndex = 0; fileIndex < inspected.Count; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mediaPath = inspected[fileIndex].Path;
            var media = inspected[fileIndex].Media;

            foreach (var language in new[] { "en", "ja" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (media.SubtitleLanguages.Contains(language)) { skipped++; continue; }
                var destination = SubtitleFileSafety.DestinationForGenerated(mediaPath, language);
                if (File.Exists(destination)) { skipped++; continue; }

                var selectedAudio = language == "ja"
                    ? media.AudioStreams.FirstOrDefault(stream => stream.Language == "ja")
                    : media.AudioStreams.FirstOrDefault(stream => stream.Language == "en");
                if (language == "en" && selectedAudio is null && media.AudioStreams.Count > 0)
                {
                    draftTranslationsPaused++;
                    await _jobs.AppendAsync(new(
                        DateTimeOffset.Now,
                        mediaPath,
                        language,
                        "Skipped",
                        "Cross-language Whisper translation paused by the verified quality gate; use a verified human SRT."), cancellationToken);
                    continue;
                }
                if (selectedAudio is null)
                {
                    if (language == "ja") japaneseUnavailable++;
                    else failed++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "Skipped", "No suitable audio track."), cancellationToken);
                    continue;
                }
                if (language == "ja" && selectedAudio.Language != "ja")
                {
                    japaneseUnavailable++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "Skipped", "Japanese audio is not present; Whisper cannot translate into Japanese."), cancellationToken);
                    continue;
                }

                progress?.Report($"Local subtitles {fileIndex + 1:N0}/{inspected.Count:N0} ┬╖ {language.ToUpperInvariant()} ┬╖ {Path.GetFileName(mediaPath)}");
                var tempFolder = Path.Combine(Path.GetTempPath(), "EasyFlix", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempFolder);
                var wavPath = Path.Combine(tempFolder, "audio.wav");
                try
                {
                    await ExtractAudioAsync(mediaPath, selectedAudio.Index, wavPath, cancellationToken);
                    var builder = factory.CreateBuilder()
                        .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                        .WithLanguage(selectedAudio.Language ?? "auto");
                    await using var processor = builder.Build();
                    await using var wave = File.OpenRead(wavPath);
                    var cues = new List<SubtitleCue>();
                    await foreach (var segment in processor.ProcessAsync(wave, cancellationToken))
                    {
                        if (!string.IsNullOrWhiteSpace(segment.Text) && segment.End > segment.Start)
                            cues.Add(new SubtitleCue(segment.Start, segment.End, segment.Text));
                    }
                    var payload = SrtWriter.Encode(cues);
                    SrtValidator.InstallNew(destination, payload);
                    generated++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "Generated", $"Whisper Small ┬╖ audio stream {selectedAudio.Index}"), cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    failed++;
                    await _jobs.AppendAsync(new(DateTimeOffset.Now, mediaPath, language, "Failed", exception.Message), cancellationToken);
                }
                finally
                {
                    try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
                }
            }
        }
        return new(entries.Length, generated, skipped, failed, japaneseUnavailable, draftTranslationsPaused);
    }

    private static async Task ExtractAudioAsync(string mediaPath, int streamIndex, string wavPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = "ffmpeg", UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", mediaPath, "-map", $"0:{streamIndex}", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0 || !File.Exists(wavPath)) throw new InvalidOperationException($"Audio extraction failed: {error.Trim()}");
    }

    private static async Task<bool> ToolAvailableAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo { FileName = name, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("-version");
            using var process = Process.Start(start);
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static IEnumerable<string> VideoFiles(MediaItem item, IReadOnlyCollection<string> excluded)
    {
        if (PathSafety.IsExcluded(item.SourcePath, excluded)) yield break;
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
            if (PathSafety.IsExcluded(directory, excluded)) continue;
            string[] files;
            string[] directories;
            try { files = Directory.GetFiles(directory); directories = Directory.GetDirectories(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
                if (!PathSafety.IsExcluded(file, excluded) && VideoExtensions.Contains(Path.GetExtension(file))) yield return file;
            foreach (var child in directories)
            {
                try { if (!PathSafety.IsReparsePoint(child)) pending.Push(child); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class SystemAwakeScope : IDisposable
    {
        private const uint Continuous = 0x80000000;
        private const uint SystemRequired = 0x00000001;
        public SystemAwakeScope() => SetThreadExecutionState(Continuous | SystemRequired);
        public void Dispose() => SetThreadExecutionState(Continuous);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint executionState);
    }
}
