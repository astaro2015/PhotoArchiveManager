namespace PhotoArchiveManager.Models;

public sealed class BurstProgress
{
    public string Stage { get; init; } = "";
    public string CurrentFile { get; init; } = "";
    public int TotalFiles { get; init; }
    public int ProcessedFiles { get; init; }
    public int ComputedHashes { get; init; }
    public int CachedHashes { get; init; }
    public int ErrorFiles { get; init; }
    public int GroupsFound { get; init; }
}
