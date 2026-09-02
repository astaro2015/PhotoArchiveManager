namespace PhotoArchiveManager.Models;

public sealed record OrganizationProgress(
    string Stage,
    int TotalFiles,
    int ProcessedFiles,
    int SucceededFiles,
    int FailedFiles,
    long BytesMoved,
    string CurrentFile);
