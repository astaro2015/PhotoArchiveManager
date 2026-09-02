namespace PhotoArchiveManager.Models;

public sealed class FaceIndexCandidate
{
    public long Id { get; init; }
    public string FullPath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public long FileSize { get; init; }
    public long LastWriteUtcTicks { get; init; }
    public int Orientation { get; init; } = 1;
    public string? CaptureDate { get; init; }
    public int FaceIndexVersion { get; init; }
    public long FaceIndexFileSize { get; init; }
    public long FaceIndexLastWriteUtcTicks { get; init; }
    public string FaceIndexError { get; init; } = "";
    public int CachedFaceCount { get; init; }

    public bool HasCurrentFaceIndex =>
        FaceIndexVersion == PeopleAnalyzerAlgorithmVersion &&
        FaceIndexFileSize == FileSize &&
        FaceIndexLastWriteUtcTicks == LastWriteUtcTicks;

    // Kept here so cache validity is deterministic even when queried without the analyzer instance.
    public const int PeopleAnalyzerAlgorithmVersion = 2;
}
