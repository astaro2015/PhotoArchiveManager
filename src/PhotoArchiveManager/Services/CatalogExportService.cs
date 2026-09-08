using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PhotoArchiveManager.Services;

public sealed class CatalogExportService
{
    private readonly DatabaseService _database;

    public CatalogExportService(DatabaseService database) => _database = database;

    public async Task<int> ExportAsync(string path, bool json, CancellationToken cancellationToken = default)
    {
        var rows = await ReadRowsAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (json)
            await WriteJsonAsync(path, rows, cancellationToken);
        else
            await WriteCsvAsync(path, rows, cancellationToken);
        return rows.Count;
    }

    private async Task<List<ExportRow>> ReadRowsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ExportRow>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ExactCounts AS (
                SELECT Sha256, COUNT(*) AS CopyCount
                FROM Files
                WHERE Sha256<>'' AND IsMissing=0 AND IsQuarantined=0 AND IsDeleted=0
                GROUP BY Sha256
            )
            SELECT f.Id, f.FullPath, f.SourceFolder, f.FileName, f.Extension, f.FileSize,
                   f.CaptureDate, f.CaptureDateSource, f.AutoCaptureDate, f.AutoCaptureDateSource, f.EffectiveYear,
                   f.Width, f.Height, f.CameraMake, f.CameraModel,
                   f.QualityScore, f.SharpnessScore, f.BlurScore, f.ExposureScore, f.ResolutionScore, f.CompressionScore, f.QualityNotes,
                   f.Sha256,
                   COALESCE(x.CopyCount, 0) AS ExactCopies,
                   (SELECT COUNT(*) FROM DetectedFaces df WHERE df.FileId=f.Id AND df.IsIgnored=0) AS FaceCount,
                   COALESCE((
                       SELECT group_concat(Name, ', ') FROM (
                           SELECT DISTINCT CASE WHEN TRIM(p.Name)<>'' THEN p.Name ELSE 'Человек #' || p.Id END AS Name
                           FROM DetectedFaces df JOIN People p ON p.Id=df.PersonId
                           WHERE df.FileId=f.Id AND df.IsIgnored=0
                           ORDER BY Name
                       )
                   ), '') AS PeopleNames,
                   e.Id, COALESCE(e.Name,''), e.StartDate, e.EndDate, COALESCE(e.IsAuto,1),
                   f.GpsLatitude, f.GpsLongitude, f.IsMissing, f.IsQuarantined, f.QuarantinePath, f.Error,
                   f.TechnicalScore, f.ContrastScore, f.NoiseScore, f.FaceScore, f.EyeScore, f.FacePoseScore, f.WorstFaceScore, f.QualityAlgorithmVersion,
                   f.EyeOpennessScore, f.ClosedEyeCount, f.BlinkPenalty
            FROM Files f
            LEFT JOIN ExactCounts x ON x.Sha256=f.Sha256 AND f.Sha256<>''
            LEFT JOIN EventFiles ef ON ef.FileId=f.Id
            LEFT JOIN Events e ON e.Id=ef.EventId
            WHERE f.IsDeleted=0
            ORDER BY COALESCE(f.CaptureDate,''), f.FullPath COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ExportRow
            {
                Id = reader.GetInt64(0), FullPath = reader.GetString(1), SourceFolder = reader.GetString(2), FileName = reader.GetString(3), Extension = reader.GetString(4), FileSize = reader.GetInt64(5),
                CaptureDate = reader.IsDBNull(6) ? null : reader.GetString(6), CaptureDateSource = reader.GetString(7), AutoCaptureDate = reader.IsDBNull(8) ? null : reader.GetString(8), AutoCaptureDateSource = reader.GetString(9), EffectiveYear = reader.GetInt32(10),
                Width = reader.GetInt32(11), Height = reader.GetInt32(12), CameraMake = reader.GetString(13), CameraModel = reader.GetString(14),
                QualityScore = reader.GetDouble(15), SharpnessScore = reader.GetDouble(16), BlurScore = reader.GetDouble(17), ExposureScore = reader.GetDouble(18), ResolutionScore = reader.GetDouble(19), CompressionScore = reader.GetDouble(20), QualityNotes = reader.GetString(21),
                Sha256 = reader.GetString(22), ExactCopies = reader.GetInt32(23), FaceCount = reader.GetInt32(24), PeopleNames = reader.GetString(25),
                EventId = reader.IsDBNull(26) ? null : reader.GetInt64(26), EventName = reader.GetString(27), EventStartDate = reader.IsDBNull(28) ? null : reader.GetString(28), EventEndDate = reader.IsDBNull(29) ? null : reader.GetString(29), EventIsAuto = reader.GetInt32(30) != 0,
                GpsLatitude = reader.IsDBNull(31) ? null : reader.GetDouble(31), GpsLongitude = reader.IsDBNull(32) ? null : reader.GetDouble(32), IsMissing = reader.GetInt32(33) != 0, IsQuarantined = reader.GetInt32(34) != 0, QuarantinePath = reader.GetString(35), Error = reader.GetString(36),
                TechnicalScore = reader.GetDouble(37), ContrastScore = reader.GetDouble(38), NoiseScore = reader.GetDouble(39), FaceScore = reader.GetDouble(40), EyeScore = reader.GetDouble(41), FacePoseScore = reader.GetDouble(42), WorstFaceScore = reader.GetDouble(43), QualityAlgorithmVersion = reader.GetInt32(44),
                EyeOpennessScore = reader.GetDouble(45), ClosedEyeCount = reader.GetInt32(46), BlinkPenalty = reader.GetDouble(47)
            });
        }
        return result;
    }

    private static async Task WriteJsonAsync(string path, List<ExportRow> rows, CancellationToken cancellationToken)
    {
        var payload = new
        {
            format = "Photo Archive Manager catalog export",
            version = AppPaths.AppVersion,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            photoCount = rows.Count,
            photos = rows
        };
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }

    private static async Task WriteCsvAsync(string path, List<ExportRow> rows, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024);
        await writer.WriteLineAsync("Id;FullPath;SourceFolder;FileName;Extension;FileSize;CaptureDate;CaptureDateSource;AutoCaptureDate;AutoCaptureDateSource;EffectiveYear;Width;Height;CameraMake;CameraModel;QualityScore;TechnicalScore;SharpnessScore;BlurScore;ExposureScore;ContrastScore;NoiseScore;ResolutionScore;CompressionScore;FaceScore;EyeScore;EyeOpennessScore;ClosedEyeCount;BlinkPenalty;FacePoseScore;WorstFaceScore;QualityAlgorithmVersion;QualityNotes;Sha256;ExactCopies;FaceCount;People;EventId;EventName;EventStartDate;EventEndDate;EventIsAuto;GpsLatitude;GpsLongitude;IsMissing;IsQuarantined;QuarantinePath;Error");
        foreach (var r in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new object?[] { r.Id, r.FullPath, r.SourceFolder, r.FileName, r.Extension, r.FileSize, r.CaptureDate, r.CaptureDateSource, r.AutoCaptureDate, r.AutoCaptureDateSource, r.EffectiveYear, r.Width, r.Height, r.CameraMake, r.CameraModel, r.QualityScore, r.TechnicalScore, r.SharpnessScore, r.BlurScore, r.ExposureScore, r.ContrastScore, r.NoiseScore, r.ResolutionScore, r.CompressionScore, r.FaceScore, r.EyeScore, r.EyeOpennessScore, r.ClosedEyeCount, r.BlinkPenalty, r.FacePoseScore, r.WorstFaceScore, r.QualityAlgorithmVersion, r.QualityNotes, r.Sha256, r.ExactCopies, r.FaceCount, r.PeopleNames, r.EventId, r.EventName, r.EventStartDate, r.EventEndDate, r.EventIsAuto, r.GpsLatitude, r.GpsLongitude, r.IsMissing, r.IsQuarantined, r.QuarantinePath, r.Error };
            await writer.WriteLineAsync(string.Join(';', values.Select(Csv)));
        }
    }

    private static string Csv(object? value)
    {
        var text = value switch
        {
            null => "",
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            float f => f.ToString("0.######", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private sealed class ExportRow
    {
        public long Id { get; init; }
        public string FullPath { get; init; } = "";
        public string SourceFolder { get; init; } = "";
        public string FileName { get; init; } = "";
        public string Extension { get; init; } = "";
        public long FileSize { get; init; }
        public string? CaptureDate { get; init; }
        public string CaptureDateSource { get; init; } = "";
        public string? AutoCaptureDate { get; init; }
        public string AutoCaptureDateSource { get; init; } = "";
        public int EffectiveYear { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string CameraMake { get; init; } = "";
        public string CameraModel { get; init; } = "";
        public double QualityScore { get; init; }
        public double TechnicalScore { get; init; }
        public double SharpnessScore { get; init; }
        public double BlurScore { get; init; }
        public double ExposureScore { get; init; }
        public double ContrastScore { get; init; }
        public double NoiseScore { get; init; }
        public double ResolutionScore { get; init; }
        public double CompressionScore { get; init; }
        public double FaceScore { get; init; }
        public double EyeScore { get; init; }
        public double EyeOpennessScore { get; init; }
        public int ClosedEyeCount { get; init; }
        public double BlinkPenalty { get; init; }
        public double FacePoseScore { get; init; }
        public double WorstFaceScore { get; init; }
        public int QualityAlgorithmVersion { get; init; }
        public string QualityNotes { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public int ExactCopies { get; init; }
        public int FaceCount { get; init; }
        public string PeopleNames { get; init; } = "";
        public long? EventId { get; init; }
        public string EventName { get; init; } = "";
        public string? EventStartDate { get; init; }
        public string? EventEndDate { get; init; }
        public bool EventIsAuto { get; init; }
        public double? GpsLatitude { get; init; }
        public double? GpsLongitude { get; init; }
        public bool IsMissing { get; init; }
        public bool IsQuarantined { get; init; }
        public string QuarantinePath { get; init; } = "";
        public string Error { get; init; } = "";
    }
}
