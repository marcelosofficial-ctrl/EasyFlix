using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed class VlcPlaybackLauncher : IPlaybackLauncher
{
    private readonly CatalogStore _store;
    private readonly string _vlcPath;

    public VlcPlaybackLauncher(CatalogStore store, string vlcPath)
    {
        _store = store;
        _vlcPath = vlcPath;
    }

    public string Name => "VLC";
    public bool SupportsAudioSelection => false;
    public bool SupportsSubtitleSelection => false;
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
        if (string.IsNullOrWhiteSpace(_vlcPath) || !File.Exists(_vlcPath))
            return new PlaybackLaunchResult(false, false, false, "The configured VLC executable was not found.");

        var port = ReserveLoopbackPort();
        var process = new Process
        {
            StartInfo = BuildStartInfo(mediaPath, port, selection)
        };
        TcpClient? client = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        var monitorOwnsResources = false;

        try
        {
            if (!process.Start())
                return new PlaybackLaunchResult(false, false, false, "VLC could not be started.");

            using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            setupTimeout.CancelAfter(TimeSpan.FromSeconds(12));
            var setupToken = setupTimeout.Token;

            client = await ConnectWithRetryAsync(port, setupToken);
            var stream = client.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            var duration = await ReadPositiveSecondsWithRetryAsync(reader, writer, "get_length", setupToken);

            var requestedPosition = selection.ResumePositionSeconds;
            var resumeRequested = requestedPosition is not null;
            var resumeEligible = requestedPosition is { } target &&
                PlaybackProgressPolicy.IsContinueWatching(
                    target,
                    selection.ResumeDurationSeconds ?? duration);
            var resumeApplied = false;

            if (resumeEligible && requestedPosition is { } resumeTarget)
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var current = await QuerySecondsAsync(reader, writer, "get_time", setupToken);
                    if (current is not null &&
                        VlcPlaybackProtocol.IsResumeApplied(current.Value, resumeTarget))
                    {
                        resumeApplied = true;
                        break;
                    }

                    await Task.Delay(150, setupToken);
                }
            }

            _ = MonitorProgressAsync(
                process,
                client,
                reader,
                writer,
                mediaFileId,
                duration);
            monitorOwnsResources = true;

            var notes = new List<string>
            {
                "Started with VLC.",
                "Playback progress is being recorded from VLC's local control interface."
            };

            if (selection.AudioStreamIndex is not null || selection.SubtitleSelectionSpecified)
                notes.Add("EasyFlix inspected the requested tracks but does not claim VLC track selection in this profile yet.");

            if (resumeRequested)
                notes.Add(resumeApplied
                    ? "The saved Continue Watching position was verified in VLC."
                    : resumeEligible
                        ? "VLC started, but EasyFlix could not verify the requested resume position."
                        : "The saved position is no longer eligible under the Continue Watching policy.");

            return new PlaybackLaunchResult(true, false, false, string.Join(" ", notes));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return new PlaybackLaunchResult(false, false, false, "Timed out while connecting to VLC's local control interface.");
        }
        catch (Exception ex)
        {
            TryTerminate(process);
            return new PlaybackLaunchResult(false, false, false, "VLC playback launch failed: " + ex.Message);
        }
        finally
        {
            if (!monitorOwnsResources)
            {
                writer?.Dispose();
                reader?.Dispose();
                client?.Dispose();
                process.Dispose();
            }
        }
    }

    private ProcessStartInfo BuildStartInfo(
        string mediaPath,
        int port,
        PlaybackSelection selection)
    {
        var start = new ProcessStartInfo
        {
            FileName = _vlcPath,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        start.ArgumentList.Add("--no-one-instance");
        start.ArgumentList.Add("--play-and-exit");
        start.ArgumentList.Add("--extraintf=rc");
        start.ArgumentList.Add($"--rc-host=127.0.0.1:{port}");
        start.ArgumentList.Add("--no-video-title-show");

        if (selection.ResumePositionSeconds is { } position &&
            PlaybackProgressPolicy.IsContinueWatching(
                position,
                selection.ResumeDurationSeconds))
        {
            start.ArgumentList.Add(
                "--start-time=" + position.ToString("0.###", CultureInfo.InvariantCulture));
        }

        start.ArgumentList.Add(mediaPath);
        return start;
    }

    private async Task MonitorProgressAsync(
        Process process,
        TcpClient client,
        StreamReader reader,
        StreamWriter writer,
        string mediaFileId,
        double? duration)
    {
        double? position = null;
        var lastPersisted = DateTimeOffset.MinValue;

        try
        {
            while (!process.HasExited)
            {
                var current = await QuerySecondsAsync(reader, writer, "get_time", CancellationToken.None);
                var currentDuration = await QuerySecondsAsync(reader, writer, "get_length", CancellationToken.None);

                if (currentDuration is > 0)
                    duration = currentDuration;
                if (current is not null)
                {
                    position = current;
                    if (DateTimeOffset.UtcNow - lastPersisted >= TimeSpan.FromSeconds(5))
                    {
                        await _store.SavePlaybackProgressAsync(mediaFileId, current.Value, duration);
                        lastPersisted = DateTimeOffset.UtcNow;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                if (position is not null)
                {
                    if (duration is > 0 &&
                        PlaybackProgressPolicy.IsCompleted(position.Value, duration))
                    {
                        await _store.SavePlaybackProgressAsync(mediaFileId, duration.Value, duration);
                    }
                    else
                    {
                        await _store.SavePlaybackProgressAsync(mediaFileId, position.Value, duration);
                    }
                }
            }
            catch
            {
            }

            writer.Dispose();
            reader.Dispose();
            client.Dispose();
            process.Dispose();
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(
        int port,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                last = ex;
                client.Dispose();
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new IOException("VLC did not open its local control socket.", last);
    }

    private static async Task<double?> ReadPositiveSecondsWithRetryAsync(
        StreamReader reader,
        StreamWriter writer,
        string command,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var value = await QuerySecondsAsync(reader, writer, command, cancellationToken);
            if (value is > 0) return value;
            await Task.Delay(100, cancellationToken);
        }

        return null;
    }

    private static async Task<double?> QuerySecondsAsync(
        StreamReader reader,
        StreamWriter writer,
        string command,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(command.AsMemory(), cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null) return null;
            if (VlcPlaybackProtocol.TryParseSeconds(line, out var seconds))
                return seconds;
        }

        return null;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
