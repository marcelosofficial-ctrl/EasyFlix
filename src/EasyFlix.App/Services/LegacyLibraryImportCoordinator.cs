using System.Runtime.InteropServices;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record LegacyLibraryBridgeResult(bool ImportedNow, LegacyLibraryMigrationResult Result);

public sealed class LegacyLibraryImportCoordinator
{
    private readonly LegacyLibraryMigrationService _migration;

    public LegacyLibraryImportCoordinator(CatalogStore store)
        => _migration = new LegacyLibraryMigrationService(store);

    public async Task<LegacyLibraryBridgeResult?> ApplyOrImportDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        var easyFlixRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyFlix");
        var artworkRoot = Path.Combine(easyFlixRoot, "artwork", "legacy");

        if (await _migration.HasBindingsAsync(cancellationToken))
        {
            var replacements = RepairLegacyArtwork(artworkRoot, cancellationToken);
            if (replacements.Count > 0)
                await _migration.ReplaceLegacyArtworkPathsAsync(replacements, cancellationToken);

            return new LegacyLibraryBridgeResult(
                false,
                await _migration.ApplySavedBindingsAsync(cancellationToken));
        }

        var root = FindDefaultLegacyRoot();
        if (root is null) return null;

        var entries = ReadShortcuts(root);
        if (entries.Count == 0) return null;

        var result = await _migration.ImportAsync(
            entries,
            artworkRoot,
            cancellationToken);

        var importedReplacements = RepairLegacyArtwork(artworkRoot, cancellationToken);
        if (importedReplacements.Count > 0)
            await _migration.ReplaceLegacyArtworkPathsAsync(importedReplacements, cancellationToken);

        WriteReport(easyFlixRoot, root, result);
        return new LegacyLibraryBridgeResult(true, result);
    }

    private static IReadOnlyDictionary<string, string> RepairLegacyArtwork(
        string artworkRoot,
        CancellationToken cancellationToken)
    {
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(artworkRoot)) return replacements;

        foreach (var iconPath in Directory.EnumerateFiles(
            artworkRoot,
            "*-legacy.ico",
            SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pngPath = Path.ChangeExtension(iconPath, ".png");
            try
            {
                if (!File.Exists(pngPath)
                    || File.GetLastWriteTimeUtc(pngPath) < File.GetLastWriteTimeUtc(iconPath))
                {
                    if (!TryExtractLargestEmbeddedPng(iconPath, pngPath))
                    {
                        using var icon = new Icon(iconPath, new Size(256, 256));
                        using var bitmap = icon.ToBitmap();
                        bitmap.Save(pngPath, ImageFormat.Png);
                    }
                }

                if (File.Exists(pngPath) && new FileInfo(pngPath).Length > 0)
                    replacements[iconPath] = pngPath;
            }
            catch
            {
                // A single malformed legacy icon must not block the local library.
            }
        }

        return replacements;
    }

    private static bool TryExtractLargestEmbeddedPng(
        string iconPath,
        string pngPath)
    {
        var bytes = File.ReadAllBytes(iconPath);
        if (bytes.Length < 22
            || bytes[0] != 0
            || bytes[1] != 0
            || bytes[2] != 1
            || bytes[3] != 0)
            return false;

        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0 || bytes.Length < 6 + (count * 16))
            return false;

        (int Pixels, int Width, int Height, int Size, int Offset)? best = null;
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
            if (!isPng) continue;

            var pixels = width * height;
            if (best is null
                || pixels > best.Value.Pixels
                || (pixels == best.Value.Pixels && size > best.Value.Size))
            {
                best = (pixels, width, height, size, offset);
            }
        }

        if (best is null)
            return false;

        using var output = File.Create(pngPath);
        output.Write(bytes, best.Value.Offset, best.Value.Size);
        return true;
    }

    private static string? FindDefaultLegacyRoot()
    {
        var candidates = new List<string>();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
            candidates.Add(Path.Combine(desktop, "Movie Library"));

        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer" })
        {
            var oneDrive = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(oneDrive))
                candidates.Add(Path.Combine(oneDrive, "Desktop", "Movie Library"));
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(Directory.Exists);
    }

    private static IReadOnlyList<LegacyLibraryEntry> ReadShortcuts(string root)
    {
        var categories = new[]
        {
            (Category: "Movies", Path: root),
            (Category: "Anime", Path: Path.Combine(root, "Anime Series")),
            (Category: "TV", Path: Path.Combine(root, "TV Shows"))
        };

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Could not create Windows Script Host.");
        var result = new List<LegacyLibraryEntry>();

        try
        {
            foreach (var category in categories)
            {
                if (!Directory.Exists(category.Path)) continue;

                foreach (var shortcutPath in Directory.EnumerateFiles(
                    category.Path, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    object? shortcutObject = null;
                    try
                    {
                        dynamic dynamicShell = shell;
                        shortcutObject = dynamicShell.CreateShortcut(shortcutPath);
                        dynamic shortcut = shortcutObject;

                        var target = Environment.ExpandEnvironmentVariables(
                            ((string?)shortcut.TargetPath ?? string.Empty).Trim());
                        var icon = CleanIconLocation(Environment.ExpandEnvironmentVariables(
                            ((string?)shortcut.IconLocation ?? string.Empty).Trim()));

                        result.Add(new LegacyLibraryEntry(
                            category.Category,
                            Path.GetFileNameWithoutExtension(shortcutPath),
                            target,
                            icon));
                    }
                    catch
                    {
                        result.Add(new LegacyLibraryEntry(
                            category.Category,
                            Path.GetFileNameWithoutExtension(shortcutPath),
                            string.Empty,
                            string.Empty));
                    }
                    finally
                    {
                        if (shortcutObject is not null && Marshal.IsComObject(shortcutObject))
                            Marshal.FinalReleaseComObject(shortcutObject);
                    }
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }

        return result;
    }

    private static string CleanIconLocation(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Regex.Replace(value, @",\s*-?\d+\s*$", string.Empty).Trim().Trim('"');
    }

    private static void WriteReport(
        string easyFlixRoot,
        string legacyRoot,
        LegacyLibraryMigrationResult result)
    {
        Directory.CreateDirectory(easyFlixRoot);
        var lines = new List<string>
        {
            "EasyFlix legacy library import report",
            $"Legacy root: {legacyRoot}",
            $"Entries seen: {result.EntriesSeen}",
            $"Bindings stored: {result.BindingsStored}",
            $"Files regrouped: {result.FilesReassigned}",
            $"Canonical titles: {result.CanonicalTitles}",
            $"Artwork copied: {result.ArtworkCopied}",
            $"Unresolved shortcuts: {result.UnmatchedEntries.Count}",
            string.Empty
        };
        lines.AddRange(result.UnmatchedEntries.Select(value => "UNRESOLVED: " + value));
        File.WriteAllLines(Path.Combine(easyFlixRoot, "legacy-import-report.txt"), lines);
    }
}
