using System.Text.RegularExpressions;
using EasyFlix.Core.Models;
using EasyFlix.Core;

namespace EasyFlix.Core.Services;

public sealed class MediaScanner
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    private static readonly Regex CameraClipName = new(
        @"^MVI(?:[_ -]?\d{3,})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<IReadOnlyList<MediaItem>> ScanAsync(
        ScanOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<MediaItem> Scan(
        ScanOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var exclusions = EasyFlixSafetyPolicy.MergeExclusions(options.ExcludedPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var results = new List<MediaItem>();
        var candidateRoots = options.SourceRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToArray();
        var roots = candidateRoots
            .Where(candidate => !candidateRoots.Any(parent =>
                parent.Length < candidate.Length && PathSafety.IsSameOrDescendant(candidate, parent)))
            .ToArray();

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root) || PathSafety.IsExcluded(root, exclusions)) continue;
            progress?.Report($"Scanning {root}");

            foreach (var directory in SafeDirectories(root, exclusions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PathSafety.IsExcluded(directory, exclusions)) continue;
                var directVideos = SafeFiles(directory)
                    .Where(file => IsVideo(file) && !IsIgnoredCameraClip(file))
                    .ToArray();
                if (directVideos.Length < 2
                    || directVideos.Count(file => LibraryIdentityHeuristics.LooksLikeEpisode(file, directory)) < 2)
                    continue;

                var kind = LibraryIdentityHeuristics.LooksLikeAnimeSeries(directory, directVideos)
                    ? MediaKind.Anime
                    : MediaKind.TvShow;
                results.Add(CreateItem(directory, root, Path.GetFileName(directory), kind, true, IsRemovable(directory), directVideos.Length, directVideos[0]));
            }

            var episodeFiles = new HashSet<string>(results.Where(item => item.SourceRoot == root && item.IsFolder)
                .SelectMany(item => SafeFiles(item.SourcePath)).Where(IsVideo), StringComparer.OrdinalIgnoreCase);

            foreach (var file in SafeFilesRecursive(root, exclusions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsVideo(file)
                    || IsIgnoredCameraClip(file)
                    || PathSafety.IsExcluded(file, exclusions)
                    || episodeFiles.Contains(file))
                    continue;
                var filename = Path.GetFileName(file);
                var directory = Path.GetDirectoryName(file) ?? string.Empty;
                var isSeries = LibraryIdentityHeuristics.LooksLikeEpisode(filename, directory)
                    || LibraryIdentityHeuristics.IsSeriesSupplement(file);
                var kind = isSeries
                    ? (LibraryIdentityHeuristics.LooksLikeAnimeSeries(
                            directory,
                            new[] { file })
                        ? MediaKind.Anime
                        : MediaKind.TvShow)
                    : MediaKind.Movie;
                results.Add(CreateItem(file, root, filename, kind, false, IsRemovable(file), 1, filename));
            }
        }

        return results
            .GroupBy(item => PathSafety.Normalize(item.SourcePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.SuggestedName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MediaItem CreateItem(string path, string root, string name, MediaKind kind, bool folder, bool removable, int count, string? sampleName = null)
    {
        var seriesTitle = kind == MediaKind.Movie
            ? string.Empty
            : LibraryIdentityHeuristics.FindSeriesTitle(path, name, sampleName);
        var seasonLabel = kind == MediaKind.Movie
            ? string.Empty
            : LibraryIdentityHeuristics.FindSeasonLabel(path, name, sampleName);
        return new()
        {
            SourcePath = path,
            SourceRoot = root,
            OriginalName = name,
            Year = MediaNameParser.FindYear(name),
            Quality = MediaNameParser.FindQuality(name),
            Kind = kind,
            IsFolder = folder,
            IsRemovable = removable,
            VideoCount = count,
            SeriesTitle = seriesTitle,
            SeasonLabel = seasonLabel,
            SuggestedName = MediaNameParser.BuildDisplayName(
                name, kind, removable, folder ? null : new FileInfo(path).Directory?.Name)
        };
    }

    private static string FindSeriesTitle(string path, string name, string? sampleName)
    {
        foreach (var candidate in SeriesCandidates(path, name, sampleName))
        {
            var title = MediaNameParser.FindSeriesTitle(candidate);
            if (!string.IsNullOrWhiteSpace(title) && title.Any(char.IsLetter)
                && !title.Equals("Season", StringComparison.OrdinalIgnoreCase)) return title;
        }
        return "Unsorted series";
    }

    private static string FindSeasonLabel(string path, string name, string? sampleName)
    {
        foreach (var candidate in new[] { name, sampleName }.Concat(PathSegments(path)))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var label = MediaNameParser.FindSeasonLabel(candidate);
            if (!string.IsNullOrWhiteSpace(label)) return label;
        }
        return "Episodes";
    }

    private static IEnumerable<string> SeriesCandidates(string path, string name, string? sampleName)
    {
        if (!string.IsNullOrWhiteSpace(sampleName))
            yield return sampleName;
        yield return name;
        foreach (var segment in PathSegments(path)) yield return segment;
    }

    private static IEnumerable<string> PathSegments(string path)
    {
        var parent = Directory.Exists(path) ? new DirectoryInfo(path).Parent : new FileInfo(path).Directory;
        while (parent is not null)
        {
            yield return parent.Name;
            parent = parent.Parent;
        }
    }

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsIgnoredCameraClip(string path)
        => CameraClipName.IsMatch(Path.GetFileNameWithoutExtension(path));
    private static IEnumerable<string> SafeDirectories(string root, IReadOnlyCollection<string> exclusions)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (PathSafety.IsExcluded(current, exclusions)) continue;
            yield return current;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current); }
            catch { continue; }
            foreach (var child in children)
                if (!PathSafety.IsReparsePoint(child)) pending.Push(child);
        }
    }

    private static IEnumerable<string> SafeFiles(string directory)
    {
        try { return Directory.EnumerateFiles(directory); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeFilesRecursive(string root, IReadOnlyCollection<string> exclusions)
        => SafeDirectories(root, exclusions).SelectMany(SafeFiles);

    private static bool IsRemovable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return root is not null && new DriveInfo(root).DriveType == DriveType.Removable;
        }
        catch { return false; }
    }
}
