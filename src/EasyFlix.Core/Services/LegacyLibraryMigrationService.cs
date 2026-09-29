using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EasyFlix.Core.Models;
using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Services;

public sealed record LegacyLibraryEntry(
    string Category,
    string ShortcutName,
    string TargetPath,
    string IconPath);

public sealed record LegacyLibraryIdentityInfo(
    MediaKind Kind,
    string CanonicalTitle,
    string? Year,
    int? SeasonHint);

public sealed record LegacyLibraryMigrationResult(
    int EntriesSeen,
    int BindingsStored,
    int FilesReassigned,
    int CanonicalTitles,
    int ArtworkCopied,
    IReadOnlyList<string> UnmatchedEntries);

public static class LegacyLibraryIdentity
{
    private static readonly Regex YearRegex = new(
        @"\((?<year>(?:19|20)\d{2})\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SingleSeasonRegex = new(
        @"\bSeason(?!s)\s*(?<season>\d{1,2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static LegacyLibraryIdentityInfo Parse(string category, string shortcutName)
    {
        var kind = category.Trim().ToLowerInvariant() switch
        {
            "anime" => MediaKind.Anime,
            "tv" or "tv show" or "tv shows" => MediaKind.TvShow,
            _ => MediaKind.Movie
        };

        var title = shortcutName.Trim();
        title = Regex.Replace(title, @"^\s*\[EXT-F\]\s*", string.Empty, RegexOptions.IgnoreCase);
        title = title.Replace("\u2013", "-").Replace("\u2014", "-");

        string? year = null;
        var yearMatch = YearRegex.Match(title);
        if (yearMatch.Success)
        {
            year = yearMatch.Groups["year"].Value;
            title = YearRegex.Replace(title, " ");
        }

        int? seasonHint = null;
        if (kind != MediaKind.Movie)
        {
            var seasonMatch = SingleSeasonRegex.Match(title);
            if (seasonMatch.Success && int.TryParse(seasonMatch.Groups["season"].Value, out var season))
                seasonHint = season;

            title = Regex.Replace(title, @"\s*-\s*(?:Season|Seasons)\b.*$", string.Empty, RegexOptions.IgnoreCase);
            title = Regex.Replace(title, @"\s*-\s*Complete\s+Series\b.*$", string.Empty, RegexOptions.IgnoreCase);
        }

        while (Regex.IsMatch(title, @"\s*\[[^\]]+\]\s*$"))
            title = Regex.Replace(title, @"\s*\[[^\]]+\]\s*$", string.Empty);

        title = Regex.Replace(title, @"\s{2,}", " ").Trim(' ', '-', '_');
        if (string.IsNullOrWhiteSpace(title))
            title = MediaNameParser.CleanTitle(shortcutName);

        return new LegacyLibraryIdentityInfo(kind, title, year, seasonHint);
    }
}

public sealed class LegacyLibraryMigrationService(CatalogStore catalogStore)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    public async Task<bool> HasBindingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM LegacyLibraryBinding;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    public async Task<LegacyLibraryMigrationResult> ImportAsync(
        IEnumerable<LegacyLibraryEntry> entries,
        string artworkCacheRoot,
        CancellationToken cancellationToken = default)
    {
        var values = entries.ToArray();
        var prepared = new List<BindingRow>();
        var unresolved = new List<string>();
        var copiedArtwork = 0;
        Directory.CreateDirectory(artworkCacheRoot);

        foreach (var entry in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.TargetPath))
            {
                unresolved.Add(entry.ShortcutName);
                continue;
            }

            var identity = LegacyLibraryIdentity.Parse(entry.Category, entry.ShortcutName);
            var target = NormalizePath(entry.TargetPath);
            var isDirectory = GuessDirectory(target);
            var titleId = CatalogIdentity.TitleId(identity.Kind, identity.CanonicalTitle, identity.Year);

