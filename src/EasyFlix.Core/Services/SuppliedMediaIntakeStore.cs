using Microsoft.Data.Sqlite;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed record SuppliedMediaMetadataUpdate(
    string Title,
    MediaKind Kind,
    string? Year,
    string Description,
    IReadOnlyList<string> MediaPaths,
    string? ArtworkPath = null,
    string? Provider = null,
    string? ProviderId = null,
    string? ProviderUrl = null);

public sealed record SuppliedMediaMetadataResult(
    string Title,
    bool Applied,
    bool Changed,
    string? TitleId,
    string? Reason);

public sealed class SuppliedMediaIntakeStore(CatalogStore catalogStore)
{
    public async Task<IReadOnlyList<SuppliedMediaMetadataResult>> ApplyAsync(
        IEnumerable<SuppliedMediaMetadataUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
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

        var results = new List<SuppliedMediaMetadataResult>();
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(update);

            var titleIds = await ResolveTitleIdsAsync(
                connection,
                update.MediaPaths,
                cancellationToken);

            if (titleIds.Count == 0)
            {
                results.Add(new(
                    update.Title,
                    Applied: false,
                    Changed: false,
                    TitleId: null,
                    Reason: "No catalog media matched the supplied exact path(s). Refresh the local library first."));
                continue;
            }

            if (titleIds.Count != 1)
            {
                results.Add(new(
                    update.Title,
                    Applied: false,
                    Changed: false,
                    TitleId: null,
                    Reason: $"Supplied paths map to {titleIds.Count} catalog titles; resolve the ambiguity before importing."));
                continue;
            }

            var titleId = titleIds.Single();
            using var transaction = connection.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");
            var changed = 0;

            changed += await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE MediaTitle
                SET Kind = $kind,
                    DisplayTitle = $title,
                    SortTitle = $title,
                    Year = CASE
                        WHEN $year IS NOT NULL AND $year <> '' THEN $year
                        ELSE Year
                    END,
                    Description = CASE
                        WHEN $description <> '' THEN $description
                        ELSE Description
                    END,
                    ArtworkPath = CASE
                        WHEN $artwork IS NOT NULL AND $artwork <> '' THEN $artwork
                        ELSE ArtworkPath
                    END,
                    UpdatedUtc = $now
                WHERE Id = $id
                  AND (
                      Kind <> $kind
                      OR DisplayTitle <> $title
                      OR SortTitle <> $title
                      OR ($year IS NOT NULL AND $year <> '' AND COALESCE(Year, '') <> $year)
                      OR ($description <> '' AND Description <> $description)
                      OR ($artwork IS NOT NULL AND $artwork <> '' AND COALESCE(ArtworkPath, '') <> $artwork)
                  );
                """,
                cancellationToken,
                ("$kind", (int)update.Kind),
                ("$title", update.Title.Trim()),
                ("$year", NullIfBlank(update.Year)),
                ("$description", update.Description?.Trim() ?? string.Empty),
                ("$artwork", NullIfBlank(update.ArtworkPath)),
                ("$now", now),
                ("$id", titleId));

            changed += await UpsertOverrideAsync(
                connection, transaction, titleId, "DisplayTitle", update.Title.Trim(), now, cancellationToken);
            changed += await UpsertOverrideAsync(
                connection, transaction, titleId, "Kind", ((int)update.Kind).ToString(), now, cancellationToken);

            if (!string.IsNullOrWhiteSpace(update.Year))
                changed += await UpsertOverrideAsync(
                    connection, transaction, titleId, "Year", update.Year.Trim(), now, cancellationToken);

            if (!string.IsNullOrWhiteSpace(update.Description))
                changed += await UpsertOverrideAsync(
                    connection, transaction, titleId, "Description", update.Description.Trim(), now, cancellationToken);

            if (!string.IsNullOrWhiteSpace(update.ArtworkPath))
            {
                var artwork = update.ArtworkPath.Trim();
                changed += await UpsertOverrideAsync(
                    connection, transaction, titleId, "ArtworkPath", artwork, now, cancellationToken);
                changed += await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO ArtworkAsset(Id, TitleId, Kind, SourceUrl, LocalPath, Width, Height)
                    VALUES($id, $titleId, 'poster-manual', NULL, $localPath, NULL, NULL)
                    ON CONFLICT(Id) DO UPDATE SET
                        LocalPath = excluded.LocalPath
                    WHERE COALESCE(ArtworkAsset.LocalPath, '') <> COALESCE(excluded.LocalPath, '');
                    """,
                    cancellationToken,
                    ("$id", titleId + ":poster-manual"),
                    ("$titleId", titleId),
                    ("$localPath", artwork));
            }

            if (!string.IsNullOrWhiteSpace(update.Provider)
                && !string.IsNullOrWhiteSpace(update.ProviderId))
            {
                var provider = update.Provider.Trim();
                var providerId = update.ProviderId.Trim();
                var providerUrl = NullIfBlank(update.ProviderUrl);
                changed += await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO ProviderMatch(
                        Id, TitleId, Provider, ProviderId, ProviderUrl, Confidence, IsManual, UpdatedUtc)
                    VALUES($id, $titleId, $provider, $providerId, $providerUrl, 1.0, 1, $now)
                    ON CONFLICT(Id) DO UPDATE SET
                        Provider = excluded.Provider,
                        ProviderId = excluded.ProviderId,
                        ProviderUrl = excluded.ProviderUrl,
                        Confidence = 1.0,
                        IsManual = 1,
                        UpdatedUtc = excluded.UpdatedUtc
                    WHERE ProviderMatch.Provider <> excluded.Provider
                       OR ProviderMatch.ProviderId <> excluded.ProviderId
                       OR COALESCE(ProviderMatch.ProviderUrl, '') <> COALESCE(excluded.ProviderUrl, '')
                       OR ProviderMatch.Confidence <> 1.0
                       OR ProviderMatch.IsManual <> 1;
                    """,
                    cancellationToken,
                    ("$id", titleId + ":" + provider.ToLowerInvariant()),
                    ("$titleId", titleId),
                    ("$provider", provider),
                    ("$providerId", providerId),
                    ("$providerUrl", providerUrl),
                    ("$now", now));
            }

            transaction.Commit();
            results.Add(new(
                update.Title,
                Applied: true,
                Changed: changed > 0,
                TitleId: titleId,
                Reason: null));
        }

        return results;
    }

    private static void Validate(SuppliedMediaMetadataUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Title))
            throw new ArgumentException("Supplied title is required.", nameof(update));
        if (update.MediaPaths is null || update.MediaPaths.Count == 0)
            throw new ArgumentException("At least one exact media path is required.", nameof(update));
        if (update.MediaPaths.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Media paths cannot be blank.", nameof(update));
        if (string.IsNullOrWhiteSpace(update.Provider) != string.IsNullOrWhiteSpace(update.ProviderId))
            throw new ArgumentException("Provider and ProviderId must be supplied together.", nameof(update));
    }

    private static async Task<HashSet<string>> ResolveTitleIdsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> mediaPaths,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawPath in mediaPaths)
        {
            var path = rawPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT TitleId
                FROM MediaFile
                WHERE lower(Path) = lower($path)
                   OR (
                       length(Path) > length($path)
                       AND lower(substr(Path, 1, length($path))) = lower($path)
                       AND substr(Path, length($path) + 1, 1) IN ('\', '/')
                   );
                """;
            command.Parameters.AddWithValue("$path", path);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<int> UpsertOverrideAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string titleId,
        string fieldName,
        string value,
        string now,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO UserOverride(Id, TitleId, FieldName, Value, UpdatedUtc)
            VALUES($id, $titleId, $fieldName, $value, $now)
            ON CONFLICT(Id) DO UPDATE SET
                TitleId = excluded.TitleId,
                FieldName = excluded.FieldName,
                Value = excluded.Value,
                UpdatedUtc = excluded.UpdatedUtc
            WHERE UserOverride.TitleId <> excluded.TitleId
               OR UserOverride.FieldName <> excluded.FieldName
               OR UserOverride.Value <> excluded.Value;
            """,
            cancellationToken,
            ("$id", titleId + ":intake:" + fieldName.ToLowerInvariant()),
            ("$titleId", titleId),
            ("$fieldName", fieldName),
            ("$value", value),
            ("$now", now));

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
