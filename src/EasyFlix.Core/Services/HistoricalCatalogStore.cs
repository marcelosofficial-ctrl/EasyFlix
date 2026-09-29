using EasyFlix.Core.Models;
using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Services;

public sealed record HistoricalCatalogEntry(
    MediaKind Kind,
    string DisplayTitle,
    string? Year,
    string RootPath,
    string Path,
    string Quality);

public sealed class HistoricalCatalogStore(CatalogStore catalogStore)
{
    public async Task<int> ImportOfflineAsync(
        IEnumerable<HistoricalCatalogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await catalogStore.InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var imported = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(entry);

            var root = PathSafety.Normalize(entry.RootPath);
            var path = PathSafety.Normalize(entry.Path);
            var title = entry.DisplayTitle.Trim();
            var quality = string.IsNullOrWhiteSpace(entry.Quality) ? "Video" : entry.Quality.Trim();
            var titleId = CatalogIdentity.TitleId(entry.Kind, title, entry.Year);
            var fileId = CatalogIdentity.FileId(path);

            await ExecuteAsync(connection, transaction, """
                INSERT INTO LibraryRoot(Path, IsOnline, LastSeenUtc)
                VALUES($root, 0, $now)
                ON CONFLICT(Path) DO UPDATE SET
                    IsOnline = CASE WHEN LibraryRoot.IsOnline = 1 THEN 1 ELSE 0 END;
                """, cancellationToken, ("$root", root), ("$now", now));

            await ExecuteAsync(connection, transaction, """
                INSERT INTO MediaTitle(
                    Id, Kind, DisplayTitle, SortTitle, Year, Description, Quality,
                    ArtworkPath, BackdropPath, CreatedUtc, UpdatedUtc)
                VALUES($id, $kind, $display, $display, $year, '', $quality, NULL, NULL, $now, $now)
                ON CONFLICT(Id) DO UPDATE SET
                    Year = COALESCE(MediaTitle.Year, excluded.Year),
                    UpdatedUtc = excluded.UpdatedUtc;
                """, cancellationToken,
                ("$id", titleId),
                ("$kind", (int)entry.Kind),
                ("$display", title),
                ("$year", entry.Year),
                ("$quality", quality),
                ("$now", now));

            await ExecuteAsync(connection, transaction, """
                INSERT INTO MediaFile(
                    Id, TitleId, SeasonId, EpisodeId, RootPath, Path, Quality,
                    SizeBytes, LastWriteUtc, Availability, MissingSinceUtc, AddedUtc, UpdatedUtc)
                VALUES($id, $titleId, NULL, NULL, $root, $path, $quality,
                    NULL, NULL, $offline, NULL, $now, $now)
                ON CONFLICT(Path) DO UPDATE SET
                    Availability = CASE
                        WHEN MediaFile.Availability = $online THEN $online
                        ELSE $offline
                    END,
                    UpdatedUtc = excluded.UpdatedUtc;
                """, cancellationToken,
                ("$id", fileId),
                ("$titleId", titleId),
                ("$root", root),
                ("$path", path),
                ("$quality", quality),
                ("$offline", (int)CatalogAvailability.Offline),
                ("$online", (int)CatalogAvailability.Online),
                ("$now", now));

            imported++;
        }

        transaction.Commit();
        return imported;
    }

    private static void Validate(HistoricalCatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DisplayTitle) ||
            string.IsNullOrWhiteSpace(entry.RootPath) ||
            string.IsNullOrWhiteSpace(entry.Path))
            throw new InvalidDataException("Historical catalog entry is incomplete.");

        var root = PathSafety.Normalize(entry.RootPath);
        var path = PathSafety.Normalize(entry.Path);

        var allowedRoot = root.StartsWith(@"B:\", StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(@"F:\", StringComparison.OrdinalIgnoreCase);
        if (!allowedRoot)
            throw new InvalidDataException("Historical bootstrap only accepts B: or F: roots.");

        if (!PathSafety.IsSameOrDescendant(path, root))
            throw new InvalidDataException("Historical media path must be inside its declared root.");

        if (EasyFlixSafetyPolicy.IsHardExcluded(root) || EasyFlixSafetyPolicy.IsHardExcluded(path))
            throw new InvalidDataException("Historical bootstrap entry points at a protected path.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
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
}