            string? localArtwork = null;
            if (!string.IsNullOrWhiteSpace(entry.IconPath) && File.Exists(entry.IconPath))
            {
                var extension = Path.GetExtension(entry.IconPath);
                if (string.IsNullOrWhiteSpace(extension)) extension = ".ico";
                localArtwork = Path.Combine(artworkCacheRoot, $"{titleId}-legacy{extension.ToLowerInvariant()}");
                var existed = File.Exists(localArtwork);
                File.Copy(entry.IconPath, localArtwork, overwrite: true);
                if (!existed) copiedArtwork++;
            }

            prepared.Add(new BindingRow(
                BindingId(target, identity),
                target,
                isDirectory,
                identity.Kind,
                identity.CanonicalTitle,
                identity.Year,
                identity.SeasonHint,
                localArtwork,
                entry.ShortcutName));
        }

        await using (var connection = await OpenAsync(cancellationToken))
        {
            await EnsureSchemaAsync(connection, cancellationToken);
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            foreach (var binding in prepared)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO LegacyLibraryBinding(
                        Id, TargetPath, IsDirectory, Kind, CanonicalTitle, Year,
                        SeasonHint, LocalArtworkPath, ShortcutName, UpdatedUtc)
                    VALUES(
                        $id, $target, $directory, $kind, $title, $year,
                        $season, $artwork, $shortcut, $now)
                    ON CONFLICT(Id) DO UPDATE SET
                        TargetPath = excluded.TargetPath,
                        IsDirectory = excluded.IsDirectory,
                        Kind = excluded.Kind,
                        CanonicalTitle = excluded.CanonicalTitle,
                        Year = excluded.Year,
                        SeasonHint = excluded.SeasonHint,
                        LocalArtworkPath = COALESCE(excluded.LocalArtworkPath, LegacyLibraryBinding.LocalArtworkPath),
                        ShortcutName = excluded.ShortcutName,
                        UpdatedUtc = excluded.UpdatedUtc;
                    """, cancellationToken,
                    ("$id", binding.Id), ("$target", binding.TargetPath),
                    ("$directory", binding.IsDirectory ? 1 : 0), ("$kind", (int)binding.Kind),
                    ("$title", binding.CanonicalTitle), ("$year", binding.Year),
                    ("$season", binding.SeasonHint), ("$artwork", binding.LocalArtworkPath),
                    ("$shortcut", binding.ShortcutName), ("$now", now));
            }

            transaction.Commit();
        }

        var applied = await ApplySavedBindingsCoreAsync(true, cancellationToken);
        var allUnresolved = unresolved
            .Concat(applied.UnmatchedEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LegacyLibraryMigrationResult(
            values.Length,
            prepared.Count,
            applied.FilesReassigned,
            applied.CanonicalTitles,
            copiedArtwork,
            allUnresolved);
    }

    public Task<LegacyLibraryMigrationResult> ApplySavedBindingsAsync(CancellationToken cancellationToken = default)
        => ApplySavedBindingsCoreAsync(false, cancellationToken);

    public async Task ReplaceLegacyArtworkPathsAsync(
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken cancellationToken = default)
    {
        if (replacements.Count == 0) return;

        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var replacement in replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(replacement.Key)
                || string.IsNullOrWhiteSpace(replacement.Value)
                || !File.Exists(replacement.Value))
                continue;

            await ExecuteAsync(connection, transaction, """
                UPDATE LegacyLibraryBinding
                SET LocalArtworkPath = $newPath,
                    UpdatedUtc = $now
                WHERE LocalArtworkPath = $oldPath COLLATE NOCASE;
                """, cancellationToken,
                ("$newPath", replacement.Value),
                ("$oldPath", replacement.Key),
                ("$now", now));

            await ExecuteAsync(connection, transaction, """
                UPDATE MediaTitle
                SET ArtworkPath = $newPath,
                    UpdatedUtc = $now
                WHERE ArtworkPath = $oldPath COLLATE NOCASE;
                """, cancellationToken,
                ("$newPath", replacement.Value),
                ("$oldPath", replacement.Key),
                ("$now", now));

            await ExecuteAsync(connection, transaction, """
                UPDATE ArtworkAsset
                SET LocalPath = $newPath
                WHERE LocalPath = $oldPath COLLATE NOCASE
                  AND Kind = 'poster-manual';
                """, cancellationToken,
                ("$newPath", replacement.Value),
                ("$oldPath", replacement.Key));
        }

        transaction.Commit();
    }

    private async Task<LegacyLibraryMigrationResult> ApplySavedBindingsCoreAsync(
        bool resetAutomaticMetadata,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var bindings = await LoadBindingsAsync(connection, cancellationToken);
        var files = await LoadFilesAsync(connection, cancellationToken);
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguousFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assignments = new Dictionary<string, FileAssignment>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in bindings)
        {
            var matches = FindMatches(binding, files);
            if (matches.Count == 0)
            {
                unmatched.Add(binding.ShortcutName);
                continue;
            }

            foreach (var file in matches)
            {
                if (ambiguousFiles.Contains(file.Id)) continue;

                if (!assignments.TryGetValue(file.Id, out var existing))
                {
                    assignments[file.Id] = new FileAssignment(file, binding);
                    continue;
                }

                if (SameCanonicalIdentity(existing.Binding, binding))
                {
                    if (binding.TargetPath.Length > existing.Binding.TargetPath.Length)
                        assignments[file.Id] = new FileAssignment(file, binding);
                    continue;
                }

                unmatched.Add(existing.Binding.ShortcutName);
                unmatched.Add(binding.ShortcutName);
                assignments.Remove(file.Id);
                ambiguousFiles.Add(file.Id);
            }
        }

        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var canonicalCount = 0;

        foreach (var group in assignments.Values
            .GroupBy(value => CanonicalGroupKey(value.Binding), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var first = group.First().Binding;
            var year = first.Kind == MediaKind.Movie
                ? first.Year
                : group.Select(value => value.Binding.Year)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var titleId = CatalogIdentity.TitleId(first.Kind, first.CanonicalTitle, year);
            var quality = group.Select(value => value.File.Quality)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) &&
                    !value.Equals("Video", StringComparison.OrdinalIgnoreCase))
                ?? "Video";
            var localArtwork = group.Select(value => value.Binding.LocalArtworkPath)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && File.Exists(value));

            if (resetAutomaticMetadata)
            {
                await ExecuteAsync(connection, transaction,
                    "DELETE FROM ProviderMatch WHERE TitleId = $titleId AND IsManual = 0;",
                    cancellationToken, ("$titleId", titleId));
                await ExecuteAsync(connection, transaction,
                    "DELETE FROM ArtworkAsset WHERE TitleId = $titleId AND Kind <> 'poster-manual';",
                    cancellationToken, ("$titleId", titleId));
            }

            await ExecuteAsync(connection, transaction, """
                INSERT INTO MediaTitle(
                    Id, Kind, DisplayTitle, SortTitle, Year, Description, Quality,
                    ArtworkPath, BackdropPath, CreatedUtc, UpdatedUtc)
                VALUES(
                    $id, $kind, $display, $sort, $year, '', $quality,
                    $artwork, NULL, $now, $now)
                ON CONFLICT(Id) DO UPDATE SET
                    Kind = excluded.Kind,
                    DisplayTitle = excluded.DisplayTitle,
                    SortTitle = excluded.SortTitle,
                    Year = COALESCE(excluded.Year, MediaTitle.Year),
                    Description = CASE WHEN $reset = 1 THEN '' ELSE MediaTitle.Description END,
                    Quality = CASE WHEN excluded.Quality <> 'Video' THEN excluded.Quality ELSE MediaTitle.Quality END,
                    ArtworkPath = CASE
                        WHEN $artwork IS NOT NULL
                             AND $artwork <> ''
                             AND (
                                 MediaTitle.ArtworkPath IS NULL
                                 OR MediaTitle.ArtworkPath = ''
                                 OR MediaTitle.ArtworkPath LIKE '%-legacy.png'
                                 OR MediaTitle.ArtworkPath LIKE '%-legacy.ico'
                             )
                        THEN $artwork
                        ELSE MediaTitle.ArtworkPath
                    END,
                    BackdropPath = CASE WHEN $reset = 1 THEN NULL ELSE MediaTitle.BackdropPath END,
                    UpdatedUtc = excluded.UpdatedUtc;
                """, cancellationToken,
                ("$id", titleId), ("$kind", (int)first.Kind),
                ("$display", first.CanonicalTitle), ("$sort", first.CanonicalTitle),
                ("$year", year), ("$quality", quality), ("$artwork", localArtwork),
                ("$now", now), ("$reset", resetAutomaticMetadata ? 1 : 0));

            await UpsertOverrideAsync(connection, transaction, titleId, "DisplayTitle", first.CanonicalTitle, now, cancellationToken);
            await UpsertOverrideAsync(connection, transaction, titleId, "Kind", ((int)first.Kind).ToString(), now, cancellationToken);
            if (!string.IsNullOrWhiteSpace(year))
                await UpsertOverrideAsync(connection, transaction, titleId, "Year", year!, now, cancellationToken);
            await UpsertOverrideAsync(connection, transaction, titleId, "LegacyLibraryBinding", "1", now, cancellationToken);

            if (!string.IsNullOrWhiteSpace(localArtwork))
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO ArtworkAsset(Id, TitleId, Kind, SourceUrl, LocalPath, Width, Height)
                    VALUES($id, $titleId, 'poster-manual', NULL, $path, NULL, NULL)
                    ON CONFLICT(Id) DO UPDATE SET
                        Kind = 'poster-manual',
                        SourceUrl = NULL,
                        LocalPath = excluded.LocalPath;
                    """, cancellationToken,
                    ("$id", titleId + ":poster-manual"),
                    ("$titleId", titleId), ("$path", localArtwork));
            }

            foreach (var assignment in group)
            {
                if (first.Kind == MediaKind.Movie)
                {
                    await ExecuteAsync(connection, transaction, """
                        UPDATE MediaFile
                        SET TitleId = $titleId, SeasonId = NULL, EpisodeId = NULL, UpdatedUtc = $now
                        WHERE Id = $fileId;
                        """, cancellationToken,
                        ("$titleId", titleId), ("$now", now), ("$fileId", assignment.File.Id));
                    continue;
                }

                var parsed = MediaNameParser.FindSeasonEpisode(
                    Path.GetFileName(assignment.File.Path),
                    Path.GetDirectoryName(assignment.File.Path));
                var seasonNumber = IsSpecialPath(assignment.File.Path)
                    ? 0
                    : parsed?.Season
                        ?? assignment.Binding.SeasonHint
                        ?? InferSeasonFromPath(assignment.File.Path)
                        ?? 1;
                var episodeNumber = parsed?.Episode ?? InferTrailingEpisode(Path.GetFileName(assignment.File.Path));
                var seasonLabel = seasonNumber == 0 ? "Specials" : $"Season {seasonNumber}";
                var seasonId = CatalogIdentity.SeasonId(titleId, seasonNumber, seasonLabel);
                var fileName = Path.GetFileName(assignment.File.Path);
                var episodeId = CatalogIdentity.EpisodeId(seasonId, episodeNumber, fileName);
                var cleaned = MediaNameParser.CleanTitle(fileName);
                var display = episodeNumber is null
                    ? (string.IsNullOrWhiteSpace(cleaned) ? fileName : cleaned)
                    : $"Episode {episodeNumber}";
                var sortKey = $"{seasonNumber:D4}|{episodeNumber ?? 9999:D4}|{fileName}";

                await ExecuteAsync(connection, transaction, """
                    INSERT INTO Season(Id, TitleId, SeasonNumber, Label)
                    VALUES($id, $titleId, $number, $label)
                    ON CONFLICT(Id) DO UPDATE SET
                        TitleId = excluded.TitleId,
                        SeasonNumber = excluded.SeasonNumber,
                        Label = excluded.Label;
                    """, cancellationToken,
                    ("$id", seasonId), ("$titleId", titleId),
                    ("$number", seasonNumber), ("$label", seasonLabel));

                await ExecuteAsync(connection, transaction, """
                    INSERT INTO Episode(Id, TitleId, SeasonId, EpisodeNumber, DisplayTitle, SortKey)
                    VALUES($id, $titleId, $seasonId, $number, $display, $sort)
                    ON CONFLICT(Id) DO UPDATE SET
                        TitleId = excluded.TitleId,
                        SeasonId = excluded.SeasonId,
                        EpisodeNumber = excluded.EpisodeNumber,
                        DisplayTitle = excluded.DisplayTitle,
                        SortKey = excluded.SortKey;
                    """, cancellationToken,
                    ("$id", episodeId), ("$titleId", titleId), ("$seasonId", seasonId),
                    ("$number", episodeNumber), ("$display", display), ("$sort", sortKey));

                await ExecuteAsync(connection, transaction, """
                    UPDATE MediaFile
                    SET TitleId = $titleId, SeasonId = $seasonId, EpisodeId = $episodeId, UpdatedUtc = $now
                    WHERE Id = $fileId;
                    """, cancellationToken,
                    ("$titleId", titleId), ("$seasonId", seasonId), ("$episodeId", episodeId),
                    ("$now", now), ("$fileId", assignment.File.Id));
            }

            canonicalCount++;
        }

        await ExecuteAsync(connection, transaction, """
            DELETE FROM Episode
            WHERE NOT EXISTS (SELECT 1 FROM MediaFile file WHERE file.EpisodeId = Episode.Id);
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM Season
            WHERE NOT EXISTS (SELECT 1 FROM MediaFile file WHERE file.SeasonId = Season.Id);
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            DELETE FROM MediaTitle
            WHERE NOT EXISTS (SELECT 1 FROM MediaFile file WHERE file.TitleId = MediaTitle.Id);
            """, cancellationToken);

        transaction.Commit();

        return new LegacyLibraryMigrationResult(
            bindings.Count, bindings.Count, assignments.Count, canonicalCount, 0,
            unmatched.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task UpsertOverrideAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string titleId,
        string field,
        string value,
        string now,
        CancellationToken cancellationToken)
        => await ExecuteAsync(connection, transaction, """
            INSERT INTO UserOverride(Id, TitleId, FieldName, Value, UpdatedUtc)
            VALUES($id, $titleId, $field, $value, $now)
            ON CONFLICT(Id) DO UPDATE SET Value = excluded.Value, UpdatedUtc = excluded.UpdatedUtc;
            """, cancellationToken,
            ("$id", titleId + ":legacy:" + field.ToLowerInvariant()),
            ("$titleId", titleId), ("$field", field), ("$value", value), ("$now", now));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await catalogStore.InitializeAsync(cancellationToken);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LegacyLibraryBinding (
                Id TEXT NOT NULL PRIMARY KEY,
                TargetPath TEXT NOT NULL,
                IsDirectory INTEGER NOT NULL,
                Kind INTEGER NOT NULL,
                CanonicalTitle TEXT NOT NULL,
                Year TEXT NULL,
                SeasonHint INTEGER NULL,
                LocalArtworkPath TEXT NULL,
                ShortcutName TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LegacyLibraryBinding_TargetPath
                ON LegacyLibraryBinding(TargetPath);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<BindingRow>> LoadBindingsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TargetPath, IsDirectory, Kind, CanonicalTitle, Year,
                   SeasonHint, LocalArtworkPath, ShortcutName
            FROM LegacyLibraryBinding
            ORDER BY LENGTH(TargetPath) DESC, ShortcutName COLLATE NOCASE;
            """;
        var result = new List<BindingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new BindingRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0,
                (MediaKind)reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<FileRow>> LoadFilesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, TitleId, Path, Quality FROM MediaFile;";
        var result = new List<FileRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new FileRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    private static IReadOnlyList<FileRow> FindMatches(BindingRow binding, IReadOnlyList<FileRow> files)
    {
        var direct = binding.IsDirectory
            ? files.Where(file => IsSameOrDescendant(file.Path, binding.TargetPath)).ToArray()
            : files.Where(file => PathsEqual(file.Path, binding.TargetPath)).ToArray();
        if (direct.Length > 0) return direct;

        var leaf = Path.GetFileName(binding.TargetPath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(leaf)) return [];

        if (!binding.IsDirectory)
        {
            var byFileName = files
                .Where(file => Path.GetFileName(file.Path).Equals(leaf, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return byFileName.Length == 1 ? byFileName : [];
        }

        var byFolder = files
            .Select(file => (File: file, Ancestor: FindAncestor(file.Path, leaf)))
            .Where(value => value.Ancestor is not null)
            .ToArray();
        var ancestors = byFolder.Select(value => value.Ancestor!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return ancestors.Length == 1 ? byFolder.Select(value => value.File).ToArray() : [];
    }

    private static string? FindAncestor(string filePath, string leaf)
    {
        var current = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var name = Path.GetFileName(current.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                return NormalizePath(current);
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static bool SameCanonicalIdentity(BindingRow left, BindingRow right)
        => CanonicalGroupKey(left).Equals(CanonicalGroupKey(right), StringComparison.OrdinalIgnoreCase);

    private static string CanonicalGroupKey(BindingRow binding)
        => binding.Kind == MediaKind.Movie
            ? $"{(int)binding.Kind}|{binding.CanonicalTitle}|{binding.Year ?? string.Empty}"
            : $"{(int)binding.Kind}|{binding.CanonicalTitle}";

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        try { return PathSafety.IsSameOrDescendant(candidate, parent); }
        catch { return false; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return NormalizePath(left).Equals(NormalizePath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool GuessDirectory(string targetPath)
    {
        if (Directory.Exists(targetPath)) return true;
        if (File.Exists(targetPath)) return false;
        return !VideoExtensions.Contains(Path.GetExtension(targetPath));
    }

    private static int? InferSeasonFromPath(string filePath)
    {
        var current = Path.GetDirectoryName(filePath);
        for (var depth = 0; depth < 5 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var name = Path.GetFileName(current.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var match = Regex.Match(name,
                @"(?:^|[\s._-])(?:S|Season)\s*0*(?<season>\d{1,2})(?:\D|$)",
                RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups["season"].Value, out var season))
                return season;
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static int? InferTrailingEpisode(string fileName)
    {
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"\[[^\]]+\]", " ");
        var match = Regex.Match(stem,
            @"(?:^|[\s._-])(?<episode>\d{1,3})(?=\D*$)",
            RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["episode"].Value, out var episode)
            ? episode : null;
    }

    private static bool IsSpecialPath(string filePath)
    {
        var current = Path.GetDirectoryName(filePath);
        for (var depth = 0; depth < 3 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var name = Path.GetFileName(current.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (Regex.IsMatch(name,
                @"\b(?:extras?|ovas?|oads?|specials?|featurettes?|textless|ncop|nced|chibi|interviews?)\b",
                RegexOptions.IgnoreCase))
                return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static string BindingId(string targetPath, LegacyLibraryIdentityInfo identity)
    {
        var input =
            $"{targetPath.ToUpperInvariant()}|{(int)identity.Kind}|{identity.CanonicalTitle.ToUpperInvariant()}|{identity.Year}|{identity.SeasonHint}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant()[..24];
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record BindingRow(
        string Id, string TargetPath, bool IsDirectory, MediaKind Kind,
        string CanonicalTitle, string? Year, int? SeasonHint,
        string? LocalArtworkPath, string ShortcutName);

    private sealed record FileRow(string Id, string TitleId, string Path, string Quality);
    private sealed record FileAssignment(FileRow File, BindingRow Binding);
}
