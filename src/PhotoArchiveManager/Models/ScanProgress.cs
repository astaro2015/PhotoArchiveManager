namespace PhotoArchiveManager.Models;

public sealed record ScanProgress(
    string Stage,
    int Total,
    int Processed,
    int Indexed,
    int Skipped,
    int Errors,
    string CurrentFile);
