using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using PhotoArchiveManager.Infrastructure;

namespace PhotoArchiveManager.Services;

public static class PortableSelfTest
{
    private const string ReportEnvironmentVariable = "PAM_PORTABLE_SELFTEST_REPORT";

    public static int Run()
    {
        var reportPath = Environment.GetEnvironmentVariable(ReportEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(reportPath))
            reportPath = Path.Combine(AppContext.BaseDirectory, "PAM_PORTABLE_SELF_TEST.txt");

        var lines = new List<string>
        {
            "Photo Archive Manager portable self-test",
            "Version=" + AppPaths.AppVersion,
            "TimestampUtc=" + DateTime.UtcNow.ToString("O"),
            "OS=" + RuntimeInformation.OSDescription,
            "ProcessArchitecture=" + RuntimeInformation.ProcessArchitecture,
            "BaseDirectory=" + AppContext.BaseDirectory
        };

        var tempRoot = Path.Combine(Path.GetTempPath(), "PAM-selftest-" + Guid.NewGuid().ToString("N"));
        var result = 1;

        try
        {
            ValidateSQLiteAndStoredDates(tempRoot, lines);
            ValidateDetachedSourceQuarantine(tempRoot, lines);

            if (!OpenCvRuntimeDiagnostics.TryProbe(out var openCvVersion, out var openCvError))
                throw new InvalidOperationException(openCvError);

            lines.Add("OpenCV=" + openCvVersion);
            var portableNative = OpenCvRuntimeDiagnostics.GetPortableNativePath();
            var loadedNative = OpenCvRuntimeDiagnostics.GetLoadedNativeModulePath();
            lines.Add("PortableOpenCvSharpExtern=" + portableNative);
            lines.Add("LoadedOpenCvSharpExtern=" + loadedNative);

            if (portableNative.StartsWith("<", StringComparison.Ordinal) ||
                loadedNative.StartsWith("<", StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(portableNative), Path.GetFullPath(loadedNative), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "OpenCV loaded from an unexpected location. Portable=" + portableNative + "; Loaded=" + loadedNative);
            }

            ValidateEmbeddedResources(lines);

            var models = new FaceModelService(Path.Combine(tempRoot, "Models"));
            models.EnsureRecognitionReady();
            lines.Add("SFaceBytes=" + new FileInfo(models.SFaceModelPath).Length);
            lines.Add("YuNetBytes=" + new FileInfo(models.YuNetModelPath).Length);

            // Loading both DNN models proves more than checking embedded-resource names:
            // the final EXE can extract its native runtime, extract the ONNX files and
            // OpenCV can actually parse them on this machine.
            using (var recognizer = Net.ReadNetFromONNX(models.SFaceModelPath))
            {
                if (recognizer is null)
                    throw new InvalidOperationException("SFace ONNX model returned null.");
                recognizer.SetPreferableBackend(Backend.OPENCV);
                recognizer.SetPreferableTarget(Target.CPU);
            }

            using (var detector = FaceDetectorYN.Create(
                       models.YuNetModelPath, "", new Size(320, 240),
                       scoreThreshold: 0.78f, nmsThreshold: 0.30f, topK: 5000,
                       backendId: Backend.OPENCV, targetId: Target.CPU))
            using (var blank = new Mat(240, 320, MatType.CV_8UC3, new Scalar(0, 0, 0)))
            using (var detections = new Mat())
            {
                detector.Detect(blank, detections);
            }

            lines.Add("FaceModels=OK");
            lines.Add("RESULT=OK");
            result = 0;
        }
        catch (Exception ex)
        {
            lines.Add("RESULT=FAIL");
            lines.Add("ERROR=" + FlattenException(ex));
        }
        finally
        {
            try
            {
                var parent = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
                File.WriteAllLines(reportPath, lines, new UTF8Encoding(false));
            }
            catch
            {
                result = 2;
            }

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // A self-test cleanup failure must not turn an otherwise healthy build red.
            }
        }

