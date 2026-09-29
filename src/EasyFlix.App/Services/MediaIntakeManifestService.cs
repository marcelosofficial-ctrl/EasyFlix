using System.Drawing;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;
using Microsoft.Data.Sqlite;

namespace EasyFlix.App.Services;

public sealed class MediaIntakeManifestDocument
{
    public int Version { get; set; } = 1;
    public List<MediaIntakeManifestEntry> Entries { get; set; } = [];
}

public sealed class MediaIntakeManifestEntry
{
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Year { get; set; }
    public string Description { get; set; } = string.Empty;
    public string[] MediaPaths { get; set; } = [];
    public string? ArtworkPath { get; set; }
    public string? Provider { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderUrl { get; set; }
    public string[] ShortcutPaths { get; set; } = [];
}

public sealed record MediaIntakeManifestSummary(
    int Entries,
    int Applied,
    int Changed,
    int Unresolved,
    int ShortcutsCreated,
    int ShortcutsUpdated);

public sealed record MediaIntakeEntryResult(
    string Title,
    string MediaPath,
    bool Applied,
    bool Changed,
    string? Reason);

public sealed record MediaIntakeManifestReport(
    MediaIntakeManifestSummary Summary,
    IReadOnlyList<MediaIntakeEntryResult> Results);

public sealed record MediaIntakePreview(
    MediaIntakeManifestDocument Manifest,
    string Format,
    int LogicalTitles,
    int NewEntries,
    int Updates,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool CanImport => Errors.Count == 0;
}

public sealed record MediaIntakeImportOutcome(
    MediaIntakeManifestReport Report,
    string SourceRecordPath,
    string ReportPath);

public sealed class MediaIntakeManifestService
{
    private const int MaxManifestBytes = 2_000_000;
    private const int MaxArtworkBytes = 10_000_000;
    private readonly CatalogStore _catalogStore;
    private readonly SuppliedMediaIntakeStore _store;
    private readonly string _intakeRoot;
    private readonly string _savedManifestPath;

    public MediaIntakeManifestService(CatalogStore store, string? intakeRoot = null)
    {
        _catalogStore = store;
        _store = new SuppliedMediaIntakeStore(store);
        _intakeRoot = string.IsNullOrWhiteSpace(intakeRoot)
            ? IntakeRoot
            : Path.GetFullPath(intakeRoot);
        _savedManifestPath = Path.Combine(_intakeRoot, "manifest.json");
    }

    public string IntakeRootPath => _intakeRoot;

    public static string IntakeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyFlix",
        "media-intake");

    public static string SavedManifestPath => Path.Combine(IntakeRoot, "manifest.json");

