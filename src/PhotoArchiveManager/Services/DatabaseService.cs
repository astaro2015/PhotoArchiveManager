using Microsoft.Data.Sqlite;
using PhotoArchiveManager.Infrastructure;
using PhotoArchiveManager.Models;

namespace PhotoArchiveManager.Services;

public sealed class DatabaseService
{
    public const int CurrentSchemaVersion = 20;

    private readonly string _connectionString;

    public DatabaseService(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState != System.Data.ConnectionState.Open) return;

            // journal_mode=WAL persists in the database, but synchronous is connection-local.
            // Apply the project's intended WAL/NORMAL write policy to every pooled/new handle,
            // otherwise some cache updates can silently fall back to SQLite's heavier default.
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
        };
        return connection;
    }

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        // Refuse a database created by a newer PAM before any persistent PRAGMA/migration is run.
        // In particular, never rewrite user_version backwards: an older executable must fail safe.
        var initialUserVersion = await GetUserVersionAsync(connection);
        if (initialUserVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Эта Data\\archive.db использует более новую схему SQLite ({initialUserVersion}), " +
                $"чем поддерживает эта версия Photo Archive Manager ({CurrentSchemaVersion}). " +
                "База не изменена. Откройте её той же или более новой версией PAM.");
        }

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;");
        await ExecuteAsync(connection, "PRAGMA synchronous=NORMAL;");
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;");
        await ExecuteAsync(connection, "PRAGMA busy_timeout=5000;");

        var sql = """
        CREATE TABLE IF NOT EXISTS SourceFolders (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Path TEXT NOT NULL COLLATE NOCASE UNIQUE,
            AddedUtc TEXT NOT NULL,
            LastScanUtc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS Files (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FullPath TEXT NOT NULL COLLATE NOCASE UNIQUE,
            SourceFolder TEXT NOT NULL COLLATE NOCASE,
            FileName TEXT NOT NULL,
            Extension TEXT NOT NULL,
            FileSize INTEGER NOT NULL,
            LastWriteUtcTicks INTEGER NOT NULL,
            CreationUtcTicks INTEGER NOT NULL,
            CaptureDate TEXT NULL,
            CaptureDateSource TEXT NOT NULL DEFAULT '',
            AutoCaptureDate TEXT NULL,
            AutoCaptureDateSource TEXT NOT NULL DEFAULT '',
            EffectiveYear INTEGER NOT NULL DEFAULT 0,
            Width INTEGER NOT NULL DEFAULT 0,
            Height INTEGER NOT NULL DEFAULT 0,
            CameraMake TEXT NOT NULL DEFAULT '',
            CameraModel TEXT NOT NULL DEFAULT '',
            Orientation INTEGER NOT NULL DEFAULT 1,
            ThumbnailPath TEXT NOT NULL DEFAULT '',
            Error TEXT NOT NULL DEFAULT '',
            IndexedUtc TEXT NOT NULL,
            LastSeenScanId TEXT NOT NULL DEFAULT '',
            IsMissing INTEGER NOT NULL DEFAULT 0,
            Sha256 TEXT NOT NULL DEFAULT '',
            HashFileSize INTEGER NOT NULL DEFAULT 0,
            HashLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            HashError TEXT NOT NULL DEFAULT '',
            HashedUtc TEXT NOT NULL DEFAULT '',
            PerceptualHash TEXT NOT NULL DEFAULT '',
            AverageHash TEXT NOT NULL DEFAULT '',
            PerceptualHashFileSize INTEGER NOT NULL DEFAULT 0,
            PerceptualHashLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            PerceptualHashError TEXT NOT NULL DEFAULT '',
            PerceptualHashedUtc TEXT NOT NULL DEFAULT '',
            PerceptualHashAlgorithmVersion INTEGER NOT NULL DEFAULT 0,
            QualityScore REAL NOT NULL DEFAULT -1,
            TechnicalScore REAL NOT NULL DEFAULT -1,
            SharpnessScore REAL NOT NULL DEFAULT -1,
            BlurScore REAL NOT NULL DEFAULT -1,
            ExposureScore REAL NOT NULL DEFAULT -1,
            ContrastScore REAL NOT NULL DEFAULT -1,
            NoiseScore REAL NOT NULL DEFAULT -1,
            ResolutionScore REAL NOT NULL DEFAULT -1,
            CompressionScore REAL NOT NULL DEFAULT -1,
            QualityNotes TEXT NOT NULL DEFAULT '',
            QualityFileSize INTEGER NOT NULL DEFAULT 0,
            QualityLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            QualityError TEXT NOT NULL DEFAULT '',
            QualityAnalyzedUtc TEXT NOT NULL DEFAULT '',
            QualityAlgorithmVersion INTEGER NOT NULL DEFAULT 0,
            FaceCount INTEGER NOT NULL DEFAULT -1,
            EyeCount INTEGER NOT NULL DEFAULT -1,
            FaceScore REAL NOT NULL DEFAULT -1,
            EyeScore REAL NOT NULL DEFAULT -1,
            FacePoseScore REAL NOT NULL DEFAULT -1,
            WorstFaceScore REAL NOT NULL DEFAULT -1,
            EyeOpennessScore REAL NOT NULL DEFAULT -1,
            ClosedEyeCount INTEGER NOT NULL DEFAULT -1,
            BlinkPenalty REAL NOT NULL DEFAULT 0,
            FaceIndexVersion INTEGER NOT NULL DEFAULT 0,
            FaceIndexOrientationVersion INTEGER NOT NULL DEFAULT 0,
            FaceIndexFileSize INTEGER NOT NULL DEFAULT 0,
            FaceIndexLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            FaceIndexError TEXT NOT NULL DEFAULT '',
            FaceIndexedUtc TEXT NOT NULL DEFAULT '',
            GpsLatitude REAL NULL,
            GpsLongitude REAL NULL,
            GpsIndexVersion INTEGER NOT NULL DEFAULT 0,
            GpsIndexFileSize INTEGER NOT NULL DEFAULT 0,
            GpsIndexLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            GpsIndexError TEXT NOT NULL DEFAULT '',
            GpsIndexedUtc TEXT NOT NULL DEFAULT '',
            SemanticEmbedding BLOB NULL,
            SemanticIndexVersion INTEGER NOT NULL DEFAULT 0,
            SemanticIndexFileSize INTEGER NOT NULL DEFAULT 0,
            SemanticIndexLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            SemanticIndexError TEXT NOT NULL DEFAULT '',
            SemanticIndexedUtc TEXT NOT NULL DEFAULT '',
            IsQuarantined INTEGER NOT NULL DEFAULT 0,
            QuarantinePath TEXT NOT NULL DEFAULT '',
            IsDeleted INTEGER NOT NULL DEFAULT 0,
            DeletedUtc TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Files_SourceFolder ON Files(SourceFolder);
        CREATE INDEX IF NOT EXISTS IX_Files_EffectiveYear ON Files(EffectiveYear);
        CREATE INDEX IF NOT EXISTS IX_Files_Camera ON Files(CameraMake, CameraModel);
        CREATE INDEX IF NOT EXISTS IX_Files_IsMissing ON Files(IsMissing);
        CREATE TABLE IF NOT EXISTS Actions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FileId INTEGER NOT NULL,
            ActionType TEXT NOT NULL,
            OriginalPath TEXT NOT NULL,
            NewPath TEXT NOT NULL,
            Sha256 TEXT NOT NULL DEFAULT '',
            FileSize INTEGER NOT NULL DEFAULT 0,
            OriginalLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            OriginalCreationUtcTicks INTEGER NOT NULL DEFAULT 0,
            CreatedUtc TEXT NOT NULL,
            UndoneUtc TEXT NULL,
            PermanentlyDeletedUtc TEXT NULL,
            Error TEXT NOT NULL DEFAULT '',
            FOREIGN KEY(FileId) REFERENCES Files(Id)
        );
        CREATE INDEX IF NOT EXISTS IX_Actions_FileId ON Actions(FileId);
        CREATE INDEX IF NOT EXISTS IX_Actions_UndoneUtc ON Actions(UndoneUtc);

        CREATE TABLE IF NOT EXISTS People (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL DEFAULT '',
            IsAuto INTEGER NOT NULL DEFAULT 1,
            RepresentativeFaceId INTEGER NULL,
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS DetectedFaces (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FileId INTEGER NOT NULL,
            PersonId INTEGER NULL,
            X INTEGER NOT NULL,
            Y INTEGER NOT NULL,
            Width INTEGER NOT NULL,
            Height INTEGER NOT NULL,
            ImageWidth INTEGER NOT NULL,
            ImageHeight INTEGER NOT NULL,
            QualityScore REAL NOT NULL DEFAULT 0,
            Embedding BLOB NOT NULL,
            ThumbnailPath TEXT NOT NULL DEFAULT '',
            IsIgnored INTEGER NOT NULL DEFAULT 0,
            CreatedUtc TEXT NOT NULL,
            FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE,
            FOREIGN KEY(PersonId) REFERENCES People(Id) ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS IX_DetectedFaces_FileId ON DetectedFaces(FileId);
        CREATE INDEX IF NOT EXISTS IX_DetectedFaces_PersonId ON DetectedFaces(PersonId);
        CREATE INDEX IF NOT EXISTS IX_DetectedFaces_IsIgnored ON DetectedFaces(IsIgnored);

        CREATE TABLE IF NOT EXISTS Events (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL DEFAULT '',
            StartDate TEXT NOT NULL,
            EndDate TEXT NOT NULL,
            IsAuto INTEGER NOT NULL DEFAULT 1,
            Confidence REAL NOT NULL DEFAULT 0,
            CenterLatitude REAL NULL,
            CenterLongitude REAL NULL,
            RepresentativeFileId INTEGER NULL,
            Notes TEXT NOT NULL DEFAULT '',
            CreatedUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS EventFiles (
            EventId INTEGER NOT NULL,
            FileId INTEGER NOT NULL,
            PRIMARY KEY(EventId, FileId),
            FOREIGN KEY(EventId) REFERENCES Events(Id) ON DELETE CASCADE,
            FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_EventFiles_EventId ON EventFiles(EventId);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_EventFiles_FileId ON EventFiles(FileId);

        CREATE TABLE IF NOT EXISTS ReviewStates (
            Context TEXT NOT NULL,
            ItemKey TEXT NOT NULL,
            IsProcessed INTEGER NOT NULL DEFAULT 0,
            UpdatedUtc TEXT NOT NULL,
            PRIMARY KEY(Context, ItemKey)
        );
        CREATE INDEX IF NOT EXISTS IX_ReviewStates_Context ON ReviewStates(Context, IsProcessed);

        CREATE TABLE IF NOT EXISTS FaceIgnoreActions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ScopeType TEXT NOT NULL,
            ScopeLabel TEXT NOT NULL,
            PersonId INTEGER NULL,
            PersonName TEXT NOT NULL DEFAULT '',
            PersonIsAuto INTEGER NOT NULL DEFAULT 1,
            CreatedUtc TEXT NOT NULL,
            UndoneUtc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS FaceIgnoreActionItems (
            ActionId INTEGER NOT NULL,
            FaceId INTEGER NOT NULL,
            PreviousPersonId INTEGER NULL,
            PRIMARY KEY(ActionId, FaceId),
            FOREIGN KEY(ActionId) REFERENCES FaceIgnoreActions(Id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_FaceIgnoreActions_UndoneUtc ON FaceIgnoreActions(UndoneUtc);

        CREATE TABLE IF NOT EXISTS OrganizationMoves (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FileId INTEGER NOT NULL,
            OriginalPath TEXT NOT NULL,
            NewPath TEXT NOT NULL,
            OriginalSourceFolder TEXT NOT NULL,
            NewSourceFolder TEXT NOT NULL,
            Sha256 TEXT NOT NULL,
            FileSize INTEGER NOT NULL,
            OriginalLastWriteUtcTicks INTEGER NOT NULL DEFAULT 0,
            OriginalCreationUtcTicks INTEGER NOT NULL DEFAULT 0,
            CreatedUtc TEXT NOT NULL,
            UndoneUtc TEXT NULL,
            Error TEXT NOT NULL DEFAULT '',
            FOREIGN KEY(FileId) REFERENCES Files(Id)
        );
        CREATE INDEX IF NOT EXISTS IX_OrganizationMoves_FileId ON OrganizationMoves(FileId);
        CREATE INDEX IF NOT EXISTS IX_OrganizationMoves_UndoneUtc ON OrganizationMoves(UndoneUtc);
        """;
        await ExecuteAsync(connection, sql);

        // Migration checks used to run PRAGMA table_info once per column (dozens of full schema
        // scans on every startup). Cache the columns per table for this initialization pass.
        var columnCache = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // v0.2 migration: preserve the v0.1 database and add hash cache columns in place.
        await EnsureColumnAsync(connection, columnCache, "Files", "Sha256", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "HashFileSize", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "HashLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "HashError", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "HashedUtc", "TEXT NOT NULL DEFAULT ''");
        // v0.2.5 migration: quarantine state and reversible action journal.
        await EnsureColumnAsync(connection, columnCache, "Files", "IsQuarantined", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "QuarantinePath", "TEXT NOT NULL DEFAULT ''");

        // v0.3 migration: cached perceptual hashes and explicit permanent-delete audit state.
        await EnsureColumnAsync(connection, columnCache, "Files", "PerceptualHash", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "AverageHash", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "PerceptualHashFileSize", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "PerceptualHashLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "PerceptualHashError", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "PerceptualHashedUtc", "TEXT NOT NULL DEFAULT ''");
        // v1.8.1 / schema 17: explicit hash cache version for bounded decode + full EXIF orientation.
        await EnsureColumnAsync(connection, columnCache, "Files", "PerceptualHashAlgorithmVersion", "INTEGER NOT NULL DEFAULT 0");

        // v0.4 migration: cached technical quality score. No file is modified by this analysis.
        await EnsureColumnAsync(connection, columnCache, "Files", "QualityScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "SharpnessScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "BlurScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "ExposureScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "ResolutionScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "CompressionScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "QualityNotes", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "QualityFileSize", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "QualityLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "QualityError", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "QualityAnalyzedUtc", "TEXT NOT NULL DEFAULT ''");

        // v0.5 migration: quality algorithm v2 adds local face/eye-aware metrics.
        // Old v0.4 quality values are kept for audit, but are not treated as current until recalculated.
        await EnsureColumnAsync(connection, columnCache, "Files", "QualityAlgorithmVersion", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceCount", "INTEGER NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "EyeCount", "INTEGER NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "EyeScore", "REAL NOT NULL DEFAULT -1");

        // v1.8 / schema 16: Quality Score v3. Fully local, multi-scale technical metrics + YuNet face quality.
        await EnsureColumnAsync(connection, columnCache, "Files", "TechnicalScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "ContrastScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "NoiseScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "FacePoseScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "WorstFaceScore", "REAL NOT NULL DEFAULT -1");

        // v1.9 / schema 18: conservative main-face eye-openness/blink metrics.
        // Pure local OpenCV heuristics; no additional model or runtime network access.
        await EnsureColumnAsync(connection, columnCache, "Files", "EyeOpennessScore", "REAL NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "ClosedEyeCount", "INTEGER NOT NULL DEFAULT -1");
        await EnsureColumnAsync(connection, columnCache, "Files", "BlinkPenalty", "REAL NOT NULL DEFAULT 0");

        // v0.7 migration: persistent local face index and person groups.
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceIndexVersion", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceIndexOrientationVersion", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceIndexFileSize", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceIndexLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceIndexError", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "FaceIndexedUtc", "TEXT NOT NULL DEFAULT ''");

        // v0.8 migration: cached GPS metadata and logical event catalog. Original files remain read-only.
        await EnsureColumnAsync(connection, columnCache, "Files", "GpsLatitude", "REAL NULL");
        await EnsureColumnAsync(connection, columnCache, "Files", "GpsLongitude", "REAL NULL");
        await EnsureColumnAsync(connection, columnCache, "Files", "GpsIndexVersion", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "GpsIndexFileSize", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "GpsIndexLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "GpsIndexError", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "GpsIndexedUtc", "TEXT NOT NULL DEFAULT ''");

        // Legacy v0.9 columns are retained so old Data databases migrate in place. 1.6.1 no longer reads or writes them.
        await EnsureColumnAsync(connection, columnCache, "Files", "SemanticEmbedding", "BLOB NULL");
        await EnsureColumnAsync(connection, columnCache, "Files", "SemanticIndexVersion", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "SemanticIndexFileSize", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "SemanticIndexLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "SemanticIndexError", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync(connection, columnCache, "Files", "SemanticIndexedUtc", "TEXT NOT NULL DEFAULT ''");

        // v1.1 migration: preserve scanner-derived date separately so a catalog-only manual correction survives rescans and can be reverted.
        await EnsureColumnAsync(connection, columnCache, "Files", "AutoCaptureDate", "TEXT NULL");
        await EnsureColumnAsync(connection, columnCache, "Files", "AutoCaptureDateSource", "TEXT NOT NULL DEFAULT ''");
        await ExecuteAsync(connection, "UPDATE Files SET AutoCaptureDate=CaptureDate WHERE AutoCaptureDate IS NULL AND CaptureDate IS NOT NULL AND CaptureDateSource<>'Ручная дата (каталог)';");
        await ExecuteAsync(connection, "UPDATE Files SET AutoCaptureDateSource=CaptureDateSource WHERE AutoCaptureDateSource='' AND CaptureDateSource<>'' AND CaptureDateSource<>'Ручная дата (каталог)';");

        await EnsureColumnAsync(connection, columnCache, "Files", "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Files", "DeletedUtc", "TEXT NULL");
        await EnsureColumnAsync(connection, columnCache, "Actions", "PermanentlyDeletedUtc", "TEXT NULL");

        // PAM 1.10.0 no longer creates, reads or writes the old favorites/rating columns.
        // Existing databases may still contain those legacy columns; leave their user data untouched.
        // Drop only the obsolete helper indexes so they no longer add write/maintenance overhead.
        await ExecuteAsync(connection, "DROP INDEX IF EXISTS IX_Files_IsFavorite;");
        await ExecuteAsync(connection, "DROP INDEX IF EXISTS IX_Files_Rating;");
        await EnsureColumnAsync(connection, columnCache, "People", "RepresentativeFaceId", "INTEGER NULL");
        await EnsureColumnAsync(connection, columnCache, "Events", "RepresentativeFileId", "INTEGER NULL");
        await EnsureColumnAsync(connection, columnCache, "Events", "Notes", "TEXT NOT NULL DEFAULT ''");

        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_FileSize ON Files(FileSize);");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_Sha256 ON Files(Sha256);");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_IsQuarantined ON Files(IsQuarantined);");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_PerceptualHash ON Files(PerceptualHash);");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_IsDeleted ON Files(IsDeleted);");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_GpsIndexVersion ON Files(GpsIndexVersion);");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Events_StartDate ON Events(StartDate);");
        // Read-heavy UI paths benefit from partial indexes that contain only active catalog rows.
        // They do not alter catalog semantics and are safe to create on existing databases.
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_ActiveCaptureDate ON Files(CaptureDate DESC, LastWriteUtcTicks DESC) WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_ActiveYearCapture ON Files(EffectiveYear, CaptureDate DESC) WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_ActiveSourceCapture ON Files(SourceFolder, CaptureDate DESC) WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_ActiveCameraDisplay ON Files(TRIM(CameraMake || ' ' || CameraModel)) WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_ActiveFileName ON Files(FileName COLLATE NOCASE, FullPath COLLATE NOCASE) WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Files_ActiveFullPath ON Files(FullPath COLLATE NOCASE) WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;");
        // v1.0 migration: reversible physical organization journal.
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_OrganizationMoves_FileId ON OrganizationMoves(FileId);");
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS IX_OrganizationMoves_UndoneUtc ON OrganizationMoves(UndoneUtc);");
        // v1.14.6 / schema 20: Organization Undo must restore the original filesystem dates,
        // not the EXIF-derived CreationTime assigned to the organized destination.
        await EnsureColumnAsync(connection, columnCache, "OrganizationMoves", "OriginalLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "OrganizationMoves", "OriginalCreationUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        // Quarantine Undo needs the exact pre-quarantine filesystem dates as well. A quarantine
        // located on FAT/exFAT can round timestamps; relying on the quarantine file would then lose
        // the original values when the user restores it.
        await EnsureColumnAsync(connection, columnCache, "Actions", "OriginalLastWriteUtcTicks", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, columnCache, "Actions", "OriginalCreationUtcTicks", "INTEGER NOT NULL DEFAULT 0");

        // Schema 20 also detaches inactive catalogue rows from their old source path. Otherwise a
        // brand-new file that later appears at the same path would hit UNIQUE(FullPath) and reuse
        // the quarantined/deleted FileId, corrupting the action journal's logical identity.
        if (initialUserVersion < 20)
        {
            await ExecuteAsync(connection, """
                UPDATE Files
                SET FullPath=CASE
                    WHEN IsQuarantined=1 AND TRIM(QuarantinePath)<>''
                        THEN QuarantinePath || '.pam-catalog-record-' || Id
                    WHEN IsDeleted=1 AND FullPath NOT LIKE ('%.pam-deleted-record-' || Id)
                        THEN FullPath || '.pam-deleted-record-' || Id
                    WHEN IsDeleted=1
                        THEN FullPath
                    ELSE FullPath
                END
                WHERE IsQuarantined=1 OR IsDeleted=1;
                """);
        }

        // v1.8.1 / schema 17: PerceptualHashAlgorithmVersion invalidates the old hash cache
        // without deleting it. v2 hashes use bounded decode and all eight EXIF orientations.
        // v1.9 / schema 18 adds cached eye-openness/blink metrics. Quality uses its independent
        // QualityAlgorithmVersion=6, so older scores are recalculated without touching originals.
        // v1.9.1 / schema 19: explicit orientation-normalization version for the face cache.
        // FaceIndexVersion=2 existed both before and after all eight EXIF Orientation values were
        // normalized consistently, so the old integer alone cannot distinguish a mirrored legacy
        // cache. Keep all DetectedFaces rows for safe in-place refresh; only mirrored 2/4/5/7 rows
        // are made stale. Non-mirrored v2 rows are certified as orientation-version 1 without work.
        if (initialUserVersion < 19)
        {
            // Older PAM releases accepted a few invariant legacy date spellings. The shared parser
            // reads all of them correctly, but SQLite comparisons are textual. Canonicalize once so
            // ORDER BY, MIN/MAX and event split boundaries stay chronologically correct too.
            await NormalizeLegacyStoredDatesAsync(connection);
        }

        // This migration belongs strictly to schema 19. Do NOT rerun it on every future schema bump:
        // doing so would mark already-refreshed mirrored orientations (2/4/5/7) stale again.
        if (initialUserVersion < 19)
        {
            await ExecuteAsync(connection, """
                UPDATE Files
                SET FaceIndexOrientationVersion=CASE
                    WHEN FaceIndexVersion=2 AND Orientation NOT IN (2,4,5,7) THEN 1
                    ELSE 0
                END;
                """);
        }

        await ExecuteAsync(connection, $"PRAGMA user_version={CurrentSchemaVersion};");
        // Let SQLite refresh planner hints opportunistically after migrations/index creation.
        await ExecuteAsync(connection, "PRAGMA optimize;");
    }

    public async Task AddSourceFolderAsync(string path)
    {
        var normalized = NormalizeDirectory(path);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO SourceFolders(Path, AddedUtc) VALUES($path, $utc);";
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<SourceFolderItem>> GetSourceFoldersAsync()
    {
        var result = new List<SourceFolderItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Path FROM SourceFolders ORDER BY Path COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new SourceFolderItem { Id = reader.GetInt64(0), Path = reader.GetString(1) });
        return result;
    }

    public async Task RemoveSourceFolderOnlyAsync(string path)
    {
        var normalized = NormalizeDirectory(path);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SourceFolders WHERE Path=$path;";
        command.Parameters.AddWithValue("$path", normalized);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> GetActiveQuarantineCountForSourceAsync(string path)
    {
        var normalized = NormalizeDirectory(path);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Files WHERE SourceFolder=$path AND IsQuarantined=1 AND IsDeleted=0;";
        command.Parameters.AddWithValue("$path", normalized);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    public async Task<SourceCatalogRemovalResult> RemoveSourceFolderAndCatalogAsync(string path)
    {
        var normalized = NormalizeDirectory(path);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        using var tx = connection.BeginTransaction();

        try
        {
            var quarantined = 0;
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT COUNT(*) FROM Files WHERE SourceFolder=$path AND IsQuarantined=1 AND IsDeleted=0;";
                q.Parameters.AddWithValue("$path", normalized);
                quarantined = Convert.ToInt32(await q.ExecuteScalarAsync());
            }

            // A normal active quarantine is a finished reversible state and must not block
            // detaching a source. But never detach if the catalogue says a file is quarantined
            // while its active QUARANTINE journal entry is missing: that would leave an orphan
            // which the UI could no longer restore or permanently delete safely.
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT COUNT(*)
                    FROM Files f
                    WHERE f.SourceFolder=$path AND f.IsQuarantined=1 AND f.IsDeleted=0
                      AND NOT EXISTS(
                          SELECT 1 FROM Actions a
                          WHERE a.FileId=f.Id AND a.ActionType='QUARANTINE'
                            AND a.UndoneUtc IS NULL AND a.PermanentlyDeletedUtc IS NULL
                      );
                    """;
                q.Parameters.AddWithValue("$path", normalized);
                var orphanedQuarantine = Convert.ToInt32(await q.ExecuteScalarAsync());
                if (orphanedQuarantine > 0)
                    throw new InvalidOperationException(
                        $"В каталоге найдено {orphanedQuarantine:N0} файлов, помеченных как карантинные, но без активной записи Undo. " +
                        "Источник не закрыт, чтобы не потерять управление этими файлами. Проверьте Data/archive.db или журнал ошибок.");
            }

            var cache = new List<string>();
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT ThumbnailPath FROM Files
                    WHERE SourceFolder=$path AND ThumbnailPath<>''
                    UNION
                    SELECT df.ThumbnailPath
                    FROM DetectedFaces df
                    JOIN Files f ON f.Id=df.FileId
                    WHERE f.SourceFolder=$path AND df.ThumbnailPath<>'';
                    """;
                q.Parameters.AddWithValue("$path", normalized);
                await using var reader = await q.ExecuteReaderAsync();
                while (await reader.ReadAsync()) cache.Add(reader.GetString(0));
            }

            // Files that have ever participated in the reversible action journal are kept as
            // hidden audit rows so Actions foreign keys and the history remain trustworthy.
            // Their faces/event memberships are still removed from the active catalog.
            var auditIds = new List<long>();
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    SELECT f.Id FROM Files f
                    WHERE f.SourceFolder=$path
                      AND (f.IsQuarantined=1
                           OR EXISTS(SELECT 1 FROM Actions a WHERE a.FileId=f.Id)
                           OR EXISTS(SELECT 1 FROM OrganizationMoves o WHERE o.FileId=f.Id));
                    """;
                q.Parameters.AddWithValue("$path", normalized);
                await using var reader = await q.ExecuteReaderAsync();
                while (await reader.ReadAsync()) auditIds.Add(reader.GetInt64(0));
            }

            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    DELETE FROM DetectedFaces
                    WHERE FileId IN (SELECT Id FROM Files WHERE SourceFolder=$path);
                    DELETE FROM EventFiles
                    WHERE FileId IN (SELECT Id FROM Files WHERE SourceFolder=$path);
                    """;
                q.Parameters.AddWithValue("$path", normalized);
                await q.ExecuteNonQueryAsync();
            }

            int deleted;
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    DELETE FROM Files
                    WHERE SourceFolder=$path
                      AND IsQuarantined=0
                      AND NOT EXISTS(SELECT 1 FROM Actions a WHERE a.FileId=Files.Id)
                      AND NOT EXISTS(SELECT 1 FROM OrganizationMoves o WHERE o.FileId=Files.Id);
                    """;
                q.Parameters.AddWithValue("$path", normalized);
                deleted = await q.ExecuteNonQueryAsync();
            }

            if (auditIds.Count > 0)
            {
                await using var q = connection.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "UPDATE Files SET IsMissing=1 WHERE SourceFolder=$path;";
                q.Parameters.AddWithValue("$path", normalized);
                await q.ExecuteNonQueryAsync();
            }

            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "DELETE FROM SourceFolders WHERE Path=$path;";
                q.Parameters.AddWithValue("$path", normalized);
                await q.ExecuteNonQueryAsync();
            }

            // Remove only empty automatically generated entities. Manually named people/events
            // are intentionally retained so user work is never silently discarded.
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    DELETE FROM Events WHERE IsAuto=1 AND NOT EXISTS(SELECT 1 FROM EventFiles ef WHERE ef.EventId=Events.Id);
                    DELETE FROM People WHERE IsAuto=1 AND Name='' AND NOT EXISTS(SELECT 1 FROM DetectedFaces df WHERE df.PersonId=People.Id);
                    """;
                await q.ExecuteNonQueryAsync();
            }

            tx.Commit();
            return new SourceCatalogRemovalResult
            {
                ActiveQuarantineCount = quarantined,
                DeletedCatalogRecords = deleted,
                AuditRecordsRetained = auditIds.Count,
                CacheFilesToDelete = cache
            };
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<int> ReplaceSourceFolderPathAsync(string oldPath, string newPath)
    {
        var oldNormalized = NormalizeDirectory(oldPath);
        var newNormalized = NormalizeDirectory(newPath);
        if (string.Equals(oldNormalized, newNormalized, StringComparison.OrdinalIgnoreCase)) return 0;

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        using var tx = connection.BeginTransaction();
        try
        {
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT COUNT(*) FROM SourceFolders WHERE Path=$newPath;";
                check.Parameters.AddWithValue("$newPath", newNormalized);
                if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0)
                    throw new InvalidOperationException("Эта папка уже добавлена как отдельный источник.");
            }

            await using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT COUNT(*) FROM Files WHERE SourceFolder=$oldPath AND IsQuarantined=1 AND IsDeleted=0;";
                check.Parameters.AddWithValue("$oldPath", oldNormalized);
                if (Convert.ToInt32(await check.ExecuteScalarAsync()) > 0)
                    throw new InvalidOperationException("У источника есть активные файлы в карантине. Сначала верните их (Undo) или разберите карантин, и только затем меняйте путь источника.");
            }

            // Only active catalogue rows have a physical path under the source root. Schema 20
            // deliberately detaches permanently deleted audit rows from their old FullPath so a
            // future new file can reuse that path safely. Do not try to remap those internal paths.
            var rows = new List<(long Id, string FullPath)>();
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT Id, FullPath FROM Files WHERE SourceFolder=$oldPath AND IsDeleted=0;";
                q.Parameters.AddWithValue("$oldPath", oldNormalized);
                await using var reader = await q.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }

            var mapped = new List<(long Id, string NewFullPath)>();
            foreach (var row in rows)
            {
                var relative = Path.GetRelativePath(oldNormalized, row.FullPath);
                if (relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative == "..")
                    throw new InvalidOperationException("В каталоге найден файл вне корня источника; замена пути отменена для безопасности.");
                mapped.Add((row.Id, Path.GetFullPath(Path.Combine(newNormalized, relative))));
            }

            foreach (var row in mapped)
            {
                await using var collision = connection.CreateCommand();
                collision.Transaction = tx;
                collision.CommandText = "SELECT COUNT(*) FROM Files WHERE FullPath=$path AND Id<>$id;";
                collision.Parameters.AddWithValue("$path", row.NewFullPath);
                collision.Parameters.AddWithValue("$id", row.Id);
                if (Convert.ToInt32(await collision.ExecuteScalarAsync()) > 0)
                    throw new InvalidOperationException("Новый путь пересекается с уже проиндексированными файлами. Удалите/разберите второй источник либо выберите другой путь.");
            }

            foreach (var row in mapped)
            {
                await using var q = connection.CreateCommand();
                q.Transaction = tx;
                q.CommandText = "UPDATE Files SET FullPath=$newFull, SourceFolder=$newRoot, IsMissing=0 WHERE Id=$id;";
                q.Parameters.AddWithValue("$newFull", row.NewFullPath);
                q.Parameters.AddWithValue("$newRoot", newNormalized);
                q.Parameters.AddWithValue("$id", row.Id);
                await q.ExecuteNonQueryAsync();
            }

            // Deleted rows are retained only for Undo/audit identity. Their schema-20 FullPath is
            // an internal detached sentinel and must stay detached, but source attribution follows
            // the renamed root so later source cleanup still finds the complete audit history.
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "UPDATE Files SET SourceFolder=$newRoot WHERE SourceFolder=$oldRoot AND IsDeleted=1;";
                q.Parameters.AddWithValue("$newRoot", newNormalized);
                q.Parameters.AddWithValue("$oldRoot", oldNormalized);
                await q.ExecuteNonQueryAsync();
            }

            // Keep reversible organization history usable if the user externally moved/renamed an entire source root.
            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    UPDATE OrganizationMoves
                    SET OriginalPath=$newRoot || substr(OriginalPath, length($oldRoot)+1), OriginalSourceFolder=$newRoot
                    WHERE OriginalSourceFolder=$oldRoot;
                    UPDATE OrganizationMoves
                    SET NewPath=$newRoot || substr(NewPath, length($oldRoot)+1), NewSourceFolder=$newRoot
                    WHERE NewSourceFolder=$oldRoot;
                    """;
                q.Parameters.AddWithValue("$newRoot", newNormalized);
                q.Parameters.AddWithValue("$oldRoot", oldNormalized);
                await q.ExecuteNonQueryAsync();
            }

            await using (var q = connection.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "UPDATE SourceFolders SET Path=$newPath, LastScanUtc=NULL WHERE Path=$oldPath;";
                q.Parameters.AddWithValue("$newPath", newNormalized);
                q.Parameters.AddWithValue("$oldPath", oldNormalized);
                if (await q.ExecuteNonQueryAsync() != 1)
                    throw new InvalidOperationException("Исходная папка не найдена в каталоге PAM.");
            }

            tx.Commit();
            return mapped.Count;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<Dictionary<string, FileSignature>> GetSignaturesAsync(string sourceFolder, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FileSignature>(StringComparer.OrdinalIgnoreCase);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FullPath, FileSize, LastWriteUtcTicks, Error FROM Files WHERE SourceFolder=$source AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;";
        command.Parameters.AddWithValue("$source", NormalizeDirectory(sourceFolder));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = new FileSignature(reader.GetInt64(1), reader.GetInt64(2), !string.IsNullOrWhiteSpace(reader.GetString(3)));
        return result;
    }

    public async Task UpsertPhotoAsync(SqliteConnection connection, SqliteTransaction transaction, PhotoRecord record, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
        INSERT INTO Files(
            FullPath, SourceFolder, FileName, Extension, FileSize, LastWriteUtcTicks, CreationUtcTicks,
            CaptureDate, CaptureDateSource, AutoCaptureDate, AutoCaptureDateSource, EffectiveYear, Width, Height, CameraMake, CameraModel,
            Orientation, GpsLatitude, GpsLongitude, GpsIndexVersion, GpsIndexFileSize, GpsIndexLastWriteUtcTicks, GpsIndexError, GpsIndexedUtc,
            ThumbnailPath, Error, IndexedUtc, LastSeenScanId, IsMissing)
        VALUES(
            $FullPath, $SourceFolder, $FileName, $Extension, $FileSize, $LastWriteUtcTicks, $CreationUtcTicks,
            $CaptureDate, $CaptureDateSource, $CaptureDate, $CaptureDateSource, $EffectiveYear, $Width, $Height, $CameraMake, $CameraModel,
            $Orientation, $GpsLatitude, $GpsLongitude, 1, $FileSize, $LastWriteUtcTicks, '', $IndexedUtc,
            $ThumbnailPath, $Error, $IndexedUtc, $LastSeenScanId, 0)
        ON CONFLICT(FullPath) DO UPDATE SET
            SourceFolder=excluded.SourceFolder,
            FileName=excluded.FileName,
            Extension=excluded.Extension,
            FileSize=excluded.FileSize,
            LastWriteUtcTicks=excluded.LastWriteUtcTicks,
            CreationUtcTicks=excluded.CreationUtcTicks,
            CaptureDate=CASE WHEN Files.CaptureDateSource=$ManualCaptureDateSource THEN Files.CaptureDate ELSE excluded.CaptureDate END,
            CaptureDateSource=CASE WHEN Files.CaptureDateSource=$ManualCaptureDateSource THEN Files.CaptureDateSource ELSE excluded.CaptureDateSource END,
            AutoCaptureDate=excluded.AutoCaptureDate,
            AutoCaptureDateSource=excluded.AutoCaptureDateSource,
            EffectiveYear=CASE WHEN Files.CaptureDateSource=$ManualCaptureDateSource THEN Files.EffectiveYear ELSE excluded.EffectiveYear END,
            Width=excluded.Width,
            Height=excluded.Height,
            CameraMake=excluded.CameraMake,
            CameraModel=excluded.CameraModel,
            Orientation=excluded.Orientation,
            GpsLatitude=excluded.GpsLatitude,
            GpsLongitude=excluded.GpsLongitude,
            GpsIndexVersion=1,
            GpsIndexFileSize=excluded.FileSize,
            GpsIndexLastWriteUtcTicks=excluded.LastWriteUtcTicks,
            GpsIndexError='',
            GpsIndexedUtc=excluded.IndexedUtc,
            ThumbnailPath=excluded.ThumbnailPath,
            Error=excluded.Error,
            IndexedUtc=excluded.IndexedUtc,
            LastSeenScanId=excluded.LastSeenScanId,
            IsMissing=0,
            Sha256='',
            HashFileSize=0,
            HashLastWriteUtcTicks=0,
            HashError='',
            HashedUtc='',
            PerceptualHash='',
            AverageHash='',
            PerceptualHashFileSize=0,
            PerceptualHashLastWriteUtcTicks=0,
            PerceptualHashError='',
            PerceptualHashedUtc='',
            PerceptualHashAlgorithmVersion=0,
            QualityScore=-1,
            TechnicalScore=-1,
            SharpnessScore=-1,
            BlurScore=-1,
            ExposureScore=-1,
            ContrastScore=-1,
            NoiseScore=-1,
            ResolutionScore=-1,
            CompressionScore=-1,
            QualityNotes='',
            QualityFileSize=0,
            QualityLastWriteUtcTicks=0,
            QualityError='',
            QualityAnalyzedUtc='',
            QualityAlgorithmVersion=0,
            FaceCount=-1,
            EyeCount=-1,
            FaceScore=-1,
            EyeScore=-1,
            FacePoseScore=-1,
            WorstFaceScore=-1,
            EyeOpennessScore=-1,
            ClosedEyeCount=-1,
            BlinkPenalty=0,
            FaceIndexVersion=0,
            FaceIndexOrientationVersion=0,
            FaceIndexFileSize=0,
            FaceIndexLastWriteUtcTicks=0,
            FaceIndexError='',
            FaceIndexedUtc='',
            IsQuarantined=0,
            QuarantinePath='',
            IsDeleted=0,
            DeletedUtc=NULL;
        """;
        AddPhotoParameters(command, record);
        command.Parameters.AddWithValue("$ManualCaptureDateSource", CaptureDatePolicy.ManualCatalog);
        await command.ExecuteNonQueryAsync(cancellationToken);

    }

    public async Task CompleteSourceScanAsync(
        string sourceFolder,
        IReadOnlyCollection<string> missingPaths,
        CancellationToken cancellationToken)
    {
        var normalizedSource = NormalizeDirectory(sourceFolder);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        // The scanner removes every path it actually sees from the pre-scan signature map.
        // Only the leftovers can have disappeared, so do not rewrite every unchanged catalog row.
        // Keep batches below SQLite's common parameter limit.
        const int batchSize = 800;
        var paths = missingPaths?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        for (var offset = 0; offset < paths.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = paths.Skip(offset).Take(batchSize).ToArray();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$source", normalizedSource);
            var names = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                names[i] = "$p" + i;
                command.Parameters.AddWithValue(names[i], batch[i]);
            }
            command.CommandText = $"UPDATE Files SET IsMissing=1 WHERE SourceFolder=$source AND IsQuarantined=0 AND IsDeleted=0 AND FullPath IN ({string.Join(',', names)});";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE SourceFolders SET LastScanUtc=$utc WHERE Path=$source;";
            command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$source", normalizedSource);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<HashCandidate>> GetHashCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<HashCandidate>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, FullPath, FileSize, LastWriteUtcTicks, Sha256, HashFileSize, HashLastWriteUtcTicks
            FROM Files
            WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0
              AND FileSize>0
              AND FileSize IN (
                  SELECT FileSize
                  FROM Files
                  WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND FileSize>0
                  GROUP BY FileSize
                  HAVING COUNT(*)>1
              )
            ORDER BY FileSize DESC, FullPath COLLATE NOCASE;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HashCandidate
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                FileSize = reader.GetInt64(2),
                LastWriteUtcTicks = reader.GetInt64(3),
                Sha256 = reader.GetString(4),
                HashFileSize = reader.GetInt64(5),
                HashLastWriteUtcTicks = reader.GetInt64(6)
            });
        }
        return result;
    }

    public async Task UpdateHashAsync(
        SqliteConnection connection,
        long fileId,
        string sha256,
        long hashFileSize,
        long hashLastWriteUtcTicks,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Files
            SET Sha256=$sha,
                HashFileSize=$size,
                HashLastWriteUtcTicks=$ticks,
                HashError=$error,
                HashedUtc=$utc
            WHERE Id=$id
              AND FileSize=$size AND LastWriteUtcTicks=$ticks
              AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;
            """;
        command.Parameters.AddWithValue("$sha", sha256 ?? "");
        command.Parameters.AddWithValue("$size", hashFileSize);
        command.Parameters.AddWithValue("$ticks", hashLastWriteUtcTicks);
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", fileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<PerceptualHashCandidate>> GetPerceptualHashCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PerceptualHashCandidate>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, FullPath, FileName, SourceFolder, ThumbnailPath, FileSize, LastWriteUtcTicks,
                   Width, Height, Orientation, CaptureDate, CaptureDateSource, CameraMake, CameraModel,
                   CASE WHEN HashFileSize=FileSize AND HashLastWriteUtcTicks=LastWriteUtcTicks AND HashError='' THEN Sha256 ELSE '' END AS ValidSha256,
                   PerceptualHash, AverageHash, PerceptualHashFileSize,
                   PerceptualHashLastWriteUtcTicks, PerceptualHashError,
                   QualityScore, SharpnessScore, BlurScore, ExposureScore, ResolutionScore, CompressionScore,
                   QualityNotes, QualityFileSize, QualityLastWriteUtcTicks, QualityError,
                   QualityAlgorithmVersion, FaceCount, EyeCount, FaceScore, EyeScore,
                   TechnicalScore, ContrastScore, NoiseScore, FacePoseScore, WorstFaceScore,
                   EyeOpennessScore, ClosedEyeCount, BlinkPenalty,
                   PerceptualHashAlgorithmVersion
            FROM Files
            WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND FileSize>0
            ORDER BY Id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PerceptualHashCandidate
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                FileName = reader.GetString(2),
                SourceFolder = reader.GetString(3),
                ThumbnailPath = reader.GetString(4),
                FileSize = reader.GetInt64(5),
                LastWriteUtcTicks = reader.GetInt64(6),
                Width = reader.GetInt32(7),
                Height = reader.GetInt32(8),
                Orientation = reader.GetInt32(9),
                CaptureDate = reader.IsDBNull(10) ? null : reader.GetString(10),
                CaptureDateSource = reader.GetString(11),
                CameraMake = reader.GetString(12),
                CameraModel = reader.GetString(13),
                Sha256 = reader.GetString(14),
                DHash = reader.GetString(15),
                AHash = reader.GetString(16),
                PerceptualHashFileSize = reader.GetInt64(17),
                PerceptualHashLastWriteUtcTicks = reader.GetInt64(18),
                PerceptualHashError = reader.GetString(19),
                QualityScore = reader.GetDouble(20),
                SharpnessScore = reader.GetDouble(21),
                BlurScore = reader.GetDouble(22),
                ExposureScore = reader.GetDouble(23),
                ResolutionScore = reader.GetDouble(24),
                CompressionScore = reader.GetDouble(25),
                QualityNotes = reader.GetString(26),
                QualityFileSize = reader.GetInt64(27),
                QualityLastWriteUtcTicks = reader.GetInt64(28),
                QualityError = reader.GetString(29),
                QualityAlgorithmVersion = reader.GetInt32(30),
                FaceCount = reader.GetInt32(31),
                EyeCount = reader.GetInt32(32),
                FaceScore = reader.GetDouble(33),
                EyeScore = reader.GetDouble(34),
                TechnicalScore = reader.GetDouble(35),
                ContrastScore = reader.GetDouble(36),
                NoiseScore = reader.GetDouble(37),
                FacePoseScore = reader.GetDouble(38),
                WorstFaceScore = reader.GetDouble(39),
                EyeOpennessScore = reader.GetDouble(40),
                ClosedEyeCount = reader.GetInt32(41),
                BlinkPenalty = reader.GetDouble(42),
                PerceptualHashAlgorithmVersion = reader.GetInt32(43)
            });
        }
        return result;
    }

    public async Task UpdatePerceptualHashAsync(
        SqliteConnection connection,
        long fileId,
        string dHash,
        string aHash,
        long fileSize,
        long lastWriteUtcTicks,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Files
            SET PerceptualHash=$dhash,
                AverageHash=$ahash,
                PerceptualHashFileSize=$size,
                PerceptualHashLastWriteUtcTicks=$ticks,
                PerceptualHashError=$error,
                PerceptualHashedUtc=$utc,
                PerceptualHashAlgorithmVersion=$algorithmVersion
            WHERE Id=$id
              AND FileSize=$size AND LastWriteUtcTicks=$ticks
              AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;
            """;
        command.Parameters.AddWithValue("$dhash", dHash ?? "");
        command.Parameters.AddWithValue("$ahash", aHash ?? "");
        command.Parameters.AddWithValue("$size", fileSize);
        command.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$algorithmVersion", PerceptualHashAlgorithmInfo.CurrentVersion);
        command.Parameters.AddWithValue("$id", fileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateQualityAsync(
        SqliteConnection connection,
        long fileId,
        long fileSize,
        long lastWriteUtcTicks,
        QualityMetrics? metrics,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Files
            SET QualityScore=$total,
                TechnicalScore=$technical,
                SharpnessScore=$sharpness,
                BlurScore=$blur,
                ExposureScore=$exposure,
                ContrastScore=$contrast,
                NoiseScore=$noise,
                ResolutionScore=$resolution,
                CompressionScore=$compression,
                QualityNotes=$notes,
                QualityFileSize=$size,
                QualityLastWriteUtcTicks=$ticks,
                QualityError=$error,
                QualityAnalyzedUtc=$utc,
                QualityAlgorithmVersion=$qualityAlgorithmVersion,
                FaceCount=$faceCount,
                EyeCount=$eyeCount,
                FaceScore=$faceScore,
                EyeScore=$eyeScore,
                FacePoseScore=$facePose,
                WorstFaceScore=$worstFace,
                EyeOpennessScore=$eyeOpenness,
                ClosedEyeCount=$closedEyeCount,
                BlinkPenalty=$blinkPenalty
            WHERE Id=$id
              AND FileSize=$size AND LastWriteUtcTicks=$ticks
              AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;
            """;
        command.Parameters.AddWithValue("$total", metrics?.TotalScore ?? -1);
        command.Parameters.AddWithValue("$technical", metrics?.TechnicalScore ?? -1);
        command.Parameters.AddWithValue("$sharpness", metrics?.SharpnessScore ?? -1);
        command.Parameters.AddWithValue("$blur", metrics?.BlurScore ?? -1);
        command.Parameters.AddWithValue("$exposure", metrics?.ExposureScore ?? -1);
        command.Parameters.AddWithValue("$contrast", metrics?.ContrastScore ?? -1);
        command.Parameters.AddWithValue("$noise", metrics?.NoiseScore ?? -1);
        command.Parameters.AddWithValue("$resolution", metrics?.ResolutionScore ?? -1);
        command.Parameters.AddWithValue("$compression", metrics?.CompressionScore ?? -1);
        command.Parameters.AddWithValue("$notes", metrics?.Notes ?? "");
        command.Parameters.AddWithValue("$faceCount", metrics?.FaceCount ?? -1);
        command.Parameters.AddWithValue("$eyeCount", metrics?.EyeCount ?? -1);
        command.Parameters.AddWithValue("$faceScore", metrics?.FaceScore ?? -1);
        command.Parameters.AddWithValue("$eyeScore", metrics?.EyeScore ?? -1);
        command.Parameters.AddWithValue("$facePose", metrics?.FacePoseScore ?? -1);
        command.Parameters.AddWithValue("$worstFace", metrics?.WorstFaceScore ?? -1);
        command.Parameters.AddWithValue("$eyeOpenness", metrics?.EyeOpennessScore ?? -1);
        command.Parameters.AddWithValue("$closedEyeCount", metrics?.ClosedEyeCount ?? -1);
        command.Parameters.AddWithValue("$blinkPenalty", metrics?.BlinkPenalty ?? 0);
        command.Parameters.AddWithValue("$qualityAlgorithmVersion", QualityAlgorithmInfo.CurrentVersion);
        command.Parameters.AddWithValue("$size", fileSize);
        command.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", fileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<DuplicateGroupItem>> GetExactDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        var byHash = new Dictionary<string, List<DuplicateFileItem>>(StringComparer.Ordinal);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH DuplicateHashes AS (
                SELECT Sha256
                FROM Files
                WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND Sha256<>'' AND HashError=''
                  AND HashFileSize=FileSize AND HashLastWriteUtcTicks=LastWriteUtcTicks
                GROUP BY Sha256
                HAVING COUNT(*)>1
            )
            SELECT f.Id, f.FullPath, f.FileName, f.SourceFolder, f.ThumbnailPath, f.FileSize,
                   f.Width, f.Height, f.CaptureDate, f.CameraMake, f.CameraModel, f.Sha256
            FROM Files f
            INNER JOIN DuplicateHashes d ON d.Sha256=f.Sha256
            WHERE f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0 AND f.HashError=''
              AND f.HashFileSize=f.FileSize AND f.HashLastWriteUtcTicks=f.LastWriteUtcTicks
            ORDER BY f.FileSize DESC, f.Sha256, f.FullPath COLLATE NOCASE;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new DuplicateFileItem
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                FileName = reader.GetString(2),
                SourceFolder = reader.GetString(3),
                ThumbnailPath = reader.GetString(4),
                FileSize = reader.GetInt64(5),
                Width = reader.GetInt32(6),
                Height = reader.GetInt32(7),
                CaptureDate = reader.IsDBNull(8) ? null : reader.GetString(8),
                CameraMake = reader.GetString(9),
                CameraModel = reader.GetString(10),
                Sha256 = reader.GetString(11)
            };

            if (!byHash.TryGetValue(item.Sha256, out var list))
            {
                list = new List<DuplicateFileItem>();
                byHash[item.Sha256] = list;
            }
            list.Add(item);
        }

        return byHash
            .Select(pair => new DuplicateGroupItem
            {
                Sha256 = pair.Key,
                FileSize = pair.Value[0].FileSize,
                Files = pair.Value
            })
            .OrderByDescending(x => x.WastedBytes)
            .ThenByDescending(x => x.FileSize)
            .ToList();
    }

    public async Task<DuplicateAnalysisStatistics> GetExactDuplicateStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(FileCount), 0),
                COALESCE(SUM(FileCount - 1), 0),
                COALESCE(SUM((FileCount - 1) * FileSize), 0)
            FROM (
                SELECT Sha256, MIN(FileSize) AS FileSize, COUNT(*) AS FileCount
                FROM Files
                WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND Sha256<>'' AND HashError=''
                  AND HashFileSize=FileSize AND HashLastWriteUtcTicks=LastWriteUtcTicks
                GROUP BY Sha256
                HAVING COUNT(*)>1
            );
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new DuplicateAnalysisStatistics(0, 0, 0, 0);

        return new DuplicateAnalysisStatistics(
            reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
    }

    private static void ApplyPhotoReviewFilter(List<string> where, SqliteCommand command, PhotoReviewFilter filter)
    {
        const string untrustedDate = "(CaptureDate IS NULL OR NOT (CaptureDateSource LIKE 'EXIF%' OR CaptureDateSource=$manualDateSource))";
        const string withoutEvent = "NOT EXISTS (SELECT 1 FROM EventFiles ef_review WHERE ef_review.FileId=Files.Id)";
        const string indexError = "TRIM(COALESCE(Error,''))<>''";

        switch (filter)
        {
            case PhotoReviewFilter.NeedsReview:
                where.Add($"({untrustedDate} OR {withoutEvent} OR {indexError})");
                command.Parameters.AddWithValue("$manualDateSource", CaptureDatePolicy.ManualCatalog);
                break;
            case PhotoReviewFilter.UntrustedDate:
                where.Add(untrustedDate);
                command.Parameters.AddWithValue("$manualDateSource", CaptureDatePolicy.ManualCatalog);
                break;
            case PhotoReviewFilter.WithoutEvent:
                where.Add(withoutEvent);
                break;
            case PhotoReviewFilter.IndexError:
                where.Add(indexError);
                break;
        }
    }

    private static string GetPhotoOrderBy(PhotoSortOrder sortOrder) => sortOrder switch
    {
        PhotoSortOrder.CaptureDateAscending =>
            "CASE WHEN CaptureDate IS NULL THEN 1 ELSE 0 END, CaptureDate ASC, LastWriteUtcTicks ASC, Id ASC",
        PhotoSortOrder.FileNameAscending =>
            "FileName COLLATE NOCASE ASC, FullPath COLLATE NOCASE ASC, Id ASC",
        PhotoSortOrder.FullPathAscending =>
            "FullPath COLLATE NOCASE ASC, Id ASC",
        _ => "CASE WHEN CaptureDate IS NULL THEN 1 ELSE 0 END, CaptureDate DESC, LastWriteUtcTicks DESC, Id DESC"
    };

    public async Task<List<PhotoItem>> QueryPhotosAsync(PhotoQuery query, CancellationToken cancellationToken = default)
    {
        var result = new List<PhotoItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        var where = new List<string> { "IsMissing=0", "IsQuarantined=0", "IsDeleted=0" };
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            where.Add("(FileName LIKE $search COLLATE NOCASE OR FullPath LIKE $search COLLATE NOCASE)");
            command.Parameters.AddWithValue("$search", "%" + query.SearchText.Trim() + "%");
        }
        if (query.Year.HasValue)
        {
            where.Add("EffectiveYear=$year");
            command.Parameters.AddWithValue("$year", query.Year.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Camera))
        {
            where.Add("TRIM(CameraMake || ' ' || CameraModel)=$camera");
            command.Parameters.AddWithValue("$camera", query.Camera);
        }
        if (!string.IsNullOrWhiteSpace(query.SourceFolder))
        {
            where.Add("SourceFolder=$source");
            command.Parameters.AddWithValue("$source", NormalizeDirectory(query.SourceFolder));
        }
        if (query.PersonId.HasValue)
        {
            where.Add("EXISTS (SELECT 1 FROM DetectedFaces df WHERE df.FileId=Files.Id AND df.PersonId=$personId AND df.IsIgnored=0) AND FaceIndexVersion=2 AND FaceIndexOrientationVersion=1 AND FaceIndexFileSize=FileSize AND FaceIndexLastWriteUtcTicks=LastWriteUtcTicks AND FaceIndexError=''");
            command.Parameters.AddWithValue("$personId", query.PersonId.Value);
        }
        if (query.EventId.HasValue)
        {
            where.Add("EXISTS (SELECT 1 FROM EventFiles ef WHERE ef.FileId=Files.Id AND ef.EventId=$eventId)");
            command.Parameters.AddWithValue("$eventId", query.EventId.Value);
        }
        if (query.DateFrom.HasValue)
        {
            where.Add("CaptureDate >= $dateFrom");
            command.Parameters.AddWithValue("$dateFrom", FormatStoredDateTime(query.DateFrom.Value));
        }
        if (query.DateToExclusive.HasValue)
        {
            where.Add("CaptureDate < $dateTo");
            command.Parameters.AddWithValue("$dateTo", FormatStoredDateTime(query.DateToExclusive.Value));
        }
        ApplyPhotoReviewFilter(where, command, query.ReviewFilter);

        command.CommandText = $"""
            SELECT Id, FullPath, FileName, SourceFolder, ThumbnailPath, FileSize, Width, Height,
                   CaptureDate, CaptureDateSource, CameraMake, CameraModel, Error
            FROM Files
            WHERE {string.Join(" AND ", where)}
            ORDER BY {GetPhotoOrderBy(query.SortOrder)}
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit);
        command.Parameters.AddWithValue("$offset", query.Offset);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PhotoItem
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                FileName = reader.GetString(2),
                SourceFolder = reader.GetString(3),
                ThumbnailPath = reader.GetString(4),
                FileSize = reader.GetInt64(5),
                Width = reader.GetInt32(6),
                Height = reader.GetInt32(7),
                CaptureDate = reader.IsDBNull(8) ? null : reader.GetString(8),
                CaptureDateSource = reader.GetString(9),
                CameraMake = reader.GetString(10),
                CameraModel = reader.GetString(11),
                Error = reader.GetString(12)
            });
        }
        return result;
    }

    public async Task<long> CountPhotosAsync(PhotoQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var where = new List<string> { "IsMissing=0", "IsQuarantined=0", "IsDeleted=0" };
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            where.Add("(FileName LIKE $search COLLATE NOCASE OR FullPath LIKE $search COLLATE NOCASE)");
            command.Parameters.AddWithValue("$search", "%" + query.SearchText.Trim() + "%");
        }
        if (query.Year.HasValue)
        {
            where.Add("EffectiveYear=$year");
            command.Parameters.AddWithValue("$year", query.Year.Value);
        }
        if (!string.IsNullOrWhiteSpace(query.Camera))
        {
            where.Add("TRIM(CameraMake || ' ' || CameraModel)=$camera");
            command.Parameters.AddWithValue("$camera", query.Camera);
        }
        if (!string.IsNullOrWhiteSpace(query.SourceFolder))
        {
            where.Add("SourceFolder=$source");
            command.Parameters.AddWithValue("$source", NormalizeDirectory(query.SourceFolder));
        }
        if (query.PersonId.HasValue)
        {
            where.Add("EXISTS (SELECT 1 FROM DetectedFaces df WHERE df.FileId=Files.Id AND df.PersonId=$personId AND df.IsIgnored=0) AND FaceIndexVersion=2 AND FaceIndexOrientationVersion=1 AND FaceIndexFileSize=FileSize AND FaceIndexLastWriteUtcTicks=LastWriteUtcTicks AND FaceIndexError=''");
            command.Parameters.AddWithValue("$personId", query.PersonId.Value);
        }
        if (query.EventId.HasValue)
        {
            where.Add("EXISTS (SELECT 1 FROM EventFiles ef WHERE ef.FileId=Files.Id AND ef.EventId=$eventId)");
            command.Parameters.AddWithValue("$eventId", query.EventId.Value);
        }
        if (query.DateFrom.HasValue)
        {
            where.Add("CaptureDate >= $dateFrom");
            command.Parameters.AddWithValue("$dateFrom", FormatStoredDateTime(query.DateFrom.Value));
        }
        if (query.DateToExclusive.HasValue)
        {
            where.Add("CaptureDate < $dateTo");
            command.Parameters.AddWithValue("$dateTo", FormatStoredDateTime(query.DateToExclusive.Value));
        }
        ApplyPhotoReviewFilter(where, command, query.ReviewFilter);
        command.CommandText = $"SELECT COUNT(*) FROM Files WHERE {string.Join(" AND ", where)};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<List<TimelineMonthItem>> GetTimelineMonthsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TimelineMonthItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH Active AS (
                SELECT Id, CaptureDate, ThumbnailPath, QualityScore,
                       substr(CaptureDate,1,7) AS YearMonth,
                       CAST(substr(CaptureDate,1,4) AS INTEGER) AS Y,
                       CAST(substr(CaptureDate,6,2) AS INTEGER) AS M
                FROM Files
                WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0
                  AND CaptureDate IS NOT NULL AND length(CaptureDate)>=7
                  AND CAST(substr(CaptureDate,1,4) AS INTEGER) BETWEEN 1900 AND 2200
                  AND CAST(substr(CaptureDate,6,2) AS INTEGER) BETWEEN 1 AND 12
            ),
            Ranked AS (
                SELECT YearMonth, ThumbnailPath,
                       ROW_NUMBER() OVER (
                           PARTITION BY YearMonth
                           ORDER BY CASE WHEN QualityScore>=0 THEN 0 ELSE 1 END,
                                    QualityScore DESC, CaptureDate ASC, Id ASC
                       ) AS rn
                FROM Active
            ),
            MonthCounts AS (
                SELECT YearMonth, Y, M, COUNT(*) AS PhotoCount
                FROM Active
                GROUP BY YearMonth, Y, M
            )
            SELECT c.Y, c.M, c.PhotoCount, COALESCE(r.ThumbnailPath,'')
            FROM MonthCounts c
            LEFT JOIN Ranked r ON r.YearMonth=c.YearMonth AND r.rn=1
            ORDER BY c.Y DESC, c.M DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TimelineMonthItem
            {
                Year = reader.GetInt32(0),
                Month = reader.GetInt32(1),
                PhotoCount = checked((int)reader.GetInt64(2)),
                RepresentativeThumbnailPath = reader.GetString(3)
            });
        }
        return result;
    }

    public async Task<List<int>> GetYearsAsync()
    {
        var result = new List<int>();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT EffectiveYear FROM Files WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND EffectiveYear>0 ORDER BY EffectiveYear DESC;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetInt32(0));
        return result;
    }

    public async Task<List<string>> GetCamerasAsync()
    {
        var result = new List<string>();
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT TRIM(CameraMake || ' ' || CameraModel) AS Camera
            FROM Files
            WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND TRIM(CameraMake || ' ' || CameraModel)<>''
            ORDER BY Camera COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<LibraryStatistics> GetStatisticsAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 THEN 1 ELSE 0 END),
                COALESCE(SUM(CASE WHEN IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 THEN FileSize ELSE 0 END), 0),
                SUM(CASE WHEN IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND Error<>'' THEN 1 ELSE 0 END),
                SUM(CASE WHEN IsMissing=1 AND IsDeleted=0 THEN 1 ELSE 0 END)
            FROM Files;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new LibraryStatistics(0, 0, 0, 0);
        return new LibraryStatistics(
            reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt64(3));
    }

    public async Task RecordQuarantineAsync(
        long fileId,
        string originalPath,
        string quarantinePath,
        string sha256,
        long fileSize,
        long originalLastWriteUtcTicks,
        long originalCreationUtcTicks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Files
                SET FullPath=$catalogPath, IsQuarantined=1, QuarantinePath=$q
                WHERE Id=$id AND FullPath=$original AND IsQuarantined=0 AND IsDeleted=0;
                """;
            update.Parameters.AddWithValue("$catalogPath", BuildInactiveCatalogPath(quarantinePath, fileId, "quarantine"));
            update.Parameters.AddWithValue("$q", quarantinePath);
            update.Parameters.AddWithValue("$original", Path.GetFullPath(originalPath));
            update.Parameters.AddWithValue("$id", fileId);
            var changed = await update.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
                throw new InvalidOperationException("Не удалось отметить файл как помещённый в карантин.");
        }

        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                INSERT INTO Actions(FileId, ActionType, OriginalPath, NewPath, Sha256, FileSize, OriginalLastWriteUtcTicks, OriginalCreationUtcTicks, CreatedUtc, UndoneUtc, Error)
                VALUES($fileId, 'QUARANTINE', $original, $new, $sha, $size, $lastWrite, $creation, $created, NULL, '');
                """;
            action.Parameters.AddWithValue("$fileId", fileId);
            action.Parameters.AddWithValue("$original", originalPath);
            action.Parameters.AddWithValue("$new", quarantinePath);
            action.Parameters.AddWithValue("$sha", sha256);
            action.Parameters.AddWithValue("$size", fileSize);
            action.Parameters.AddWithValue("$lastWrite", originalLastWriteUtcTicks);
            action.Parameters.AddWithValue("$creation", originalCreationUtcTicks);
            action.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            await action.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordDirectExactDeletionAsync(
        long fileId,
        string originalPath,
        string tombstonePath,
        string sha256,
        long fileSize,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var utc = DateTime.UtcNow.ToString("O");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Files
                SET FullPath=$catalogPath, IsQuarantined=0, QuarantinePath='', IsMissing=1, IsDeleted=1, DeletedUtc=$utc
                WHERE Id=$id AND FullPath=$original AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;
                """;
            update.Parameters.AddWithValue("$utc", utc);
            update.Parameters.AddWithValue("$catalogPath", BuildInactiveCatalogPath(tombstonePath, fileId, "deleted"));
            update.Parameters.AddWithValue("$id", fileId);
            update.Parameters.AddWithValue("$original", Path.GetFullPath(originalPath));
            var changed = await update.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
                throw new InvalidOperationException("Не удалось отметить точный дубль как удалённый: запись каталога изменилась.");
        }

        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                INSERT INTO Actions(FileId, ActionType, OriginalPath, NewPath, Sha256, FileSize, CreatedUtc, UndoneUtc, PermanentlyDeletedUtc, Error)
                VALUES($fileId, 'DELETE_EXACT_DIRECT', $original, $new, $sha, $size, $created, NULL, $deleted, '');
                """;
            action.Parameters.AddWithValue("$fileId", fileId);
            action.Parameters.AddWithValue("$original", originalPath);
            action.Parameters.AddWithValue("$new", tombstonePath);
            action.Parameters.AddWithValue("$sha", sha256);
            action.Parameters.AddWithValue("$size", fileSize);
            action.Parameters.AddWithValue("$created", utc);
            action.Parameters.AddWithValue("$deleted", utc);
            await action.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkQuarantineUndoneAsync(
        long actionId, long fileId,
        long restoredLastWriteUtcTicks, long restoredCreationUtcTicks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        string originalPath;
        string quarantinePath;

        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT OriginalPath, NewPath
                FROM Actions
                WHERE Id=$id AND FileId=$fileId AND ActionType='QUARANTINE'
                  AND UndoneUtc IS NULL AND PermanentlyDeletedUtc IS NULL;
                """;
            read.Parameters.AddWithValue("$id", actionId);
            read.Parameters.AddWithValue("$fileId", fileId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Запись карантина уже отменена или не найдена.");
            originalPath = Path.GetFullPath(reader.GetString(0));
            quarantinePath = Path.GetFullPath(reader.GetString(1));
        }

        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                UPDATE Actions
                SET UndoneUtc=$utc
                WHERE Id=$id AND FileId=$fileId AND ActionType='QUARANTINE' AND UndoneUtc IS NULL AND PermanentlyDeletedUtc IS NULL;
                """;
            action.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            action.Parameters.AddWithValue("$id", actionId);
            action.Parameters.AddWithValue("$fileId", fileId);
            var changed = await action.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
                throw new InvalidOperationException("Запись карантина уже отменена или не найдена.");
        }

        await using (var file = connection.CreateCommand())
        {
            file.Transaction = transaction;
            file.CommandText = """
                UPDATE Files
                SET FullPath=$original, FileName=$fileName, IsQuarantined=0, QuarantinePath='',
                    IsMissing=CASE WHEN EXISTS(SELECT 1 FROM SourceFolders sf WHERE sf.Path=Files.SourceFolder) THEN 0 ELSE 1 END,
                    IsDeleted=0, DeletedUtc=NULL,
                    CreationUtcTicks=$creationTicks, LastWriteUtcTicks=$lastWriteTicks,
                    HashLastWriteUtcTicks=CASE WHEN HashFileSize=FileSize AND Sha256<>'' THEN $lastWriteTicks ELSE HashLastWriteUtcTicks END,
                    PerceptualHashLastWriteUtcTicks=CASE WHEN PerceptualHashFileSize=FileSize AND PerceptualHash<>'' THEN $lastWriteTicks ELSE PerceptualHashLastWriteUtcTicks END,
                    QualityLastWriteUtcTicks=CASE WHEN QualityFileSize=FileSize AND QualityAlgorithmVersion>0 THEN $lastWriteTicks ELSE QualityLastWriteUtcTicks END,
                    FaceIndexLastWriteUtcTicks=CASE WHEN FaceIndexFileSize=FileSize AND FaceIndexVersion>0 THEN $lastWriteTicks ELSE FaceIndexLastWriteUtcTicks END,
                    GpsIndexLastWriteUtcTicks=CASE WHEN GpsIndexFileSize=FileSize AND GpsIndexVersion>0 THEN $lastWriteTicks ELSE GpsIndexLastWriteUtcTicks END,
                    SemanticIndexLastWriteUtcTicks=CASE WHEN SemanticIndexFileSize=FileSize AND SemanticIndexVersion>0 THEN $lastWriteTicks ELSE SemanticIndexLastWriteUtcTicks END
                WHERE Id=$fileId AND IsQuarantined=1 AND IsDeleted=0 AND QuarantinePath=$quarantine;
                """;
            file.Parameters.AddWithValue("$original", originalPath);
            file.Parameters.AddWithValue("$fileName", Path.GetFileName(originalPath));
            file.Parameters.AddWithValue("$quarantine", quarantinePath);
            file.Parameters.AddWithValue("$creationTicks", restoredCreationUtcTicks);
            file.Parameters.AddWithValue("$lastWriteTicks", restoredLastWriteUtcTicks);
            file.Parameters.AddWithValue("$fileId", fileId);
            var changed = await file.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
                throw new InvalidOperationException("Файл не найден в каталоге при Undo.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkQuarantinePermanentlyDeletedAsync(long actionId, long fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var utc = DateTime.UtcNow.ToString("O");

        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                UPDATE Actions
                SET PermanentlyDeletedUtc=$utc
                WHERE Id=$id AND FileId=$fileId AND ActionType='QUARANTINE'
                  AND UndoneUtc IS NULL AND PermanentlyDeletedUtc IS NULL;
                """;
            action.Parameters.AddWithValue("$utc", utc);
            action.Parameters.AddWithValue("$id", actionId);
            action.Parameters.AddWithValue("$fileId", fileId);
            var changed = await action.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
                throw new InvalidOperationException("Запись карантина уже восстановлена, удалена или не найдена.");
        }

        await using (var file = connection.CreateCommand())
        {
            file.Transaction = transaction;
            file.CommandText = """
                UPDATE Files
                SET IsQuarantined=0, QuarantinePath='', IsMissing=1, IsDeleted=1, DeletedUtc=$utc
                WHERE Id=$fileId;
                """;
            file.Parameters.AddWithValue("$utc", utc);
            file.Parameters.AddWithValue("$fileId", fileId);
            var changed = await file.ExecuteNonQueryAsync(cancellationToken);
            if (changed != 1)
                throw new InvalidOperationException("Файл не найден в каталоге при окончательном удалении.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<QuarantineActionItem>> GetQuarantineActionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<QuarantineActionItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id, a.FileId, a.OriginalPath, a.NewPath, a.Sha256, a.FileSize, a.OriginalLastWriteUtcTicks, a.OriginalCreationUtcTicks, a.CreatedUtc, a.UndoneUtc, a.PermanentlyDeletedUtc, a.Error, f.SourceFolder
            FROM Actions a
            INNER JOIN Files f ON f.Id=a.FileId
            WHERE a.ActionType='QUARANTINE'
            ORDER BY CASE WHEN a.UndoneUtc IS NULL AND a.PermanentlyDeletedUtc IS NULL THEN 0 ELSE 1 END, a.Id DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new QuarantineActionItem
            {
                Id = reader.GetInt64(0),
                FileId = reader.GetInt64(1),
                OriginalPath = reader.GetString(2),
                QuarantinePath = reader.GetString(3),
                Sha256 = reader.GetString(4),
                FileSize = reader.GetInt64(5),
                OriginalLastWriteUtcTicks = reader.GetInt64(6),
                OriginalCreationUtcTicks = reader.GetInt64(7),
                CreatedUtc = reader.GetString(8),
                UndoneUtc = reader.IsDBNull(9) ? null : reader.GetString(9),
                PermanentlyDeletedUtc = reader.IsDBNull(10) ? null : reader.GetString(10),
                Error = reader.GetString(11),
                OriginalSourceFolder = reader.GetString(12)
            });
        }
        return result;
    }


    public async Task<List<FaceIndexCandidate>> GetFaceIndexCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FaceIndexCandidate>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, FullPath, FileName, ThumbnailPath, FileSize, LastWriteUtcTicks, Orientation, CaptureDate,
                   FaceIndexVersion, FaceIndexOrientationVersion, FaceIndexFileSize, FaceIndexLastWriteUtcTicks, FaceIndexError,
                   (SELECT COUNT(*) FROM DetectedFaces df WHERE df.FileId=Files.Id AND df.IsIgnored=0)
            FROM Files
            WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0 AND FileSize>0
            ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FaceIndexCandidate
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                FileName = reader.GetString(2),
                ThumbnailPath = reader.GetString(3),
                FileSize = reader.GetInt64(4),
                LastWriteUtcTicks = reader.GetInt64(5),
                Orientation = reader.GetInt32(6),
                CaptureDate = reader.IsDBNull(7) ? null : reader.GetString(7),
                FaceIndexVersion = reader.GetInt32(8),
                FaceIndexOrientationVersion = reader.GetInt32(9),
                FaceIndexFileSize = reader.GetInt64(10),
                FaceIndexLastWriteUtcTicks = reader.GetInt64(11),
                FaceIndexError = reader.GetString(12),
                CachedFaceCount = checked((int)reader.GetInt64(13))
            });
        }
        return result;
    }

    public async Task<int> CountFacesForFileAsync(long fileId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DetectedFaces WHERE FileId=$id AND IsIgnored=0;";
        command.Parameters.AddWithValue("$id", fileId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<List<string>> ReplaceDetectedFacesAsync(
        long fileId,
        long fileSize,
        long lastWriteUtcTicks,
        int algorithmVersion,
        int orientation,
        int previousOrientationVersion,
        IReadOnlyList<DetectedFaceDraft> faces,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        // Refresh the per-file face index without blindly destroying user curation. Reusing the
        // old row Id for a confidently matched face preserves PersonId, IsIgnored, pinned covers
        // and FaceIgnoreActionItems. This matters for cache migrations (including schema 19) and
        // for ordinary re-indexing after metadata/code changes.
        var existing = new List<ExistingFaceRefreshState>();
        await using (var readExisting = connection.CreateCommand())
        {
            readExisting.Transaction = transaction;
            readExisting.CommandText = """
                SELECT df.Id, df.PersonId, df.IsIgnored, df.X, df.Y, df.Width, df.Height, df.ImageWidth, df.ImageHeight,
                       df.Embedding, df.ThumbnailPath,
                       CASE WHEN EXISTS(SELECT 1 FROM People pr WHERE pr.RepresentativeFaceId=df.Id) THEN 1 ELSE 0 END,
                       COALESCE(p.IsAuto,1), COALESCE(p.Name,'')
                FROM DetectedFaces df
                LEFT JOIN People p ON p.Id=df.PersonId
                WHERE df.FileId=$id
                ORDER BY df.Id;
                """;
            readExisting.Parameters.AddWithValue("$id", fileId);
            await using var reader = await readExisting.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(new ExistingFaceRefreshState(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetInt32(2) != 0,
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7),
                    reader.GetInt32(8),
                    BytesToFloatArray((byte[])reader[9]),
                    reader.GetString(10),
                    reader.GetInt32(11) != 0,
                    reader.GetInt32(12) != 0,
                    reader.GetString(13)));
            }
        }

        var oldThumbnailPaths = existing
            .Select(x => x.ThumbnailPath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matches = MatchFacesForRefresh(existing, faces, orientation, previousOrientationVersion);
        var matchedExisting = new HashSet<long>(matches.Select(x => x.Existing.Id));
        var matchedDrafts = new HashSet<int>(matches.Select(x => x.DraftIndex));

        foreach (var match in matches)
        {
            var face = faces[match.DraftIndex];

            // An unnamed automatic cluster is disposable. If a refreshed face leaves such a cluster,
            // do not leave People.RepresentativeFaceId pointing at a face that no longer belongs to it.
            // Named/manual groups keep their PersonId and therefore keep their explicit cover.
            if (!match.Existing.PreservePersonAssignment && match.Existing.IsRepresentative)
            {
                await using var clearAutoCover = connection.CreateCommand();
                clearAutoCover.Transaction = transaction;
                clearAutoCover.CommandText = "UPDATE People SET RepresentativeFaceId=NULL, UpdatedUtc=$utc WHERE RepresentativeFaceId=$faceId;";
                clearAutoCover.Parameters.AddWithValue("$faceId", match.Existing.Id);
                clearAutoCover.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                await clearAutoCover.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var updateFace = connection.CreateCommand();
            updateFace.Transaction = transaction;
            updateFace.CommandText = """
                UPDATE DetectedFaces
                SET PersonId=CASE WHEN $preservePerson=1 THEN PersonId ELSE NULL END,
                    X=$x, Y=$y, Width=$w, Height=$h, ImageWidth=$iw, ImageHeight=$ih,
                    QualityScore=$quality, Embedding=$embedding, ThumbnailPath=$thumb
                WHERE Id=$faceId AND FileId=$fileId;
                """;
            updateFace.Parameters.AddWithValue("$preservePerson", match.Existing.PreservePersonAssignment ? 1 : 0);
            updateFace.Parameters.AddWithValue("$x", face.X);
            updateFace.Parameters.AddWithValue("$y", face.Y);
            updateFace.Parameters.AddWithValue("$w", face.Width);
            updateFace.Parameters.AddWithValue("$h", face.Height);
            updateFace.Parameters.AddWithValue("$iw", face.ImageWidth);
            updateFace.Parameters.AddWithValue("$ih", face.ImageHeight);
            updateFace.Parameters.AddWithValue("$quality", face.QualityScore);
            updateFace.Parameters.Add("$embedding", SqliteType.Blob).Value = FloatArrayToBytes(face.Embedding);
            updateFace.Parameters.AddWithValue("$thumb", face.ThumbnailPath ?? "");
            updateFace.Parameters.AddWithValue("$faceId", match.Existing.Id);
            updateFace.Parameters.AddWithValue("$fileId", fileId);
            if (await updateFace.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Не удалось обновить сопоставленное лицо в индексе.");
        }

        for (var i = 0; i < faces.Count; i++)
        {
            if (matchedDrafts.Contains(i)) continue;
            var face = faces[i];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO DetectedFaces(
                    FileId, PersonId, X, Y, Width, Height, ImageWidth, ImageHeight,
                    QualityScore, Embedding, ThumbnailPath, IsIgnored, CreatedUtc)
                VALUES($fileId, NULL, $x, $y, $w, $h, $iw, $ih, $quality, $embedding, $thumb, 0, $utc);
                """;
            insert.Parameters.AddWithValue("$fileId", fileId);
            insert.Parameters.AddWithValue("$x", face.X);
            insert.Parameters.AddWithValue("$y", face.Y);
            insert.Parameters.AddWithValue("$w", face.Width);
            insert.Parameters.AddWithValue("$h", face.Height);
            insert.Parameters.AddWithValue("$iw", face.ImageWidth);
            insert.Parameters.AddWithValue("$ih", face.ImageHeight);
            insert.Parameters.AddWithValue("$quality", face.QualityScore);
            insert.Parameters.Add("$embedding", SqliteType.Blob).Value = FloatArrayToBytes(face.Embedding);
            insert.Parameters.AddWithValue("$thumb", face.ThumbnailPath ?? "");
            insert.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // Faces that genuinely disappeared are removed. Clear pinned-cover pointers first and
        // prune undo-item references so the UI cannot offer an undo for a detection that no longer
        // exists. Named/manual People rows are deliberately retained even if this was their last face.
        foreach (var old in existing)
        {
            if (matchedExisting.Contains(old.Id)) continue;

            await using (var clearCover = connection.CreateCommand())
            {
                clearCover.Transaction = transaction;
                clearCover.CommandText = "UPDATE People SET RepresentativeFaceId=NULL, UpdatedUtc=$utc WHERE RepresentativeFaceId=$faceId;";
                clearCover.Parameters.AddWithValue("$faceId", old.Id);
                clearCover.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                await clearCover.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var pruneUndo = connection.CreateCommand())
            {
                pruneUndo.Transaction = transaction;
                pruneUndo.CommandText = "DELETE FROM FaceIgnoreActionItems WHERE FaceId=$faceId;";
                pruneUndo.Parameters.AddWithValue("$faceId", old.Id);
                await pruneUndo.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteFace = connection.CreateCommand())
            {
                deleteFace.Transaction = transaction;
                deleteFace.CommandText = "DELETE FROM DetectedFaces WHERE Id=$faceId AND FileId=$fileId;";
                deleteFace.Parameters.AddWithValue("$faceId", old.Id);
                deleteFace.Parameters.AddWithValue("$fileId", fileId);
                await deleteFace.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var cleanupAutoGroups = connection.CreateCommand())
        {
            cleanupAutoGroups.Transaction = transaction;
            cleanupAutoGroups.CommandText = "DELETE FROM People WHERE IsAuto=1 AND TRIM(Name)='' AND NOT EXISTS(SELECT 1 FROM DetectedFaces df WHERE df.PersonId=People.Id);";
            await cleanupAutoGroups.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Files
                SET FaceIndexVersion=$version,
                    FaceIndexOrientationVersion=$orientationVersion,
                    FaceIndexFileSize=$size,
                    FaceIndexLastWriteUtcTicks=$ticks,
                    FaceIndexError='',
                    FaceIndexedUtc=$utc
                WHERE Id=$id AND FileSize=$size AND LastWriteUtcTicks=$ticks
                  AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;
                """;
            update.Parameters.AddWithValue("$version", algorithmVersion);
            update.Parameters.AddWithValue("$orientationVersion", FaceIndexCandidate.CurrentOrientationVersion);
            update.Parameters.AddWithValue("$size", fileSize);
            update.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
            update.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", fileId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new IOException("Каталог изменился во время индексации лиц. Выполните анализ ещё раз.");
        }

        await transaction.CommitAsync(cancellationToken);
        return oldThumbnailPaths;
    }

    public async Task MarkFaceIndexErrorAsync(
        long fileId,
        long fileSize,
        long lastWriteUtcTicks,
        int algorithmVersion,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Files
            SET FaceIndexVersion=$version,
                FaceIndexFileSize=$size,
                FaceIndexLastWriteUtcTicks=$ticks,
                FaceIndexError=$error,
                FaceIndexedUtc=$utc
            WHERE Id=$id AND FileSize=$size AND LastWriteUtcTicks=$ticks
              AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;
            """;
        command.Parameters.AddWithValue("$version", algorithmVersion);
        command.Parameters.AddWithValue("$size", fileSize);
        command.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", fileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ResetUnnamedAutomaticPeopleAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                UPDATE DetectedFaces
                SET PersonId=NULL
                WHERE PersonId IN (SELECT Id FROM People WHERE IsAuto=1 AND TRIM(Name)='');
                """;
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM People WHERE IsAuto=1 AND TRIM(Name)='';";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<FaceEmbeddingCandidate>> GetUngroupedFaceEmbeddingsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FaceEmbeddingCandidate>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT df.Id, df.FileId, df.PersonId, df.QualityScore, df.Embedding
            FROM DetectedFaces df
            JOIN Files f ON f.Id=df.FileId
            WHERE df.PersonId IS NULL AND df.IsIgnored=0
              AND f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0
              AND f.FaceIndexVersion=2 AND f.FaceIndexOrientationVersion=1 AND f.FaceIndexFileSize=f.FileSize AND f.FaceIndexLastWriteUtcTicks=f.LastWriteUtcTicks AND f.FaceIndexError=''
              AND length(df.Embedding)>0
            ORDER BY df.QualityScore DESC, df.Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FaceEmbeddingCandidate
            {
                FaceId = reader.GetInt64(0),
                FileId = reader.GetInt64(1),
                PersonId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                QualityScore = reader.GetDouble(3),
                Embedding = BytesToFloatArray((byte[])reader[4])
            });
        }
        return result;
    }

    public async Task<long> CreateAutomaticPersonGroupAsync(IReadOnlyList<long> faceIds, CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0) throw new ArgumentException("Группа лиц пуста.", nameof(faceIds));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var utc = DateTime.UtcNow.ToString("O");
        long personId;

        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "INSERT INTO People(Name, IsAuto, CreatedUtc, UpdatedUtc) VALUES('', 1, $utc, $utc);";
            create.Parameters.AddWithValue("$utc", utc);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var getId = connection.CreateCommand())
        {
            getId.Transaction = transaction;
            getId.CommandText = "SELECT last_insert_rowid();";
            personId = Convert.ToInt64(await getId.ExecuteScalarAsync(cancellationToken));
        }

        await using (var assign = connection.CreateCommand())
        {
            assign.Transaction = transaction;
            var names = new List<string>(faceIds.Count);
            for (var i = 0; i < faceIds.Count; i++)
            {
                var name = "$f" + i;
                names.Add(name);
                assign.Parameters.AddWithValue(name, faceIds[i]);
            }
            assign.CommandText = $"UPDATE DetectedFaces SET PersonId=$personId WHERE PersonId IS NULL AND IsIgnored=0 AND Id IN ({string.Join(",", names)});";
            assign.Parameters.AddWithValue("$personId", personId);
            await assign.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return personId;
    }

    public async Task<List<PersonGroupItem>> GetPeopleGroupsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PersonGroupItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // Special virtual group for faces not confidently clustered yet.
        await using (var ungrouped = connection.CreateCommand())
        {
            ungrouped.CommandText = """
                SELECT COUNT(*), COUNT(DISTINCT df.FileId),
                       COALESCE((SELECT df2.ThumbnailPath
                                 FROM DetectedFaces df2 JOIN Files f2 ON f2.Id=df2.FileId
                                 WHERE df2.PersonId IS NULL AND df2.IsIgnored=0
                                   AND f2.IsMissing=0 AND f2.IsQuarantined=0 AND f2.IsDeleted=0
                                   AND f2.FaceIndexVersion=2 AND f2.FaceIndexOrientationVersion=1 AND f2.FaceIndexFileSize=f2.FileSize AND f2.FaceIndexLastWriteUtcTicks=f2.LastWriteUtcTicks AND f2.FaceIndexError=''
                                 ORDER BY df2.QualityScore DESC, df2.Id LIMIT 1), '')
                FROM DetectedFaces df JOIN Files f ON f.Id=df.FileId
                WHERE df.PersonId IS NULL AND df.IsIgnored=0
                  AND f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0
                  AND f.FaceIndexVersion=2 AND f.FaceIndexOrientationVersion=1 AND f.FaceIndexFileSize=f.FileSize AND f.FaceIndexLastWriteUtcTicks=f.LastWriteUtcTicks AND f.FaceIndexError='';
                """;
            await using var reader = await ungrouped.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) > 0)
            {
                result.Add(new PersonGroupItem
                {
                    Id = 0,
                    Name = "",
                    FaceCount = checked((int)reader.GetInt64(0)),
                    PhotoCount = checked((int)reader.GetInt64(1)),
                    RepresentativeThumbnailPath = reader.GetString(2)
                });
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT p.Id, p.Name, COUNT(df.Id), COUNT(DISTINCT df.FileId),
                       COALESCE((SELECT df2.ThumbnailPath
                                 FROM DetectedFaces df2 JOIN Files f2 ON f2.Id=df2.FileId
                                 WHERE df2.PersonId=p.Id AND df2.IsIgnored=0
                                   AND f2.IsMissing=0 AND f2.IsQuarantined=0 AND f2.IsDeleted=0
                                   AND f2.FaceIndexVersion=2 AND f2.FaceIndexOrientationVersion=1 AND f2.FaceIndexFileSize=f2.FileSize AND f2.FaceIndexLastWriteUtcTicks=f2.LastWriteUtcTicks AND f2.FaceIndexError=''
                                 ORDER BY CASE WHEN df2.Id=(
                                     SELECT p2.RepresentativeFaceId FROM People p2 WHERE p2.Id=df2.PersonId
                                 ) THEN 0 ELSE 1 END, df2.QualityScore DESC, df2.Id LIMIT 1), ''),
                       CASE WHEN p.RepresentativeFaceId IS NOT NULL AND EXISTS(
                           SELECT 1 FROM DetectedFaces dfr JOIN Files fr ON fr.Id=dfr.FileId
                           WHERE dfr.Id=p.RepresentativeFaceId AND dfr.PersonId=p.Id AND dfr.IsIgnored=0
                             AND fr.IsMissing=0 AND fr.IsQuarantined=0 AND fr.IsDeleted=0
                             AND fr.FaceIndexVersion=2 AND fr.FaceIndexOrientationVersion=1 AND fr.FaceIndexFileSize=fr.FileSize AND fr.FaceIndexLastWriteUtcTicks=fr.LastWriteUtcTicks AND fr.FaceIndexError=''
                       ) THEN p.RepresentativeFaceId ELSE NULL END AS ActiveRepresentativeFaceId
                FROM People p
                JOIN DetectedFaces df ON df.PersonId=p.Id AND df.IsIgnored=0
                JOIN Files f ON f.Id=df.FileId AND f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0
                    AND f.FaceIndexVersion=2 AND f.FaceIndexOrientationVersion=1 AND f.FaceIndexFileSize=f.FileSize AND f.FaceIndexLastWriteUtcTicks=f.LastWriteUtcTicks AND f.FaceIndexError=''
                GROUP BY p.Id, p.Name, p.RepresentativeFaceId
                HAVING COUNT(df.Id)>0
                ORDER BY CASE WHEN TRIM(p.Name)='' THEN 1 ELSE 0 END, p.Name COLLATE NOCASE, COUNT(df.Id) DESC, p.Id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new PersonGroupItem
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    FaceCount = checked((int)reader.GetInt64(2)),
                    PhotoCount = checked((int)reader.GetInt64(3)),
                    RepresentativeThumbnailPath = reader.GetString(4),
                    RepresentativeFaceId = reader.IsDBNull(5) ? null : reader.GetInt64(5)
                });
            }
        }
        return result;
    }

    public async Task<List<FaceItem>> GetFacesForPersonAsync(long personId, int limit = 2000, CancellationToken cancellationToken = default)
    {
        var result = new List<FaceItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT df.Id, df.FileId, df.PersonId, COALESCE(p.Name,''), f.FullPath, f.FileName,
                   df.ThumbnailPath, f.ThumbnailPath, f.CaptureDate,
                   df.X, df.Y, df.Width, df.Height, df.QualityScore, df.IsIgnored
            FROM DetectedFaces df
            JOIN Files f ON f.Id=df.FileId
            LEFT JOIN People p ON p.Id=df.PersonId
            WHERE df.IsIgnored=0 AND f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0
              AND f.FaceIndexVersion=2 AND f.FaceIndexOrientationVersion=1 AND f.FaceIndexFileSize=f.FileSize AND f.FaceIndexLastWriteUtcTicks=f.LastWriteUtcTicks AND f.FaceIndexError=''
              AND (($personId=0 AND df.PersonId IS NULL) OR ($personId<>0 AND df.PersonId=$personId))
            ORDER BY COALESCE(f.CaptureDate,'') DESC, df.QualityScore DESC, df.Id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$personId", personId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FaceItem
            {
                FaceId = reader.GetInt64(0),
                FileId = reader.GetInt64(1),
                PersonId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                PersonName = reader.GetString(3),
                FullPath = reader.GetString(4),
                FileName = reader.GetString(5),
                FaceThumbnailPath = reader.GetString(6),
                PhotoThumbnailPath = reader.GetString(7),
                CaptureDate = reader.IsDBNull(8) ? null : reader.GetString(8),
                X = reader.GetInt32(9),
                Y = reader.GetInt32(10),
                Width = reader.GetInt32(11),
                Height = reader.GetInt32(12),
                QualityScore = reader.GetDouble(13),
                IsIgnored = reader.GetInt32(14) != 0
            });
        }
        return result;
    }

    public async Task SetPersonRepresentativeFaceAsync(long personId, long faceId, CancellationToken cancellationToken = default)
    {
        if (personId <= 0 || faceId <= 0) throw new ArgumentOutOfRangeException();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE People
            SET RepresentativeFaceId=$faceId, IsAuto=0, UpdatedUtc=$utc
            WHERE Id=$personId AND EXISTS(
                SELECT 1 FROM DetectedFaces df
                WHERE df.Id=$faceId AND df.PersonId=$personId AND df.IsIgnored=0
            );
            """;
        command.Parameters.AddWithValue("$personId", personId);
        command.Parameters.AddWithValue("$faceId", faceId);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Выбранное лицо не относится к этой группе.");
    }

    public async Task AssignFaceToPersonAsync(long faceId, long personId, CancellationToken cancellationToken = default)
    {
        if (faceId <= 0) throw new ArgumentOutOfRangeException(nameof(faceId));
        if (personId <= 0) throw new ArgumentOutOfRangeException(nameof(personId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        long? previousPersonId;
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = "SELECT PersonId FROM DetectedFaces WHERE Id=$faceId;";
            previous.Parameters.AddWithValue("$faceId", faceId);
            var value = await previous.ExecuteScalarAsync(cancellationToken);
            if (value is null || value is DBNull)
                previousPersonId = null;
            else
                previousPersonId = Convert.ToInt64(value);
        }

        await using (var clearCover = connection.CreateCommand())
        {
            clearCover.Transaction = transaction;
            clearCover.CommandText = "UPDATE People SET RepresentativeFaceId=NULL, UpdatedUtc=$utc WHERE RepresentativeFaceId=$faceId AND Id<>$target;";
            clearCover.Parameters.AddWithValue("$faceId", faceId);
            clearCover.Parameters.AddWithValue("$target", personId);
            clearCover.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await clearCover.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE DetectedFaces
                SET PersonId=$personId, IsIgnored=0
                WHERE Id=$faceId AND EXISTS(SELECT 1 FROM People WHERE Id=$personId);
                """;
            command.Parameters.AddWithValue("$faceId", faceId);
            command.Parameters.AddWithValue("$personId", personId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Не удалось назначить лицо выбранному человеку.");
        }

        await using (var pin = connection.CreateCommand())
        {
            pin.Transaction = transaction;
            pin.CommandText = previousPersonId.HasValue && previousPersonId.Value > 0
                ? "UPDATE People SET IsAuto=0, UpdatedUtc=$utc WHERE Id=$target OR Id=$source;"
                : "UPDATE People SET IsAuto=0, UpdatedUtc=$utc WHERE Id=$target;";
            pin.Parameters.AddWithValue("$target", personId);
            if (previousPersonId.HasValue && previousPersonId.Value > 0)
                pin.Parameters.AddWithValue("$source", previousPersonId.Value);
            pin.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await pin.ExecuteNonQueryAsync(cancellationToken);
        }

        if (previousPersonId.HasValue && previousPersonId.Value > 0 && previousPersonId.Value != personId)
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM People WHERE Id=$source AND NOT EXISTS(SELECT 1 FROM DetectedFaces WHERE PersonId=$source);";
            cleanup.Parameters.AddWithValue("$source", previousPersonId.Value);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MergePersonGroupsAsync(long sourcePersonId, long targetPersonId, CancellationToken cancellationToken = default)
    {
        if (sourcePersonId <= 0 || targetPersonId <= 0 || sourcePersonId == targetPersonId)
            throw new ArgumentException("Некорректные группы для объединения.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM People WHERE Id IN ($source,$target);";
            check.Parameters.AddWithValue("$source", sourcePersonId);
            check.Parameters.AddWithValue("$target", targetPersonId);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != 2)
                throw new InvalidOperationException("Одна из групп людей больше не существует.");
        }

        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = "UPDATE DetectedFaces SET PersonId=$target WHERE PersonId=$source;";
            move.Parameters.AddWithValue("$source", sourcePersonId);
            move.Parameters.AddWithValue("$target", targetPersonId);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var preserve = connection.CreateCommand())
        {
            preserve.Transaction = transaction;
            preserve.CommandText = """
                UPDATE People
                SET RepresentativeFaceId=COALESCE(RepresentativeFaceId, (SELECT RepresentativeFaceId FROM People WHERE Id=$source)),
                    IsAuto=0,
                    UpdatedUtc=$utc
                WHERE Id=$target;
                """;
            preserve.Parameters.AddWithValue("$source", sourcePersonId);
            preserve.Parameters.AddWithValue("$target", targetPersonId);
            preserve.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await preserve.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM People WHERE Id=$source;";
            delete.Parameters.AddWithValue("$source", sourcePersonId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE People SET UpdatedUtc=$utc WHERE Id=$target;";
            touch.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            touch.Parameters.AddWithValue("$target", targetPersonId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MergePersonGroupsBatchAsync(IEnumerable<long> personIds, long targetPersonId, CancellationToken cancellationToken = default)
    {
        var ids = personIds.Where(x => x > 0).Distinct().ToList();
        if (ids.Count < 2 || targetPersonId <= 0 || !ids.Contains(targetPersonId))
            throw new ArgumentException("Для массового объединения нужно выбрать минимум две существующие группы и одну из них оставить целевой.");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var placeholders = ids.Select((_, i) => "$p" + i).ToArray();
        var inClause = string.Join(",", placeholders);

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = $"SELECT COUNT(*) FROM People WHERE Id IN ({inClause});";
            for (var i = 0; i < ids.Count; i++) check.Parameters.AddWithValue(placeholders[i], ids[i]);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != ids.Count)
                throw new InvalidOperationException("Одна или несколько выбранных групп людей больше не существуют.");
        }

        var sourceIds = ids.Where(x => x != targetPersonId).ToList();
        var sourcePlaceholders = sourceIds.Select((_, i) => "$s" + i).ToArray();
        var sourceClause = string.Join(",", sourcePlaceholders);

        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = $"UPDATE DetectedFaces SET PersonId=$target WHERE PersonId IN ({sourceClause});";
            move.Parameters.AddWithValue("$target", targetPersonId);
            for (var i = 0; i < sourceIds.Count; i++) move.Parameters.AddWithValue(sourcePlaceholders[i], sourceIds[i]);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var preserve = connection.CreateCommand())
        {
            preserve.Transaction = transaction;
            preserve.CommandText = $"""
                UPDATE People
                SET RepresentativeFaceId=COALESCE(
                        RepresentativeFaceId,
                        (SELECT RepresentativeFaceId FROM People WHERE Id IN ({sourceClause}) AND RepresentativeFaceId IS NOT NULL LIMIT 1)),
                    IsAuto=0,
                    UpdatedUtc=$utc
                WHERE Id=$target;
                """;
            preserve.Parameters.AddWithValue("$target", targetPersonId);
            preserve.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            for (var i = 0; i < sourceIds.Count; i++) preserve.Parameters.AddWithValue(sourcePlaceholders[i], sourceIds[i]);
            await preserve.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM People WHERE Id IN ({sourceClause});";
            for (var i = 0; i < sourceIds.Count; i++) delete.Parameters.AddWithValue(sourcePlaceholders[i], sourceIds[i]);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != sourceIds.Count)
                throw new InvalidOperationException("Не удалось удалить все исходные группы после объединения.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> DeletePersonGroupsAsync(IEnumerable<long> personIds, CancellationToken cancellationToken = default)
    {
        var ids = personIds.Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0) return 0;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var placeholders = ids.Select((_, i) => "$p" + i).ToArray();
        var inClause = string.Join(",", placeholders);

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = $"SELECT COUNT(*) FROM People WHERE Id IN ({inClause});";
            for (var i = 0; i < ids.Count; i++) check.Parameters.AddWithValue(placeholders[i], ids[i]);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != ids.Count)
                throw new InvalidOperationException("Одна или несколько выбранных групп людей больше не существуют.");
        }

        int faceCount;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = $"SELECT COUNT(*) FROM DetectedFaces WHERE PersonId IN ({inClause});";
            for (var i = 0; i < ids.Count; i++) count.Parameters.AddWithValue(placeholders[i], ids[i]);
            faceCount = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }

        await using (var unassign = connection.CreateCommand())
        {
            unassign.Transaction = transaction;
            unassign.CommandText = $"UPDATE DetectedFaces SET PersonId=NULL WHERE PersonId IN ({inClause});";
            for (var i = 0; i < ids.Count; i++) unassign.Parameters.AddWithValue(placeholders[i], ids[i]);
            await unassign.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM People WHERE Id IN ({inClause});";
            for (var i = 0; i < ids.Count; i++) delete.Parameters.AddWithValue(placeholders[i], ids[i]);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != ids.Count)
                throw new InvalidOperationException("Не удалось расформировать все выбранные группы.");
        }

        await transaction.CommitAsync(cancellationToken);
        return faceCount;
    }

    public async Task<int> DeletePersonGroupAsync(long personId, CancellationToken cancellationToken = default)
    {
        if (personId <= 0) throw new ArgumentOutOfRangeException(nameof(personId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        int faceCount;
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM DetectedFaces WHERE PersonId=$id;";
            check.Parameters.AddWithValue("$id", personId);
            faceCount = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken));
        }

        await using (var unassign = connection.CreateCommand())
        {
            unassign.Transaction = transaction;
            unassign.CommandText = "UPDATE DetectedFaces SET PersonId=NULL WHERE PersonId=$id;";
            unassign.Parameters.AddWithValue("$id", personId);
            await unassign.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM People WHERE Id=$id;";
            delete.Parameters.AddWithValue("$id", personId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Группа человека больше не существует.");
        }

        await transaction.CommitAsync(cancellationToken);
        return faceCount;
    }

    public async Task RenamePersonAsync(long personId, string name, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (personId <= 0) throw new ArgumentOutOfRangeException(nameof(personId));
        if (name.Length > 100) name = name[..100];
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE People SET Name=$name, IsAuto=0, UpdatedUtc=$utc WHERE Id=$id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", personId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Группа человека не найдена.");
    }

    public async Task RemoveFaceFromPersonAsync(long faceId, CancellationToken cancellationToken = default)
    {
        if (faceId <= 0) throw new ArgumentOutOfRangeException(nameof(faceId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        long? previousPersonId;
        await using (var previous = connection.CreateCommand())
        {
            previous.Transaction = transaction;
            previous.CommandText = "SELECT PersonId FROM DetectedFaces WHERE Id=$id;";
            previous.Parameters.AddWithValue("$id", faceId);
            var value = await previous.ExecuteScalarAsync(cancellationToken);
            previousPersonId = value is null || value is DBNull ? null : Convert.ToInt64(value);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE People SET RepresentativeFaceId=NULL, IsAuto=0, UpdatedUtc=$utc WHERE RepresentativeFaceId=$id;
                UPDATE DetectedFaces SET PersonId=NULL WHERE Id=$id;
                """;
            update.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", faceId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (previousPersonId.HasValue && previousPersonId.Value > 0)
        {
            await using var pinOrCleanup = connection.CreateCommand();
            pinOrCleanup.Transaction = transaction;
            pinOrCleanup.CommandText = """
                UPDATE People SET IsAuto=0, UpdatedUtc=$utc
                WHERE Id=$source AND EXISTS(SELECT 1 FROM DetectedFaces WHERE PersonId=$source);
                DELETE FROM People WHERE Id=$source AND NOT EXISTS(SELECT 1 FROM DetectedFaces WHERE PersonId=$source);
                """;
            pinOrCleanup.Parameters.AddWithValue("$source", previousPersonId.Value);
            pinOrCleanup.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await pinOrCleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task IgnoreFaceAsync(long faceId, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE People SET RepresentativeFaceId=NULL, UpdatedUtc=$utc WHERE RepresentativeFaceId=$id;
            UPDATE DetectedFaces SET IsIgnored=1, PersonId=NULL WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", faceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> IgnorePersonGroupAsync(long personId, string scopeLabel, CancellationToken cancellationToken = default)
    {
        if (personId < 0) throw new ArgumentOutOfRangeException(nameof(personId));
        scopeLabel = string.IsNullOrWhiteSpace(scopeLabel) ? (personId == 0 ? "Без группы" : $"Человек #{personId}") : scopeLabel.Trim();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        string personName = "";
        var personIsAuto = true;
        if (personId > 0)
        {
            await using var person = connection.CreateCommand();
            person.Transaction = transaction;
            person.CommandText = "SELECT Name, IsAuto FROM People WHERE Id=$id;";
            person.Parameters.AddWithValue("$id", personId);
            await using var reader = await person.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Группа человека больше не существует.");
            personName = reader.GetString(0);
            personIsAuto = reader.GetInt32(1) != 0;
        }

        long actionId;
        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                INSERT INTO FaceIgnoreActions(ScopeType,ScopeLabel,PersonId,PersonName,PersonIsAuto,CreatedUtc)
                VALUES('group',$label,$personId,$name,$auto,$utc);
                SELECT last_insert_rowid();
                """;
            action.Parameters.AddWithValue("$label", scopeLabel);
            action.Parameters.AddWithValue("$personId", personId > 0 ? (object)personId : DBNull.Value);
            action.Parameters.AddWithValue("$name", personName);
            action.Parameters.AddWithValue("$auto", personIsAuto ? 1 : 0);
            action.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            actionId = Convert.ToInt64(await action.ExecuteScalarAsync(cancellationToken));
        }

        await using (var snapshot = connection.CreateCommand())
        {
            snapshot.Transaction = transaction;
            snapshot.CommandText = personId == 0
                ? "INSERT INTO FaceIgnoreActionItems(ActionId,FaceId,PreviousPersonId) SELECT $action,Id,PersonId FROM DetectedFaces WHERE PersonId IS NULL AND IsIgnored=0;"
                : "INSERT INTO FaceIgnoreActionItems(ActionId,FaceId,PreviousPersonId) SELECT $action,Id,PersonId FROM DetectedFaces WHERE PersonId=$personId AND IsIgnored=0;";
            snapshot.Parameters.AddWithValue("$action", actionId);
            snapshot.Parameters.AddWithValue("$personId", personId);
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }

        int count;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.Transaction = transaction;
            countCmd.CommandText = "SELECT COUNT(*) FROM FaceIgnoreActionItems WHERE ActionId=$action;";
            countCmd.Parameters.AddWithValue("$action", actionId);
            count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }
        if (count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        await using (var ignore = connection.CreateCommand())
        {
            ignore.Transaction = transaction;
            ignore.CommandText = "UPDATE DetectedFaces SET IsIgnored=1, PersonId=NULL WHERE Id IN (SELECT FaceId FROM FaceIgnoreActionItems WHERE ActionId=$action);";
            ignore.Parameters.AddWithValue("$action", actionId);
            await ignore.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<(int FacesIgnored, int ActionsCreated)> IgnorePersonGroupsBatchAsync(
        IEnumerable<(long PersonId, string ScopeLabel)> groups,
        CancellationToken cancellationToken = default)
    {
        var items = groups
            .Where(x => x.PersonId >= 0)
            .GroupBy(x => x.PersonId)
            .Select(x => (PersonId: x.Key, ScopeLabel: x.Last().ScopeLabel))
            .ToList();
        if (items.Count == 0) return (0, 0);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var totalFaces = 0;
        var actionCount = 0;

        foreach (var item in items)
        {
            var personId = item.PersonId;
            var scopeLabel = string.IsNullOrWhiteSpace(item.ScopeLabel)
                ? (personId == 0 ? "Без группы" : $"Человек #{personId}")
                : item.ScopeLabel.Trim();
            string personName = "";
            var personIsAuto = true;

            if (personId > 0)
            {
                await using var person = connection.CreateCommand();
                person.Transaction = transaction;
                person.CommandText = "SELECT Name, IsAuto FROM People WHERE Id=$id;";
                person.Parameters.AddWithValue("$id", personId);
                await using var reader = await person.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException($"Группа человека #{personId} больше не существует.");
                personName = reader.GetString(0);
                personIsAuto = reader.GetInt32(1) != 0;
            }

            long actionId;
            await using (var action = connection.CreateCommand())
            {
                action.Transaction = transaction;
                action.CommandText = """
                    INSERT INTO FaceIgnoreActions(ScopeType,ScopeLabel,PersonId,PersonName,PersonIsAuto,CreatedUtc)
                    VALUES('group',$label,$personId,$name,$auto,$utc);
                    SELECT last_insert_rowid();
                    """;
                action.Parameters.AddWithValue("$label", scopeLabel);
                action.Parameters.AddWithValue("$personId", personId > 0 ? (object)personId : DBNull.Value);
                action.Parameters.AddWithValue("$name", personName);
                action.Parameters.AddWithValue("$auto", personIsAuto ? 1 : 0);
                action.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                actionId = Convert.ToInt64(await action.ExecuteScalarAsync(cancellationToken));
            }

            await using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = transaction;
                snapshot.CommandText = personId == 0
                    ? "INSERT INTO FaceIgnoreActionItems(ActionId,FaceId,PreviousPersonId) SELECT $action,Id,PersonId FROM DetectedFaces WHERE PersonId IS NULL AND IsIgnored=0;"
                    : "INSERT INTO FaceIgnoreActionItems(ActionId,FaceId,PreviousPersonId) SELECT $action,Id,PersonId FROM DetectedFaces WHERE PersonId=$personId AND IsIgnored=0;";
                snapshot.Parameters.AddWithValue("$action", actionId);
                snapshot.Parameters.AddWithValue("$personId", personId);
                await snapshot.ExecuteNonQueryAsync(cancellationToken);
            }

            int count;
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.Transaction = transaction;
                countCmd.CommandText = "SELECT COUNT(*) FROM FaceIgnoreActionItems WHERE ActionId=$action;";
                countCmd.Parameters.AddWithValue("$action", actionId);
                count = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
            }

            if (count == 0)
            {
                await using var deleteEmpty = connection.CreateCommand();
                deleteEmpty.Transaction = transaction;
                deleteEmpty.CommandText = "DELETE FROM FaceIgnoreActions WHERE Id=$action;";
                deleteEmpty.Parameters.AddWithValue("$action", actionId);
                await deleteEmpty.ExecuteNonQueryAsync(cancellationToken);
                continue;
            }

            await using (var ignore = connection.CreateCommand())
            {
                ignore.Transaction = transaction;
                ignore.CommandText = "UPDATE DetectedFaces SET IsIgnored=1, PersonId=NULL WHERE Id IN (SELECT FaceId FROM FaceIgnoreActionItems WHERE ActionId=$action);";
                ignore.Parameters.AddWithValue("$action", actionId);
                await ignore.ExecuteNonQueryAsync(cancellationToken);
            }

            totalFaces += count;
            actionCount++;
        }

        await transaction.CommitAsync(cancellationToken);
        return (totalFaces, actionCount);
    }

    public async Task<int> IgnoreFaceWithUndoAsync(long faceId, string scopeLabel, CancellationToken cancellationToken = default)
    {
        if (faceId <= 0) throw new ArgumentOutOfRangeException(nameof(faceId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        long? previousPersonId = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT PersonId FROM DetectedFaces WHERE Id=$id AND IsIgnored=0;";
            read.Parameters.AddWithValue("$id", faceId);
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is null) { await transaction.RollbackAsync(cancellationToken); return 0; }
            if (value != DBNull.Value) previousPersonId = Convert.ToInt64(value);
        }
        long actionId;
        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = "INSERT INTO FaceIgnoreActions(ScopeType,ScopeLabel,PersonId,PersonName,PersonIsAuto,CreatedUtc) VALUES('face',$label,NULL,'',1,$utc); SELECT last_insert_rowid();";
            action.Parameters.AddWithValue("$label", string.IsNullOrWhiteSpace(scopeLabel) ? "Одно обнаружение лица" : scopeLabel.Trim());
            action.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            actionId = Convert.ToInt64(await action.ExecuteScalarAsync(cancellationToken));
        }
        await using (var item = connection.CreateCommand())
        {
            item.Transaction = transaction;
            item.CommandText = "INSERT INTO FaceIgnoreActionItems(ActionId,FaceId,PreviousPersonId) VALUES($action,$face,$person);";
            item.Parameters.AddWithValue("$action", actionId);
            item.Parameters.AddWithValue("$face", faceId);
            item.Parameters.AddWithValue("$person", previousPersonId.HasValue ? (object)previousPersonId.Value : DBNull.Value);
            await item.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var ignore = connection.CreateCommand())
        {
            ignore.Transaction = transaction;
            ignore.CommandText = "UPDATE DetectedFaces SET IsIgnored=1, PersonId=NULL WHERE Id=$id;";
            ignore.Parameters.AddWithValue("$id", faceId);
            await ignore.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return 1;
    }

    public async Task<FaceIgnoreActionItem?> GetLatestActiveFaceIgnoreActionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id,a.ScopeType,a.ScopeLabel,a.CreatedUtc,a.UndoneUtc,COUNT(i.FaceId)
            FROM FaceIgnoreActions a JOIN FaceIgnoreActionItems i ON i.ActionId=a.Id
            WHERE a.UndoneUtc IS NULL
            GROUP BY a.Id
            ORDER BY a.Id DESC LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new FaceIgnoreActionItem
        {
            Id = reader.GetInt64(0), ScopeType = reader.GetString(1), ScopeLabel = reader.GetString(2), CreatedUtc = reader.GetString(3),
            UndoneUtc = reader.IsDBNull(4) ? null : reader.GetString(4), FaceCount = reader.GetInt32(5)
        };
    }

    public async Task<int> UndoFaceIgnoreActionAsync(long actionId, CancellationToken cancellationToken = default)
    {
        if (actionId <= 0) throw new ArgumentOutOfRangeException(nameof(actionId));
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        long? personId = null;
        string personName = "";
        var personIsAuto = true;
        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = "SELECT PersonId,PersonName,PersonIsAuto FROM FaceIgnoreActions WHERE Id=$id AND UndoneUtc IS NULL;";
            action.Parameters.AddWithValue("$id", actionId);
            await using var reader = await action.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Эта операция исключения уже восстановлена или не существует.");
            personId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            personName = reader.GetString(1);
            personIsAuto = reader.GetInt32(2) != 0;
        }
        if (personId.HasValue)
        {
            await using var restorePerson = connection.CreateCommand();
            restorePerson.Transaction = transaction;
            restorePerson.CommandText = "INSERT OR IGNORE INTO People(Id,Name,IsAuto,CreatedUtc,UpdatedUtc) VALUES($id,$name,$auto,$utc,$utc);";
            restorePerson.Parameters.AddWithValue("$id", personId.Value);
            restorePerson.Parameters.AddWithValue("$name", personName);
            restorePerson.Parameters.AddWithValue("$auto", personIsAuto ? 1 : 0);
            restorePerson.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await restorePerson.ExecuteNonQueryAsync(cancellationToken);
        }

        int restored;
        await using (var restore = connection.CreateCommand())
        {
            restore.Transaction = transaction;
            restore.CommandText = """
                UPDATE DetectedFaces
                SET IsIgnored=0,
                    PersonId=(
                        SELECT CASE
                            WHEN i.PreviousPersonId IS NULL THEN NULL
                            WHEN EXISTS(SELECT 1 FROM People p WHERE p.Id=i.PreviousPersonId) THEN i.PreviousPersonId
                            ELSE NULL
                        END
                        FROM FaceIgnoreActionItems i
                        WHERE i.ActionId=$action AND i.FaceId=DetectedFaces.Id
                    )
                WHERE Id IN (SELECT FaceId FROM FaceIgnoreActionItems WHERE ActionId=$action);
                """;
            restore.Parameters.AddWithValue("$action", actionId);
            restored = await restore.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var finish = connection.CreateCommand())
        {
            finish.Transaction = transaction;
            finish.CommandText = "UPDATE FaceIgnoreActions SET UndoneUtc=$utc WHERE Id=$id;";
            finish.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            finish.Parameters.AddWithValue("$id", actionId);
            await finish.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return restored;
    }

    public async Task<List<EventMetadataCandidate>> GetEventMetadataCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<EventMetadataCandidate>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, FullPath, FileSize, LastWriteUtcTicks,
                   GpsIndexVersion, GpsIndexFileSize, GpsIndexLastWriteUtcTicks
            FROM Files
            WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0
              AND (GpsIndexVersion<>1 OR GpsIndexFileSize<>FileSize OR GpsIndexLastWriteUtcTicks<>LastWriteUtcTicks)
            ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EventMetadataCandidate
            {
                FileId = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                FileSize = reader.GetInt64(2),
                LastWriteUtcTicks = reader.GetInt64(3),
                GpsIndexVersion = reader.GetInt32(4),
                GpsIndexFileSize = reader.GetInt64(5),
                GpsIndexLastWriteUtcTicks = reader.GetInt64(6)
            });
        }
        return result;
    }

    public async Task UpdateGpsMetadataAsync(
        SqliteConnection connection,
        long fileId,
        long fileSize,
        long lastWriteUtcTicks,
        double? latitude,
        double? longitude,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Files
            SET GpsLatitude=$lat,
                GpsLongitude=$lon,
                GpsIndexVersion=1,
                GpsIndexFileSize=$size,
                GpsIndexLastWriteUtcTicks=$ticks,
                GpsIndexError=$error,
                GpsIndexedUtc=$utc
            WHERE Id=$id AND FileSize=$size AND LastWriteUtcTicks=$ticks
              AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0;
            """;
        command.Parameters.AddWithValue("$lat", (object?)latitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$lon", (object?)longitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", fileSize);
        command.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
        command.Parameters.AddWithValue("$error", error ?? "");
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", fileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetManualCaptureDateAsync(long fileId, DateTime captureDate, CancellationToken cancellationToken = default)
    {
        if (fileId <= 0) throw new ArgumentOutOfRangeException(nameof(fileId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Files
                SET CaptureDate=$date, CaptureDateSource=$source, EffectiveYear=$year
                WHERE Id=$id AND IsDeleted=0;
                """;
            update.Parameters.AddWithValue("$date", FormatStoredDateTime(captureDate));
            update.Parameters.AddWithValue("$source", CaptureDatePolicy.ManualCatalog);
            update.Parameters.AddWithValue("$year", captureDate.Year);
            update.Parameters.AddWithValue("$id", fileId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Фотография больше не доступна в каталоге.");
        }

        await DetachFromAutomaticEventAsync(connection, transaction, fileId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> SetManualCaptureDatesAsync(
        IReadOnlyCollection<long> fileIds,
        DateTime chosenDate,
        bool preserveExistingTime,
        CancellationToken cancellationToken = default)
    {
        var ids = fileIds?.Where(x => x > 0).Distinct().ToArray() ?? [];
        if (ids.Length == 0) return 0;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var updated = 0;
        var now = DateTime.UtcNow.ToString("O");

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT CaptureDate FROM Files WHERE Id=$id AND IsDeleted=0;";
        var readId = read.Parameters.Add("$id", SqliteType.Integer);

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE Files SET CaptureDate=$date,CaptureDateSource=$source,EffectiveYear=$year WHERE Id=$id AND IsDeleted=0;";
        var updateDate = update.Parameters.Add("$date", SqliteType.Text);
        update.Parameters.AddWithValue("$source", CaptureDatePolicy.ManualCatalog);
        var updateYear = update.Parameters.Add("$year", SqliteType.Integer);
        var updateId = update.Parameters.Add("$id", SqliteType.Integer);

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readId.Value = id;
            var currentValue = await read.ExecuteScalarAsync(cancellationToken);
            if (currentValue is null) continue;
            var effective = chosenDate;
            if (preserveExistingTime && currentValue != DBNull.Value && TryParseStoredDateTime(Convert.ToString(currentValue), out var currentDate))
                effective = chosenDate.Date + currentDate.TimeOfDay;
            updateDate.Value = FormatStoredDateTime(effective);
            updateYear.Value = effective.Year;
            updateId.Value = id;
            updated += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (updated > 0)
        {
            const int batchSize = 850;
            for (var offset = 0; offset < ids.Length; offset += batchSize)
            {
                var batch = ids.Skip(offset).Take(batchSize).ToArray();
                await using var unlink = connection.CreateCommand();
                unlink.Transaction = transaction;
                var names = new string[batch.Length];
                for (var i = 0; i < batch.Length; i++)
                {
                    names[i] = "$f" + i;
                    unlink.Parameters.AddWithValue(names[i], batch[i]);
                }
                unlink.CommandText = $"DELETE FROM EventFiles WHERE FileId IN ({string.Join(',', names)}) AND EventId IN (SELECT Id FROM Events WHERE IsAuto=1);";
                await unlink.ExecuteNonQueryAsync(cancellationToken);
            }
            await RefreshAllEventAggregatesAsync(connection, transaction, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> ResetManualCaptureDateAsync(long fileId, CancellationToken cancellationToken = default)
    {
        if (fileId <= 0) throw new ArgumentOutOfRangeException(nameof(fileId));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        string? autoDate;
        string autoSource;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT CaptureDateSource, AutoCaptureDate, AutoCaptureDateSource FROM Files WHERE Id=$id AND IsDeleted=0;";
            read.Parameters.AddWithValue("$id", fileId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Фотография больше не доступна в каталоге.");
            if (!CaptureDatePolicy.IsManual(reader.GetString(0))) return false;
            autoDate = reader.IsDBNull(1) ? null : reader.GetString(1);
            autoSource = reader.GetString(2);
        }

        var year = TryParseStoredDateTime(autoDate, out var parsed) ? parsed.Year : 0;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Files
                SET CaptureDate=$date, CaptureDateSource=$source, EffectiveYear=$year
                WHERE Id=$id;
                """;
            update.Parameters.AddWithValue("$date", (object?)autoDate ?? DBNull.Value);
            update.Parameters.AddWithValue("$source", autoSource ?? "");
            update.Parameters.AddWithValue("$year", year);
            update.Parameters.AddWithValue("$id", fileId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await DetachFromAutomaticEventAsync(connection, transaction, fileId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task DetachFromAutomaticEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long fileId,
        CancellationToken cancellationToken)
    {
        await using (var unlink = connection.CreateCommand())
        {
            unlink.Transaction = transaction;
            unlink.CommandText = """
                DELETE FROM EventFiles
                WHERE FileId=$fileId AND EventId IN (SELECT Id FROM Events WHERE IsAuto=1);
                """;
            unlink.Parameters.AddWithValue("$fileId", fileId);
            await unlink.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var refresh = connection.CreateCommand())
        {
            refresh.Transaction = transaction;
            refresh.CommandText = """
                UPDATE Events
                SET StartDate=COALESCE((SELECT MIN(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id), StartDate),
                    EndDate=COALESCE((SELECT MAX(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id), EndDate),
                    CenterLatitude=(SELECT AVG(f.GpsLatitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                    CenterLongitude=(SELECT AVG(f.GpsLongitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                    UpdatedUtc=$utc
                WHERE EXISTS(SELECT 1 FROM EventFiles ef WHERE ef.EventId=Events.Id);
                DELETE FROM Events WHERE IsAuto=1 AND NOT EXISTS(SELECT 1 FROM EventFiles ef WHERE ef.EventId=Events.Id);
                """;
            refresh.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task RefreshAllEventAggregatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string utc,
        CancellationToken cancellationToken)
    {
        await using var refresh = connection.CreateCommand();
        refresh.Transaction = transaction;
        refresh.CommandText = """
            UPDATE Events
            SET StartDate=COALESCE((SELECT MIN(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id), StartDate),
                EndDate=COALESCE((SELECT MAX(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id), EndDate),
                CenterLatitude=(SELECT AVG(f.GpsLatitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                CenterLongitude=(SELECT AVG(f.GpsLongitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=Events.Id AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                UpdatedUtc=$utc
            WHERE EXISTS(SELECT 1 FROM EventFiles ef WHERE ef.EventId=Events.Id);
            DELETE FROM Events WHERE IsAuto=1 AND NOT EXISTS(SELECT 1 FROM EventFiles ef WHERE ef.EventId=Events.Id);
            """;
        refresh.Parameters.AddWithValue("$utc", utc);
        await refresh.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<EventCandidate>> GetEventCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<EventCandidate>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.Id, f.FullPath, f.FileName, f.ThumbnailPath, f.CaptureDate, f.CaptureDateSource,
                   f.GpsLatitude, f.GpsLongitude,
                   COALESCE((
                       SELECT GROUP_CONCAT(PersonId, ',') FROM (
                           SELECT DISTINCT p.Id AS PersonId
                           FROM DetectedFaces df
                           JOIN People p ON p.Id=df.PersonId
                           WHERE df.FileId=f.Id AND df.IsIgnored=0 AND p.Name<>''
                             AND f.FaceIndexVersion=2 AND f.FaceIndexOrientationVersion=1 AND f.FaceIndexFileSize=f.FileSize AND f.FaceIndexLastWriteUtcTicks=f.LastWriteUtcTicks AND f.FaceIndexError=''
                           ORDER BY p.Id
                       )
                   ), ''),
                   COALESCE((
                       SELECT GROUP_CONCAT(PersonName, '|||') FROM (
                           SELECT DISTINCT p.Name AS PersonName
                           FROM DetectedFaces df
                           JOIN People p ON p.Id=df.PersonId
                           WHERE df.FileId=f.Id AND df.IsIgnored=0 AND p.Name<>''
                             AND f.FaceIndexVersion=2 AND f.FaceIndexOrientationVersion=1 AND f.FaceIndexFileSize=f.FileSize AND f.FaceIndexLastWriteUtcTicks=f.LastWriteUtcTicks AND f.FaceIndexError=''
                           ORDER BY p.Name COLLATE NOCASE
                       )
                   ), '')
            FROM Files f
            WHERE f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0
              AND f.CaptureDate IS NOT NULL AND (f.CaptureDateSource LIKE 'EXIF%' OR f.CaptureDateSource=$manualDateSource)
              AND NOT EXISTS (
                  SELECT 1 FROM EventFiles ef
                  JOIN Events e ON e.Id=ef.EventId
                  WHERE ef.FileId=f.Id AND e.IsAuto=0
              )
            ORDER BY f.CaptureDate, f.Id;
            """;
        command.Parameters.AddWithValue("$manualDateSource", CaptureDatePolicy.ManualCatalog);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryParseStoredDateTime(reader.GetString(4), out var captureDate))
            {
                LoggingService.Warn("Event candidate skipped because CaptureDate has an invalid storage format: fileId=" + reader.GetInt64(0));
                continue;
            }

            var ids = new HashSet<long>();
            var idText = reader.GetString(8);
            foreach (var token in idText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (long.TryParse(token, out var id)) ids.Add(id);

            var names = reader.GetString(9)
                .Split("|||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            result.Add(new EventCandidate
            {
                FileId = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                FileName = reader.GetString(2),
                ThumbnailPath = reader.GetString(3),
                CaptureDate = captureDate,
                CaptureDateSource = reader.GetString(5),
                Latitude = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                Longitude = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                PersonIds = ids,
                PersonNames = names
            });
        }
        return result;
    }

    public async Task<int> CountPhotosWithoutReliableCaptureDateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM Files
            WHERE IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0
              AND (CaptureDate IS NULL OR (CaptureDateSource NOT LIKE 'EXIF%' AND CaptureDateSource<>$manualDateSource));
            """;
        command.Parameters.AddWithValue("$manualDateSource", CaptureDatePolicy.ManualCatalog);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<int> CountPhotosInManualEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(DISTINCT ef.FileId)
            FROM EventFiles ef
            JOIN Events e ON e.Id=ef.EventId
            JOIN Files f ON f.Id=ef.FileId
            WHERE e.IsAuto=0 AND f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0;
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<(int EventsCreated, int PhotosAssigned)> ReplaceAutomaticEventsAsync(
        IReadOnlyList<EventDraft> drafts,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var clearLinks = connection.CreateCommand())
        {
            clearLinks.Transaction = transaction;
            clearLinks.CommandText = "DELETE FROM EventFiles WHERE EventId IN (SELECT Id FROM Events WHERE IsAuto=1);";
            await clearLinks.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clearEvents = connection.CreateCommand())
        {
            clearEvents.Transaction = transaction;
            clearEvents.CommandText = "DELETE FROM Events WHERE IsAuto=1;";
            await clearEvents.ExecuteNonQueryAsync(cancellationToken);
        }

        var created = 0;
        var assigned = 0;
        foreach (var draft in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (draft.FileIds.Count == 0) continue;

            long eventId;
            await using (var insertEvent = connection.CreateCommand())
            {
                insertEvent.Transaction = transaction;
                insertEvent.CommandText = """
                    INSERT INTO Events(Name, StartDate, EndDate, IsAuto, Confidence, CenterLatitude, CenterLongitude, CreatedUtc, UpdatedUtc)
                    VALUES($name,$start,$end,1,$confidence,$lat,$lon,$utc,$utc);
                    SELECT last_insert_rowid();
                    """;
                insertEvent.Parameters.AddWithValue("$name", draft.Name);
                insertEvent.Parameters.AddWithValue("$start", FormatStoredDateTime(draft.StartDate));
                insertEvent.Parameters.AddWithValue("$end", FormatStoredDateTime(draft.EndDate));
                insertEvent.Parameters.AddWithValue("$confidence", draft.Confidence);
                insertEvent.Parameters.AddWithValue("$lat", (object?)draft.CenterLatitude ?? DBNull.Value);
                insertEvent.Parameters.AddWithValue("$lon", (object?)draft.CenterLongitude ?? DBNull.Value);
                insertEvent.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                eventId = Convert.ToInt64(await insertEvent.ExecuteScalarAsync(cancellationToken));
            }

            foreach (var fileId in draft.FileIds.Distinct())
            {
                await using var link = connection.CreateCommand();
                link.Transaction = transaction;
                link.CommandText = """
                    INSERT OR IGNORE INTO EventFiles(EventId, FileId)
                    SELECT $eventId, $fileId
                    WHERE NOT EXISTS (
                        SELECT 1 FROM EventFiles ef2 JOIN Events e2 ON e2.Id=ef2.EventId
                        WHERE ef2.FileId=$fileId AND e2.IsAuto=0
                    );
                    """;
                link.Parameters.AddWithValue("$eventId", eventId);
                link.Parameters.AddWithValue("$fileId", fileId);
                assigned += await link.ExecuteNonQueryAsync(cancellationToken);
            }
            created++;
        }

        await transaction.CommitAsync(cancellationToken);
        return (created, assigned);
    }

    public async Task<List<EventGroupItem>> GetEventGroupsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<EventGroupItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.Id, e.Name, e.StartDate, e.EndDate, e.IsAuto, e.Confidence,
                   e.CenterLatitude, e.CenterLongitude,
                   COUNT(ef.FileId) AS PhotoCount,
                   COALESCE((
                       SELECT GROUP_CONCAT(PersonName, ', ') FROM (
                           SELECT DISTINCT p.Name AS PersonName
                           FROM EventFiles ef2
                           JOIN DetectedFaces df ON df.FileId=ef2.FileId AND df.IsIgnored=0
                           JOIN People p ON p.Id=df.PersonId
                           JOIN Files f2 ON f2.Id=ef2.FileId
                           WHERE ef2.EventId=e.Id AND p.Name<>''
                             AND f2.FaceIndexVersion=2 AND f2.FaceIndexOrientationVersion=1 AND f2.FaceIndexFileSize=f2.FileSize AND f2.FaceIndexLastWriteUtcTicks=f2.LastWriteUtcTicks AND f2.FaceIndexError=''
                           ORDER BY p.Name COLLATE NOCASE
                           LIMIT 8
                       )
                   ), '') AS PeopleNames,
                   COALESCE((
                       SELECT f3.ThumbnailPath
                       FROM EventFiles ef3 JOIN Files f3 ON f3.Id=ef3.FileId
                       WHERE ef3.EventId=e.Id AND f3.IsMissing=0 AND f3.IsQuarantined=0 AND f3.IsDeleted=0
                       ORDER BY CASE WHEN f3.Id=(
                                    SELECT e3.RepresentativeFileId FROM Events e3 WHERE e3.Id=ef3.EventId
                                ) THEN 0 ELSE 1 END,
                                CASE WHEN f3.QualityScore>=0 THEN 0 ELSE 1 END,
                                f3.QualityScore DESC, COALESCE(f3.CaptureDate,'') ASC, f3.Id ASC LIMIT 1
                   ), '') AS RepresentativeThumbnailPath,
                   CASE WHEN e.RepresentativeFileId IS NOT NULL AND EXISTS(
                       SELECT 1 FROM EventFiles efr JOIN Files fr ON fr.Id=efr.FileId
                       WHERE efr.EventId=e.Id AND efr.FileId=e.RepresentativeFileId
                         AND fr.IsMissing=0 AND fr.IsQuarantined=0 AND fr.IsDeleted=0
                   ) THEN e.RepresentativeFileId ELSE NULL END AS ActiveRepresentativeFileId,
                   e.Notes
            FROM Events e
            JOIN EventFiles ef ON ef.EventId=e.Id
            JOIN Files f ON f.Id=ef.FileId
            WHERE f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0
            GROUP BY e.Id
            HAVING COUNT(ef.FileId)>0
            ORDER BY e.StartDate DESC, e.Id DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryParseStoredDateTime(reader.GetString(2), out var start)) continue;
            if (!TryParseStoredDateTime(reader.GetString(3), out var end)) end = start;
            result.Add(new EventGroupItem
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                StartDate = start,
                EndDate = end,
                IsAuto = reader.GetInt32(4) != 0,
                Confidence = reader.GetDouble(5),
                CenterLatitude = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                CenterLongitude = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                PhotoCount = checked((int)reader.GetInt64(8)),
                PeopleNames = reader.GetString(9),
                RepresentativeThumbnailPath = reader.GetString(10),
                RepresentativeFileId = reader.IsDBNull(11) ? null : reader.GetInt64(11),
                Notes = reader.GetString(12)
            });
        }
        return result;
    }

    public async Task SetEventRepresentativePhotoAsync(long eventId, long fileId, CancellationToken cancellationToken = default)
    {
        if (eventId <= 0 || fileId <= 0) throw new ArgumentOutOfRangeException();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Events SET RepresentativeFileId=$fileId, IsAuto=0, UpdatedUtc=$utc
            WHERE Id=$eventId AND EXISTS(SELECT 1 FROM EventFiles WHERE EventId=$eventId AND FileId=$fileId);
            """;
        command.Parameters.AddWithValue("$eventId", eventId);
        command.Parameters.AddWithValue("$fileId", fileId);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Выбранное фото не относится к событию.");
    }

    public async Task SetEventNotesAsync(long eventId, string notes, CancellationToken cancellationToken = default)
    {
        if (eventId <= 0) throw new ArgumentOutOfRangeException(nameof(eventId));
        notes = (notes ?? "").Trim();
        if (notes.Length > 2000) notes = notes[..2000];
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Events SET Notes=$notes, IsAuto=0, UpdatedUtc=$utc WHERE Id=$id;";
        command.Parameters.AddWithValue("$notes", notes);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", eventId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Событие не найдено.");
    }

    public async Task<long> SplitEventAsync(long eventId, long splitFileId, string newEventName, CancellationToken cancellationToken = default)
    {
        if (eventId <= 0 || splitFileId <= 0) throw new ArgumentOutOfRangeException();
        newEventName = (newEventName ?? "").Trim();
        if (newEventName.Length > 160) newEventName = newEventName[..160];

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        string splitDate;
        await using (var split = connection.CreateCommand())
        {
            split.Transaction = transaction;
            split.CommandText = """
                SELECT f.CaptureDate FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId
                WHERE ef.EventId=$eventId AND ef.FileId=$fileId;
                """;
            split.Parameters.AddWithValue("$eventId", eventId);
            split.Parameters.AddWithValue("$fileId", splitFileId);
            var value = await split.ExecuteScalarAsync(cancellationToken);
            if (value is null || value is DBNull || string.IsNullOrWhiteSpace(Convert.ToString(value)))
                throw new InvalidOperationException("У выбранного кадра нет даты, поэтому использовать его как точку разделения нельзя.");
            splitDate = Convert.ToString(value)!;
        }

        var sourceCount = 0L;
        var moveCount = 0L;
        await using (var counts = connection.CreateCommand())
        {
            counts.Transaction = transaction;
            counts.CommandText = """
                SELECT COUNT(*), SUM(CASE WHEN COALESCE(f.CaptureDate,'')>$splitDate OR (f.CaptureDate=$splitDate AND f.Id>=$splitId) THEN 1 ELSE 0 END)
                FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$eventId;
                """;
            counts.Parameters.AddWithValue("$eventId", eventId);
            counts.Parameters.AddWithValue("$splitDate", splitDate);
            counts.Parameters.AddWithValue("$splitId", splitFileId);
            await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                sourceCount = reader.GetInt64(0);
                moveCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            }
        }
        if (moveCount <= 0 || moveCount >= sourceCount)
            throw new InvalidOperationException("Для разделения выбранный кадр должен находиться не первым в событии: часть фотографий должна остаться в исходном событии.");

        var utc = DateTime.UtcNow.ToString("O");
        long newEventId;
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                INSERT INTO Events(Name,StartDate,EndDate,IsAuto,Confidence,CenterLatitude,CenterLongitude,RepresentativeFileId,Notes,CreatedUtc,UpdatedUtc)
                VALUES($name,$splitDate,$splitDate,0,1,NULL,NULL,$splitId,'',$utc,$utc);
                SELECT last_insert_rowid();
                """;
            create.Parameters.AddWithValue("$name", newEventName);
            create.Parameters.AddWithValue("$splitDate", splitDate);
            create.Parameters.AddWithValue("$splitId", splitFileId);
            create.Parameters.AddWithValue("$utc", utc);
            newEventId = Convert.ToInt64(await create.ExecuteScalarAsync(cancellationToken));
        }

        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = """
                UPDATE EventFiles SET EventId=$newEventId
                WHERE EventId=$eventId AND FileId IN (
                    SELECT ef.FileId FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId
                    WHERE ef.EventId=$eventId AND (COALESCE(f.CaptureDate,'')>$splitDate OR (f.CaptureDate=$splitDate AND f.Id>=$splitId))
                );
                """;
            move.Parameters.AddWithValue("$newEventId", newEventId);
            move.Parameters.AddWithValue("$eventId", eventId);
            move.Parameters.AddWithValue("$splitDate", splitDate);
            move.Parameters.AddWithValue("$splitId", splitFileId);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshEventAggregateAsync(connection, transaction, eventId, markManual: true, cancellationToken);
        await RefreshEventAggregateAsync(connection, transaction, newEventId, markManual: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return newEventId;
    }

    public async Task RenameEventAsync(long eventId, string name, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (eventId <= 0) throw new ArgumentOutOfRangeException(nameof(eventId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Название события не может быть пустым.");
        if (name.Length > 160) name = name[..160];
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Events SET Name=$name, IsAuto=0, UpdatedUtc=$utc WHERE Id=$id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", eventId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Событие не найдено.");
    }

    public async Task MergeEventsAsync(long sourceEventId, long targetEventId, string targetName, CancellationToken cancellationToken = default)
    {
        if (sourceEventId <= 0 || targetEventId <= 0 || sourceEventId == targetEventId)
            throw new ArgumentException("Некорректные события для объединения.");
        targetName = (targetName ?? "").Trim();
        if (targetName.Length > 160) targetName = targetName[..160];

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM Events WHERE Id IN ($source,$target);";
            check.Parameters.AddWithValue("$source", sourceEventId);
            check.Parameters.AddWithValue("$target", targetEventId);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != 2)
                throw new InvalidOperationException("Одно из событий больше не существует.");
        }

        await using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = """
                UPDATE OR IGNORE EventFiles SET EventId=$target WHERE EventId=$source;
                DELETE FROM EventFiles WHERE EventId=$source;
                """;
            move.Parameters.AddWithValue("$source", sourceEventId);
            move.Parameters.AddWithValue("$target", targetEventId);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var preserve = connection.CreateCommand())
        {
            preserve.Transaction = transaction;
            preserve.CommandText = """
                UPDATE Events
                SET RepresentativeFileId=COALESCE(RepresentativeFileId, (SELECT RepresentativeFileId FROM Events WHERE Id=$source)),
                    Notes=CASE
                        WHEN TRIM(Notes)='' THEN COALESCE((SELECT Notes FROM Events WHERE Id=$source),'')
                        WHEN TRIM(COALESCE((SELECT Notes FROM Events WHERE Id=$source),''))='' THEN Notes
                        ELSE Notes || char(10) || char(10) || (SELECT Notes FROM Events WHERE Id=$source)
                    END
                WHERE Id=$target;
                """;
            preserve.Parameters.AddWithValue("$source", sourceEventId);
            preserve.Parameters.AddWithValue("$target", targetEventId);
            await preserve.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM Events WHERE Id=$source;";
            delete.Parameters.AddWithValue("$source", sourceEventId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var refresh = connection.CreateCommand())
        {
            refresh.Transaction = transaction;
            refresh.CommandText = """
                UPDATE Events
                SET Name=CASE WHEN $name<>'' THEN $name ELSE Name END,
                    IsAuto=0,
                    StartDate=COALESCE((SELECT MIN(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$target), StartDate),
                    EndDate=COALESCE((SELECT MAX(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$target), EndDate),
                    CenterLatitude=(SELECT AVG(f.GpsLatitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$target AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                    CenterLongitude=(SELECT AVG(f.GpsLongitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$target AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                    UpdatedUtc=$utc
                WHERE Id=$target;
                """;
            refresh.Parameters.AddWithValue("$name", targetName);
            refresh.Parameters.AddWithValue("$target", targetEventId);
            refresh.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }


    public async Task<(int Moved, bool SourceDeleted)> MoveEventPhotosAsync(
        long sourceEventId,
        long targetEventId,
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken = default)
    {
        if (sourceEventId <= 0 || targetEventId <= 0 || sourceEventId == targetEventId)
            throw new ArgumentException("Некорректные события для переноса.");

        var ids = fileIds?.Where(x => x > 0).Distinct().ToArray() ?? [];
        if (ids.Length == 0) return (0, false);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var checkEvents = connection.CreateCommand())
        {
            checkEvents.Transaction = transaction;
            checkEvents.CommandText = "SELECT COUNT(*) FROM Events WHERE Id IN ($source,$target);";
            checkEvents.Parameters.AddWithValue("$source", sourceEventId);
            checkEvents.Parameters.AddWithValue("$target", targetEventId);
            if (Convert.ToInt32(await checkEvents.ExecuteScalarAsync(cancellationToken)) != 2)
                throw new InvalidOperationException("Одно из событий больше не существует.");
        }

        var moved = 0;
        const int batchSize = 800; // SQLite usually allows 999 bound variables per statement; leave room for source/target.
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            var parameterNames = new string[batch.Length];
            await using var move = connection.CreateCommand();
            move.Transaction = transaction;
            move.Parameters.AddWithValue("$source", sourceEventId);
            move.Parameters.AddWithValue("$target", targetEventId);
            for (var i = 0; i < batch.Length; i++)
            {
                parameterNames[i] = "$f" + i;
                move.Parameters.AddWithValue(parameterNames[i], batch[i]);
            }
            move.CommandText = $"UPDATE EventFiles SET EventId=$target WHERE EventId=$source AND FileId IN ({string.Join(',', parameterNames)});";
            moved += await move.ExecuteNonQueryAsync(cancellationToken);
        }
        if (moved <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (0, false);
        }

        await RefreshEventAggregateAsync(connection, transaction, targetEventId, markManual: true, cancellationToken);

        long sourceCount;
        await using (var countSource = connection.CreateCommand())
        {
            countSource.Transaction = transaction;
            countSource.CommandText = "SELECT COUNT(*) FROM EventFiles WHERE EventId=$id;";
            countSource.Parameters.AddWithValue("$id", sourceEventId);
            sourceCount = Convert.ToInt64(await countSource.ExecuteScalarAsync(cancellationToken));
        }

        var sourceDeleted = sourceCount == 0;
        if (sourceDeleted)
        {
            await using var deleteSource = connection.CreateCommand();
            deleteSource.Transaction = transaction;
            deleteSource.CommandText = "DELETE FROM Events WHERE Id=$id;";
            deleteSource.Parameters.AddWithValue("$id", sourceEventId);
            await deleteSource.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await RefreshEventAggregateAsync(connection, transaction, sourceEventId, markManual: true, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (moved, sourceDeleted);
    }

    public async Task<(int Assigned, int SourceEventsDeleted)> AssignFilesToEventAsync(
        long targetEventId,
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken = default)
    {
        if (targetEventId <= 0) throw new ArgumentOutOfRangeException(nameof(targetEventId));
        var ids = fileIds?.Where(x => x > 0).Distinct().ToArray() ?? [];
        if (ids.Length == 0) return (0, 0);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM Events WHERE Id=$id;";
            check.Parameters.AddWithValue("$id", targetEventId);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException("Выбранное событие больше не существует.");
        }

        var sourceEventIds = new HashSet<long>();
        const int batchSize = 800;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            await using var sources = connection.CreateCommand();
            sources.Transaction = transaction;
            var names = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                names[i] = "$f" + i;
                sources.Parameters.AddWithValue(names[i], batch[i]);
            }
            sources.CommandText = $"SELECT DISTINCT EventId FROM EventFiles WHERE FileId IN ({string.Join(',', names)}) AND EventId<>$target;";
            sources.Parameters.AddWithValue("$target", targetEventId);
            await using var reader = await sources.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) sourceEventIds.Add(reader.GetInt64(0));
        }

        var assigned = 0;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            var names = new string[batch.Length];
            await using var move = connection.CreateCommand();
            move.Transaction = transaction;
            move.Parameters.AddWithValue("$target", targetEventId);
            for (var i = 0; i < batch.Length; i++)
            {
                names[i] = "$f" + i;
                move.Parameters.AddWithValue(names[i], batch[i]);
            }
            move.CommandText = $"UPDATE EventFiles SET EventId=$target WHERE FileId IN ({string.Join(',', names)}) AND EventId<>$target;";
            await move.ExecuteNonQueryAsync(cancellationToken);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$target", targetEventId);
            var insertNames = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                insertNames[i] = "$i" + i;
                insert.Parameters.AddWithValue(insertNames[i], batch[i]);
            }
            insert.CommandText = $"INSERT OR IGNORE INTO EventFiles(EventId,FileId) SELECT $target,Id FROM Files WHERE Id IN ({string.Join(',', insertNames)}) AND IsDeleted=0;";
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.Parameters.AddWithValue("$target", targetEventId);
            var names = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                names[i] = "$f" + i;
                count.Parameters.AddWithValue(names[i], batch[i]);
            }
            count.CommandText = $"SELECT COUNT(*) FROM EventFiles WHERE EventId=$target AND FileId IN ({string.Join(',', names)});";
            assigned += Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (assigned <= 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (0, 0);
        }

        await RefreshEventAggregateAsync(connection, transaction, targetEventId, markManual: true, cancellationToken);
        var deleted = 0;
        foreach (var sourceId in sourceEventIds)
        {
            long count;
            await using (var sourceCount = connection.CreateCommand())
            {
                sourceCount.Transaction = transaction;
                sourceCount.CommandText = "SELECT COUNT(*) FROM EventFiles WHERE EventId=$id;";
                sourceCount.Parameters.AddWithValue("$id", sourceId);
                count = Convert.ToInt64(await sourceCount.ExecuteScalarAsync(cancellationToken));
            }
            if (count == 0)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Events WHERE Id=$id;";
                delete.Parameters.AddWithValue("$id", sourceId);
                deleted += await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await RefreshEventAggregateAsync(connection, transaction, sourceId, markManual: true, cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return (assigned, deleted);
    }

    private static async Task RefreshEventAggregateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long eventId,
        bool markManual,
        CancellationToken cancellationToken)
    {
        await using var refresh = connection.CreateCommand();
        refresh.Transaction = transaction;
        refresh.CommandText = """
            UPDATE Events
            SET IsAuto=CASE WHEN $manual=1 THEN 0 ELSE IsAuto END,
                StartDate=COALESCE((SELECT MIN(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$id), StartDate),
                EndDate=COALESCE((SELECT MAX(f.CaptureDate) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$id), EndDate),
                CenterLatitude=(SELECT AVG(f.GpsLatitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$id AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                CenterLongitude=(SELECT AVG(f.GpsLongitude) FROM EventFiles ef JOIN Files f ON f.Id=ef.FileId WHERE ef.EventId=$id AND f.GpsLatitude IS NOT NULL AND f.GpsLongitude IS NOT NULL),
                RepresentativeFileId=CASE WHEN RepresentativeFileId IS NOT NULL AND EXISTS(SELECT 1 FROM EventFiles ef WHERE ef.EventId=$id AND ef.FileId=RepresentativeFileId) THEN RepresentativeFileId ELSE NULL END,
                UpdatedUtc=$utc
            WHERE Id=$id;
            """;
        refresh.Parameters.AddWithValue("$manual", markManual ? 1 : 0);
        refresh.Parameters.AddWithValue("$id", eventId);
        refresh.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        await refresh.ExecuteNonQueryAsync(cancellationToken);
    }


    public async Task<List<OrganizationCandidate>> GetOrganizationCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<OrganizationCandidate>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.Id, f.FullPath, f.SourceFolder, f.FileName, f.ThumbnailPath, f.FileSize, f.LastWriteUtcTicks,
                   f.CaptureDate, f.CaptureDateSource,
                   COALESCE(e.Name,''), COALESCE(e.IsAuto,1), e.StartDate, e.EndDate,
                   f.Sha256, f.HashFileSize, f.HashLastWriteUtcTicks,
                   COALESCE((
                       SELECT om.OriginalPath
                       FROM OrganizationMoves om
                       WHERE om.FileId=f.Id
                       ORDER BY om.Id ASC
                       LIMIT 1
                   ), f.FullPath) AS FirstOriginalPath,
                   COALESCE((
                       SELECT group_concat(PersonName, char(31)) FROM (
                           SELECT DISTINCT TRIM(p.Name) AS PersonName
                           FROM DetectedFaces df
                           JOIN People p ON p.Id=df.PersonId
                           WHERE df.FileId=f.Id AND df.IsIgnored=0
                             AND p.IsAuto=0 AND TRIM(p.Name)<>''
                             AND f.FaceIndexVersion=2
                             AND f.FaceIndexOrientationVersion=1
                             AND f.FaceIndexFileSize=f.FileSize
                             AND f.FaceIndexLastWriteUtcTicks=f.LastWriteUtcTicks
                             AND f.FaceIndexError=''
                           ORDER BY PersonName COLLATE NOCASE
                       )
                   ), '') AS NamedPeople,
                   f.AutoCaptureDate, f.AutoCaptureDateSource
            FROM Files f
            LEFT JOIN EventFiles ef ON ef.FileId=f.Id
            LEFT JOIN Events e ON e.Id=ef.EventId
            WHERE f.IsMissing=0 AND f.IsQuarantined=0 AND f.IsDeleted=0 AND f.FileSize>0
            ORDER BY COALESCE(f.CaptureDate,''), f.Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OrganizationCandidate
            {
                Id = reader.GetInt64(0),
                FullPath = reader.GetString(1),
                SourceFolder = reader.GetString(2),
                FileName = reader.GetString(3),
                ThumbnailPath = reader.GetString(4),
                FileSize = reader.GetInt64(5),
                LastWriteUtcTicks = reader.GetInt64(6),
                CaptureDate = reader.IsDBNull(7) ? null : reader.GetString(7),
                CaptureDateSource = reader.GetString(8),
                EventName = reader.GetString(9),
                EventIsAuto = reader.GetInt32(10) != 0,
                EventStartDate = reader.IsDBNull(11) ? null : reader.GetString(11),
                EventEndDate = reader.IsDBNull(12) ? null : reader.GetString(12),
                Sha256 = reader.GetString(13),
                HashFileSize = reader.GetInt64(14),
                HashLastWriteUtcTicks = reader.GetInt64(15),
                OriginalFileName = Path.GetFileName(reader.GetString(16)),
                NamedPeople = reader.GetString(17)
                    .Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                AutoCaptureDate = reader.IsDBNull(18) ? null : reader.GetString(18),
                AutoCaptureDateSource = reader.GetString(19)
            });
        }
        return result;
    }

    public async Task RecordOrganizationMoveAsync(
        long fileId,
        string originalPath,
        string newPath,
        string originalSourceFolder,
        string newSourceFolder,
        string sha256,
        long fileSize,
        long originalLastWriteUtcTicks,
        long originalCreationUtcTicks,
        long destinationLastWriteUtcTicks,
        long destinationCreationUtcTicks,
        CancellationToken cancellationToken = default)
    {
        newSourceFolder = NormalizeDirectory(newSourceFolder);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "INSERT OR IGNORE INTO SourceFolders(Path, AddedUtc) VALUES($path,$utc);";
            source.Parameters.AddWithValue("$path", newSourceFolder);
            source.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await source.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Files
                SET FullPath=$newPath, SourceFolder=$newSource, FileName=$fileName, IsMissing=0,
                    CreationUtcTicks=$creationTicks, LastWriteUtcTicks=$lastWriteTicks,
                    HashLastWriteUtcTicks=CASE WHEN HashFileSize=FileSize AND Sha256<>'' THEN $lastWriteTicks ELSE HashLastWriteUtcTicks END,
                    PerceptualHashLastWriteUtcTicks=CASE WHEN PerceptualHashFileSize=FileSize AND PerceptualHash<>'' THEN $lastWriteTicks ELSE PerceptualHashLastWriteUtcTicks END,
                    QualityLastWriteUtcTicks=CASE WHEN QualityFileSize=FileSize AND QualityAlgorithmVersion>0 THEN $lastWriteTicks ELSE QualityLastWriteUtcTicks END,
                    FaceIndexLastWriteUtcTicks=CASE WHEN FaceIndexFileSize=FileSize AND FaceIndexVersion>0 THEN $lastWriteTicks ELSE FaceIndexLastWriteUtcTicks END,
                    GpsIndexLastWriteUtcTicks=CASE WHEN GpsIndexFileSize=FileSize AND GpsIndexVersion>0 THEN $lastWriteTicks ELSE GpsIndexLastWriteUtcTicks END,
                    SemanticIndexLastWriteUtcTicks=CASE WHEN SemanticIndexFileSize=FileSize AND SemanticIndexVersion>0 THEN $lastWriteTicks ELSE SemanticIndexLastWriteUtcTicks END
                WHERE Id=$id AND FullPath=$oldPath AND IsQuarantined=0 AND IsDeleted=0;
                """;
            update.Parameters.AddWithValue("$newPath", Path.GetFullPath(newPath));
            update.Parameters.AddWithValue("$newSource", newSourceFolder);
            update.Parameters.AddWithValue("$fileName", Path.GetFileName(newPath));
            update.Parameters.AddWithValue("$creationTicks", destinationCreationUtcTicks);
            update.Parameters.AddWithValue("$lastWriteTicks", destinationLastWriteUtcTicks);
            update.Parameters.AddWithValue("$id", fileId);
            update.Parameters.AddWithValue("$oldPath", Path.GetFullPath(originalPath));
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Каталог PAM изменился после построения плана. Пересканируйте и постройте план заново.");
        }

        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                INSERT INTO OrganizationMoves(FileId,OriginalPath,NewPath,OriginalSourceFolder,NewSourceFolder,Sha256,FileSize,OriginalLastWriteUtcTicks,OriginalCreationUtcTicks,CreatedUtc,UndoneUtc,Error)
                VALUES($fileId,$old,$new,$oldSource,$newSource,$sha,$size,$originalLastWrite,$originalCreation,$utc,NULL,'');
                """;
            action.Parameters.AddWithValue("$fileId", fileId);
            action.Parameters.AddWithValue("$old", Path.GetFullPath(originalPath));
            action.Parameters.AddWithValue("$new", Path.GetFullPath(newPath));
            action.Parameters.AddWithValue("$oldSource", originalSourceFolder);
            action.Parameters.AddWithValue("$newSource", newSourceFolder);
            action.Parameters.AddWithValue("$sha", sha256);
            action.Parameters.AddWithValue("$size", fileSize);
            action.Parameters.AddWithValue("$originalLastWrite", originalLastWriteUtcTicks);
            action.Parameters.AddWithValue("$originalCreation", originalCreationUtcTicks);
            action.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await action.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<OrganizationActionItem>> GetOrganizationMovesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<OrganizationActionItem>();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,FileId,OriginalPath,NewPath,OriginalSourceFolder,NewSourceFolder,Sha256,FileSize,OriginalLastWriteUtcTicks,OriginalCreationUtcTicks,CreatedUtc,UndoneUtc,Error
            FROM OrganizationMoves
            ORDER BY CASE WHEN UndoneUtc IS NULL THEN 0 ELSE 1 END, Id DESC
            LIMIT 5000;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OrganizationActionItem
            {
                Id=reader.GetInt64(0), FileId=reader.GetInt64(1), OriginalPath=reader.GetString(2), NewPath=reader.GetString(3),
                OriginalSourceFolder=reader.GetString(4), NewSourceFolder=reader.GetString(5), Sha256=reader.GetString(6), FileSize=reader.GetInt64(7),
                OriginalLastWriteUtcTicks=reader.GetInt64(8), OriginalCreationUtcTicks=reader.GetInt64(9),
                CreatedUtc=reader.GetString(10), UndoneUtc=reader.IsDBNull(11)?null:reader.GetString(11), Error=reader.GetString(12)
            });
        }
        return result;
    }

    public async Task MarkOrganizationMoveUndoneAsync(
        long actionId, long fileId,
        long restoredLastWriteUtcTicks, long restoredCreationUtcTicks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        string originalPath;
        string newPath;
        string originalSource;

        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT OriginalPath,NewPath,OriginalSourceFolder FROM OrganizationMoves
                WHERE Id=$id AND FileId=$fileId AND UndoneUtc IS NULL;
                """;
            read.Parameters.AddWithValue("$id", actionId);
            read.Parameters.AddWithValue("$fileId", fileId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Операция организации уже отменена или не найдена.");
            originalPath=reader.GetString(0); newPath=reader.GetString(1); originalSource=reader.GetString(2);
        }

        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "INSERT OR IGNORE INTO SourceFolders(Path,AddedUtc) VALUES($path,$utc);";
            source.Parameters.AddWithValue("$path", originalSource);
            source.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            await source.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var file = connection.CreateCommand())
        {
            file.Transaction = transaction;
            file.CommandText = """
                UPDATE Files
                SET FullPath=$old,SourceFolder=$source,FileName=$name,IsMissing=0,
                    CreationUtcTicks=$creationTicks, LastWriteUtcTicks=$lastWriteTicks,
                    HashLastWriteUtcTicks=CASE WHEN HashFileSize=FileSize AND Sha256<>'' THEN $lastWriteTicks ELSE HashLastWriteUtcTicks END,
                    PerceptualHashLastWriteUtcTicks=CASE WHEN PerceptualHashFileSize=FileSize AND PerceptualHash<>'' THEN $lastWriteTicks ELSE PerceptualHashLastWriteUtcTicks END,
                    QualityLastWriteUtcTicks=CASE WHEN QualityFileSize=FileSize AND QualityAlgorithmVersion>0 THEN $lastWriteTicks ELSE QualityLastWriteUtcTicks END,
                    FaceIndexLastWriteUtcTicks=CASE WHEN FaceIndexFileSize=FileSize AND FaceIndexVersion>0 THEN $lastWriteTicks ELSE FaceIndexLastWriteUtcTicks END,
                    GpsIndexLastWriteUtcTicks=CASE WHEN GpsIndexFileSize=FileSize AND GpsIndexVersion>0 THEN $lastWriteTicks ELSE GpsIndexLastWriteUtcTicks END,
                    SemanticIndexLastWriteUtcTicks=CASE WHEN SemanticIndexFileSize=FileSize AND SemanticIndexVersion>0 THEN $lastWriteTicks ELSE SemanticIndexLastWriteUtcTicks END
                WHERE Id=$fileId AND FullPath=$new AND IsQuarantined=0 AND IsDeleted=0;
                """;
            file.Parameters.AddWithValue("$old", originalPath);
            file.Parameters.AddWithValue("$source", originalSource);
            file.Parameters.AddWithValue("$name", Path.GetFileName(originalPath));
            file.Parameters.AddWithValue("$creationTicks", restoredCreationUtcTicks);
            file.Parameters.AddWithValue("$lastWriteTicks", restoredLastWriteUtcTicks);
            file.Parameters.AddWithValue("$fileId", fileId);
            file.Parameters.AddWithValue("$new", newPath);
            if (await file.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Каталог PAM не совпадает с журналом Undo.");
        }

        await using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = "UPDATE OrganizationMoves SET UndoneUtc=$utc WHERE Id=$id AND FileId=$fileId AND UndoneUtc IS NULL;";
            action.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            action.Parameters.AddWithValue("$id", actionId);
            action.Parameters.AddWithValue("$fileId", fileId);
            if (await action.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Не удалось отметить Undo в журнале.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record ExistingFaceRefreshState(
        long Id,
        long? PersonId,
        bool IsIgnored,
        int X,
        int Y,
        int Width,
        int Height,
        int ImageWidth,
        int ImageHeight,
        float[] Embedding,
        string ThumbnailPath,
        bool IsRepresentative,
        bool PersonIsAuto,
        string PersonName)
    {
        public bool PreservePersonAssignment => PersonId.HasValue && (!PersonIsAuto || !string.IsNullOrWhiteSpace(PersonName));
        public bool HasCuratedState => PreservePersonAssignment || IsIgnored || IsRepresentative;
        public double AreaRatio => Width > 0 && Height > 0 && ImageWidth > 0 && ImageHeight > 0
            ? Width * (double)Height / Math.Max(1.0, ImageWidth * (double)ImageHeight)
            : 0;
    }

    private sealed record FaceRefreshMatch(ExistingFaceRefreshState Existing, int DraftIndex, double Similarity);

    private sealed record FaceRefreshCandidate(int ExistingIndex, int DraftIndex, double Similarity, double AreaSimilarity);

    private static List<FaceRefreshMatch> MatchFacesForRefresh(
        IReadOnlyList<ExistingFaceRefreshState> existing,
        IReadOnlyList<DetectedFaceDraft> drafts,
        int orientation,
        int previousOrientationVersion)
    {
        var result = new List<FaceRefreshMatch>();
        if (existing.Count == 0 || drafts.Count == 0) return result;

        var candidates = new List<FaceRefreshCandidate>();
        for (var oi = 0; oi < existing.Count; oi++)
        {
            var old = existing[oi];
            if (old.Embedding.Length < 64) continue;
            for (var ni = 0; ni < drafts.Count; ni++)
            {
                var fresh = drafts[ni];
                if (fresh.Embedding.Length != old.Embedding.Length || fresh.Embedding.Length < 64) continue;

                var oldArea = old.AreaRatio;
                var newArea = fresh.Width > 0 && fresh.Height > 0 && fresh.ImageWidth > 0 && fresh.ImageHeight > 0
                    ? fresh.Width * (double)fresh.Height / Math.Max(1.0, fresh.ImageWidth * (double)fresh.ImageHeight)
                    : 0;
                var areaSimilarity = oldArea > 0 && newArea > 0
                    ? Math.Min(oldArea, newArea) / Math.Max(oldArea, newArea)
                    : 0;
                if (areaSimilarity < 0.35) continue;

                var similarity = CosineNormalized(old.Embedding, fresh.Embedding);
                if (similarity < 0.25) continue;
                candidates.Add(new FaceRefreshCandidate(oi, ni, similarity, areaSimilarity));
            }
        }
        if (candidates.Count > 0)
        {
            // Mutual-best matching plus a uniqueness margin keeps manual person assignments from
            // jumping between two similar-looking people in the same group photo. A one-face photo
            // can use a lower threshold because there is no competing identity inside the file.
            var byOld = candidates.GroupBy(x => x.ExistingIndex)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Similarity).ThenByDescending(x => x.AreaSimilarity).ToArray());
            var byNew = candidates.GroupBy(x => x.DraftIndex)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Similarity).ThenByDescending(x => x.AreaSimilarity).ToArray());

            foreach (var candidate in candidates
                         .OrderByDescending(x => x.Similarity)
                         .ThenByDescending(x => x.AreaSimilarity))
            {
                var old = existing[candidate.ExistingIndex];
                var oldList = byOld[candidate.ExistingIndex];
                var newList = byNew[candidate.DraftIndex];
                if (oldList[0].DraftIndex != candidate.DraftIndex || newList[0].ExistingIndex != candidate.ExistingIndex)
                    continue;

                var singlePair = existing.Count == 1 && drafts.Count == 1;
                var minimumSimilarity = singlePair ? 0.30 : old.HasCuratedState ? 0.42 : 0.36;
                if (candidate.Similarity < minimumSimilarity) continue;

                if (old.HasCuratedState && !singlePair && candidate.Similarity < 0.68)
                {
                    var oldSecond = oldList.Length > 1 ? oldList[1].Similarity : -1.0;
                    var newSecond = newList.Length > 1 ? newList[1].Similarity : -1.0;
                    if (candidate.Similarity - oldSecond < 0.055 || candidate.Similarity - newSecond < 0.055)
                        continue;
                }

                result.Add(new FaceRefreshMatch(old, candidate.DraftIndex, candidate.Similarity));
            }
        }

        // Schema 19 invalidates old face caches for mirrored EXIF orientations 2/4/5/7.
        // Those legacy crops were made before the mirror/transpose was applied, so an SFace
        // embedding can legitimately move enough to miss the conservative identity threshold.
        // For *curated* unmatched faces only, use the deterministic EXIF geometry transform as a
        // second chance.  Requiring mutual-best spatial overlap prevents a manual person assignment
        // from jumping to another face in a crowded photo.
        if (previousOrientationVersion < FaceIndexCandidate.CurrentOrientationVersion &&
            orientation is 2 or 4 or 5 or 7)
        {
            AddMirroredOrientationGeometryMatches(existing, drafts, orientation, result);
        }

        return result;
    }

    private static void AddMirroredOrientationGeometryMatches(
        IReadOnlyList<ExistingFaceRefreshState> existing,
        IReadOnlyList<DetectedFaceDraft> drafts,
        int orientation,
        List<FaceRefreshMatch> matches)
    {
        var usedOld = matches.Select(x => x.Existing.Id).ToHashSet();
        var usedNew = matches.Select(x => x.DraftIndex).ToHashSet();
        var candidates = new List<(int OldIndex, int NewIndex, double Score, double Iou, double CenterDistance)>();

        for (var oi = 0; oi < existing.Count; oi++)
        {
            var old = existing[oi];
            if (usedOld.Contains(old.Id) || !old.HasCuratedState) continue;
            var mapped = MapLegacyFaceRectToCurrentOrientation(old, orientation);
            if (mapped is null) continue;

            for (var ni = 0; ni < drafts.Count; ni++)
            {
                if (usedNew.Contains(ni)) continue;
                var fresh = NormalizeFaceRect(drafts[ni]);
                if (fresh is null) continue;

                var iou = RectIou(mapped.Value, fresh.Value);
                var dx = mapped.Value.Cx - fresh.Value.Cx;
                var dy = mapped.Value.Cy - fresh.Value.Cy;
                var centerDistance = Math.Sqrt(dx * dx + dy * dy);
                var areaSimilarity = Math.Min(mapped.Value.Area, fresh.Value.Area) / Math.Max(mapped.Value.Area, fresh.Value.Area);
                if (areaSimilarity < 0.30) continue;
                if (iou < 0.18 && centerDistance > 0.075) continue;

                var centerScore = Math.Max(0.0, 1.0 - centerDistance / 0.14);
                var score = 0.68 * iou + 0.22 * centerScore + 0.10 * areaSimilarity;
                if (score >= 0.42) candidates.Add((oi, ni, score, iou, centerDistance));
            }
        }

        if (candidates.Count == 0) return;
        var byOld = candidates.GroupBy(x => x.OldIndex).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Score).ToArray());
        var byNew = candidates.GroupBy(x => x.NewIndex).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Score).ToArray());

        foreach (var c in candidates.OrderByDescending(x => x.Score))
        {
            if (usedOld.Contains(existing[c.OldIndex].Id) || usedNew.Contains(c.NewIndex)) continue;
            if (byOld[c.OldIndex][0].NewIndex != c.NewIndex || byNew[c.NewIndex][0].OldIndex != c.OldIndex) continue;

            // When another spatial candidate is almost as good, ambiguity is safer than silently
            // transferring a user's manual label/ignore state to the wrong person.
            var oldSecond = byOld[c.OldIndex].Length > 1 ? byOld[c.OldIndex][1].Score : -1.0;
            var newSecond = byNew[c.NewIndex].Length > 1 ? byNew[c.NewIndex][1].Score : -1.0;
            if (c.Score - oldSecond < 0.08 || c.Score - newSecond < 0.08) continue;

            var old = existing[c.OldIndex];
            matches.Add(new FaceRefreshMatch(old, c.NewIndex, c.Score));
            usedOld.Add(old.Id);
            usedNew.Add(c.NewIndex);
        }
    }

    private readonly record struct NormalizedFaceRect(double Left, double Top, double Right, double Bottom)
    {
        public double Area => Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
        public double Cx => (Left + Right) * 0.5;
        public double Cy => (Top + Bottom) * 0.5;
    }

    private static NormalizedFaceRect? NormalizeFaceRect(DetectedFaceDraft face)
    {
        if (face.ImageWidth <= 0 || face.ImageHeight <= 0 || face.Width <= 0 || face.Height <= 0) return null;
        return ClampRect(new NormalizedFaceRect(
            face.X / (double)face.ImageWidth,
            face.Y / (double)face.ImageHeight,
            (face.X + face.Width) / (double)face.ImageWidth,
            (face.Y + face.Height) / (double)face.ImageHeight));
    }

    private static NormalizedFaceRect? MapLegacyFaceRectToCurrentOrientation(ExistingFaceRefreshState face, int orientation)
    {
        if (face.ImageWidth <= 0 || face.ImageHeight <= 0 || face.Width <= 0 || face.Height <= 0) return null;
        var x1 = face.X / (double)face.ImageWidth;
        var y1 = face.Y / (double)face.ImageHeight;
        var x2 = (face.X + face.Width) / (double)face.ImageWidth;
        var y2 = (face.Y + face.Height) / (double)face.ImageHeight;
        var points = new[]
        {
            MapExifPoint(x1, y1, orientation), MapExifPoint(x2, y1, orientation),
            MapExifPoint(x1, y2, orientation), MapExifPoint(x2, y2, orientation)
        };
        return ClampRect(new NormalizedFaceRect(
            points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X), points.Max(p => p.Y)));
    }

    private static (double X, double Y) MapExifPoint(double x, double y, int orientation) => orientation switch
    {
        2 => (1.0 - x, y),
        4 => (x, 1.0 - y),
        5 => (y, x),
        7 => (1.0 - y, 1.0 - x),
        _ => (x, y)
    };

    private static NormalizedFaceRect ClampRect(NormalizedFaceRect r) => new(
        Math.Clamp(r.Left, 0.0, 1.0), Math.Clamp(r.Top, 0.0, 1.0),
        Math.Clamp(r.Right, 0.0, 1.0), Math.Clamp(r.Bottom, 0.0, 1.0));

    private static double RectIou(NormalizedFaceRect a, NormalizedFaceRect b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        var intersection = Math.Max(0.0, right - left) * Math.Max(0.0, bottom - top);
        var union = a.Area + b.Area - intersection;
        return union <= 1e-12 ? 0.0 : intersection / union;
    }

    private static double CosineNormalized(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return -1;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }
        var denom = Math.Sqrt(aa * bb);
        return denom < 1e-12 ? -1 : dot / denom;
    }

    private static byte[] FloatArrayToBytes(float[] values)
    {
        if (values.Length == 0) return Array.Empty<byte>();
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloatArray(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) return Array.Empty<float>();
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    private static void AddPhotoParameters(SqliteCommand command, PhotoRecord r)
    {
        command.Parameters.AddWithValue("$FullPath", r.FullPath);
        command.Parameters.AddWithValue("$SourceFolder", NormalizeDirectory(r.SourceFolder));
        command.Parameters.AddWithValue("$FileName", r.FileName);
        command.Parameters.AddWithValue("$Extension", r.Extension);
        command.Parameters.AddWithValue("$FileSize", r.FileSize);
        command.Parameters.AddWithValue("$LastWriteUtcTicks", r.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$CreationUtcTicks", r.CreationUtcTicks);
        command.Parameters.AddWithValue("$CaptureDate", (object?)r.CaptureDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$CaptureDateSource", r.CaptureDateSource);
        command.Parameters.AddWithValue("$EffectiveYear", r.EffectiveYear);
        command.Parameters.AddWithValue("$Width", r.Width);
        command.Parameters.AddWithValue("$Height", r.Height);
        command.Parameters.AddWithValue("$CameraMake", r.CameraMake);
        command.Parameters.AddWithValue("$CameraModel", r.CameraModel);
        command.Parameters.AddWithValue("$Orientation", r.Orientation);
        command.Parameters.AddWithValue("$GpsLatitude", (object?)r.GpsLatitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$GpsLongitude", (object?)r.GpsLongitude ?? DBNull.Value);
        command.Parameters.AddWithValue("$ThumbnailPath", r.ThumbnailPath);
        command.Parameters.AddWithValue("$Error", r.Error);
        command.Parameters.AddWithValue("$IndexedUtc", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$LastSeenScanId", r.LastSeenScanId);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        Dictionary<string, HashSet<string>> columnCache,
        string tableName,
        string columnName,
        string definition)
    {
        if (!columnCache.TryGetValue(tableName, out var columns))
        {
            columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var check = connection.CreateCommand();
            check.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await check.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
            columnCache[tableName] = columns;
        }

        if (columns.Contains(columnName)) return;

        await ExecuteAsync(connection, $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};");
        columns.Add(columnName);
    }

    private static string BuildInactiveCatalogPath(string physicalReferencePath, long fileId, string state)
    {
        var full = Path.GetFullPath(physicalReferencePath);
        return full + $".pam-{state}-catalog-record-{fileId}";
    }

    private static async Task NormalizeLegacyStoredDatesAsync(SqliteConnection connection)
    {
        var files = new List<(long Id, string? CaptureDate, string? AutoCaptureDate)>();
        await using (var readFiles = connection.CreateCommand())
        {
            readFiles.CommandText = "SELECT Id, CaptureDate, AutoCaptureDate FROM Files WHERE CaptureDate IS NOT NULL OR AutoCaptureDate IS NOT NULL;";
            await using var reader = await readFiles.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                files.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var events = new List<(long Id, string StartDate, string EndDate)>();
        await using (var readEvents = connection.CreateCommand())
        {
            readEvents.CommandText = "SELECT Id, StartDate, EndDate FROM Events;";
            await using var reader = await readEvents.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                events.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        await using var transaction = connection.BeginTransaction();
        foreach (var row in files)
        {
            var capture = row.CaptureDate;
            var autoCapture = row.AutoCaptureDate;
            var captureChanged = StoredDateTime.TryNormalize(capture, out var normalizedCapture) &&
                                 !string.Equals(capture, normalizedCapture, StringComparison.Ordinal);
            var autoChanged = StoredDateTime.TryNormalize(autoCapture, out var normalizedAuto) &&
                              !string.Equals(autoCapture, normalizedAuto, StringComparison.Ordinal);
            if (!captureChanged && !autoChanged) continue;

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE Files SET CaptureDate=$capture, AutoCaptureDate=$auto WHERE Id=$id;";
            update.Parameters.AddWithValue("$capture", captureChanged ? normalizedCapture : (object?)capture ?? DBNull.Value);
            update.Parameters.AddWithValue("$auto", autoChanged ? normalizedAuto : (object?)autoCapture ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync();
        }

        foreach (var row in events)
        {
            var startChanged = StoredDateTime.TryNormalize(row.StartDate, out var normalizedStart) &&
                               !string.Equals(row.StartDate, normalizedStart, StringComparison.Ordinal);
            var endChanged = StoredDateTime.TryNormalize(row.EndDate, out var normalizedEnd) &&
                             !string.Equals(row.EndDate, normalizedEnd, StringComparison.Ordinal);
            if (!startChanged && !endChanged) continue;

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE Events SET StartDate=$start, EndDate=$end WHERE Id=$id;";
            update.Parameters.AddWithValue("$start", startChanged ? normalizedStart : row.StartDate);
            update.Parameters.AddWithValue("$end", endChanged ? normalizedEnd : row.EndDate);
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var value = await command.ExecuteScalarAsync();
        return value is null || value is DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return fullPath;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string FormatStoredDateTime(DateTime value) => StoredDateTime.Format(value);

    private static bool TryParseStoredDateTime(string? value, out DateTime result) =>
        StoredDateTime.TryParse(value, out result);

}
