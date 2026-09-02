namespace PhotoArchiveManager.Models;

public sealed class PerceptualHashCandidate
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SourceFolder { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public long FileSize { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Orientation { get; init; } = 1;
    public string? CaptureDate { get; init; }
    public string CaptureDateSource { get; init; } = "";
    public string CameraMake { get; init; } = "";
    public string CameraModel { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string DHash { get; init; } = "";
    public string AHash { get; init; } = "";
    public long PerceptualHashFileSize { get; init; }
    public long PerceptualHashLastWriteUtcTicks { get; init; }
    public string PerceptualHashError { get; init; } = "";

    public double QualityScore { get; init; } = -1;
    public double SharpnessScore { get; init; } = -1;
    public double BlurScore { get; init; } = -1;
    public double ExposureScore { get; init; } = -1;
    public double ResolutionScore { get; init; } = -1;
    public double CompressionScore { get; init; } = -1;
    public string QualityNotes { get; init; } = "";
    public long QualityFileSize { get; init; }
    public long QualityLastWriteUtcTicks { get; init; }
    public string QualityError { get; init; } = "";
    public int QualityAlgorithmVersion { get; init; }
    public int FaceCount { get; init; } = -1;
    public int EyeCount { get; init; } = -1;
    public double FaceScore { get; init; } = -1;
    public double EyeScore { get; init; } = -1;

    public bool HasCurrentPerceptualRecord =>
        PerceptualHashFileSize == FileSize &&
        PerceptualHashLastWriteUtcTicks == LastWriteUtcTicks &&
        ((!string.IsNullOrWhiteSpace(DHash) && !string.IsNullOrWhiteSpace(AHash)) || !string.IsNullOrWhiteSpace(PerceptualHashError));

    public bool HasValidCachedHash =>
        HasCurrentPerceptualRecord &&
        !string.IsNullOrWhiteSpace(DHash) &&
        !string.IsNullOrWhiteSpace(AHash) &&
        string.IsNullOrWhiteSpace(PerceptualHashError);

    public bool HasCurrentQualityRecord =>
        QualityFileSize == FileSize &&
        QualityLastWriteUtcTicks == LastWriteUtcTicks &&
        QualityAlgorithmVersion == 2 &&
        (QualityScore >= 0 || !string.IsNullOrWhiteSpace(QualityError));

    public bool HasValidCachedQuality =>
        HasCurrentQualityRecord && QualityScore >= 0 && string.IsNullOrWhiteSpace(QualityError);
}
