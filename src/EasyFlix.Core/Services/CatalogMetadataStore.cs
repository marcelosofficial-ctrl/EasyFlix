using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Services;

public sealed record CatalogMetadataUpdate(
    string TitleId,
    string? Year,
    string Description,
    string? ArtworkPath,
    string Provider,
    string ProviderId,
    string ProviderUrl,
    string? ArtworkSourceUrl,
    double Confidence,
    string? BackdropPath = null,
    string? BackdropSourceUrl = null);

public sealed class CatalogMetadataStore(CatalogStore catalogStore)
{
    public async Task ApplyAsync(
        CatalogMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.TitleId))
            throw new ArgumentException("Title id is required.", nameof(update));
        if (string.IsNullOrWhiteSpace(update.Provider))
            throw new ArgumentException("Provider is required.", nameof(update));
        if (string.IsNullOrWhiteSpace(update.ProviderId))
            throw new ArgumentException("Provider id is required.", nameof(update));
        if (update.Confidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(update));

        await catalogStore.InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await ExecuteAsync(connection, transaction, """
            UPDATE MediaTitle
            SET Year = CASE
                    WHEN (Year IS NULL OR Year = '') AND $year IS NOT NULL AND $year <> '' THEN $year
                    ELSE Year
                END,
                Description = CASE
                    WHEN Description = '' AND $description <> '' THEN $description
                    ELSE Description
                END,
                ArtworkPath = CASE
                    WHEN $artworkPath IS NOT NULL
                         AND $artworkPath <> ''
                         AND (
                             ArtworkPath IS NULL
                             OR ArtworkPath = ''
                             OR ArtworkPath LIKE '%-legacy.png'
                             OR ArtworkPath LIKE '%-legacy.ico'
                         )
                    THEN $artworkPath
                    ELSE ArtworkPath
                END,
                BackdropPath = CASE
                    WHEN $backdropPath IS NOT NULL
                         AND $backdropPath <> ''
                         AND (BackdropPath IS NULL OR BackdropPath = '')
                    THEN $backdropPath
                    ELSE BackdropPath
                END,
                UpdatedUtc = $now
            WHERE Id = $titleId;
            """, cancellationToken,
            ("$year", update.Year),
            ("$description", update.Description ?? string.Empty),
            ("$artworkPath", update.ArtworkPath),
            ("$backdropPath", update.BackdropPath),
            ("$now", now),
            ("$titleId", update.TitleId));

        var providerMatchId = update.TitleId + ":" + update.Provider.Trim().ToLowerInvariant();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO ProviderMatch(
                Id, TitleId, Provider, ProviderId, ProviderUrl, Confidence, IsManual, UpdatedUtc)
            VALUES($id, $titleId, $provider, $providerId, $providerUrl, $confidence, 0, $now)
            ON CONFLICT(Id) DO UPDATE SET
                ProviderId = excluded.ProviderId,
                ProviderUrl = excluded.ProviderUrl,
                Confidence = excluded.Confidence,
                UpdatedUtc = excluded.UpdatedUtc;
            """, cancellationToken,
            ("$id", providerMatchId),
            ("$titleId", update.TitleId),
            ("$provider", update.Provider),
            ("$providerId", update.ProviderId),
            ("$providerUrl", update.ProviderUrl),
            ("$confidence", update.Confidence),
            ("$now", now));

        if (!string.IsNullOrWhiteSpace(update.ArtworkPath))
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO ArtworkAsset(Id, TitleId, Kind, SourceUrl, LocalPath, Width, Height)
                VALUES($id, $titleId, 'poster', $sourceUrl, $localPath, NULL, NULL)
                ON CONFLICT(Id) DO UPDATE SET
                    SourceUrl = excluded.SourceUrl,
                    LocalPath = excluded.LocalPath;
                """, cancellationToken,
                ("$id", update.TitleId + ":poster"),
                ("$titleId", update.TitleId),
                ("$sourceUrl", update.ArtworkSourceUrl),
                ("$localPath", update.ArtworkPath));
        }

        if (!string.IsNullOrWhiteSpace(update.BackdropPath))
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO ArtworkAsset(Id, TitleId, Kind, SourceUrl, LocalPath, Width, Height)
                VALUES($id, $titleId, 'backdrop', $sourceUrl, $localPath, NULL, NULL)
                ON CONFLICT(Id) DO UPDATE SET
                    SourceUrl = excluded.SourceUrl,
                    LocalPath = excluded.LocalPath;
                """, cancellationToken,
                ("$id", update.TitleId + ":backdrop"),
                ("$titleId", update.TitleId),
                ("$sourceUrl", update.BackdropSourceUrl),
                ("$localPath", update.BackdropPath));
        }

        transaction.Commit();
    }

    public async Task<int> ClearWeakAutomaticArtworkAsync(
        double minimumConfidence,
        CancellationToken cancellationToken = default)
    {
        if (minimumConfidence is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence));

        await catalogStore.InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogStore.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var clearTitleArtwork = connection.CreateCommand();
        clearTitleArtwork.Transaction = transaction;
        clearTitleArtwork.CommandText = """
            UPDATE MediaTitle
            SET ArtworkPath = NULL,
                UpdatedUtc = $now
            WHERE ArtworkPath IS NOT NULL
              AND ArtworkPath <> ''
              AND Id IN (
                  SELECT DISTINCT TitleId
                  FROM ProviderMatch
                  WHERE IsManual = 0
                    AND Confidence < $minimumConfidence
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM ArtworkAsset manualArtwork
                  WHERE manualArtwork.TitleId = MediaTitle.Id
                    AND manualArtwork.Kind = 'poster-manual'
              );
            """;
        clearTitleArtwork.Parameters.AddWithValue("$now", now);
        clearTitleArtwork.Parameters.AddWithValue("$minimumConfidence", minimumConfidence);
        var changed = await clearTitleArtwork.ExecuteNonQueryAsync(cancellationToken);

        await using var clearTitleBackdrop = connection.CreateCommand();
        clearTitleBackdrop.Transaction = transaction;
        clearTitleBackdrop.CommandText = """
            UPDATE MediaTitle
            SET BackdropPath = NULL,
                UpdatedUtc = $now
            WHERE BackdropPath IS NOT NULL
              AND BackdropPath <> ''
              AND Id IN (
                  SELECT DISTINCT TitleId
                  FROM ProviderMatch
                  WHERE IsManual = 0
                    AND Confidence < $minimumConfidence
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM ArtworkAsset manualBackdrop
                  WHERE manualBackdrop.TitleId = MediaTitle.Id
                    AND manualBackdrop.Kind = 'backdrop-manual'
              );
            """;
        clearTitleBackdrop.Parameters.AddWithValue("$now", now);
        clearTitleBackdrop.Parameters.AddWithValue("$minimumConfidence", minimumConfidence);
        changed += await clearTitleBackdrop.ExecuteNonQueryAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, """
            DELETE FROM ArtworkAsset
            WHERE Kind IN ('poster', 'backdrop')
              AND TitleId IN (
                  SELECT DISTINCT TitleId
                  FROM ProviderMatch
                  WHERE IsManual = 0
                    AND Confidence < $minimumConfidence
              );
            """, cancellationToken,
            ("$minimumConfidence", minimumConfidence));

        transaction.Commit();
        return changed;
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
