using System.IO;
using System.Text.Json;

namespace EasyFlix.App.Services;

public sealed record PlaybackSettings(string? MpvPath, string? VlcPath = null);

public sealed class PlaybackSettingsService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyFlix",
        "playback-settings.json");

    public PlaybackSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<PlaybackSettings>(File.ReadAllText(_path))
                       ?? new PlaybackSettings(null, null);
        }
        catch
        {
        }

        return new PlaybackSettings(null, null);
    }

    public void Save(PlaybackSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }

    public static string? FindMpvExecutable()
    {
        foreach (var candidate in EnumerateMpvCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
            }
        }

        return null;
    }

    public static string? FindVlcExecutable()
    {
        foreach (var candidate in EnumerateVlcCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateMpvCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddIfPresent(
            ICollection<string> target,
            ISet<string> seenPaths,
            string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var full = Path.GetFullPath(path);
                if (seenPaths.Add(full))
                    target.Add(full);
            }
            catch
            {
            }
        }

        var candidates = new List<string>();
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                AddIfPresent(candidates, seen, Path.Combine(entry.Trim().Trim('"'), "mpv.exe"));
        }

        AddIfPresent(
            candidates,
            seen,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Links",
                "mpv.exe"));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddIfPresent(
            candidates,
            seen,
            Path.Combine(userProfile, "scoop", "shims", "mpv.exe"));

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        AddIfPresent(
            candidates,
            seen,
            Path.Combine(programData, "chocolatey", "bin", "mpv.exe"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        AddIfPresent(candidates, seen, Path.Combine(programFiles, "mpv", "mpv.exe"));

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        AddIfPresent(candidates, seen, Path.Combine(programFilesX86, "mpv", "mpv.exe"));

        return candidates;
    }

    private static IEnumerable<string> EnumerateVlcCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        static void Add(
            ICollection<string> target,
            ISet<string> seenPaths,
            string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (seenPaths.Add(full))
                    target.Add(full);
            }
            catch
            {
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                Add(candidates, seen, Path.Combine(entry.Trim().Trim('"'), "vlc.exe"));
        }

        Add(
            candidates,
            seen,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WinGet",
                "Links",
                "vlc.exe"));

        Add(
            candidates,
            seen,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VideoLAN",
                "VLC",
                "vlc.exe"));

        Add(
            candidates,
            seen,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "VideoLAN",
                "VLC",
                "vlc.exe"));

        Add(
            candidates,
            seen,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "VideoLAN",
                "VLC",
                "vlc.exe"));

        return candidates;
    }
}
