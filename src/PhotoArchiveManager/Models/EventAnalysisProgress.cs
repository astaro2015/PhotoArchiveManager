namespace PhotoArchiveManager.Models;

public sealed record EventAnalysisProgress(
    string Stage,
    int ProcessedFiles,
    int TotalFiles,
    int GpsReadFiles,
    int GpsCachedFiles,
    int GpsFoundFiles,
    int ErrorFiles,
    int EventsFound,
    string CurrentFile);
