using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record PlaybackSelection(
    int? AudioStreamIndex,
    CatalogSubtitleStreamInfo? SubtitleTrack,
    bool SubtitleSelectionSpecified,
    double? ResumePositionSeconds,
    double? ResumeDurationSeconds);

public sealed record PlaybackLaunchResult(
    bool Started,
    bool AppliedAudioSelection,
    bool AppliedSubtitleSelection,
    string Message);

public interface IPlaybackLauncher
{
    string Name { get; }
    bool SupportsAudioSelection { get; }
    bool SupportsSubtitleSelection { get; }
    bool SupportsResume { get; }
    bool SupportsProgressTracking { get; }

    Task<PlaybackLaunchResult> LaunchAsync(
        string mediaPath,
        string mediaFileId,
        PlaybackSelection selection,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsDefaultPlaybackLauncher : IPlaybackLauncher
{
    public string Name => "Windows default player";
    public bool SupportsAudioSelection => false;
    public bool SupportsSubtitleSelection => false;
    public bool SupportsResume => false;
    public bool SupportsProgressTracking => false;

    public Task<PlaybackLaunchResult> LaunchAsync(
        string mediaPath,
        string mediaFileId,
        PlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return Task.FromResult(new PlaybackLaunchResult(false, false, false, "The selected media file is not available."));

        try
        {
            Process.Start(new ProcessStartInfo(mediaPath) { UseShellExecute = true });

            var requestedTrackChoice =
                selection.AudioStreamIndex is not null ||
                selection.SubtitleSelectionSpecified;
            var requestedResume = selection.ResumePositionSeconds is not null;
            var notes = new List<string> { "Started with the Windows default player." };
            if (requestedTrackChoice)
                notes.Add("EasyFlix inspected your track choices but did not force them because this player profile does not expose a trustworthy track-selection contract.");
            if (requestedResume)
                notes.Add("The saved Continue Watching position was not applied because the Windows default player does not expose a trustworthy seek contract.");
            var message = string.Join(" ", notes);

            return Task.FromResult(new PlaybackLaunchResult(true, false, false, message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PlaybackLaunchResult(false, false, false, "Playback launch failed: " + ex.Message));
        }
    }
}

public sealed class MpvPlaybackLauncher : IPlaybackLauncher
{
    private readonly CatalogStore _store;
    private readonly string _mpvPath;

    public MpvPlaybackLauncher(CatalogStore store, string mpvPath)
    {
        _store = store;
        _mpvPath = mpvPath;
    }

    public string Name => "mpv";
    public bool SupportsAudioSelection => true;
    public bool SupportsSubtitleSelection => true;
    public bool SupportsResume => true;
    public bool SupportsProgressTracking => true;

    public async Task<PlaybackLaunchResult> LaunchAsync(
        string mediaPath,
        string mediaFileId,
        PlaybackSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
            return new PlaybackLaunchResult(false, false, false, "The selected media file is not available.");
        if (string.IsNullOrWhiteSpace(_mpvPath) || !File.Exists(_mpvPath))
            return new PlaybackLaunchResult(false, false, false, "The configured mpv executable was not found.");

        var pipeName = "easyflix-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        var process = new Process { StartInfo = BuildStartInfo(mediaPath, pipeName, selection) };
        NamedPipeClientStream? pipe = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        var monitorOwnsResources = false;

        try
        {
            if (!process.Start())
                return new PlaybackLaunchResult(false, false, false, "mpv could not be started.");

            pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            setupTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var setupToken = setupTimeout.Token;

            await pipe.ConnectAsync(setupToken);
            reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            var tracks = await ReadTracksWhenReadyAsync(reader, writer, setupToken);
            var resolved = MpvPlaybackProtocol.ResolveSelection(
                tracks,
                selection.AudioStreamIndex,
                selection.SubtitleTrack,
                selection.SubtitleSelectionSpecified);

            var audioApplied = false;
            var subtitleApplied = false;

            if (resolved.AudioTrackId is not null)
            {
                audioApplied =
                    await SetPropertyAsync(reader, writer, "aid", resolved.AudioTrackId.Value, 200, setupToken) &&
                    await PropertyEqualsInt64Async(reader, writer, "aid", resolved.AudioTrackId.Value, 201, setupToken);
            }

            if (selection.SubtitleSelectionSpecified)
            {
                if (resolved.DisableSubtitles)
                {
                    subtitleApplied =
                        await SetPropertyAsync(reader, writer, "sid", "no", 210, setupToken) &&
                        await PropertyEqualsStringAsync(reader, writer, "sid", "no", 211, setupToken);
                }
                else if (resolved.SubtitleTrackId is not null)
                {
                    subtitleApplied =
                        await SetPropertyAsync(reader, writer, "sid", resolved.SubtitleTrackId.Value, 212, setupToken) &&
                        await PropertyEqualsInt64Async(reader, writer, "sid", resolved.SubtitleTrackId.Value, 213, setupToken);
                }
            }

            var duration = await GetOptionalDoubleAsync(reader, writer, "duration", 220, setupToken);

            var requestedPosition = selection.ResumePositionSeconds;
            var resumeRequested = requestedPosition is not null;
            var resumeEligible = requestedPosition is { } resumePosition &&
                PlaybackProgressPolicy.IsContinueWatching(
                    resumePosition,
                    selection.ResumeDurationSeconds ?? duration);
            var resumeApplied = false;

            if (resumeEligible && requestedPosition is { } verifiedTarget)
            {
                var setResume = await SetPropertyAsync(
                    reader,
                    writer,
                    "time-pos",
                    verifiedTarget,
                    221,
                    setupToken);
                if (setResume)
                {
                    var verifiedPosition = await GetOptionalDoubleAsync(
                        reader,
                        writer,
                        "time-pos",
                        222,
                        setupToken);
                    resumeApplied = verifiedPosition is not null &&
                                    Math.Abs(verifiedPosition.Value - verifiedTarget) <= 5;
                }
            }

            await SendCommandAsync(
                reader,
                writer,
                new object[] { "observe_property", 101, "time-pos" },
                223,
                setupToken);

            if (!await SetPropertyAsync(reader, writer, "pause", false, 224, setupToken))
                throw new InvalidOperationException("mpv did not accept the resume command.");

            _ = MonitorProgressAsync(process, pipe, reader, writer, mediaFileId, duration);
            monitorOwnsResources = true;

            var notes = new List<string> { "Started with mpv." };
            if (selection.AudioStreamIndex is not null)
                notes.Add(audioApplied
                    ? "The selected audio track was verified and applied."
                    : "The selected audio track could not be mapped uniquely, so EasyFlix left mpv's audio choice unchanged.");
            if (selection.SubtitleSelectionSpecified)
                notes.Add(subtitleApplied
                    ? "The subtitle choice was verified and applied."
                    : "The selected subtitle could not be mapped uniquely, so EasyFlix did not claim it was applied.");
            if (resumeRequested)
                notes.Add(resumeApplied
                    ? "The saved Continue Watching position was verified and applied before playback resumed."
                    : resumeEligible
                        ? "The saved Continue Watching position could not be verified, so EasyFlix did not claim it was applied."
                        : "The saved position is no longer eligible under the Continue Watching policy, so EasyFlix did not seek to it.");

            notes.Add("Playback progress is being recorded from mpv IPC.");
            return new PlaybackLaunchResult(true, audioApplied, subtitleApplied, string.Join(" ", notes));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return new PlaybackLaunchResult(false, false, false, "Timed out while connecting to or configuring mpv.");
        }
        catch (Exception ex)
        {
            TryTerminate(process);
            return new PlaybackLaunchResult(false, false, false, "mpv playback launch failed: " + ex.Message);
        }
        finally
        {
            if (!monitorOwnsResources)
            {
                writer?.Dispose();
                reader?.Dispose();
                pipe?.Dispose();
                process.Dispose();
            }
        }
    }

    private ProcessStartInfo BuildStartInfo(
        string mediaPath,
        string pipeName,
        PlaybackSelection selection)
    {
        var start = new ProcessStartInfo
        {
            FileName = _mpvPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        start.ArgumentList.Add("--terminal=no");
        start.ArgumentList.Add("--pause=yes");
        start.ArgumentList.Add("--force-window=yes");
        start.ArgumentList.Add("--input-ipc-server=\\\\.\\pipe\\" + pipeName);

        if (selection.SubtitleTrack?.SourcePath is { Length: > 0 } subtitlePath &&
            File.Exists(subtitlePath))
            start.ArgumentList.Add("--sub-file=" + subtitlePath);

        start.ArgumentList.Add(mediaPath);
        return start;
    }

    private static async Task<IReadOnlyList<MpvTrackInfo>> ReadTracksWhenReadyAsync(
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var response = await SendCommandAsync(
                reader,
                writer,
                new object[] { "get_property", "track-list" },
                100 + attempt,
                cancellationToken);

            if (IsSuccess(response) &&
                response.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                var tracks = MpvPlaybackProtocol.ParseTrackList(data);
                if (tracks.Count > 0) return tracks;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new InvalidOperationException("mpv did not expose a usable track list.");
    }

    private static async Task<bool> SetPropertyAsync(
        StreamReader reader,
        StreamWriter writer,
        string property,
        object value,
        int requestId,
        CancellationToken cancellationToken)
        => IsSuccess(await SendCommandAsync(
            reader,
            writer,
            new[] { (object)"set_property", property, value },
            requestId,
            cancellationToken));

    private static async Task<bool> PropertyEqualsInt64Async(
        StreamReader reader,
        StreamWriter writer,
        string property,
        long expected,
        int requestId,
        CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            reader,
            writer,
            new object[] { "get_property", property },
            requestId,
            cancellationToken);
        return IsSuccess(response) &&
               response.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.Number &&
               data.TryGetInt64(out var value) &&
               value == expected;
    }

    private static async Task<bool> PropertyEqualsStringAsync(
        StreamReader reader,
        StreamWriter writer,
        string property,
        string expected,
        int requestId,
        CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            reader,
            writer,
            new object[] { "get_property", property },
            requestId,
            cancellationToken);
        return IsSuccess(response) &&
               response.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.String &&
               string.Equals(data.GetString(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<double?> GetOptionalDoubleAsync(
        StreamReader reader,
        StreamWriter writer,
        string property,
        int requestId,
        CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            reader,
            writer,
            new object[] { "get_property", property },
            requestId,
            cancellationToken);
        if (!IsSuccess(response) ||
            !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Number ||
            !data.TryGetDouble(out var value) ||
            !double.IsFinite(value) ||
            value <= 0)
            return null;
        return value;
    }

    private static async Task<JsonElement> SendCommandAsync(
        StreamReader reader,
        StreamWriter writer,
        object[] command,
        int requestId,
        CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new
        {
            command,
            request_id = requestId
        });

        await writer.WriteLineAsync(request.AsMemory(), cancellationToken);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) throw new EndOfStreamException("mpv IPC closed.");

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("request_id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt32(out var value) &&
                value == requestId)
                return root.Clone();
        }
    }

    private async Task MonitorProgressAsync(
        Process process,
        NamedPipeClientStream pipe,
        StreamReader reader,
        StreamWriter writer,
        string mediaFileId,
        double? duration)
    {
        double? position = null;
        var completedAtNaturalEnd = false;
        var lastPersisted = DateTimeOffset.MinValue;

        try
        {
            while (pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (!root.TryGetProperty("event", out var eventValue))
                    continue;

                var eventName = eventValue.GetString();
                if (MpvPlaybackProtocol.IsNaturalEndFile(root))
                {
                    completedAtNaturalEnd = true;
                    break;
                }
                if (string.Equals(eventName, "shutdown", StringComparison.Ordinal))
                    break;
                if (!string.Equals(eventName, "property-change", StringComparison.Ordinal) ||
                    !root.TryGetProperty("name", out var nameValue) ||
                    !string.Equals(nameValue.GetString(), "time-pos", StringComparison.Ordinal) ||
                    !root.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Number ||
                    !data.TryGetDouble(out var current) ||
                    !double.IsFinite(current))
                    continue;

                position = Math.Max(0, current);
                if (DateTimeOffset.UtcNow - lastPersisted >= TimeSpan.FromSeconds(5))
                {
                    await _store.SavePlaybackProgressAsync(mediaFileId, position.Value, duration);
                    lastPersisted = DateTimeOffset.UtcNow;
                }
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                if (completedAtNaturalEnd && duration is > 0)
                {
                    await _store.SavePlaybackProgressAsync(mediaFileId, duration.Value, duration);
                }
                else if (position is not null)
                {
                    await _store.SavePlaybackProgressAsync(mediaFileId, position.Value, duration);
                }
            }
            catch
            {
            }

            writer.Dispose();
            reader.Dispose();
            pipe.Dispose();
            process.Dispose();
        }
    }

    private static bool IsSuccess(JsonElement response)
        => response.TryGetProperty("error", out var error) &&
           string.Equals(error.GetString(), "success", StringComparison.Ordinal);

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