        return result;
    }

    private static void ValidateSQLiteAndStoredDates(string tempRoot, ICollection<string> lines)
    {
        if (!StoredDateTime.TryParse("2026-09-02 12:34:56", out var canonical) ||
            canonical.Year != 2026 || canonical.Month != 9 || canonical.Day != 2 ||
            !StoredDateTime.TryParse("2026-09-02 12.34.56", out var legacy) || legacy.Second != 56)
            throw new InvalidOperationException("Culture-independent stored-date parser self-test failed.");
        lines.Add("StoredDateTime=OK");

        var dbPath = Path.Combine(tempRoot, "SQLite", "archive.db");
        var database = new DatabaseService(dbPath);
        database.InitializeAsync().GetAwaiter().GetResult();
        using var connection = database.CreateConnection();
        connection.Open();

        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            var schema = Convert.ToInt32(version.ExecuteScalar());
            if (schema != DatabaseService.CurrentSchemaVersion)
                throw new InvalidOperationException(
                    "SQLite schema self-test expected user_version=" + DatabaseService.CurrentSchemaVersion + ", got " + schema + ".");
            lines.Add("SQLiteSchema=" + schema);
        }

        var requiredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TechnicalScore", "WorstFaceScore", "EyeOpennessScore", "ClosedEyeCount",
            "BlinkPenalty", "PerceptualHashAlgorithmVersion", "FaceIndexOrientationVersion"
        };
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "PRAGMA table_info(Files);";
            using var reader = columns.ExecuteReader();
            while (reader.Read()) requiredColumns.Remove(reader.GetString(1));
        }
        if (requiredColumns.Count != 0)
            throw new InvalidOperationException("SQLite Files table misses release columns: " + string.Join(", ", requiredColumns));
        lines.Add("SQLiteReleaseColumns=OK");

        ValidateSchema19Migration(tempRoot, lines);
        ValidateSchema20Migration(tempRoot, lines);

        // Fail-safe compatibility: an older PAM must never rewrite a future archive.db backwards.
        var futurePath = Path.Combine(tempRoot, "SQLiteFuture", "archive.db");
        var futureDatabase = new DatabaseService(futurePath);
        using (var futureConnection = futureDatabase.CreateConnection())
        {
            futureConnection.Open();
            using var setFuture = futureConnection.CreateCommand();
            setFuture.CommandText = $"PRAGMA user_version={DatabaseService.CurrentSchemaVersion + 1};";
            setFuture.ExecuteNonQuery();
        }

        var rejectedFuture = false;
        try
        {
            futureDatabase.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            rejectedFuture = true;
        }
        if (!rejectedFuture)
            throw new InvalidOperationException("SQLite future-schema self-test expected a safe refusal.");

        using (var verifyFuture = futureDatabase.CreateConnection())
        {
            verifyFuture.Open();
            using var readFuture = verifyFuture.CreateCommand();
            readFuture.CommandText = "PRAGMA user_version;";
            var preserved = Convert.ToInt32(readFuture.ExecuteScalar());
            if (preserved != DatabaseService.CurrentSchemaVersion + 1)
                throw new InvalidOperationException("Future SQLite schema version was modified during refusal.");
        }
        lines.Add("SQLiteFutureSchemaRefusal=OK");
    }

    private static void ValidateSchema19Migration(string tempRoot, ICollection<string> lines)
    {
        var migrationPath = Path.Combine(tempRoot, "SQLiteMigration18To19", "archive.db");
        var before = new DatabaseService(migrationPath);
        before.InitializeAsync().GetAwaiter().GetResult();

        using (var connection = before.CreateConnection())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var insertFile = connection.CreateCommand())
            {
                insertFile.Transaction = transaction;
                insertFile.CommandText = """
                    INSERT INTO Files(
                        FullPath, SourceFolder, FileName, Extension, FileSize,
                        LastWriteUtcTicks, CreationUtcTicks, CaptureDate, CaptureDateSource,
                        AutoCaptureDate, AutoCaptureDateSource, Orientation, IndexedUtc,
                        FaceIndexVersion, FaceIndexOrientationVersion)
                    VALUES
                        ('C:\selftest\normal.jpg', 'C:\selftest', 'normal.jpg', '.jpg', 100,
                         101, 100, '2026-09-02 12.34.56', 'EXIF', '2026-09-02T12:34:55.25', 'EXIF',
                         1, '2026-09-02T10:00:00.0000000Z', 2, 0),
                        ('C:\selftest\mirrored.jpg', 'C:\selftest', 'mirrored.jpg', '.jpg', 200,
                         201, 200, '2026-09-02T12:35:56', 'EXIF', NULL, '',
                         2, '2026-09-02T10:00:01.0000000Z', 2, 1);
                    """;
                insertFile.ExecuteNonQuery();
            }

            using (var insertEvent = connection.CreateCommand())
            {
                insertEvent.Transaction = transaction;
                insertEvent.CommandText = """
                    INSERT INTO Events(Name, StartDate, EndDate, CreatedUtc, UpdatedUtc)
                    VALUES('legacy-date-self-test', '2026-09-02 12.30.00', '2026-09-02T13:45:01.5',
                           '2026-09-02T10:00:00.0000000Z', '2026-09-02T10:00:00.0000000Z');
                    """;
                insertEvent.ExecuteNonQuery();
            }

            using (var downgradeVersion = connection.CreateCommand())
            {
                downgradeVersion.Transaction = transaction;
                downgradeVersion.CommandText = "PRAGMA user_version=18;";
                downgradeVersion.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        var migrated = new DatabaseService(migrationPath);
        migrated.InitializeAsync().GetAwaiter().GetResult();
        using var verify = migrated.CreateConnection();
        verify.Open();

        using (var normal = verify.CreateCommand())
        {
            normal.CommandText = "SELECT CaptureDate, AutoCaptureDate, FaceIndexOrientationVersion FROM Files WHERE FileName='normal.jpg';";
            using var reader = normal.ExecuteReader();
            if (!reader.Read() ||
                reader.GetString(0) != "2026-09-02 12:34:56" ||
                reader.GetString(1) != "2026-09-02 12:34:55.25" ||
                reader.GetInt32(2) != 1)
                throw new InvalidOperationException("SQLite schema-19 migration did not canonicalize/certify a non-mirrored face row.");
        }

        using (var mirrored = verify.CreateCommand())
        {
            mirrored.CommandText = "SELECT CaptureDate, FaceIndexOrientationVersion FROM Files WHERE FileName='mirrored.jpg';";
            using var reader = mirrored.ExecuteReader();
            if (!reader.Read() ||
                reader.GetString(0) != "2026-09-02 12:35:56" ||
                reader.GetInt32(1) != 0)
                throw new InvalidOperationException("SQLite schema-19 migration failed to invalidate a mirrored legacy face row.");
        }

        using (var legacyEvent = verify.CreateCommand())
        {
            legacyEvent.CommandText = "SELECT StartDate, EndDate FROM Events WHERE Name='legacy-date-self-test';";
            using var reader = legacyEvent.ExecuteReader();
            if (!reader.Read() ||
                reader.GetString(0) != "2026-09-02 12:30:00" ||
                reader.GetString(1) != "2026-09-02 13:45:01.5")
                throw new InvalidOperationException("SQLite schema-19 migration did not canonicalize legacy event dates.");
        }

        using (var version = verify.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar()) != DatabaseService.CurrentSchemaVersion)
                throw new InvalidOperationException("SQLite schema-19 migration did not restore the current user_version.");
        }

        lines.Add("SQLiteMigration18To19=OK");
    }

    private static void ValidateSchema20Migration(string tempRoot, ICollection<string> lines)
    {
        var migrationPath = Path.Combine(tempRoot, "SQLiteMigration19To20", "archive.db");
        var before = new DatabaseService(migrationPath);
        before.InitializeAsync().GetAwaiter().GetResult();

        using (var connection = before.CreateConnection())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();

            // Seed inactive rows with their legacy schema-19 FullPath values. Schema 20 must
            // detach those logical audit records so a brand-new photo can later reuse the old
            // physical path without inheriting IsQuarantined/IsDeleted from the historical FileId.
            using (var seed = connection.CreateCommand())
            {
                seed.Transaction = transaction;
                seed.CommandText = """
                    INSERT INTO Files(FullPath,SourceFolder,FileName,Extension,FileSize,LastWriteUtcTicks,CreationUtcTicks,CaptureDateSource,IndexedUtc,IsQuarantined,QuarantinePath,IsDeleted)
                    VALUES
                      ('C:\selftest\reuse-quarantine.jpg','C:\selftest','reuse-quarantine.jpg','.jpg',10,10,10,'','2026-09-03T00:00:00Z',1,'C:\quarantine\reuse-quarantine.jpg',0),
                      ('C:\selftest\reuse-deleted.jpg','C:\selftest','reuse-deleted.jpg','.jpg',11,11,11,'','2026-09-03T00:00:00Z',0,'',1);
                    """;
                seed.ExecuteNonQuery();
            }

            // Simulate a real schema-19 database: the two journals existed, but the original
            // filesystem timestamp columns introduced by schema 20 did not. SQLite bundled with
            // Microsoft.Data.Sqlite supports DROP COLUMN; rebuilding these two tables would test
            // the same EnsureColumn path but add much more brittle DDL to the portable smoke test.
            foreach (var sql in new[]
                     {
                         "ALTER TABLE Actions DROP COLUMN OriginalLastWriteUtcTicks;",
                         "ALTER TABLE Actions DROP COLUMN OriginalCreationUtcTicks;",
                         "ALTER TABLE OrganizationMoves DROP COLUMN OriginalLastWriteUtcTicks;",
                         "ALTER TABLE OrganizationMoves DROP COLUMN OriginalCreationUtcTicks;",
                         "PRAGMA user_version=19;"
                     })
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        var migrated = new DatabaseService(migrationPath);
        migrated.InitializeAsync().GetAwaiter().GetResult();
        using var verify = migrated.CreateConnection();
        verify.Open();

        static HashSet<string> ReadColumns(SqliteConnection connection, string table)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(" + table + ");";
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(1));
            return result;
        }

        var actionColumns = ReadColumns(verify, "Actions");
        var organizationColumns = ReadColumns(verify, "OrganizationMoves");
        foreach (var column in new[] { "OriginalLastWriteUtcTicks", "OriginalCreationUtcTicks" })
        {
            if (!actionColumns.Contains(column))
                throw new InvalidOperationException("SQLite schema-20 migration did not add Actions." + column + ".");
            if (!organizationColumns.Contains(column))
                throw new InvalidOperationException("SQLite schema-20 migration did not add OrganizationMoves." + column + ".");
        }

        using (var inactive = verify.CreateCommand())
        {
            inactive.CommandText = """
                SELECT SUM(CASE WHEN FullPath='C:\selftest\reuse-quarantine.jpg' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN FullPath='C:\selftest\reuse-deleted.jpg' THEN 1 ELSE 0 END)
                FROM Files WHERE IsQuarantined=1 OR IsDeleted=1;
                """;
            using var reader = inactive.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != 0 || reader.GetInt32(1) != 0)
                throw new InvalidOperationException("SQLite schema-20 migration did not detach inactive catalogue FullPath values.");
        }

        // The old physical paths must now be reusable by genuinely new FileIds.
        using (var reuse = verify.CreateCommand())
        {
            reuse.CommandText = """
                INSERT INTO Files(FullPath,SourceFolder,FileName,Extension,FileSize,LastWriteUtcTicks,CreationUtcTicks,CaptureDateSource,IndexedUtc)
                VALUES
                  ('C:\selftest\reuse-quarantine.jpg','C:\selftest','reuse-quarantine.jpg','.jpg',20,20,20,'','2026-09-03T00:00:01Z'),
                  ('C:\selftest\reuse-deleted.jpg','C:\selftest','reuse-deleted.jpg','.jpg',21,21,21,'','2026-09-03T00:00:01Z');
                """;
            reuse.ExecuteNonQuery();
        }

        using (var version = verify.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar()) != DatabaseService.CurrentSchemaVersion)
                throw new InvalidOperationException("SQLite schema-20 migration did not restore the current user_version.");
        }

        lines.Add("SQLiteMigration19To20=OK");
    }

    private static void ValidateDetachedSourceQuarantine(string tempRoot, ICollection<string> lines)
    {
        var dbPath = Path.Combine(tempRoot, "detach-source.db");
        var sourceRoot = Path.Combine(tempRoot, "DetachedSource");
        var originalPath = Path.Combine(sourceRoot, "photo.jpg");
        var quarantinePath = Path.Combine(tempRoot, "Quarantine", "photo.jpg");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);

        var database = new DatabaseService(dbPath);
        database.InitializeAsync().GetAwaiter().GetResult();
        database.AddSourceFolderAsync(sourceRoot).GetAwaiter().GetResult();

        long fileId;
        long actionId;
        using (var connection = database.CreateConnection())
        {
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var file = connection.CreateCommand())
            {
                file.Transaction = transaction;
                file.CommandText = """
                    INSERT INTO Files(
                        FullPath, SourceFolder, FileName, Extension, FileSize, LastWriteUtcTicks, CreationUtcTicks,
                        CaptureDateSource, IndexedUtc, IsMissing, IsQuarantined, QuarantinePath, IsDeleted)
                    VALUES($catalog,$source,'photo.jpg','.jpg',123,200,100,'','2026-09-03T00:00:00Z',0,1,$quarantine,0);
                    SELECT last_insert_rowid();
                    """;
                file.Parameters.AddWithValue("$catalog", Path.Combine(tempRoot, ".pam-quarantine-test", "photo.jpg"));
                file.Parameters.AddWithValue("$source", Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                file.Parameters.AddWithValue("$quarantine", quarantinePath);
                fileId = Convert.ToInt64(file.ExecuteScalar());
            }

            using (var action = connection.CreateCommand())
            {
                action.Transaction = transaction;
                action.CommandText = """
                    INSERT INTO Actions(FileId,ActionType,OriginalPath,NewPath,Sha256,FileSize,OriginalLastWriteUtcTicks,OriginalCreationUtcTicks,CreatedUtc,Error)
                    VALUES($fileId,'QUARANTINE',$original,$quarantine,$sha,123,200,100,'2026-09-03T00:00:00Z','');
                    SELECT last_insert_rowid();
                    """;
                action.Parameters.AddWithValue("$fileId", fileId);
                action.Parameters.AddWithValue("$original", originalPath);
                action.Parameters.AddWithValue("$quarantine", quarantinePath);
                action.Parameters.AddWithValue("$sha", new string('A', 64));
                actionId = Convert.ToInt64(action.ExecuteScalar());
            }

            transaction.Commit();
        }

        var removed = database.RemoveSourceFolderAndCatalogAsync(sourceRoot).GetAwaiter().GetResult();
        if (removed.ActiveQuarantineCount != 1 || removed.AuditRecordsRetained < 1)
            throw new InvalidOperationException("Detached-source self-test did not retain the active quarantine audit row.");

        using (var verifyDetached = database.CreateConnection())
        {
            verifyDetached.Open();
            using var command = verifyDetached.CreateCommand();
            command.CommandText = """
                SELECT f.IsMissing, f.IsQuarantined,
                       (SELECT COUNT(*) FROM SourceFolders WHERE Path=f.SourceFolder)
                FROM Files f WHERE f.Id=$id;
                """;
            command.Parameters.AddWithValue("$id", fileId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != 1 || reader.GetInt32(1) != 1 || reader.GetInt32(2) != 0)
                throw new InvalidOperationException("Detached-source self-test did not hide the quarantined audit row/source correctly.");
        }

        database.MarkQuarantineUndoneAsync(actionId, fileId, 200, 100).GetAwaiter().GetResult();

        using (var verifyUndo = database.CreateConnection())
        {
            verifyUndo.Open();
            using var command = verifyUndo.CreateCommand();
            command.CommandText = "SELECT FullPath, IsMissing, IsQuarantined, IsDeleted FROM Files WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", fileId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() ||
                !string.Equals(Path.GetFullPath(reader.GetString(0)), Path.GetFullPath(originalPath), StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(1) != 1 || reader.GetInt32(2) != 0 || reader.GetInt32(3) != 0)
            {
                throw new InvalidOperationException("Undo of a detached-source quarantine unexpectedly reactivated the closed source.");
            }
        }

        lines.Add("DetachedSourceQuarantineUndo=OK");
    }

    private static void ValidateEmbeddedResources(ICollection<string> lines)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames();
        var required = new (string FileName, long MinimumBytes)[]
        {
            ("face_recognition_sface_2021dec.onnx", 10_000_000),
            ("face_detection_yunet_2023mar.onnx", 150_000)
        };

        foreach (var item in required)
        {
            var resourceName = resources.FirstOrDefault(x =>
                x.EndsWith("." + item.FileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
                throw new InvalidOperationException("Embedded face resource is missing: " + item.FileName);

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Cannot open embedded face resource: " + item.FileName);
            if (stream.Length <= item.MinimumBytes)
                throw new InvalidDataException("Embedded face resource is unexpectedly small: " + item.FileName);

            lines.Add("EmbeddedResource=" + item.FileName + ";Bytes=" + stream.Length);
        }
    }

    private static string FlattenException(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
            parts.Add(current.GetType().Name + ": " + current.Message);
        return string.Join(" | ", parts);
    }
}