    public MediaIntakePreview Preview(string sourcePath)
    {
        var (manifest, format, parseWarnings) = LoadHandoff(sourcePath);
        var warnings = parseWarnings.ToList();
        var errors = new List<string>();

        foreach (var entry in manifest.Entries)
        {
            try
            {
                ValidateEntry(entry);
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.Title}: {ex.Message}");
                continue;
            }

            foreach (var rawPath in entry.MediaPaths)
            {
                var path = Environment.ExpandEnvironmentVariables(rawPath.Trim());
                if (!File.Exists(path) && !Directory.Exists(path))
                    errors.Add($"{entry.Title}: media path does not exist: {path}");
            }

            if (!string.IsNullOrWhiteSpace(entry.ArtworkPath))
            {
                var artwork = Environment.ExpandEnvironmentVariables(entry.ArtworkPath.Trim());
                if (!File.Exists(artwork))
                    errors.Add($"{entry.Title}: supplied artwork does not exist: {artwork}");
            }

            foreach (var rawShortcut in entry.ShortcutPaths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(rawShortcut)
                    && !rawShortcut.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"{entry.Title}: shortcut path does not end in .lnk: {rawShortcut}");
            }
        }

        var existing = TryLoadSavedManifest();
        var updates = manifest.Entries.Count(
            incoming => existing.Entries.Any(existingEntry => EntriesMatch(existingEntry, incoming)));
        var logicalTitles = manifest.Entries
            .Select(entry => $"{entry.Kind.Trim()}|{entry.Title.Trim()}|{entry.Year?.Trim() ?? string.Empty}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new(
            manifest,
            format,
            logicalTitles,
            manifest.Entries.Count - updates,
            updates,
            warnings.Distinct(StringComparer.Ordinal).ToArray(),
            errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = Path.Combine(
            _intakeRoot,
            "backups",
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backup);

        var databaseBackup = Path.Combine(backup, "easyflix.db");
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _catalogStore.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString()))
        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databaseBackup,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString()))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }

        if (File.Exists(_savedManifestPath))
            File.Copy(_savedManifestPath, Path.Combine(backup, "manifest-before.json"), overwrite: true);

        return backup;
    }

    public async Task<MediaIntakeImportOutcome> ImportHandoffAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var preview = Preview(sourcePath);
        if (!preview.CanImport)
            throw new InvalidDataException(string.Join(Environment.NewLine, preview.Errors));

        var sourceRecord = PreserveSourceRecord(sourcePath);
        var merged = MergeManifests(TryLoadSavedManifest(), preview.Manifest);
        SaveManifest(merged);

        var report = await ApplyDocumentAsync(preview.Manifest, cancellationToken);
        var reportPath = WriteReport(sourcePath, preview, report);
        return new(report, sourceRecord, reportPath);
    }

    public async Task<MediaIntakeManifestSummary> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
        => (await ImportHandoffAsync(sourcePath, cancellationToken)).Report.Summary;

    public async Task<MediaIntakeManifestSummary?> ApplySavedAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_savedManifestPath))
            return null;
        return (await ApplyDocumentAsync(LoadManifest(_savedManifestPath), cancellationToken)).Summary;
    }

    public async Task<MediaIntakeManifestReport?> ApplySavedWithReportAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_savedManifestPath))
            return null;
        return await ApplyDocumentAsync(LoadManifest(_savedManifestPath), cancellationToken);
    }

    public async Task<MediaIntakeManifestReport> ApplyDocumentAsync(
        MediaIntakeManifestDocument manifest,
        CancellationToken cancellationToken = default)
    {
        var applied = 0;
        var changed = 0;
        var unresolved = 0;
        var shortcutsCreated = 0;
        var shortcutsUpdated = 0;
        var details = new List<MediaIntakeEntryResult>();

        foreach (var entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntry(entry);

            var cachedArtwork = CacheArtwork(entry, cancellationToken);
            var update = new SuppliedMediaMetadataUpdate(
                entry.Title.Trim(),
                ParseKind(entry.Kind),
                NullIfBlank(entry.Year),
                entry.Description?.Trim() ?? string.Empty,
                entry.MediaPaths.Select(path => path.Trim()).ToArray(),
                cachedArtwork,
                NullIfBlank(entry.Provider),
                NullIfBlank(entry.ProviderId),
                NullIfBlank(entry.ProviderUrl));

            var result = (await _store.ApplyAsync([update], cancellationToken)).Single();
            var mediaPath = entry.MediaPaths.FirstOrDefault() ?? string.Empty;
            if (!result.Applied)
            {
                unresolved++;
                details.Add(new(entry.Title, mediaPath, false, false, result.Reason));
                continue;
            }

            applied++;
            if (result.Changed)
                changed++;

            var shortcutResult = RepairShortcuts(entry, cancellationToken);
            shortcutsCreated += shortcutResult.Created;
            shortcutsUpdated += shortcutResult.Updated;
            details.Add(new(entry.Title, mediaPath, true, result.Changed, null));
        }

        var summary = new MediaIntakeManifestSummary(
            manifest.Entries.Count,
            applied,
            changed,
            unresolved,
            shortcutsCreated,
            shortcutsUpdated);
        return new(summary, details);
    }

    private static (MediaIntakeManifestDocument Manifest, string Format, IReadOnlyList<string> Warnings) LoadHandoff(
        string path)
    {
        ValidateSourceFile(path);
        var extension = Path.GetExtension(path);

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return (LoadManifest(path), "JSON manifest", []);

        if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EasyFlix media handoffs must be .txt or .json files.");

        var parsed = MediaHandoffTextParser.Parse(File.ReadAllText(path));
        if (parsed.Entries.Count == 0)
            throw new InvalidDataException(
                "No media entries were recognized in the text handoff. Use the EasyFlix handoff format shown in Add Media Center.");

        var manifest = new MediaIntakeManifestDocument
        {
            Entries = parsed.Entries.Select(entry => new MediaIntakeManifestEntry
            {
                Title = entry.Title,
                Kind = entry.Kind switch
                {
                    MediaKind.Movie => "Movie",
                    MediaKind.TvShow => "TV",
                    MediaKind.Anime => "Anime",
                    _ => entry.Kind.ToString()
                },
                Year = entry.Year,
                Description = entry.Description,
                MediaPaths = [entry.MediaPath],
                ArtworkPath = entry.ArtworkPath,
                Provider = entry.Provider,
                ProviderId = entry.ProviderId,
                ProviderUrl = entry.ProviderUrl,
                ShortcutPaths = string.IsNullOrWhiteSpace(entry.ShortcutPath) ? [] : [entry.ShortcutPath]
            }).ToList()
        };

        return (manifest, "TXT handoff", parsed.Warnings);
    }

    private static void ValidateSourceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Handoff path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("EasyFlix media handoff was not found.", path);
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaxManifestBytes)
            throw new InvalidDataException("EasyFlix media handoff is empty or exceeds the 2 MB limit.");
    }

    private static MediaIntakeManifestDocument LoadManifest(string path)
    {
        ValidateSourceFile(path);
        var manifest = JsonSerializer.Deserialize<MediaIntakeManifestDocument>(
            File.ReadAllText(path),
            JsonOptions(writeIndented: false))
            ?? throw new InvalidDataException("EasyFlix intake manifest could not be parsed.");

        if (manifest.Version != 1)
            throw new InvalidDataException($"Unsupported EasyFlix intake manifest version: {manifest.Version}.");
        if (manifest.Entries is null || manifest.Entries.Count == 0)
            throw new InvalidDataException("EasyFlix intake manifest contains no entries.");

        foreach (var entry in manifest.Entries)
            ValidateEntry(entry);
        return manifest;
    }

    private MediaIntakeManifestDocument TryLoadSavedManifest()
    {
        if (!File.Exists(_savedManifestPath))
            return new MediaIntakeManifestDocument();
        return LoadManifest(_savedManifestPath);
    }

    private static MediaIntakeManifestDocument MergeManifests(
        MediaIntakeManifestDocument existing,
        MediaIntakeManifestDocument incoming)
    {
        var merged = existing.Entries.ToList();

        foreach (var entry in incoming.Entries)
        {
            var index = merged.FindLastIndex(existingEntry => EntriesMatch(existingEntry, entry));
            if (index >= 0)
            {
                merged[index] = entry;
            }
            else
            {
                merged.Add(entry);
            }
        }

        return new MediaIntakeManifestDocument { Version = 1, Entries = merged };
    }

    private static bool EntriesMatch(MediaIntakeManifestEntry left, MediaIntakeManifestEntry right)
    {
        var leftShortcuts = NormalizedPathKeys(left.ShortcutPaths);
        var rightShortcuts = NormalizedPathKeys(right.ShortcutPaths);
        if (leftShortcuts.Overlaps(rightShortcuts))
            return true;

        var leftMedia = NormalizedPathKeys(left.MediaPaths);
        var rightMedia = NormalizedPathKeys(right.MediaPaths);
        return leftMedia.Overlaps(rightMedia);
    }

    private static HashSet<string> NormalizedPathKeys(IEnumerable<string>? paths)
    {
        return (paths ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizePathKey)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePathKey(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    private void SaveManifest(MediaIntakeManifestDocument manifest)
    {
        Directory.CreateDirectory(_intakeRoot);
        File.WriteAllText(
            _savedManifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions(writeIndented: true)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string PreserveSourceRecord(string sourcePath)
    {
        var directory = Path.Combine(_intakeRoot, "source-records");
        Directory.CreateDirectory(directory);
        var safeName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(
            directory,
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}__{safeName}");
        File.Copy(sourcePath, destination, overwrite: false);
        return destination;
    }

    private string WriteReport(
        string sourcePath,
        MediaIntakePreview preview,
        MediaIntakeManifestReport report)
    {
        var directory = Path.Combine(_intakeRoot, "reports");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(
            directory,
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}__{Path.GetFileNameWithoutExtension(sourcePath)}.txt");

        var builder = new StringBuilder();
        builder.AppendLine("EASYFLIX MEDIA HANDOFF IMPORT REPORT");
        builder.AppendLine($"Source: {Path.GetFileName(sourcePath)}");
        builder.AppendLine($"Format: {preview.Format}");
        builder.AppendLine($"Source entries: {preview.Manifest.Entries.Count}");
        builder.AppendLine($"Logical titles: {preview.LogicalTitles}");
        builder.AppendLine($"New entries: {preview.NewEntries}");
        builder.AppendLine($"Updates: {preview.Updates}");
        builder.AppendLine($"Applied: {report.Summary.Applied}");
        builder.AppendLine($"Changed: {report.Summary.Changed}");
        builder.AppendLine($"Unresolved: {report.Summary.Unresolved}");
        builder.AppendLine($"Shortcuts created: {report.Summary.ShortcutsCreated}");
        builder.AppendLine($"Shortcuts updated: {report.Summary.ShortcutsUpdated}");
        builder.AppendLine();
        foreach (var item in report.Results)
            builder.AppendLine(
                $"{(item.Applied ? "PASS" : "UNRESOLVED")} | {item.Title} | {item.MediaPath}"
                + (string.IsNullOrWhiteSpace(item.Reason) ? string.Empty : " | " + item.Reason));

        File.WriteAllText(destination, builder.ToString(), new UTF8Encoding(false));
        return destination;
    }

    private static void ValidateEntry(MediaIntakeManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Title))
            throw new InvalidDataException("Every intake entry requires a title.");
        _ = ParseKind(entry.Kind);
        if (entry.MediaPaths is null || entry.MediaPaths.Length == 0 || entry.MediaPaths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"'{entry.Title}' requires at least one exact media path.");
        if (string.IsNullOrWhiteSpace(entry.Provider) != string.IsNullOrWhiteSpace(entry.ProviderId))
            throw new InvalidDataException($"'{entry.Title}' must provide provider and ProviderId together.");
    }

    private static MediaKind ParseKind(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "movie" => MediaKind.Movie,
            "tv" or "tvshow" or "tv show" or "series" => MediaKind.TvShow,
            "anime" => MediaKind.Anime,
            _ => throw new InvalidDataException($"Unsupported media kind '{value}'. Use Movie, TV, or Anime.")
        };

    private static string? CacheArtwork(
        MediaIntakeManifestEntry entry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(entry.ArtworkPath))
            return null;

        var source = Environment.ExpandEnvironmentVariables(entry.ArtworkPath.Trim());
        if (!File.Exists(source))
            return null;

        var info = new FileInfo(source);
        if (info.Length <= 0 || info.Length > MaxArtworkBytes)
            throw new InvalidDataException($"Artwork for '{entry.Title}' is empty or exceeds the 10 MB limit.");

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(source);
        var sourceHash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        var identityBytes = Encoding.UTF8.GetBytes($"{entry.Kind}|{entry.Title}|{entry.Year}");
        var identityHash = Convert.ToHexString(SHA256.HashData(identityBytes))[..16];

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyFlix",
            "artwork",
            "manual");
        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, $"{identityHash}-{sourceHash}-manual.png");
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
            return destination;

        var extension = Path.GetExtension(source);
        if (string.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryExtractLargestEmbeddedPng(bytes, destination))
            {
                using var icon = new Icon(source, new Size(256, 256));
                using var bitmap = icon.ToBitmap();
                bitmap.Save(destination, ImageFormat.Png);
            }
        }
        else if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: true);
        }
        else
        {
            using var image = Image.FromFile(source);
            image.Save(destination, ImageFormat.Png);
        }

        if (!File.Exists(destination) || new FileInfo(destination).Length <= 0)
            throw new InvalidDataException($"Artwork for '{entry.Title}' could not be cached.");

        return destination;
    }

    private static (int Created, int Updated) RepairShortcuts(
        MediaIntakeManifestEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.ShortcutPaths is null || entry.ShortcutPaths.Length == 0)
            return (0, 0);

        var target = Environment.ExpandEnvironmentVariables(entry.MediaPaths[0].Trim());
        if (!File.Exists(target) && !Directory.Exists(target))
            return (0, 0);

        var artwork = NullIfBlank(entry.ArtworkPath);
        if (artwork is not null)
            artwork = Environment.ExpandEnvironmentVariables(artwork);
        var iconLocation = artwork is not null
                           && File.Exists(artwork)
                           && string.Equals(Path.GetExtension(artwork), ".ico", StringComparison.OrdinalIgnoreCase)
            ? artwork + ",0"
            : null;

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var created = 0;
        var updated = 0;

        try
        {
            foreach (var rawShortcut in entry.ShortcutPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(rawShortcut))
                    continue;

                var shortcutPath = Environment.ExpandEnvironmentVariables(rawShortcut.Trim());
                Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                try
                {
                    var existed = File.Exists(shortcutPath);
                    var workingDirectory = Directory.Exists(target)
                        ? target
                        : Path.GetDirectoryName(target)!;
                    var description = "EasyFlix: " + entry.Title.Trim();
                    var needsRefresh = !existed
                        || !SamePath(Convert.ToString(shortcut.TargetPath), target)
                        || !SamePath(Convert.ToString(shortcut.WorkingDirectory), workingDirectory)
                        || !string.Equals(Convert.ToString(shortcut.Description), description, StringComparison.Ordinal)
                        || iconLocation is not null
                           && !string.Equals(Convert.ToString(shortcut.IconLocation), iconLocation, StringComparison.OrdinalIgnoreCase);

                    if (!needsRefresh)
                        continue;

                    shortcut.TargetPath = target;
                    shortcut.WorkingDirectory = workingDirectory;
                    shortcut.Description = description;
                    if (iconLocation is not null)
                        shortcut.IconLocation = iconLocation;
                    shortcut.Save();

                    if (existed) updated++; else created++;
                }
                finally
                {
                    if (Marshal.IsComObject(shortcut))
                        Marshal.FinalReleaseComObject(shortcut);
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }

        return (created, updated);
    }

    private static bool TryExtractLargestEmbeddedPng(byte[] bytes, string destination)
    {
        if (bytes.Length < 22
            || bytes[0] != 0
            || bytes[1] != 0
            || bytes[2] != 1
            || bytes[3] != 0)
            return false;

        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0 || bytes.Length < 6 + (count * 16))
            return false;

        (int Pixels, int Size, int Offset)? best = null;
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + (index * 16);
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            var size = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            if (size < 8 || offset < 0 || offset > bytes.Length - size)
                continue;

            var isPng =
                bytes[offset] == 0x89
                && bytes[offset + 1] == 0x50
                && bytes[offset + 2] == 0x4E
                && bytes[offset + 3] == 0x47
                && bytes[offset + 4] == 0x0D
                && bytes[offset + 5] == 0x0A
                && bytes[offset + 6] == 0x1A
                && bytes[offset + 7] == 0x0A;
            if (!isPng)
                continue;

            var pixels = width * height;
            if (best is null
                || pixels > best.Value.Pixels
                || pixels == best.Value.Pixels && size > best.Value.Size)
                best = (pixels, size, offset);
        }

        if (best is null)
            return false;

        using var output = File.Create(destination);
        output.Write(bytes, best.Value.Offset, best.Value.Size);
        return true;
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions JsonOptions(bool writeIndented)
        => new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = writeIndented
        };

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
