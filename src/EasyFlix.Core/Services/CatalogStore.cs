using EasyFlix.Core.Models;
using Microsoft.Data.Sqlite;

namespace EasyFlix.Core.Services;

public sealed class CatalogStore
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private volatile bool _initialized;

    public CatalogStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    public string DatabasePath => _databasePath;

    private const string FavoriteFieldName = "favorite";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized)
            return;

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS SchemaInfo (
                Version INTEGER NOT NULL PRIMARY KEY,
                AppliedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS LibraryRoot (
                Path TEXT NOT NULL PRIMARY KEY,
                IsOnline INTEGER NOT NULL,
                LastSeenUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS MediaTitle (
                Id TEXT NOT NULL PRIMARY KEY,
                Kind INTEGER NOT NULL,
                DisplayTitle TEXT NOT NULL,
                SortTitle TEXT NOT NULL,
                Year TEXT NULL,
                Description TEXT NOT NULL DEFAULT '',
                Quality TEXT NOT NULL DEFAULT 'Video',
                ArtworkPath TEXT NULL,
                BackdropPath TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Season (
                Id TEXT NOT NULL PRIMARY KEY,
                TitleId TEXT NOT NULL,
                SeasonNumber INTEGER NULL,
                Label TEXT NOT NULL,
                FOREIGN KEY (TitleId) REFERENCES MediaTitle(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Episode (
                Id TEXT NOT NULL PRIMARY KEY,
                TitleId TEXT NOT NULL,
                SeasonId TEXT NOT NULL,
                EpisodeNumber INTEGER NULL,
                DisplayTitle TEXT NOT NULL,
                SortKey TEXT NOT NULL,
                FOREIGN KEY (TitleId) REFERENCES MediaTitle(Id) ON DELETE CASCADE,
                FOREIGN KEY (SeasonId) REFERENCES Season(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS MediaFile (
                Id TEXT NOT NULL PRIMARY KEY,
                TitleId TEXT NOT NULL,
                SeasonId TEXT NULL,
                EpisodeId TEXT NULL,
                RootPath TEXT NOT NULL,
                Path TEXT NOT NULL UNIQUE,
                Quality TEXT NOT NULL DEFAULT 'Video',
                SizeBytes INTEGER NULL,
                LastWriteUtc TEXT NULL,
                Availability INTEGER NOT NULL,
                MissingSinceUtc TEXT NULL,
                AddedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (TitleId) REFERENCES MediaTitle(Id) ON DELETE CASCADE,
                FOREIGN KEY (SeasonId) REFERENCES Season(Id) ON DELETE SET NULL,
                FOREIGN KEY (EpisodeId) REFERENCES Episode(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS AudioTrack (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MediaFileId TEXT NOT NULL,
                StreamIndex INTEGER NULL,
                Language TEXT NULL,
                RawLanguage TEXT NULL,
                Codec TEXT NULL,
                Channels INTEGER NULL,
                TrackTitle TEXT NULL,
                Historical INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (MediaFileId) REFERENCES MediaFile(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SubtitleTrack (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MediaFileId TEXT NOT NULL,
                StreamIndex INTEGER NULL,
                Language TEXT NULL,
                RawLanguage TEXT NULL,
                Kind TEXT NOT NULL,
                SourcePath TEXT NULL,
                Codec TEXT NULL,
                Forced INTEGER NOT NULL DEFAULT 0,
                HearingImpaired INTEGER NOT NULL DEFAULT 0,
                Historical INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (MediaFileId) REFERENCES MediaFile(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ArtworkAsset (
                Id TEXT NOT NULL PRIMARY KEY,
                TitleId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                SourceUrl TEXT NULL,
                LocalPath TEXT NULL,
                Width INTEGER NULL,
                Height INTEGER NULL,
                FOREIGN KEY (TitleId) REFERENCES MediaTitle(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ProviderMatch (
                Id TEXT NOT NULL PRIMARY KEY,
                TitleId TEXT NOT NULL,
                Provider TEXT NOT NULL,
                ProviderId TEXT NOT NULL,
                ProviderUrl TEXT NULL,
                Confidence REAL NOT NULL,
                IsManual INTEGER NOT NULL DEFAULT 0,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (TitleId) REFERENCES MediaTitle(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS ScanRun (
                Id TEXT NOT NULL PRIMARY KEY,
                StartedUtc TEXT NOT NULL,
                CompletedUtc TEXT NULL,
                Result TEXT NOT NULL,
                RootsOnline INTEGER NOT NULL DEFAULT 0,
                RootsOffline INTEGER NOT NULL DEFAULT 0,
                TitlesObserved INTEGER NOT NULL DEFAULT 0,
                FilesObserved INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS PathHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MediaFileId TEXT NOT NULL,
                PreviousPath TEXT NOT NULL,
                CurrentPath TEXT NOT NULL,
                ChangedUtc TEXT NOT NULL,
                FOREIGN KEY (MediaFileId) REFERENCES MediaFile(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS PlaybackProgress (
                MediaFileId TEXT NOT NULL PRIMARY KEY,
                PositionSeconds REAL NOT NULL,
                DurationSeconds REAL NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (MediaFileId) REFERENCES MediaFile(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS UserOverride (
                Id TEXT NOT NULL PRIMARY KEY,
                TitleId TEXT NOT NULL,
                FieldName TEXT NOT NULL,
                Value TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (TitleId) REFERENCES MediaTitle(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_Season_TitleId ON Season(TitleId);
            CREATE INDEX IF NOT EXISTS IX_Episode_SeasonId ON Episode(SeasonId);
            CREATE INDEX IF NOT EXISTS IX_MediaFile_TitleId ON MediaFile(TitleId);
            CREATE INDEX IF NOT EXISTS IX_MediaFile_RootPath ON MediaFile(RootPath);
            CREATE INDEX IF NOT EXISTS IX_MediaFile_Availability ON MediaFile(Availability);
            CREATE INDEX IF NOT EXISTS IX_AudioTrack_MediaFileId ON AudioTrack(MediaFileId);
            CREATE INDEX IF NOT EXISTS IX_SubtitleTrack_MediaFileId ON SubtitleTrack(MediaFileId);

            INSERT OR IGNORE INTO SchemaInfo(Version, AppliedUtc) VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> LoadKnownRootPathsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Path
            FROM (
                SELECT Path FROM LibraryRoot
                UNION
                SELECT DISTINCT RootPath AS Path
                FROM MediaFile
                WHERE RootPath IS NOT NULL AND TRIM(RootPath) <> ''
            )
            ORDER BY Path COLLATE NOCASE;
            """;

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var path = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(path))
                result.Add(PathSafety.Normalize(path));
        }

        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task MarkRootOfflineAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var root = PathSafety.Normalize(rootPath);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await ExecuteAsync(connection, transaction, """
            INSERT INTO LibraryRoot(Path, IsOnline, LastSeenUtc) VALUES ($root, 0, $now)
            ON CONFLICT(Path) DO UPDATE SET IsOnline = 0, LastSeenUtc = excluded.LastSeenUtc;
            """, cancellationToken, ("$root", root), ("$now", now));

        await ExecuteAsync(connection, transaction, """
            UPDATE MediaFile
            SET Availability = $offline, UpdatedUtc = $now
            WHERE RootPath = $root AND Availability = $online;
            """, cancellationToken,
            ("$offline", (int)CatalogAvailability.Offline), ("$online", (int)CatalogAvailability.Online),
            ("$now", now), ("$root", root));

        transaction.Commit();
    }

    public async Task ReplaceOnlineRootAsync(CatalogRootSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var root = PathSafety.Normalize(snapshot.RootPath);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var titleIdMap = await ResolveKnownSeriesTitleIdsAsync(
            connection,
            transaction,
            snapshot.Titles,
            cancellationToken);
        var seasonIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var episodeIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await ExecuteAsync(connection, transaction, """
            INSERT INTO LibraryRoot(Path, IsOnline, LastSeenUtc) VALUES ($root, 1, $now)
            ON CONFLICT(Path) DO UPDATE SET IsOnline = 1, LastSeenUtc = excluded.LastSeenUtc;
            """, cancellationToken, ("$root", root), ("$now", now));

        await ExecuteAsync(connection, transaction, """
            UPDATE MediaFile
            SET Availability = $missing,
                MissingSinceUtc = COALESCE(MissingSinceUtc, $now),
                UpdatedUtc = $now
            WHERE RootPath = $root;
            """, cancellationToken,
            ("$missing", (int)CatalogAvailability.Missing), ("$now", now), ("$root", root));

        foreach (var title in snapshot.Titles)
        {
            var mappedTitleId = titleIdMap.GetValueOrDefault(title.Id, title.Id);
            if (!string.Equals(mappedTitleId, title.Id, StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE MediaTitle
                    SET Quality = CASE
                            WHEN $quality <> 'Video' THEN $quality
                            ELSE Quality
                        END,
                        UpdatedUtc = $now
                    WHERE Id = $id;
                    """, cancellationToken,
                    ("$quality", title.Quality),
                    ("$now", now),
                    ("$id", mappedTitleId));
                continue;
            }

            await ExecuteAsync(connection, transaction, """
                INSERT INTO MediaTitle(
                    Id, Kind, DisplayTitle, SortTitle, Year, Description, Quality,
                    ArtworkPath, BackdropPath, CreatedUtc, UpdatedUtc)
                VALUES(
                    $id, $kind, $display, $sort, $year, $description, $quality,
                    $artwork, $backdrop, $now, $now)
                ON CONFLICT(Id) DO UPDATE SET
                    Kind = excluded.Kind,
                    DisplayTitle = excluded.DisplayTitle,
                    SortTitle = excluded.SortTitle,
                    Year = COALESCE(excluded.Year, MediaTitle.Year),
                    Description = CASE WHEN MediaTitle.Description <> '' THEN MediaTitle.Description ELSE excluded.Description END,
                    Quality = CASE WHEN excluded.Quality <> 'Video' THEN excluded.Quality ELSE MediaTitle.Quality END,
                    ArtworkPath = COALESCE(MediaTitle.ArtworkPath, excluded.ArtworkPath),
                    BackdropPath = COALESCE(MediaTitle.BackdropPath, excluded.BackdropPath),
                    UpdatedUtc = excluded.UpdatedUtc;
                """, cancellationToken,
                ("$id", mappedTitleId), ("$kind", (int)title.Kind), ("$display", title.DisplayTitle),
                ("$sort", title.SortTitle), ("$year", title.Year), ("$description", title.Description),
                ("$quality", title.Quality), ("$artwork", title.ArtworkPath), ("$backdrop", title.BackdropPath),
                ("$now", now));
        }

        foreach (var season in snapshot.Seasons)
        {
            var mappedTitleId = titleIdMap.GetValueOrDefault(season.TitleId, season.TitleId);
            var mappedSeasonId = CatalogIdentity.SeasonId(
                mappedTitleId,
                season.SeasonNumber,
                season.Label);
            seasonIdMap[season.Id] = mappedSeasonId;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO Season(Id, TitleId, SeasonNumber, Label)
                VALUES($id, $title, $number, $label)
                ON CONFLICT(Id) DO UPDATE SET TitleId = excluded.TitleId, SeasonNumber = excluded.SeasonNumber, Label = excluded.Label;
                """, cancellationToken,
                ("$id", mappedSeasonId), ("$title", mappedTitleId),
                ("$number", season.SeasonNumber), ("$label", season.Label));
        }

        foreach (var episode in snapshot.Episodes)
        {
            var mappedTitleId = titleIdMap.GetValueOrDefault(episode.TitleId, episode.TitleId);
            var mappedSeasonId = seasonIdMap.GetValueOrDefault(episode.SeasonId, episode.SeasonId);
            var mappedEpisodeId = CatalogIdentity.EpisodeId(
                mappedSeasonId,
                episode.EpisodeNumber,
                EpisodeFileNameFromSortKey(episode.SortKey));
            episodeIdMap[episode.Id] = mappedEpisodeId;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO Episode(Id, TitleId, SeasonId, EpisodeNumber, DisplayTitle, SortKey)
                VALUES($id, $title, $season, $number, $display, $sort)
                ON CONFLICT(Id) DO UPDATE SET
                    TitleId = excluded.TitleId,
                    SeasonId = excluded.SeasonId,
                    EpisodeNumber = excluded.EpisodeNumber,
                    DisplayTitle = excluded.DisplayTitle,
                    SortKey = excluded.SortKey;
                """, cancellationToken,
                ("$id", mappedEpisodeId), ("$title", mappedTitleId), ("$season", mappedSeasonId),
                ("$number", episode.EpisodeNumber), ("$display", episode.DisplayTitle), ("$sort", episode.SortKey));
        }

        foreach (var file in snapshot.Files)
        {
            var mappedTitleId = titleIdMap.GetValueOrDefault(file.TitleId, file.TitleId);
            var mappedSeasonId = file.SeasonId is null
                ? null
                : seasonIdMap.GetValueOrDefault(file.SeasonId, file.SeasonId);
            var mappedEpisodeId = file.EpisodeId is null
                ? null
                : episodeIdMap.GetValueOrDefault(file.EpisodeId, file.EpisodeId);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO MediaFile(
                    Id, TitleId, SeasonId, EpisodeId, RootPath, Path, Quality,
                    SizeBytes, LastWriteUtc, Availability, MissingSinceUtc, AddedUtc, UpdatedUtc)
                VALUES(
                    $id, $title, $season, $episode, $root, $path, $quality,
                    $size, $lastWrite, $online, NULL, $now, $now)
                ON CONFLICT(Id) DO UPDATE SET
                    TitleId = excluded.TitleId,
                    SeasonId = excluded.SeasonId,
                    EpisodeId = excluded.EpisodeId,
                    RootPath = excluded.RootPath,
                    Path = excluded.Path,
                    Quality = excluded.Quality,
                    SizeBytes = excluded.SizeBytes,
                    LastWriteUtc = excluded.LastWriteUtc,
                    Availability = excluded.Availability,
                    MissingSinceUtc = NULL,
                    UpdatedUtc = excluded.UpdatedUtc;
                """, cancellationToken,
                ("$id", file.Id), ("$title", mappedTitleId), ("$season", mappedSeasonId),
                ("$episode", mappedEpisodeId), ("$root", root), ("$path", file.Path), ("$quality", file.Quality),
                ("$size", file.SizeBytes), ("$lastWrite", file.LastWriteUtc?.ToString("O")),
                ("$online", (int)CatalogAvailability.Online), ("$now", now));
        }

        transaction.Commit();
    }

    public async Task<IReadOnlyList<CatalogTitleSummary>> LoadTitleSummariesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                t.Id, t.Kind, t.DisplayTitle, t.Year, t.Description, t.Quality,
                t.ArtworkPath, t.BackdropPath,
                MIN(CASE WHEN f.Availability <> 0 THEN f.RootPath END) AS RequiredRootPath,
                CASE
                    WHEN SUM(CASE WHEN f.Availability = 0 THEN 1 ELSE 0 END) > 0 THEN 0
                    WHEN SUM(CASE WHEN f.Availability = 1 THEN 1 ELSE 0 END) > 0 THEN 1
                    ELSE 2
                END AS Availability,
                COUNT(DISTINCT f.SeasonId) AS SeasonCount,
                COUNT(DISTINCT f.EpisodeId) AS EpisodeCount,
                t.UpdatedUtc
            FROM MediaTitle t
            LEFT JOIN MediaFile f ON f.TitleId = t.Id
            GROUP BY t.Id
            HAVING COUNT(f.Id) > 0
            ORDER BY t.SortTitle COLLATE NOCASE;
            """;

        var result = new List<CatalogTitleSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadTitleSummary(reader));
        }
        return result;
    }

    public async Task<CatalogTitleDetails?> LoadTitleDetailsAsync(string titleId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);

        CatalogTitleSummary? title = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    t.Id, t.Kind, t.DisplayTitle, t.Year, t.Description, t.Quality,
                    t.ArtworkPath, t.BackdropPath,
                    MIN(CASE WHEN f.Availability <> 0 THEN f.RootPath END) AS RequiredRootPath,
                    CASE
                        WHEN SUM(CASE WHEN f.Availability = 0 THEN 1 ELSE 0 END) > 0 THEN 0
                        WHEN SUM(CASE WHEN f.Availability = 1 THEN 1 ELSE 0 END) > 0 THEN 1
                        ELSE 2
                    END AS Availability,
                    COUNT(DISTINCT CASE WHEN f.Id IS NOT NULL THEN s.Id END) AS SeasonCount,
                    COUNT(DISTINCT CASE WHEN f.Id IS NOT NULL THEN e.Id END) AS EpisodeCount,
                    t.UpdatedUtc
                FROM MediaTitle t
                LEFT JOIN MediaFile f ON f.TitleId = t.Id
                LEFT JOIN Season s ON s.Id = f.SeasonId
                LEFT JOIN Episode e ON e.Id = f.EpisodeId
                WHERE t.Id = $id
                GROUP BY t.Id;
                """;
            command.Parameters.AddWithValue("$id", titleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) title = ReadTitleSummary(reader);
        }

        if (title is null) return null;

        var filesByEpisode = new Dictionary<string, CatalogMediaFileSummary>(StringComparer.OrdinalIgnoreCase);
        var movieFiles = new List<CatalogMediaFileSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, EpisodeId, Path, RootPath, Quality, Availability, SizeBytes, LastWriteUtc
                FROM MediaFile
                WHERE TitleId = $id
                ORDER BY Path COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$id", titleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var file = ReadMediaFile(reader, 0, 2);
                if (reader.IsDBNull(1)) movieFiles.Add(file);
                else filesByEpisode[reader.GetString(1)] = file;
            }
        }

        var seasons = new List<CatalogSeasonSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.Id, s.SeasonNumber, s.Label, e.Id, e.EpisodeNumber, e.DisplayTitle
                FROM Season s
                LEFT JOIN Episode e ON e.SeasonId = s.Id
                WHERE s.TitleId = $id
                ORDER BY COALESCE(s.SeasonNumber, 9999), s.Label COLLATE NOCASE, e.SortKey COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$id", titleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            string? currentSeasonId = null;
            int? currentSeasonNumber = null;
            string currentLabel = string.Empty;
            var currentEpisodes = new List<CatalogEpisodeSummary>();

            while (await reader.ReadAsync(cancellationToken))
            {
                var seasonId = reader.GetString(0);
                if (!string.Equals(currentSeasonId, seasonId, StringComparison.OrdinalIgnoreCase))
                {
                    if (currentSeasonId is not null)
                        seasons.Add(new CatalogSeasonSummary(currentSeasonId, currentSeasonNumber, currentLabel, currentEpisodes.ToArray()));
                    currentSeasonId = seasonId;
                    currentSeasonNumber = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetInt64(1));
                    currentLabel = reader.GetString(2);
                    currentEpisodes = [];
                }

                if (reader.IsDBNull(3)) continue;
                var episodeId = reader.GetString(3);
                if (!filesByEpisode.TryGetValue(episodeId, out var file)) continue;
                var episodeNumber = reader.IsDBNull(4) ? (int?)null : Convert.ToInt32(reader.GetInt64(4));
                currentEpisodes.Add(new CatalogEpisodeSummary(episodeId, episodeNumber, reader.GetString(5), file));
            }

            if (currentSeasonId is not null)
                seasons.Add(new CatalogSeasonSummary(currentSeasonId, currentSeasonNumber, currentLabel, currentEpisodes.ToArray()));
        }

        return new CatalogTitleDetails(title, seasons, movieFiles);
    }

    public async Task SaveStreamSnapshotAsync(
        string mediaFileId,
        CatalogMediaStreamInfo streamInfo,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        await ExecuteAsync(connection, transaction, "DELETE FROM AudioTrack WHERE MediaFileId = $id;", cancellationToken, ("$id", mediaFileId));
        await ExecuteAsync(connection, transaction, "DELETE FROM SubtitleTrack WHERE MediaFileId = $id;", cancellationToken, ("$id", mediaFileId));

        foreach (var stream in streamInfo.AudioStreams)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO AudioTrack(MediaFileId, StreamIndex, Language, RawLanguage, Historical)
                VALUES($id, $index, $language, $language, 0);
                """, cancellationToken, ("$id", mediaFileId), ("$index", stream.Index), ("$language", stream.Language));
        }

        foreach (var track in streamInfo.SubtitleTracks
                     .OrderBy(value => value.Language, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Kind, StringComparer.OrdinalIgnoreCase))
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO SubtitleTrack(MediaFileId, StreamIndex, Language, RawLanguage, Kind, SourcePath, Historical)
                VALUES($id, $index, $language, $language, $kind, $sourcePath, 0);
                """, cancellationToken,
                ("$id", mediaFileId), ("$index", track.StreamIndex), ("$language", track.Language),
                ("$kind", track.Kind), ("$sourcePath", track.SourcePath));
        }

        transaction.Commit();
    }

    public async Task SetTitleFavoriteAsync(
        string titleId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
            throw new ArgumentException("Title id is required.", nameof(titleId));

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        if (isFavorite)
        {
            command.CommandText = """
                INSERT INTO UserOverride(Id, TitleId, FieldName, Value, UpdatedUtc)
                VALUES($id, $titleId, $field, '1', $updated)
                ON CONFLICT(Id) DO UPDATE SET
                    TitleId = excluded.TitleId,
                    FieldName = excluded.FieldName,
                    Value = excluded.Value,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$id", FavoriteOverrideId(titleId));
            command.Parameters.AddWithValue("$titleId", titleId);
            command.Parameters.AddWithValue("$field", FavoriteFieldName);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        }
        else
        {
            command.CommandText = """
                DELETE FROM UserOverride
                WHERE TitleId = $titleId AND FieldName = $field;
                """;
            command.Parameters.AddWithValue("$titleId", titleId);
            command.Parameters.AddWithValue("$field", FavoriteFieldName);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsTitleFavoriteAsync(
        string titleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
            throw new ArgumentException("Title id is required.", nameof(titleId));

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM UserOverride
            WHERE TitleId = $titleId AND FieldName = $field AND Value = '1'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$titleId", titleId);
        command.Parameters.AddWithValue("$field", FavoriteFieldName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<IReadOnlyList<string>> LoadFavoriteTitleIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TitleId
            FROM UserOverride
            WHERE FieldName = $field AND Value = '1'
            ORDER BY UpdatedUtc DESC, TitleId COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$field", FavoriteFieldName);

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    private static string FavoriteOverrideId(string titleId)
        => $"favorite:{titleId}";

    public async Task SavePlaybackProgressAsync(
        string mediaFileId,
        double positionSeconds,
        double? durationSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaFileId))
            throw new ArgumentException("Media file id is required.", nameof(mediaFileId));
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(positionSeconds));
        if (durationSeconds is not null &&
            (!double.IsFinite(durationSeconds.Value) || durationSeconds.Value <= 0))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO PlaybackProgress(MediaFileId, PositionSeconds, DurationSeconds, UpdatedUtc)
            VALUES($id, $position, $duration, $updated)
            ON CONFLICT(MediaFileId) DO UPDATE SET
                PositionSeconds = excluded.PositionSeconds,
                DurationSeconds = excluded.DurationSeconds,
                UpdatedUtc = excluded.UpdatedUtc;
            """, cancellationToken,
            ("$id", mediaFileId),
            ("$position", positionSeconds),
            ("$duration", durationSeconds),
            ("$updated", DateTimeOffset.UtcNow.ToString("O")));
        transaction.Commit();
    }

    public async Task<(double PositionSeconds, double? DurationSeconds)?> LoadPlaybackProgressAsync(
        string mediaFileId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PositionSeconds, DurationSeconds
            FROM PlaybackProgress
            WHERE MediaFileId = $id;
            """;
        command.Parameters.AddWithValue("$id", mediaFileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return (
            reader.GetDouble(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1));
    }

    public async Task DeletePlaybackProgressAsync(
        string mediaFileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaFileId))
            throw new ArgumentException("Media file id is required.", nameof(mediaFileId));

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlaybackProgress WHERE MediaFileId = $id;";
        command.Parameters.AddWithValue("$id", mediaFileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteAllPlaybackProgressAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlaybackProgress;";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UpNextEntry>> LoadUpNextAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);

        var completed = new List<(string TitleId, string MediaFileId, DateTimeOffset UpdatedUtc)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    f.TitleId,
                    p.MediaFileId,
                    p.PositionSeconds,
                    p.DurationSeconds,
                    p.UpdatedUtc
                FROM PlaybackProgress p
                INNER JOIN MediaFile f ON f.Id = p.MediaFileId
                INNER JOIN MediaTitle t ON t.Id = f.TitleId
                WHERE f.Availability = $online
                  AND f.EpisodeId IS NOT NULL
                  AND t.Kind <> $movie
                ORDER BY p.UpdatedUtc DESC;
                """;
            command.Parameters.AddWithValue("$online", (int)CatalogAvailability.Online);
            command.Parameters.AddWithValue("$movie", (int)MediaKind.Movie);

            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var titleId = reader.GetString(0);
                if (!seenTitles.Add(titleId))
                    continue;

                var position = reader.GetDouble(2);
                double? duration = reader.IsDBNull(3) ? null : reader.GetDouble(3);
                if (!PlaybackProgressPolicy.IsCompleted(position, duration))
                    continue;

                if (!DateTimeOffset.TryParse(reader.GetString(4), out var updatedUtc))
                    updatedUtc = DateTimeOffset.MinValue;

                completed.Add((titleId, reader.GetString(1), updatedUtc));
            }
        }

        var result = new List<UpNextEntry>(Math.Min(limit, completed.Count));
        foreach (var source in completed)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    e.Id,
                    f.Id,
                    s.Label,
                    e.EpisodeNumber,
                    e.DisplayTitle
                FROM Episode e
                INNER JOIN Season s ON s.Id = e.SeasonId
                INNER JOIN MediaFile f ON f.EpisodeId = e.Id
                WHERE e.TitleId = $titleId
                  AND f.Availability = $online
                ORDER BY
                    COALESCE(s.SeasonNumber, 9999),
                    s.Label COLLATE NOCASE,
                    e.SortKey COLLATE NOCASE,
                    f.Path COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$titleId", source.TitleId);
            command.Parameters.AddWithValue("$online", (int)CatalogAvailability.Online);

            var episodes = new List<(string EpisodeId, string MediaFileId, string ContextLabel)>();
            var currentEpisodeIndex = -1;
            string? lastEpisodeId = null;
            var episodeIndex = -1;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var episodeId = reader.GetString(0);
                var mediaFileId = reader.GetString(1);
                if (!string.Equals(lastEpisodeId, episodeId, StringComparison.OrdinalIgnoreCase))
                {
                    lastEpisodeId = episodeId;
                    episodeIndex++;

                    var seasonLabel = reader.GetString(2);
                    int? episodeNumber = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                    var episodeTitle = reader.GetString(4);
                    var episodeLabel = episodeNumber is null
                        ? episodeTitle
                        : $"E{episodeNumber:00}  {episodeTitle}";
                    episodes.Add((episodeId, mediaFileId, $"{seasonLabel} | {episodeLabel}"));
                }

                if (string.Equals(mediaFileId, source.MediaFileId, StringComparison.OrdinalIgnoreCase))
                    currentEpisodeIndex = episodeIndex;
            }

            var nextIndex = currentEpisodeIndex + 1;
            if (currentEpisodeIndex < 0 || nextIndex >= episodes.Count)
                continue;

            var next = episodes[nextIndex];
            result.Add(new UpNextEntry(
                source.TitleId,
                next.MediaFileId,
                next.ContextLabel,
                source.UpdatedUtc));

            if (result.Count == limit)
                break;
        }

        return result;
    }

    public async Task<IReadOnlyList<PlaybackProgressEntry>> LoadPlaybackHistoryAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                f.TitleId,
                p.MediaFileId,
                p.PositionSeconds,
                p.DurationSeconds,
                p.UpdatedUtc,
                s.Label,
                e.DisplayTitle
            FROM PlaybackProgress p
            INNER JOIN MediaFile f ON f.Id = p.MediaFileId
            LEFT JOIN Season s ON s.Id = f.SeasonId
            LEFT JOIN Episode e ON e.Id = f.EpisodeId
            ORDER BY p.UpdatedUtc DESC;
            """;

        var result = new List<PlaybackProgressEntry>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var position = reader.GetDouble(2);
            double? duration = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            if (position < PlaybackProgressPolicy.MinimumResumeSeconds &&
                !PlaybackProgressPolicy.IsCompleted(position, duration))
                continue;

            if (!DateTimeOffset.TryParse(reader.GetString(4), out var updatedUtc))
                updatedUtc = DateTimeOffset.MinValue;

            var seasonLabel = reader.IsDBNull(5) ? null : reader.GetString(5);
            var episodeTitle = reader.IsDBNull(6) ? null : reader.GetString(6);
            var contextLabel =
                !string.IsNullOrWhiteSpace(seasonLabel) && !string.IsNullOrWhiteSpace(episodeTitle)
                    ? $"{seasonLabel} | {episodeTitle}"
                    : seasonLabel ?? episodeTitle;

            result.Add(new PlaybackProgressEntry(
                reader.GetString(0),
                reader.GetString(1),
                position,
                duration,
                updatedUtc,
                contextLabel));

            if (result.Count == limit)
                break;
        }

        return result;
    }

    public async Task<IReadOnlyList<PlaybackProgressEntry>> LoadContinueWatchingAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                f.TitleId,
                p.MediaFileId,
                p.PositionSeconds,
                p.DurationSeconds,
                p.UpdatedUtc,
                s.Label,
                e.DisplayTitle
            FROM PlaybackProgress p
            INNER JOIN MediaFile f ON f.Id = p.MediaFileId
            LEFT JOIN Season s ON s.Id = f.SeasonId
            LEFT JOIN Episode e ON e.Id = f.EpisodeId
            WHERE f.Availability = $online
            ORDER BY p.UpdatedUtc DESC;
            """;
        command.Parameters.AddWithValue("$online", (int)CatalogAvailability.Online);

        var candidates = new List<PlaybackProgressEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!DateTimeOffset.TryParse(reader.GetString(4), out var updatedUtc))
                updatedUtc = DateTimeOffset.MinValue;

            var seasonLabel = reader.IsDBNull(5) ? null : reader.GetString(5);
            var episodeTitle = reader.IsDBNull(6) ? null : reader.GetString(6);
            var contextLabel =
                !string.IsNullOrWhiteSpace(seasonLabel) && !string.IsNullOrWhiteSpace(episodeTitle)
                    ? $"{seasonLabel} | {episodeTitle}"
                    : seasonLabel ?? episodeTitle;

            candidates.Add(new PlaybackProgressEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                updatedUtc,
                contextLabel));
        }

        return PlaybackProgressPolicy.SelectContinueWatching(candidates, limit);
    }

    public async Task<PlaybackProgressEntry?> LoadLatestResumableProgressForTitleAsync(
        string titleId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(titleId))
            throw new ArgumentException("Title id is required.", nameof(titleId));

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                f.TitleId,
                p.MediaFileId,
                p.PositionSeconds,
                p.DurationSeconds,
                p.UpdatedUtc
            FROM PlaybackProgress p
            INNER JOIN MediaFile f ON f.Id = p.MediaFileId
            WHERE f.TitleId = $titleId
              AND f.Availability = $online
            ORDER BY p.UpdatedUtc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$titleId", titleId);
        command.Parameters.AddWithValue("$online", (int)CatalogAvailability.Online);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        if (!DateTimeOffset.TryParse(reader.GetString(4), out var updatedUtc))
            updatedUtc = DateTimeOffset.MinValue;

        var entry = new PlaybackProgressEntry(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDouble(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3),
            updatedUtc);

        return PlaybackProgressPolicy.IsContinueWatching(
            entry.PositionSeconds,
            entry.DurationSeconds)
            ? entry
            : null;
    }


    private static async Task<Dictionary<string, string>> ResolveKnownSeriesTitleIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<CatalogTitleRecord> incomingTitles,
        CancellationToken cancellationToken)
    {
        var known = new List<KnownSeriesIdentity>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    t.Id,
                    t.Kind,
                    t.DisplayTitle,
                    COALESCE((
                        SELECT lower(p2.Provider) || ':' || p2.ProviderId
                        FROM ProviderMatch p2
                        WHERE p2.TitleId = t.Id
                          AND p2.Confidence >= 0.80
                        ORDER BY
                            CASE WHEN lower(p2.Provider) = 'tvmaze' THEN 0 ELSE 1 END,
                            p2.Confidence DESC,
                            lower(p2.Provider),
                            p2.ProviderId
                        LIMIT 1
                    ), '') AS ProviderKey,
                    (
                        SELECT COUNT(*)
                        FROM MediaFile f
                        WHERE f.TitleId = t.Id
                    ) AS FileCount
                FROM MediaTitle t
                WHERE t.Kind <> $movie
                  AND EXISTS (
                      SELECT 1
                      FROM ProviderMatch p
                      WHERE p.TitleId = t.Id
                        AND p.Confidence >= 0.80
                  );
                """;
            command.Parameters.AddWithValue("$movie", (int)MediaKind.Movie);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                known.Add(new KnownSeriesIdentity(
                    reader.GetString(0),
                    (MediaKind)Convert.ToInt32(reader.GetInt64(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    Convert.ToInt32(reader.GetInt64(4))));
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in incomingTitles.Where(title => title.Kind != MediaKind.Movie))
        {
            var ranked = known
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = SeriesIdentityScore(
                        incoming.Kind,
                        incoming.DisplayTitle,
                        candidate)
                })
                .Where(value => value.Score >= 70)
                .OrderByDescending(value => value.Score)
                .ThenBy(value => value.Candidate.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (ranked.Length == 0) continue;

            var bestScore = ranked[0].Score;
            var bestProviderKey = ranked[0].Candidate.ProviderKey;
            if (string.IsNullOrWhiteSpace(bestProviderKey)) continue;

            var providerGroups = ranked
                .GroupBy(
                    value => value.Candidate.ProviderKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    ProviderKey = group.Key,
                    BestScore = group.Max(value => value.Score),
                    Values = group.ToArray()
                })
                .OrderByDescending(group => group.BestScore)
                .ThenBy(group => group.ProviderKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (providerGroups.Length > 1
                && providerGroups[0].BestScore == providerGroups[1].BestScore)
                continue;

            var bestGroup = providerGroups[0];
            var canonicalPool = bestGroup.Values
                .Where(value => value.Score >= bestGroup.BestScore - 5)
                .Select(value => value.Candidate)
                .DistinctBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(candidate => candidate.FileCount)
                .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (canonicalPool.Length == 0) continue;
            result[incoming.Id] = canonicalPool[0].Id;
        }

        return result;
    }

    private static int SeriesIdentityScore(
        MediaKind incomingKind,
        string incomingTitle,
        KnownSeriesIdentity candidate)
    {
        var incoming = NormalizeIdentityText(incomingTitle);
        var known = NormalizeIdentityText(candidate.DisplayTitle);
        if (incoming.Length == 0 || known.Length == 0) return 0;

        var kindBonus = incomingKind == candidate.Kind ? 5 : 0;
        if (string.Equals(incoming, known, StringComparison.Ordinal))
            return 100 + kindBonus;

        if (incoming.Length >= 4
            && known.EndsWith(" " + incoming, StringComparison.Ordinal))
            return 90 + kindBonus;

        if (incoming.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2
            && known.StartsWith(incoming + " ", StringComparison.Ordinal))
            return 88 + kindBonus;

        var incomingTokens = incoming
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var knownTokens = known
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var shared = incomingTokens.Intersect(knownTokens, StringComparer.Ordinal).Count();
        var union = incomingTokens.Union(knownTokens, StringComparer.Ordinal).Count();
        if (shared < 2 || union == 0) return 0;

        var ratio = (double)shared / union;
        return ratio >= 0.65
            ? 70 + (int)Math.Round(ratio * 20) + kindBonus
            : 0;
    }

    private static string NormalizeIdentityText(string value)
        => string.Join(
            ' ',
            new string(
                    value.ToLowerInvariant()
                        .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
                        .ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string EpisodeFileNameFromSortKey(string sortKey)
    {
        var first = sortKey.IndexOf('|');
        if (first < 0) return sortKey;
        var second = sortKey.IndexOf('|', first + 1);
        return second < 0 ? sortKey : sortKey[(second + 1)..];
    }

    private sealed record KnownSeriesIdentity(
        string Id,
        MediaKind Kind,
        string DisplayTitle,
        string ProviderKey,
        int FileCount);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
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

    private static CatalogTitleSummary ReadTitleSummary(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            (MediaKind)Convert.ToInt32(reader.GetInt64(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            (CatalogAvailability)Convert.ToInt32(reader.GetInt64(9)),
            Convert.ToInt32(reader.GetInt64(10)),
            Convert.ToInt32(reader.GetInt64(11)),
            DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(8) ? null : reader.GetString(8));

    private static CatalogMediaFileSummary ReadMediaFile(SqliteDataReader reader, int idOrdinal, int pathOrdinal)
        => new(
            reader.GetString(idOrdinal),
            reader.GetString(pathOrdinal),
            reader.GetString(pathOrdinal + 1),
            reader.GetString(pathOrdinal + 2),
            (CatalogAvailability)Convert.ToInt32(reader.GetInt64(pathOrdinal + 3)),
            reader.IsDBNull(pathOrdinal + 4) ? null : reader.GetInt64(pathOrdinal + 4),
            reader.IsDBNull(pathOrdinal + 5) ? null : DateTimeOffset.Parse(reader.GetString(pathOrdinal + 5)));
}
