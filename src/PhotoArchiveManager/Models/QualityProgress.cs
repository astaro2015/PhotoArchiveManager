namespace PhotoArchiveManager.Models;

public sealed class QualityProgress
{
    public string Stage { get; init; } = "";
    public string CurrentFile { get; init; } = "";
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int ComputedFiles { get; init; }
    public int CachedFiles { get; init; }
    public int ErrorFiles { get; init; }
}
