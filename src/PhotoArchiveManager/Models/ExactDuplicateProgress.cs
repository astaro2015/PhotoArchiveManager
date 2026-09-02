namespace PhotoArchiveManager.Models;

public sealed record ExactDuplicateProgress(
    string Stage,
    int CandidateFiles,
    int ProcessedFiles,
    int HashedFiles,
    int CachedFiles,
    int ErrorFiles,
    long ProcessedBytes,
    long TotalBytes,
    string CurrentFile);
